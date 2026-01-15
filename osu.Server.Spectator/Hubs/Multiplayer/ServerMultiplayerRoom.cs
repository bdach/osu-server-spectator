// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using osu.Game.Online;
using osu.Game.Online.API;
using osu.Game.Online.Multiplayer;
using osu.Game.Online.Multiplayer.Countdown;
using osu.Game.Online.Rooms;
using osu.Game.Rulesets;
using osu.Server.Spectator.Database;
using osu.Server.Spectator.Database.Models;
using osu.Server.Spectator.Extensions;
using osu.Server.Spectator.Hubs.Multiplayer.Matchmaking;
using osu.Server.Spectator.Hubs.Multiplayer.Standard;
using osu.Server.Spectator.Services;

namespace osu.Server.Spectator.Hubs.Multiplayer
{
    public class ServerMultiplayerRoom : MultiplayerRoom
    {
        /// <summary>
        /// The amount of time allowed for players to finish loading gameplay before they're either forced into gameplay (if loaded) or booted to the menu (if still loading).
        /// </summary>
        private static readonly TimeSpan gameplay_load_timeout = TimeSpan.FromSeconds(30);

        private readonly IServerMultiplayerRoomController hub;
        private readonly IDatabaseFactory dbFactory;
        private readonly MultiplayerEventNotifier eventNotifier;
        private readonly ISharedInterop sharedInterop;
        private readonly ChatFilters chatFilters;

        private IMatchController controller = null!;

        private readonly HashSet<int> refereeIds = [];

        private ServerMultiplayerRoom(long roomId,
                                      IServiceProvider serviceProvider)
            : base(roomId)
        {
            hub = serviceProvider.GetRequiredService<IServerMultiplayerRoomController>();
            dbFactory = serviceProvider.GetRequiredService<IDatabaseFactory>();
            eventNotifier = serviceProvider.GetRequiredService<MultiplayerEventNotifier>();
            sharedInterop = serviceProvider.GetRequiredService<ISharedInterop>();
            chatFilters = serviceProvider.GetRequiredService<ChatFilters>();
        }

        /// <summary>
        /// Attempt to retrieve and construct a room from the database backend, based on a room ID specification.
        /// This will check the database backing to ensure things are in a consistent state.
        /// This will also mark the room as active, indicating that this server is now in control of the room's lifetime.
        /// </summary>
        /// <param name="roomId">The room identifier.</param>
        /// <exception cref="InvalidOperationException">If the room does not exist in the database.</exception>
        /// <exception cref="InvalidStateException">If the match has already ended.</exception>
        public static async Task<ServerMultiplayerRoom> InitialiseAsync(
            long roomId,
            IServiceProvider serviceProvider)
        {
            ServerMultiplayerRoom room = new ServerMultiplayerRoom(roomId, serviceProvider);

            // TODO: this call should be transactional, and mark the room as managed by this server instance.
            // This will allow for other instances to know not to reinitialise the room if the host arrives there.
            // Alternatively, we can move lobby retrieval away from osu-web and not require this in the first place.
            // Needs further discussion and consideration either way.
            using (var db = room.dbFactory.GetInstance())
            {
                //hub.Log(room, null, $"Retrieving room {roomId} from database");
                var databaseRoom = await db.GetRealtimeRoomAsync(roomId);

                if (databaseRoom == null)
                    throw new InvalidOperationException("Specified match does not exist.");

                if (databaseRoom.ends_at != null && databaseRoom.ends_at < DateTimeOffset.Now)
                    throw new InvalidStateException("Match has already ended.");

                room.ChannelID = databaseRoom.channel_id;
                room.Settings = new MultiplayerRoomSettings
                {
                    Name = databaseRoom.name,
                    Password = databaseRoom.password,
                    MatchType = databaseRoom.type.ToMatchType(),
                    QueueMode = databaseRoom.queue_mode.ToQueueMode(),
                    AutoStartDuration = TimeSpan.FromSeconds(databaseRoom.auto_start_duration),
                    AutoSkip = databaseRoom.auto_skip
                };

                foreach (var item in await db.GetAllPlaylistItemsAsync(roomId))
                    room.Playlist.Add(item.ToMultiplayerPlaylistItem());

                await room.ChangeMatchType(room.Settings.MatchType);

                //hub.Log(room, null, "Marking room active");
                await db.MarkRoomActiveAsync(room);
            }

            return room;
        }

        public static async Task<ServerMultiplayerRoom> InitialiseMatchmakingAsync(
            long roomId,
            uint poolId,
            IServiceProvider serviceProvider)
        {
            var room = await InitialiseAsync(roomId, serviceProvider);

            if (room.controller is not MatchmakingMatchController matchmakingController)
                throw new InvalidOperationException("Failed to initialise the matchmaking room (invalid controller).");

            matchmakingController.PoolId = poolId;
            return room;
        }

        public async Task AddUser(MultiplayerRoomUser user, MultiplayerRoomUserRole role)
        {
            // because match controllers may send subsequent information via Users collection hooks,
            // inform clients before adding user to the room.
            await eventNotifier.OnPlayerJoinedAsync(RoomID, user);

            Users.Add(user);
            using (var db = dbFactory.GetInstance())
                await db.AddRoomParticipantAsync(this, user);

            switch (role)
            {
                case MultiplayerRoomUserRole.Player:
                    try
                    {
                        // Run in background so we don't hold locks on user/room states.
                        _ = sharedInterop.AddUserToRoomAsync(user.UserID, RoomID, Settings.Password);
                    }
                    catch
                    {
                        // Errors are logged internally by SharedInterop.
                    }

                    break;

                case MultiplayerRoomUserRole.Referee:
                    refereeIds.Add(user.UserID);
                    break;
            }

            await controller.HandleUserJoined(user);
        }

        /// <summary>
        /// Ensures that all states in this <see cref="ServerMultiplayerRoom"/> are valid to be newly serialised out to a client.
        /// </summary>
        public void UpdateForRetrieval()
        {
            foreach (var countdown in ActiveCountdowns)
            {
                var countdownInfo = trackedCountdowns[countdown];

                DateTimeOffset countdownEnd = countdownInfo.StartTime + countdownInfo.Duration;
                TimeSpan timeRemaining = countdownEnd - DateTimeOffset.Now;

                countdown.TimeRemaining = timeRemaining.TotalSeconds > 0 ? timeRemaining : TimeSpan.Zero;
            }
        }

        public async Task RemoveUser(MultiplayerRoomUser user)
        {
            Users.Remove(user);
            using (var db = dbFactory.GetInstance())
                await db.RemoveRoomParticipantAsync(this, user);

            if (!refereeIds.Contains(user.UserID))
            {
                try
                {
                    // Run in background so we don't hold locks on user/room states.
                    _ = sharedInterop.RemoveUserFromRoomAsync(user.UserID, RoomID);
                }
                catch
                {
                    // Errors are logged internally by SharedInterop.
                }
            }
            else
            {
                refereeIds.Remove(user.UserID);
            }

            await checkVotesToSkipPassed();
            await controller.HandleUserLeft(user);
        }

        public async Task SetNewHost(MultiplayerRoomUser newHost)
        {
            Host = newHost;
            await eventNotifier.OnHostChangedAsync(RoomID, newHost.UserID);

            using (var db = dbFactory.GetInstance())
                await db.UpdateRoomHostAsync(this);
        }

        public async Task ChangeUserState(MultiplayerRoomUser user, MultiplayerUserState newState)
        {
            // There's a potential that a client attempts to change state while a message from the server is in transit. Silently block these changes rather than informing the client.
            switch (newState)
            {
                // If a client triggered `Idle` (ie. un-readying) before they received the `WaitingForLoad` message from the match starting.
                case MultiplayerUserState.Idle:
                    if (user.State.IsGameplayState())
                        return;

                    break;

                // If a client a triggered gameplay state before they received the `Idle` message from their gameplay being aborted.
                case MultiplayerUserState.Loaded:
                case MultiplayerUserState.ReadyForGameplay:
                    if (!user.State.IsGameplayState())
                        return;

                    break;
            }

            ensureValidStateSwitch(user.State, newState);

            await ChangeAndBroadcastUserState(user, newState);

            // Signal newly-spectating users to load gameplay if currently in the middle of play.
            if (newState == MultiplayerUserState.Spectating
                && (State == MultiplayerRoomState.WaitingForLoad || State == MultiplayerRoomState.Playing))
            {
                await eventNotifier.OnGameplayStartedAsync(RoomID, user.UserID);
            }

            await UpdateRoomStateIfRequired();
        }

        /// <summary>
        /// Given a room and a state transition, throw if there's an issue with the sequence of events.
        /// </summary>
        /// <param name="oldState">The old state.</param>
        /// <param name="newState">The new state.</param>
        private void ensureValidStateSwitch(MultiplayerUserState oldState, MultiplayerUserState newState)
        {
            switch (newState)
            {
                case MultiplayerUserState.Idle:
                    if (oldState.IsGameplayState())
                        throw new InvalidStateException("Cannot return to idle without aborting gameplay.");

                    // any non-gameplay state can return to idle.
                    break;

                case MultiplayerUserState.Ready:
                    if (oldState != MultiplayerUserState.Idle)
                        throw new InvalidStateChangeException(oldState, newState);

                    if (controller.CurrentItem.Expired)
                        throw new InvalidStateException("Cannot ready up while all items have been played.");

                    break;

                case MultiplayerUserState.WaitingForLoad:
                    // state is managed by the server.
                    throw new InvalidStateChangeException(oldState, newState);

                case MultiplayerUserState.Loaded:
                    if (oldState != MultiplayerUserState.WaitingForLoad)
                        throw new InvalidStateChangeException(oldState, newState);

                    break;

                case MultiplayerUserState.ReadyForGameplay:
                    if (oldState != MultiplayerUserState.Loaded)
                        throw new InvalidStateChangeException(oldState, newState);

                    break;

                case MultiplayerUserState.Playing:
                    // state is managed by the server.
                    throw new InvalidStateChangeException(oldState, newState);

                case MultiplayerUserState.FinishedPlay:
                    if (oldState != MultiplayerUserState.Playing)
                        throw new InvalidStateChangeException(oldState, newState);

                    break;

                case MultiplayerUserState.Results:
                    // state is managed by the server.
                    throw new InvalidStateChangeException(oldState, newState);

                case MultiplayerUserState.Spectating:
                    if (oldState != MultiplayerUserState.Idle && oldState != MultiplayerUserState.Ready)
                        throw new InvalidStateChangeException(oldState, newState);

                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(newState), newState, null);
            }
        }

        public async Task ChangeAndBroadcastUserState(MultiplayerRoomUser user, MultiplayerUserState state)
        {
            //((IServerMultiplayerRoomController)this).Log(room, user, $"User state changed from {user.State} to {state}");

            user.State = state;
            await eventNotifier.OnUserStateChangedAsync(RoomID, user.UserID, user.State);
            await controller.HandleUserStateChanged(user);
        }

        public async Task UpdateRoomStateIfRequired(GameplayAbortReason? abortReason = null)
        {
            //check whether a room state change is required.
            switch (State)
            {
                case MultiplayerRoomState.Open:
                    if (Settings.AutoStartEnabled)
                    {
                        bool shouldHaveCountdown = !controller.CurrentItem.Expired && Users.Any(u => u.State == MultiplayerUserState.Ready);

                        if (shouldHaveCountdown && !ActiveCountdowns.Any(c => c is MatchStartCountdown))
                            await StartCountdown(new MatchStartCountdown { TimeRemaining = Settings.AutoStartDuration }, StartMatch);
                    }

                    break;

                case MultiplayerRoomState.WaitingForLoad:
                    int countGameplayUsers = Users.Count(u => u.State.IsGameplayState());
                    int countReadyUsers = Users.Count(u => u.State == MultiplayerUserState.ReadyForGameplay);

                    // Attempt to start gameplay when no more users need to change states. If all users have aborted, this will abort the match.
                    if (countReadyUsers == countGameplayUsers)
                        await startOrStopGameplay(this);

                    break;

                case MultiplayerRoomState.Playing:
                    if (Users.All(u => u.State != MultiplayerUserState.Playing))
                    {
                        bool anyUserFinishedPlay = false;

                        foreach (var u in Users.Where(u => u.State == MultiplayerUserState.FinishedPlay))
                        {
                            anyUserFinishedPlay = true;
                            await ChangeAndBroadcastUserState(u, MultiplayerUserState.Results);
                        }

                        await changeRoomState(MultiplayerRoomState.Open);

                        if (anyUserFinishedPlay)
                            await eventNotifier.OnGameCompletedAsync(RoomID, CurrentPlaylistItem.ID);
                        else
                            await eventNotifier.OnGameAbortedAsync(RoomID, CurrentPlaylistItem.ID, abortReason);

                        await controller.HandleGameplayCompleted();
                    }

                    break;
            }
        }

        private async Task changeRoomState(MultiplayerRoomState newState)
        {
            //log(room, $"Room state changing from {room.State} to {newState}");

            State = newState;
            using (var db = dbFactory.GetInstance())
                await db.UpdateRoomStatusAsync(this);

            await eventNotifier.OnRoomStateChangedAsync(RoomID, newState);
        }

        public async Task ChangeBeatmapAvailability(MultiplayerRoomUser user, BeatmapAvailability newBeatmapAvailability)
        {
            if (user.BeatmapAvailability.Equals(newBeatmapAvailability))
                return;

            user.BeatmapAvailability = newBeatmapAvailability;

            await eventNotifier.OnUserBeatmapAvailabilityChangedAsync(RoomID, user.UserID, user.BeatmapAvailability);
        }

        public async Task ChangeUserStyle(MultiplayerRoomUser user, int? beatmapId, int? rulesetId)
        {
            if (user.BeatmapId == beatmapId && user.RulesetId == rulesetId)
                return;

            //((IServerMultiplayerRoomController)this).Log(room, user, $"User style changing from (b:{user.BeatmapId}, r:{user.RulesetId}) to (b:{beatmapId}, r:{rulesetId})");

            if (rulesetId < 0 || rulesetId > ILegacyRuleset.MAX_LEGACY_RULESET_ID)
                throw new InvalidStateException("Attempted to select an unsupported ruleset.");

            if (beatmapId != null || rulesetId != null)
            {
                if (!controller.CurrentItem.Freestyle)
                    throw new InvalidStateException("Current item does not allow free user styles.");

                using (var db = dbFactory.GetInstance())
                {
                    database_beatmap itemBeatmap = (await db.GetBeatmapAsync(controller.CurrentItem.BeatmapID))!;
                    database_beatmap? userBeatmap = beatmapId == null ? itemBeatmap : await db.GetBeatmapAsync(beatmapId.Value);

                    if (userBeatmap == null)
                        throw new InvalidStateException("Invalid beatmap selected.");

                    if (userBeatmap.beatmapset_id != itemBeatmap.beatmapset_id)
                        throw new InvalidStateException("Selected beatmap is not from the same beatmap set.");

                    if (rulesetId != null && userBeatmap.playmode != 0 && rulesetId != userBeatmap.playmode)
                        throw new InvalidStateException("Selected ruleset is not supported for the given beatmap.");
                }
            }

            user.BeatmapId = beatmapId;
            user.RulesetId = rulesetId;

            if (!controller.CurrentItem.ValidateUserMods(user, user.Mods, out var validMods))
            {
                user.Mods = validMods.ToArray();
                await eventNotifier.OnUserModsChangedAsync(RoomID, user.UserID, user.Mods);
            }

            await eventNotifier.OnUserStyleChangedAsync(RoomID, user.UserID, beatmapId, rulesetId);
        }

        public async Task ChangeUserMods(MultiplayerRoomUser user, IEnumerable<APIMod> newMods)
        {
            var newModList = newMods.ToList();

            if (!controller.CurrentItem.ValidateUserMods(user, newModList, out var validMods))
                throw new InvalidStateException($"Incompatible mods were selected: {string.Join(',', newModList.Except(validMods).Select(m => m.Acronym))}");

            if (user.Mods.SequenceEqual(newModList))
                return;

            user.Mods = newModList;

            await eventNotifier.OnUserModsChangedAsync(RoomID, user.UserID, newModList);
        }

        public Task ChangeMatchType(MatchType type)
        {
            switch (type)
            {
                case MatchType.Matchmaking:
                    return ChangeMatchType(new MatchmakingMatchController(this, dbFactory, eventNotifier));

                case MatchType.TeamVersus:
                    return ChangeMatchType(new TeamVersusMatchController(this, dbFactory, eventNotifier));

                default:
                    return ChangeMatchType(new HeadToHeadMatchController(this, dbFactory, eventNotifier));
            }
        }

        public async Task ChangeMatchType(IMatchController controller)
        {
            this.controller = controller;

            await this.controller.Initialise();

            foreach (var u in Users)
                await this.controller.HandleUserJoined(u);
        }

        public static async Task StartMatch(ServerMultiplayerRoom room)
        {
            if (room.State != MultiplayerRoomState.Open)
                throw new InvalidStateException("Can't start match when already in a running state.");

            if (room.controller.CurrentItem.Expired)
                throw new InvalidStateException("Cannot start an expired playlist item.");

            // If no users are ready, skip the current item in the queue.
            if (room.Users.All(u => u.State != MultiplayerUserState.Ready))
            {
                await room.controller.HandleGameplayCompleted();
                return;
            }

            // This is the very first time users get a "gameplay" state. Reset any properties for the gameplay session.
            foreach (var user in room.Users)
                await room.changeUserVoteToSkipIntro(user, false);

            var readyUsers = room.Users.Where(u =>
                u.BeatmapAvailability.State == DownloadState.LocallyAvailable
                && (u.State == MultiplayerUserState.Ready || u.State == MultiplayerUserState.Idle)
            ).ToArray();

            foreach (var u in readyUsers)
                await room.ChangeAndBroadcastUserState(u, MultiplayerUserState.WaitingForLoad);

            await room.changeRoomState(MultiplayerRoomState.WaitingForLoad);

            await room.eventNotifier.OnGameStartedAsync(room.RoomID, room.controller.CurrentItem.ID, room.controller.GetMatchDetails());

            await room.StartCountdown(new ForceGameplayStartCountdown { TimeRemaining = gameplay_load_timeout }, startOrStopGameplay);
        }

        /// <summary>
        /// Starts gameplay for all users in the <see cref="MultiplayerUserState.Loaded"/> or <see cref="MultiplayerUserState.ReadyForGameplay"/> states,
        /// and aborts gameplay for any others in the <see cref="MultiplayerUserState.WaitingForLoad"/> state.
        /// </summary>
        private static async Task startOrStopGameplay(ServerMultiplayerRoom room)
        {
            Debug.Assert(room.State == MultiplayerRoomState.WaitingForLoad);

            await room.StopAllCountdowns<ForceGameplayStartCountdown>();

            bool anyUserPlaying = false;

            // Start gameplay for users that are able to, and abort the others which cannot.
            foreach (var user in room.Users)
            {
                // TODO: bit of an issue, this. whats it even doing??
                //string? connectionId = users.GetConnectionIdForUser(user.UserID);

                //if (connectionId == null)
                //    continue;

                if (user.CanStartGameplay())
                {
                    await room.ChangeAndBroadcastUserState(user, MultiplayerUserState.Playing);
                    await room.eventNotifier.OnGameplayStartedAsync(room.RoomID, user.UserID);
                    anyUserPlaying = true;
                }
                else if (user.State == MultiplayerUserState.WaitingForLoad)
                {
                    await room.ChangeAndBroadcastUserState(user, MultiplayerUserState.Idle);
                    await room.eventNotifier.OnGameplayAbortedAsync(room.RoomID, user.UserID, GameplayAbortReason.LoadTookTooLong);
                    //((IServerMultiplayerRoomController)this).Log(room, user, "Gameplay aborted because this user took too long to load.");
                }
            }

            if (anyUserPlaying)
                await room.changeRoomState(MultiplayerRoomState.Playing);
            else
            {
                await room.changeRoomState(MultiplayerRoomState.Open);
                await room.eventNotifier.OnGameAbortedAsync(room.RoomID, room.CurrentPlaylistItem.ID, null);
                await room.controller.HandleGameplayCompleted();
            }
        }

        public async Task AbortMatch()
        {
            if (State != MultiplayerRoomState.WaitingForLoad && State != MultiplayerRoomState.Playing)
                throw new InvalidStateException("Cannot abort a match that hasn't started.");

            foreach (var user in Users)
                await ChangeAndBroadcastUserState(user, MultiplayerUserState.Idle);

            await UpdateRoomStateIfRequired(GameplayAbortReason.HostAbortedTheMatch);
        }

        public async Task AbortGameplay(MultiplayerRoomUser user)
        {
            if (!user.State.IsGameplayState())
                throw new InvalidStateException("Cannot abort gameplay while not in a gameplay state");

            await ChangeAndBroadcastUserState(user, MultiplayerUserState.Idle);
            await UpdateRoomStateIfRequired();
        }

        public async Task VoteToSkipIntro(MultiplayerRoomUser user)
        {
            if (!user.State.IsGameplayState())
                throw new InvalidStateException("Cannot skip while not in a gameplay state");

            await changeUserVoteToSkipIntro(user, true);
            await checkVotesToSkipPassed();
        }

        private async Task changeUserVoteToSkipIntro(MultiplayerRoomUser user, bool voted)
        {
            if (user.VotedToSkipIntro == voted)
                return;

            //((IServerMultiplayerRoomController)this).Log(room, user, $"Changing user vote to skip intro => {voted}");

            user.VotedToSkipIntro = voted;
            await eventNotifier.OnUserVotedToSkipIntro(RoomID, user.UserID, user.VotedToSkipIntro);
        }

        private async Task checkVotesToSkipPassed()
        {
            int countVotedUsers = Users.Count(u => u.State == MultiplayerUserState.Playing && u.VotedToSkipIntro);
            int countGameplayUsers = Users.Count(u => u.State == MultiplayerUserState.Playing);

            if (countVotedUsers >= countGameplayUsers / 2 + 1)
                await eventNotifier.OnVoteToSkipIntroPassed(RoomID);
        }

        public async Task AddPlaylistItem(MultiplayerRoomUser user, MultiplayerPlaylistItem item)
        {
            await controller.AddPlaylistItem(item, user);
            await UpdateRoomStateIfRequired();
        }

        public Task EditPlaylistItem(MultiplayerRoomUser user, MultiplayerClientState clientState, MultiplayerPlaylistItem item)
            => controller.EditPlaylistItem(item, user, clientState);

        public async Task RemovePlaylistItem(MultiplayerRoomUser user, long playlistItemId)
        {
            await controller.RemovePlaylistItem(playlistItemId, user);
            await UpdateRoomStateIfRequired();
        }

        public async Task OnPlaylistItemChanged(MultiplayerPlaylistItem item, bool beatmapChanged)
        {
            if (item.ID == Settings.PlaylistItemId)
            {
                await ensureAllUsersValidStyle();
                await unreadyAllUsers(beatmapChanged);
            }

            await eventNotifier.OnPlaylistItemChangedAsync(RoomID, item);
        }

        public async Task ChangeSettings(MultiplayerRoomSettings settings)
        {
            settings.Name = await chatFilters.FilterAsync(settings.Name);

            // Server is authoritative over the playlist item ID.
            // Todo: This needs to change for tournament mode.
            settings.PlaylistItemId = Settings.PlaylistItemId;

            if (Settings.Equals(settings))
                return;

            var previousSettings = Settings;

            if (settings.MatchType == MatchType.Playlists)
                throw new InvalidStateException("Invalid match type selected");

            try
            {
                Settings = settings;
                await updateDatabaseSettings();
            }
            catch
            {
                // rollback settings if an error occurred when updating the database.
                Settings = previousSettings;
                throw;
            }

            if (previousSettings.MatchType != settings.MatchType)
            {
                await ChangeMatchType(settings.MatchType);
                //log(room, $"Switching room ruleset to {room.Controller}");
            }

            await controller.HandleSettingsChanged();
            await OnSettingsChanged(false);

            await UpdateRoomStateIfRequired();
        }

        private async Task updateDatabaseSettings()
        {
            var playlistItem = Playlist.FirstOrDefault(item => item.ID == Settings.PlaylistItemId);

            if (playlistItem == null)
                throw new InvalidStateException("Attempted to select a playlist item not contained by the room.");

            using (var db = dbFactory.GetInstance())
                await db.UpdateRoomSettingsAsync(this);
        }

        public async Task OnSettingsChanged(bool playlistItemChanged)
        {
            await ensureAllUsersValidStyle();

            // this should probably only happen for gameplay-related changes, but let's just keep things simple for now.
            await unreadyAllUsers(playlistItemChanged);

            await eventNotifier.OnSettingsChangedAsync(RoomID, Settings);
        }

        private async Task ensureAllUsersValidStyle()
        {
            if (!controller.CurrentItem.Freestyle)
            {
                // Reset entire style when freestyle is disabled.
                foreach (var user in Users)
                    await ChangeUserStyle(user, null, null);
            }
            else
            {
                database_beatmap itemBeatmap;
                database_beatmap[] validDifficulties;

                using (var db = dbFactory.GetInstance())
                {
                    itemBeatmap = (await db.GetBeatmapAsync(controller.CurrentItem.BeatmapID))!;
                    validDifficulties = await db.GetBeatmapsAsync(itemBeatmap.beatmapset_id);
                }

                foreach (var user in Users)
                {
                    int? userBeatmapId = user.BeatmapId;
                    int? userRulesetId = user.RulesetId;

                    database_beatmap? foundBeatmap = validDifficulties.SingleOrDefault(b => b.beatmap_id == userBeatmapId);

                    // Reset beatmap style if it's not a valid difficulty for the current beatmap set.
                    if (userBeatmapId != null && foundBeatmap == null)
                        userBeatmapId = null;

                    int beatmapRuleset = foundBeatmap?.playmode ?? itemBeatmap.playmode;

                    // Reset ruleset style when it's no longer valid for the resolved beatmap.
                    if (userRulesetId != null && beatmapRuleset > 0 && userRulesetId != beatmapRuleset)
                        userRulesetId = null;

                    await ChangeUserStyle(user, userBeatmapId, userRulesetId);
                }
            }

            foreach (var user in Users)
            {
                if (!controller.CurrentItem.ValidateUserMods(user, user.Mods, out var validMods))
                    await ChangeUserMods(user, validMods);
            }
        }

        private async Task unreadyAllUsers(bool resetBeatmapAvailability)
        {
            //log(room, "Unreadying all users");

            foreach (var u in Users.Where(u => u.State == MultiplayerUserState.Ready).ToArray())
                await ChangeAndBroadcastUserState(u, MultiplayerUserState.Idle);

            if (resetBeatmapAvailability)
            {
                //log(room, "Resetting all users' beatmap availability");

                foreach (var user in Users)
                    await ChangeBeatmapAvailability(user, new BeatmapAvailability(DownloadState.Unknown));
            }

            // Assume some destructive operation took place to warrant unreadying all users, and pre-emptively stop any match start countdown.
            // For example, gameplay-specific changes to the match settings or the current playlist item.
            await StopAllCountdowns<MatchStartCountdown>();
        }

        #region Countdowns

        private int nextCountdownId;
        private readonly Dictionary<MultiplayerCountdown, CountdownInfo> trackedCountdowns = new Dictionary<MultiplayerCountdown, CountdownInfo>();

        /// <summary>
        /// Starts a new countdown.
        /// </summary>
        /// <param name="countdown">The countdown to start. The <see cref="MultiplayerRoom"/> will receive this object for the duration of the countdown.</param>
        /// <param name="onComplete">A callback to be invoked when the countdown completes.</param>
        public async Task StartCountdown<T>(T countdown, Func<ServerMultiplayerRoom, Task>? onComplete = null)
            where T : MultiplayerCountdown
        {
            if (countdown.IsExclusive)
                await StopAllCountdowns<T>();

            countdown.ID = Interlocked.Increment(ref nextCountdownId);

            CountdownInfo countdownInfo = new CountdownInfo(countdown);

            trackedCountdowns[countdown] = countdownInfo;
            ActiveCountdowns.Add(countdown);

            await eventNotifier.OnNewMatchEventAsync(RoomID, new CountdownStartedEvent(countdown));

            countdownInfo.Task = start();

            async Task start()
            {
                // Run the countdown.
                try
                {
                    using (var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(countdownInfo.StopSource.Token, countdownInfo.SkipSource.Token))
                        await Task.Delay(countdownInfo.Duration, cancellationSource.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Clients need to be notified of cancellations in the following code.
                }

                // Notify users that the countdown has finished (or cancelled) and run the continuation.
                // Note: The room must be re-retrieved rather than using our own instance to enforce single-thread access.
                using (var roomUsage = await hub.TryGetRoom(RoomID))
                {
                    try
                    {
                        if (roomUsage?.Item == null)
                            return;

                        if (countdownInfo.StopSource.IsCancellationRequested)
                            return;

                        Debug.Assert(trackedCountdowns.ContainsKey(countdown));
                        Debug.Assert(ActiveCountdowns.Contains(countdown));

                        await StopCountdown(countdown);

                        // The continuation could be run outside of the room lock, however it seems saner to run it within the same lock as the cancellation token usage.
                        // Furthermore, providing a room-id instead of the room becomes cumbersome for usages, so this also provides a nicer API.
                        if (onComplete != null)
                            await onComplete(roomUsage.Item);
                    }
                    finally
                    {
                        countdownInfo.Dispose();
                    }
                }
            }
        }

        /// <summary>
        /// Stops all countdowns of the given type, preventing their callbacks from running.
        /// </summary>
        /// <typeparam name="T">The countdown type.</typeparam>
        public async Task StopAllCountdowns<T>()
            where T : MultiplayerCountdown
        {
            foreach (var countdown in ActiveCountdowns.OfType<T>().ToArray())
                await StopCountdown(countdown);
        }

        /// <summary>
        /// Stops the given countdown, preventing its callback from running.
        /// </summary>
        /// <param name="countdown">The countdown to stop.</param>
        public async Task StopCountdown(MultiplayerCountdown countdown)
        {
            if (!trackedCountdowns.TryGetValue(countdown, out CountdownInfo? countdownInfo))
                return;

            countdownInfo.StopSource.Cancel();

            trackedCountdowns.Remove(countdown);
            ActiveCountdowns.Remove(countdownInfo.Countdown);

            await eventNotifier.OnNewMatchEventAsync(RoomID, new CountdownStoppedEvent(countdownInfo.Countdown.ID));
        }

        /// <summary>
        /// Skips to the end of the given countdown and runs its callback (e.g. to start the match) as soon as possible unless the countdown has been cancelled.
        /// </summary>
        /// <param name="countdown">The countdown.</param>
        /// <returns>
        /// A task which will become completed when the active countdown completes. Make sure to await this *outside* a usage.
        /// </returns>
        public Task SkipToEndOfCountdown(MultiplayerCountdown? countdown)
        {
            if (countdown == null || !trackedCountdowns.TryGetValue(countdown, out CountdownInfo? countdownInfo))
                return Task.CompletedTask;

            countdownInfo.SkipSource.Cancel();
            return countdownInfo.Task;
        }

        /// <summary>
        /// Retrieves the task for the given countdown, if one is running.
        /// </summary>
        /// <param name="countdown">The countdown to retrieve the task of.</param>
        public Task GetCountdownTask(MultiplayerCountdown? countdown)
            => countdown == null || !trackedCountdowns.TryGetValue(countdown, out CountdownInfo? countdownInfo) ? Task.CompletedTask : countdownInfo.Task;

        /// <summary>
        /// Searches the currently active countdowns and retrieves one of the given type.
        /// </summary>
        /// <typeparam name="T">The countdown type.</typeparam>
        /// <returns>A countdown of the given type, or null if no such countdown is running.</returns>
        public T? FindCountdownOfType<T>() where T : MultiplayerCountdown
            => ActiveCountdowns.OfType<T>().FirstOrDefault();

        /// <summary>
        /// Searches the currently active countdowns and retrieves the one matching a given ID.
        /// </summary>
        /// <param name="countdownId">The countdown ID.</param>
        /// <returns>The countdown matching the given ID, or null if no such countdown is running.</returns>
        public MultiplayerCountdown? FindCountdownById(int countdownId)
            => ActiveCountdowns.SingleOrDefault(c => c.ID == countdownId);

        /// <summary>
        /// Retrieves the remaining time for a countdown.
        /// </summary>
        /// <param name="countdown">The countdown.</param>
        /// <returns>The remaining time.</returns>
        /// <exception cref="InvalidOperationException"></exception>
        public TimeSpan GetCountdownRemainingTime(MultiplayerCountdown? countdown)
        {
            if (countdown == null || !trackedCountdowns.TryGetValue(countdown, out CountdownInfo? countdownInfo))
                return TimeSpan.Zero;

            TimeSpan elapsed = DateTimeOffset.Now - countdownInfo.StartTime;
            return elapsed >= countdownInfo.Duration ? TimeSpan.Zero : countdownInfo.Duration - elapsed;
        }

        private class CountdownInfo : IDisposable
        {
            public readonly MultiplayerCountdown Countdown;
            public readonly CancellationTokenSource StopSource = new CancellationTokenSource();
            public readonly CancellationTokenSource SkipSource = new CancellationTokenSource();
            public readonly DateTimeOffset StartTime = DateTimeOffset.Now;
            public readonly TimeSpan Duration;

            public Task Task { get; set; } = null!;

            public CountdownInfo(MultiplayerCountdown countdown)
            {
                Countdown = countdown;
                Duration = countdown.TimeRemaining;
            }

            public void Dispose()
            {
                StopSource.Dispose();
                SkipSource.Dispose();
            }
        }

        #endregion

        #region Controller proxying

        public Task<bool> UserCanJoin(int userId) => controller.UserCanJoin(userId);
        public Task HandleUserRequest(MultiplayerRoomUser user, MatchUserRequest request) => controller.HandleUserRequest(user, request);

        public new MultiplayerPlaylistItem CurrentPlaylistItem => controller.CurrentItem;

        // TODO: dodge but it is what it is
        public Task ToggleSelectionAsync(MultiplayerRoomUser user, long playlistItemId) => ((MatchmakingMatchController)controller).ToggleSelectionAsync(user, playlistItemId);
        public void SkipToNextStage() => ((MatchmakingMatchController)controller).SkipToNextStage(out _);

        #endregion
    }
}
