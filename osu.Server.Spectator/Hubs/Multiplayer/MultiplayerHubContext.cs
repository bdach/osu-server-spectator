// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using MessagePack;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using osu.Game.Online;
using osu.Game.Online.API;
using osu.Game.Online.Matchmaking;
using osu.Game.Online.Multiplayer;
using osu.Game.Online.Multiplayer.Countdown;
using osu.Game.Online.Rooms;
using osu.Game.Rulesets;
using osu.Server.Spectator.Database.Models;
using osu.Server.Spectator.Entities;
using osu.Server.Spectator.Extensions;
using osu.Server.Spectator.Services;
using IDatabaseFactory = osu.Server.Spectator.Database.IDatabaseFactory;

namespace osu.Server.Spectator.Hubs.Multiplayer
{
    /// <summary>
    /// Allows communication with multiplayer clients from potentially outside of a direct <see cref="MultiplayerHub"/> context.
    /// </summary>
    public class MultiplayerHubContext
        : IServerMultiplayerRoomController, IMultiplayerUserHubContext, IMultiplayerRefereeHubContext
    {
        /// <summary>
        /// The amount of time allowed for players to finish loading gameplay before they're either forced into gameplay (if loaded) or booted to the menu (if still loading).
        /// </summary>
        private static readonly TimeSpan gameplay_load_timeout = TimeSpan.FromSeconds(30);

        private readonly IHubContext<MultiplayerHub> context;
        private readonly EntityStore<ServerMultiplayerRoom> rooms;
        private readonly EntityStore<MultiplayerClientState> users;
        private readonly IDatabaseFactory databaseFactory;
        private readonly ILogger logger;
        private readonly MultiplayerEventLogger multiplayerEventLogger;
        private readonly ISharedInterop sharedInterop;
        private readonly ChatFilters chatFilters;

        public MultiplayerHubContext(
            IHubContext<MultiplayerHub> context,
            EntityStore<ServerMultiplayerRoom> rooms,
            EntityStore<MultiplayerClientState> users,
            ILoggerFactory loggerFactory,
            IDatabaseFactory databaseFactory,
            MultiplayerEventLogger multiplayerEventLogger,
            ISharedInterop sharedInterop,
            ChatFilters chatFilters)
        {
            this.context = context;
            this.rooms = rooms;
            this.users = users;
            this.databaseFactory = databaseFactory;
            this.multiplayerEventLogger = multiplayerEventLogger;
            this.sharedInterop = sharedInterop;
            this.chatFilters = chatFilters;

            logger = loggerFactory.CreateLogger(nameof(MultiplayerHub).Replace("Hub", string.Empty));
        }

        #region IServerMultiplayerRoomController

        Task<ItemUsage<ServerMultiplayerRoom>?> IServerMultiplayerRoomController.TryGetRoom(long roomId)
        {
            return rooms.TryGetForUse(roomId);
        }

        Task IServerMultiplayerRoomController.NotifyNewMatchEvent(ServerMultiplayerRoom room, MatchServerEvent e)
        {
            return context.Clients.Group(MultiplayerHub.GetGroupId(room.RoomID)).SendAsync(nameof(IMultiplayerClient.MatchEvent), e);
        }

        Task IServerMultiplayerRoomController.NotifyMatchRoomStateChanged(ServerMultiplayerRoom room)
        {
            return context.Clients.Group(MultiplayerHub.GetGroupId(room.RoomID)).SendAsync(nameof(IMultiplayerClient.MatchRoomStateChanged), room.MatchState);
        }

        Task IServerMultiplayerRoomController.NotifyMatchUserStateChanged(ServerMultiplayerRoom room, MultiplayerRoomUser user)
        {
            return context.Clients.Group(MultiplayerHub.GetGroupId(room.RoomID)).SendAsync(nameof(IMultiplayerClient.MatchUserStateChanged), user.UserID, user.MatchState);
        }

        Task IServerMultiplayerRoomController.NotifyPlaylistItemAdded(ServerMultiplayerRoom room, MultiplayerPlaylistItem item)
        {
            return context.Clients.Group(MultiplayerHub.GetGroupId(room.RoomID)).SendAsync(nameof(IMultiplayerClient.PlaylistItemAdded), item);
        }

        Task IServerMultiplayerRoomController.NotifyPlaylistItemRemoved(ServerMultiplayerRoom room, long playlistItemId)
        {
            return context.Clients.Group(MultiplayerHub.GetGroupId(room.RoomID)).SendAsync(nameof(IMultiplayerClient.PlaylistItemRemoved), playlistItemId);
        }

        async Task IServerMultiplayerRoomController.NotifyPlaylistItemChanged(ServerMultiplayerRoom room, MultiplayerPlaylistItem item, bool beatmapChanged)
        {
            if (item.ID == room.Settings.PlaylistItemId)
            {
                await ensureAllUsersValidStyle(room);
                await ((IServerMultiplayerRoomController)this).UnreadyAllUsers(room, beatmapChanged);
            }

            await context.Clients.Group(MultiplayerHub.GetGroupId(room.RoomID)).SendAsync(nameof(IMultiplayerClient.PlaylistItemChanged), item);
        }

        async Task IServerMultiplayerRoomController.NotifySettingsChanged(ServerMultiplayerRoom room, bool playlistItemChanged)
        {
            await ensureAllUsersValidStyle(room);

            // this should probably only happen for gameplay-related changes, but let's just keep things simple for now.
            await ((IServerMultiplayerRoomController)this).UnreadyAllUsers(room, playlistItemChanged);

            await context.Clients.Group(MultiplayerHub.GetGroupId(room.RoomID)).SendAsync(nameof(IMultiplayerClient.SettingsChanged), room.Settings);
        }

        private async Task ensureAllUsersValidStyle(ServerMultiplayerRoom room)
        {
            if (!room.Controller.CurrentItem.Freestyle)
            {
                // Reset entire style when freestyle is disabled.
                foreach (var user in room.Users)
                    await ((IServerMultiplayerRoomController)this).ChangeUserStyle(null, null, room, user);
            }
            else
            {
                database_beatmap itemBeatmap;
                database_beatmap[] validDifficulties;

                using (var db = databaseFactory.GetInstance())
                {
                    itemBeatmap = (await db.GetBeatmapAsync(room.Controller.CurrentItem.BeatmapID))!;
                    validDifficulties = await db.GetBeatmapsAsync(itemBeatmap.beatmapset_id);
                }

                foreach (var user in room.Users)
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

                    await ((IServerMultiplayerRoomController)this).ChangeUserStyle(userBeatmapId, userRulesetId, room, user);
                }
            }

            foreach (var user in room.Users)
            {
                if (!room.Controller.CurrentItem.ValidateUserMods(user, user.Mods, out var validMods))
                    await ((IServerMultiplayerRoomController)this).ChangeUserMods(validMods, room, user);
            }
        }

        async Task IServerMultiplayerRoomController.UnreadyAllUsers(ServerMultiplayerRoom room, bool resetBeatmapAvailability)
        {
            log(room, "Unreadying all users");

            foreach (var u in room.Users.Where(u => u.State == MultiplayerUserState.Ready).ToArray())
                await ((IServerMultiplayerRoomController)this).ChangeAndBroadcastUserState(room, u, MultiplayerUserState.Idle);

            if (resetBeatmapAvailability)
            {
                log(room, "Resetting all users' beatmap availability");

                foreach (var user in room.Users)
                    await ((IServerMultiplayerRoomController)this).ChangeAndBroadcastUserBeatmapAvailability(room, user, new BeatmapAvailability(DownloadState.Unknown));
            }

            // Assume some destructive operation took place to warrant unreadying all users, and pre-emptively stop any match start countdown.
            // For example, gameplay-specific changes to the match settings or the current playlist item.
            await room.StopAllCountdowns<MatchStartCountdown>();
        }

        async Task IServerMultiplayerRoomController.ChangeUserStyle(int? beatmapId, int? rulesetId, ServerMultiplayerRoom room, MultiplayerRoomUser user)
        {
            if (user.BeatmapId == beatmapId && user.RulesetId == rulesetId)
                return;

            ((IServerMultiplayerRoomController)this).Log(room, user, $"User style changing from (b:{user.BeatmapId}, r:{user.RulesetId}) to (b:{beatmapId}, r:{rulesetId})");

            if (rulesetId < 0 || rulesetId > ILegacyRuleset.MAX_LEGACY_RULESET_ID)
                throw new InvalidStateException("Attempted to select an unsupported ruleset.");

            if (beatmapId != null || rulesetId != null)
            {
                if (!room.Controller.CurrentItem.Freestyle)
                    throw new InvalidStateException("Current item does not allow free user styles.");

                using (var db = databaseFactory.GetInstance())
                {
                    database_beatmap itemBeatmap = (await db.GetBeatmapAsync(room.Controller.CurrentItem.BeatmapID))!;
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

            if (!room.Controller.CurrentItem.ValidateUserMods(user, user.Mods, out var validMods))
            {
                user.Mods = validMods.ToArray();
                await context.Clients.Group(MultiplayerHub.GetGroupId(room.RoomID)).SendAsync(nameof(IMultiplayerClient.UserModsChanged), user.UserID, user.Mods);
            }

            await context.Clients.Group(MultiplayerHub.GetGroupId(room.RoomID)).SendAsync(nameof(IMultiplayerClient.UserStyleChanged), user.UserID, beatmapId, rulesetId);
        }

        async Task IServerMultiplayerRoomController.ChangeUserMods(IEnumerable<APIMod> newMods, ServerMultiplayerRoom room, MultiplayerRoomUser user)
        {
            var newModList = newMods.ToList();

            if (!room.Controller.CurrentItem.ValidateUserMods(user, newModList, out var validMods))
                throw new InvalidStateException($"Incompatible mods were selected: {string.Join(',', newModList.Except(validMods).Select(m => m.Acronym))}");

            if (user.Mods.SequenceEqual(newModList))
                return;

            user.Mods = newModList;

            await context.Clients.Group(MultiplayerHub.GetGroupId(room.RoomID)).SendAsync(nameof(IMultiplayerClient.UserModsChanged), user.UserID, newModList);
        }

        async Task IServerMultiplayerRoomController.ChangeAndBroadcastUserState(ServerMultiplayerRoom room, MultiplayerRoomUser user, MultiplayerUserState state)
        {
            ((IServerMultiplayerRoomController)this).Log(room, user, $"User state changed from {user.State} to {state}");

            user.State = state;

            await context.Clients.Group(MultiplayerHub.GetGroupId(room.RoomID)).SendAsync(nameof(IMultiplayerClient.UserStateChanged), user.UserID, user.State);

            await room.Controller.HandleUserStateChanged(user);
        }

        async Task IServerMultiplayerRoomController.ChangeAndBroadcastUserBeatmapAvailability(ServerMultiplayerRoom room, MultiplayerRoomUser user, BeatmapAvailability newBeatmapAvailability)
        {
            if (user.BeatmapAvailability.Equals(newBeatmapAvailability))
                return;

            user.BeatmapAvailability = newBeatmapAvailability;

            await context.Clients.Group(MultiplayerHub.GetGroupId(room.RoomID)).SendAsync(nameof(IMultiplayerClient.UserBeatmapAvailabilityChanged), user.UserID, user.BeatmapAvailability);
        }

        async Task IServerMultiplayerRoomController.ChangeUserVoteToSkipIntro(ServerMultiplayerRoom room, MultiplayerRoomUser user, bool voted)
        {
            if (user.VotedToSkipIntro == voted)
                return;

            ((IServerMultiplayerRoomController)this).Log(room, user, $"Changing user vote to skip intro => {voted}");

            user.VotedToSkipIntro = voted;
            await context.Clients.Group(MultiplayerHub.GetGroupId(room.RoomID)).SendAsync(nameof(IMultiplayerClient.UserVotedToSkipIntro), user.UserID, voted);
        }

        async Task IServerMultiplayerRoomController.StartMatch(ServerMultiplayerRoom room)
        {
            if (room.State != MultiplayerRoomState.Open)
                throw new InvalidStateException("Can't start match when already in a running state.");

            if (room.Controller.CurrentItem.Expired)
                throw new InvalidStateException("Cannot start an expired playlist item.");

            // If no users are ready, skip the current item in the queue.
            if (room.Users.All(u => u.State != MultiplayerUserState.Ready))
            {
                await room.Controller.HandleGameplayCompleted();
                return;
            }

            // This is the very first time users get a "gameplay" state. Reset any properties for the gameplay session.
            foreach (var user in room.Users)
                await ((IServerMultiplayerRoomController)this).ChangeUserVoteToSkipIntro(room, user, false);

            var readyUsers = room.Users.Where(u =>
                u.BeatmapAvailability.State == DownloadState.LocallyAvailable
                && (u.State == MultiplayerUserState.Ready || u.State == MultiplayerUserState.Idle)
            ).ToArray();

            foreach (var u in readyUsers)
                await ((IServerMultiplayerRoomController)this).ChangeAndBroadcastUserState(room, u, MultiplayerUserState.WaitingForLoad);

            await changeRoomState(room, MultiplayerRoomState.WaitingForLoad);

            await context.Clients.Group(MultiplayerHub.GetGroupId(room.RoomID)).SendAsync(nameof(IMultiplayerClient.LoadRequested));

            await room.StartCountdown(new ForceGameplayStartCountdown { TimeRemaining = gameplay_load_timeout }, startOrStopGameplay);

            await multiplayerEventLogger.LogGameStartedAsync(room.RoomID, room.Controller.CurrentItem.ID, room.Controller.GetMatchDetails());
        }

        async Task IServerMultiplayerRoomController.UpdateRoomStateIfRequired(ServerMultiplayerRoom room)
        {
            //check whether a room state change is required.
            switch (room.State)
            {
                case MultiplayerRoomState.Open:
                    if (room.Settings.AutoStartEnabled)
                    {
                        bool shouldHaveCountdown = !room.Controller.CurrentItem.Expired && room.Users.Any(u => u.State == MultiplayerUserState.Ready);

                        if (shouldHaveCountdown && !room.ActiveCountdowns.Any(c => c is MatchStartCountdown))
                            await room.StartCountdown(new MatchStartCountdown { TimeRemaining = room.Settings.AutoStartDuration }, r => ((IServerMultiplayerRoomController)this).StartMatch(r));
                    }

                    break;

                case MultiplayerRoomState.WaitingForLoad:
                    int countGameplayUsers = room.Users.Count(u => IsGameplayState(u.State));
                    int countReadyUsers = room.Users.Count(u => u.State == MultiplayerUserState.ReadyForGameplay);

                    // Attempt to start gameplay when no more users need to change states. If all users have aborted, this will abort the match.
                    if (countReadyUsers == countGameplayUsers)
                        await startOrStopGameplay(room);

                    break;

                case MultiplayerRoomState.Playing:
                    if (room.Users.All(u => u.State != MultiplayerUserState.Playing))
                    {
                        bool anyUserFinishedPlay = false;

                        foreach (var u in room.Users.Where(u => u.State == MultiplayerUserState.FinishedPlay))
                        {
                            anyUserFinishedPlay = true;
                            await ((IServerMultiplayerRoomController)this).ChangeAndBroadcastUserState(room, u, MultiplayerUserState.Results);
                        }

                        await changeRoomState(room, MultiplayerRoomState.Open);
                        await context.Clients.Group(MultiplayerHub.GetGroupId(room.RoomID)).SendAsync(nameof(IMultiplayerClient.ResultsReady));

                        if (anyUserFinishedPlay)
                            await multiplayerEventLogger.LogGameCompletedAsync(room.RoomID, room.CurrentPlaylistItem.ID);
                        else
                            await multiplayerEventLogger.LogGameAbortedAsync(room.RoomID, room.CurrentPlaylistItem.ID);

                        await room.Controller.HandleGameplayCompleted();
                    }

                    break;
            }
        }

        async Task IServerMultiplayerRoomController.NotifyMatchmakingItemSelected(ServerMultiplayerRoom room, int userId, long playlistItemId)
        {
            await context.Clients.Group(MultiplayerHub.GetGroupId(room.RoomID)).SendAsync(nameof(IMatchmakingClient.MatchmakingItemSelected), userId, playlistItemId);
        }

        async Task IServerMultiplayerRoomController.NotifyMatchmakingItemDeselected(ServerMultiplayerRoom room, int userId, long playlistItemId)
        {
            await context.Clients.Group(MultiplayerHub.GetGroupId(room.RoomID)).SendAsync(nameof(IMatchmakingClient.MatchmakingItemDeselected), userId, playlistItemId);
        }

        async Task IServerMultiplayerRoomController.CheckVotesToSkipPassed(ServerMultiplayerRoom room)
        {
            int countVotedUsers = room.Users.Count(u => u.State == MultiplayerUserState.Playing && u.VotedToSkipIntro);
            int countGameplayUsers = room.Users.Count(u => u.State == MultiplayerUserState.Playing);

            if (countVotedUsers >= countGameplayUsers / 2 + 1)
                await context.Clients.Group(MultiplayerHub.GetGroupId(room.RoomID)).SendAsync(nameof(IMultiplayerClient.VoteToSkipIntroPassed));
        }

        async Task IServerMultiplayerRoomController.LeaveRoom(int userId, MultiplayerClientState state, ItemUsage<ServerMultiplayerRoom> roomUsage, bool wasKick)
        {
            if (state.CurrentRoomID == null)
                return;

            try
            {
                var room = roomUsage.Item;
                if (room == null)
                    throw new InvalidOperationException("Attempted to operate on a null room");

                var user = room.Users.FirstOrDefault(u => u.UserID == state.UserId);

                ((IServerMultiplayerRoomController)this).Log(room, user, wasKick ? "User kicked" : "User left");

                await context.Groups.RemoveFromGroupAsync(state.ConnectionId, MultiplayerHub.GetGroupId(room.RoomID));

                if (user == null)
                    throw new InvalidStateException("User was not in the expected room.");

                await room.RemoveUser(user);
                await removeDatabaseUser(room, user);

                try
                {
                    // Run in background so we don't hold locks on user/room states.
                    _ = sharedInterop.RemoveUserFromRoomAsync(state.UserId, state.CurrentRoomID.Value);
                }
                catch
                {
                    // Errors are logged internally by SharedInterop.
                }

                // handle closing the room if the only participant is the user which is leaving.
                if (room.Users.Count == 0)
                {
                    await endDatabaseMatch(userId, room);

                    // only destroy the usage after the database operation succeeds.
                    log(room, userId, "Stopping tracking of room (all users left).");
                    roomUsage.Destroy();
                    return;
                }

                await ((IServerMultiplayerRoomController)this).UpdateRoomStateIfRequired(room);

                // if this user was the host, we need to arbitrarily transfer host so the room can continue to exist.
                if (room.Host?.Equals(user) == true)
                {
                    // there *has* to still be at least one user in the room (see user check above).
                    var newHost = room.Users.First();

                    await setNewHost(room, newHost);
                }

                if (wasKick)
                {
                    // the target user has already been removed from the group, so send the message to them separately.
                    await context.Clients.Client(state.ConnectionId).SendAsync(nameof(IMultiplayerClient.UserKicked), user);
                    await context.Clients.Group(MultiplayerHub.GetGroupId(room.RoomID)).SendAsync(nameof(IMultiplayerClient.UserKicked), user);
                }
                else
                    await context.Clients.Group(MultiplayerHub.GetGroupId(room.RoomID)).SendAsync(nameof(IMultiplayerClient.UserLeft), user);
            }
            finally
            {
                state.ClearRoom();
            }
        }

        private async Task changeRoomState(ServerMultiplayerRoom room, MultiplayerRoomState newState)
        {
            log(room, $"Room state changing from {room.State} to {newState}");

            room.State = newState;
            using (var db = databaseFactory.GetInstance())
                await db.UpdateRoomStatusAsync(room);

            await context.Clients.Group(MultiplayerHub.GetGroupId(room.RoomID)).SendAsync(nameof(IMultiplayerClient.RoomStateChanged), newState);
        }

        /// <summary>
        /// Starts gameplay for all users in the <see cref="MultiplayerUserState.Loaded"/> or <see cref="MultiplayerUserState.ReadyForGameplay"/> states,
        /// and aborts gameplay for any others in the <see cref="MultiplayerUserState.WaitingForLoad"/> state.
        /// </summary>
        private async Task startOrStopGameplay(ServerMultiplayerRoom room)
        {
            Debug.Assert(room.State == MultiplayerRoomState.WaitingForLoad);

            await room.StopAllCountdowns<ForceGameplayStartCountdown>();

            bool anyUserPlaying = false;

            // Start gameplay for users that are able to, and abort the others which cannot.
            foreach (var user in room.Users)
            {
                string? connectionId = users.GetConnectionIdForUser(user.UserID);

                if (connectionId == null)
                    continue;

                if (user.CanStartGameplay())
                {
                    await ((IServerMultiplayerRoomController)this).ChangeAndBroadcastUserState(room, user, MultiplayerUserState.Playing);
                    await context.Clients.Client(connectionId).SendAsync(nameof(IMultiplayerClient.GameplayStarted));
                    anyUserPlaying = true;
                }
                else if (user.State == MultiplayerUserState.WaitingForLoad)
                {
                    await ((IServerMultiplayerRoomController)this).ChangeAndBroadcastUserState(room, user, MultiplayerUserState.Idle);
                    await context.Clients.Client(connectionId).SendAsync(nameof(IMultiplayerClient.GameplayAborted), GameplayAbortReason.LoadTookTooLong);
                    ((IServerMultiplayerRoomController)this).Log(room, user, "Gameplay aborted because this user took too long to load.");
                }
            }

            if (anyUserPlaying)
                await changeRoomState(room, MultiplayerRoomState.Playing);
            else
            {
                await changeRoomState(room, MultiplayerRoomState.Open);
                await multiplayerEventLogger.LogGameAbortedAsync(room.RoomID, room.CurrentPlaylistItem.ID);
                await room.Controller.HandleGameplayCompleted();
            }
        }

        void IServerMultiplayerRoomController.Log(ServerMultiplayerRoom room, MultiplayerRoomUser? user, string message, LogLevel logLevel)
        {
            logger.Log(logLevel, "[user:{userId}] [room:{roomID}] {message}",
                user?.UserID.ToString() ?? "???",
                room.RoomID,
                message.Trim());
        }

        #endregion

        #region IMultiplayerUserHubContext

        async Task IMultiplayerUserHubContext.InitialiseUserState(HubCallerContext caller)
        {
            using (var usage = await getOrCreateUserState(caller))
                usage.Item = new MultiplayerClientState(caller.ConnectionId, caller.GetUserId());
        }

        async Task IMultiplayerUserHubContext.CleanUpUserState(MultiplayerClientState state)
        {
            if (state.CurrentRoomID != null)
            {
                using (var roomUsage = await getUserRoom(state))
                    await ((IServerMultiplayerRoomController)this).LeaveRoom(state.UserId, state, roomUsage, false);
            }
        }

        async Task<MultiplayerRoom> IMultiplayerUserHubContext.CreateRoom(HubCallerContext caller, MultiplayerRoom room)
        {
            log(caller, "Attempting to create room");

            using (var db = databaseFactory.GetInstance())
            {
                if (await db.IsUserRestrictedAsync(caller.GetUserId()))
                    throw new InvalidStateException("Can't join a room when restricted.");
            }

            long roomId = await sharedInterop.CreateRoomAsync(caller.GetUserId(), room);
            await multiplayerEventLogger.LogRoomCreatedAsync(roomId, caller.GetUserId());

            return await joinOrCreateRoom(caller, roomId, room.Settings.Password, true);
        }

        async Task<MultiplayerRoom> IMultiplayerUserHubContext.JoinRoomWithPassword(HubCallerContext caller, long roomId, string password)
        {
            log(caller, $"Attempting to join room {roomId}");

            using (var db = databaseFactory.GetInstance())
            {
                if (await db.IsUserRestrictedAsync(caller.GetUserId()))
                    throw new InvalidStateException("Can't join a room when restricted.");
            }

            return await joinOrCreateRoom(caller, roomId, password, false);
        }

        private async Task<MultiplayerRoom> joinOrCreateRoom(HubCallerContext caller, long roomId, string password, bool isNewRoom)
        {
            byte[] roomBytes;

            using (var userUsage = await getOrCreateUserState(caller))
            {
                Debug.Assert(userUsage.Item != null);

                if (userUsage.Item.CurrentRoomID != null)
                    throw new InvalidStateException("Can't join a room when already in another room.");

                var roomUser = new MultiplayerRoomUser(caller.GetUserId());

                try
                {
                    using (var roomUsage = await rooms.GetForUse(roomId, isNewRoom))
                    {
                        ServerMultiplayerRoom? room = null;

                        try
                        {
                            room = roomUsage.Item ??= await ServerMultiplayerRoom.InitialiseAsync(roomId, this, databaseFactory, multiplayerEventLogger);

                            // this is a sanity check to keep *rooms* in a good state.
                            // in theory the connection clean-up code should handle this correctly.
                            if (room.Users.Any(u => u.UserID == roomUser.UserID))
                                throw new InvalidOperationException($"User {roomUser.UserID} attempted to join room {room.RoomID} they are already present in.");

                            if (!await room.Controller.UserCanJoin(roomUser.UserID))
                                throw new InvalidStateException("Not eligible to join this room.");

                            if (!string.IsNullOrEmpty(room.Settings.Password))
                            {
                                if (room.Settings.Password != password)
                                    throw new InvalidPasswordException();
                            }

                            if (isNewRoom && room.Settings.MatchType != MatchType.Matchmaking)
                                room.Host = roomUser;

                            userUsage.Item.SetRoom(roomId);

                            // because match controllers may send subsequent information via Users collection hooks,
                            // inform clients before adding user to the room.
                            await context.Clients.Group(MultiplayerHub.GetGroupId(roomId)).SendAsync(nameof(IMultiplayerClient.UserJoined), roomUser);

                            await room.AddUser(roomUser);
                            room.UpdateForRetrieval();

                            await addDatabaseUser(room, roomUser);
                            await context.Groups.AddToGroupAsync(caller.ConnectionId, MultiplayerHub.GetGroupId(roomId));

                            log(room, caller, "User joined");
                        }
                        catch
                        {
                            try
                            {
                                if (userUsage.Item.CurrentRoomID != null)
                                {
                                    // the user was joined to the room, so we can run the standard leaveRoom method.
                                    // this will handle closing the room if this was the only user.
                                    await ((IServerMultiplayerRoomController)this).LeaveRoom(caller.GetUserId(), userUsage.Item, roomUsage, false);
                                }
                                else if (isNewRoom)
                                {
                                    if (room != null)
                                    {
                                        // the room was retrieved and associated to the usage, but something failed before the user (host) could join.
                                        // for now, let's mark the room as ended if this happens.
                                        await endDatabaseMatch(caller.GetUserId(), room);
                                    }

                                    roomUsage.Destroy();
                                }
                            }
                            finally
                            {
                                // no matter how we end up cleaning up the room, ensure the user's state is cleared.
                                userUsage.Item.ClearRoom();
                            }

                            throw;
                        }

                        roomBytes = MessagePackSerializer.Serialize<MultiplayerRoom>(room, MultiplayerHub.MESSAGE_PACK_OPTIONS);
                    }
                }
                catch (KeyNotFoundException)
                {
                    log(caller, "Dropping attempt to join room before the host.", LogLevel.Error);
                    throw new InvalidStateException("Failed to join the room, please try again.");
                }
            }

            try
            {
                // Run in background so we don't hold locks on user/room states.
                _ = sharedInterop.AddUserToRoomAsync(caller.GetUserId(), roomId, password);
            }
            catch
            {
                // Errors are logged internally by SharedInterop.
            }

            await multiplayerEventLogger.LogPlayerJoinedAsync(roomId, caller.GetUserId());

            return MessagePackSerializer.Deserialize<MultiplayerRoom>(roomBytes, MultiplayerHub.MESSAGE_PACK_OPTIONS);
        }

        async Task IMultiplayerUserHubContext.LeaveRoom(HubCallerContext caller)
        {
            log(caller, "Requesting to leave room");
            long roomId;

            using (var userUsage = await getOrCreateUserState(caller))
            {
                Debug.Assert(userUsage.Item != null);

                if (userUsage.Item.CurrentRoomID == null)
                    return;

                roomId = userUsage.Item.CurrentRoomID.Value;
                await leaveRoom(caller, userUsage.Item, false);
            }

            await multiplayerEventLogger.LogPlayerLeftAsync(roomId, caller.GetUserId());
        }

        private async Task leaveRoom(HubCallerContext caller, MultiplayerClientState state, bool wasKick)
        {
            if (state.CurrentRoomID == null)
                return;

            using (var roomUsage = await getUserRoom(state))
                await ((IServerMultiplayerRoomController)this).LeaveRoom(caller.GetUserId(), state, roomUsage, wasKick);
        }

        async Task IMultiplayerUserHubContext.InvitePlayer(HubCallerContext caller, int userId)
        {
            using (var db = databaseFactory.GetInstance())
            {
                bool isRestricted = await db.IsUserRestrictedAsync(userId);
                if (isRestricted)
                    throw new InvalidStateException("Can't invite a restricted user to a room.");

                var relation = await db.GetUserRelation(caller.GetUserId(), userId);

                // The local user has the player they are trying to invite blocked.
                if (relation?.foe == true)
                    throw new UserBlockedException();

                var inverseRelation = await db.GetUserRelation(userId, caller.GetUserId());

                // The player being invited has the local user blocked.
                if (inverseRelation?.foe == true)
                    throw new UserBlockedException();

                // The player being invited disallows unsolicited PMs and the local user is not their friend.
                if (inverseRelation?.friend != true && !await db.GetUserAllowsPMs(userId))
                    throw new UserBlocksPMsException();
            }

            using (var userUsage = await getOrCreateUserState(caller))
            {
                Debug.Assert(userUsage.Item != null);

                using (var roomUsage = await getUserRoom(userUsage.Item))
                {
                    var user = userUsage.Item;
                    var room = roomUsage.Item;

                    if (user == null)
                        throw new InvalidStateException("Local user was not found in the expected room");

                    if (room == null)
                        throw new InvalidOperationException("Attempted to operate on a null room");

                    if (room.Settings.MatchType == MatchType.Matchmaking)
                        throw new InvalidStateException("Can't invite players to matchmaking rooms.");

                    await context.Clients.User(userId.ToString()).SendAsync(nameof(IMultiplayerClient.Invited), user.UserId, room.RoomID, room.Settings.Password);
                }
            }
        }

        async Task IMultiplayerUserHubContext.TransferHost(HubCallerContext caller, int userId)
        {
            long roomId;

            using (var userUsage = await getOrCreateUserState(caller))
            {
                Debug.Assert(userUsage.Item != null);

                using (var roomUsage = await getUserRoom(userUsage.Item))
                {
                    var room = roomUsage.Item;

                    if (room == null)
                        throw new InvalidOperationException("Attempted to operate on a null room");

                    log(room, caller, $"Transferring host from {room.Host?.UserID} to {userId}");
                    roomId = room.RoomID;

                    ensureIsHost(caller, room);

                    var newHost = room.Users.FirstOrDefault(u => u.UserID == userId);

                    if (newHost == null)
                        throw new Exception("Target user is not in the current room");

                    await setNewHost(room, newHost);
                }
            }

            await multiplayerEventLogger.LogHostChangedAsync(roomId, userId);
        }

        async Task IMultiplayerUserHubContext.KickUser(HubCallerContext caller, int userId)
        {
            long roomId;

            using (var userUsage = await getOrCreateUserState(caller))
            {
                Debug.Assert(userUsage.Item != null);

                using (var roomUsage = await getUserRoom(userUsage.Item))
                {
                    var room = roomUsage.Item;

                    if (room == null)
                        throw new InvalidOperationException("Attempted to operate on a null room");

                    log(room, caller, $"Kicking user {userId}");
                    roomId = room.RoomID;

                    if (userId == userUsage.Item?.UserId)
                        throw new InvalidStateException("Can't kick self");

                    ensureIsHost(caller, room);

                    var kickTarget = room.Users.FirstOrDefault(u => u.UserID == userId);

                    if (kickTarget == null)
                        throw new InvalidOperationException("Target user is not in the current room");

                    using (var targetUserUsage = await users.GetForUse(kickTarget.UserID))
                    {
                        Debug.Assert(targetUserUsage.Item != null);

                        if (targetUserUsage.Item.CurrentRoomID == null)
                            throw new InvalidOperationException();

                        await ((IServerMultiplayerRoomController)this).LeaveRoom(caller.GetUserId(), targetUserUsage.Item, roomUsage, true);
                    }
                }
            }

            await multiplayerEventLogger.LogPlayerKickedAsync(roomId, userId);
        }

        async Task IMultiplayerUserHubContext.ChangeState(HubCallerContext caller, MultiplayerUserState newState)
        {
            using (var userUsage = await getOrCreateUserState(caller))
            {
                Debug.Assert(userUsage.Item != null);

                using (var roomUsage = await getUserRoom(userUsage.Item))
                {
                    var room = roomUsage.Item;

                    if (room == null)
                        throw new InvalidOperationException("Attempted to operate on a null room");

                    var user = room.Users.FirstOrDefault(u => u.UserID == caller.GetUserId());

                    if (user == null)
                        throw new InvalidStateException("Local user was not found in the expected room");

                    if (user.State == newState)
                        return;

                    // There's a potential that a client attempts to change state while a message from the server is in transit. Silently block these changes rather than informing the client.
                    switch (newState)
                    {
                        // If a client triggered `Idle` (ie. un-readying) before they received the `WaitingForLoad` message from the match starting.
                        case MultiplayerUserState.Idle:
                            if (IsGameplayState(user.State))
                                return;

                            break;

                        // If a client a triggered gameplay state before they received the `Idle` message from their gameplay being aborted.
                        case MultiplayerUserState.Loaded:
                        case MultiplayerUserState.ReadyForGameplay:
                            if (!IsGameplayState(user.State))
                                return;

                            break;
                    }

                    log(room, caller, $"User changing state from {user.State} to {newState}");

                    ensureValidStateSwitch(room, user.State, newState);

                    await ((IServerMultiplayerRoomController)this).ChangeAndBroadcastUserState(room, user, newState);

                    // Signal newly-spectating users to load gameplay if currently in the middle of play.
                    if (newState == MultiplayerUserState.Spectating
                        && (room.State == MultiplayerRoomState.WaitingForLoad || room.State == MultiplayerRoomState.Playing))
                    {
                        await context.Clients.User(caller.UserIdentifier!).SendAsync(nameof(IMultiplayerClient.LoadRequested));
                    }

                    await ((IServerMultiplayerRoomController)this).UpdateRoomStateIfRequired(room);
                }
            }
        }
        
        /// <summary>
        /// Given a room and a state transition, throw if there's an issue with the sequence of events.
        /// </summary>
        /// <param name="room">The room.</param>
        /// <param name="oldState">The old state.</param>
        /// <param name="newState">The new state.</param>
        private void ensureValidStateSwitch(ServerMultiplayerRoom room, MultiplayerUserState oldState, MultiplayerUserState newState)
        {
            switch (newState)
            {
                case MultiplayerUserState.Idle:
                    if (IsGameplayState(oldState))
                        throw new InvalidStateException("Cannot return to idle without aborting gameplay.");

                    // any non-gameplay state can return to idle.
                    break;

                case MultiplayerUserState.Ready:
                    if (oldState != MultiplayerUserState.Idle)
                        throw new InvalidStateChangeException(oldState, newState);

                    if (room.Controller.CurrentItem.Expired)
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

        async Task IMultiplayerUserHubContext.ChangeBeatmapAvailability(HubCallerContext caller, BeatmapAvailability newBeatmapAvailability)
        {
            using (var userUsage = await getOrCreateUserState(caller))
            {
                Debug.Assert(userUsage.Item != null);

                using (var roomUsage = await getUserRoom(userUsage.Item))
                {
                    var room = roomUsage.Item;

                    if (room == null)
                        throw new InvalidOperationException("Attempted to operate on a null room");

                    var user = room.Users.FirstOrDefault(u => u.UserID == caller.GetUserId());

                    if (user == null)
                        throw new InvalidOperationException("Local user was not found in the expected room");

                    await ((IServerMultiplayerRoomController)this).ChangeAndBroadcastUserBeatmapAvailability(room, user, newBeatmapAvailability);
                }
            }
        }

        async Task IMultiplayerUserHubContext.ChangeUserStyle(HubCallerContext caller, int? beatmapId, int? rulesetId)
        {
            using (var userUsage = await getOrCreateUserState(caller))
            {
                Debug.Assert(userUsage.Item != null);

                using (var roomUsage = await getUserRoom(userUsage.Item))
                {
                    var room = roomUsage.Item;

                    if (room == null)
                        throw new InvalidOperationException("Attempted to operate on a null room");

                    var user = room.Users.FirstOrDefault(u => u.UserID == caller.GetUserId());

                    if (user == null)
                        throw new InvalidOperationException("Local user was not found in the expected room");

                    await ((IServerMultiplayerRoomController)this).ChangeUserStyle(beatmapId, rulesetId, room, user);
                }
            }
        }

        async Task IMultiplayerUserHubContext.ChangeUserMods(HubCallerContext caller, IEnumerable<APIMod> newMods)
        {
            using (var userUsage = await getOrCreateUserState(caller))
            {
                Debug.Assert(userUsage.Item != null);

                using (var roomUsage = await getUserRoom(userUsage.Item))
                {
                    var room = roomUsage.Item;

                    if (room == null)
                        throw new InvalidOperationException("Attempted to operate on a null room");

                    var user = room.Users.FirstOrDefault(u => u.UserID == caller.GetUserId());

                    if (user == null)
                        throw new InvalidOperationException("Local user was not found in the expected room");

                    await ((IServerMultiplayerRoomController)this).ChangeUserMods(newMods, room, user);
                }
            }
        }

        async Task IMultiplayerUserHubContext.SendMatchRequest(HubCallerContext caller, MatchUserRequest request)
        {
            using (var userUsage = await getOrCreateUserState(caller))
            {
                Debug.Assert(userUsage.Item != null);

                using (var roomUsage = await getUserRoom(userUsage.Item))
                {
                    var room = roomUsage.Item;

                    if (room == null)
                        throw new InvalidOperationException("Attempted to operate on a null room");

                    var user = room.Users.FirstOrDefault(u => u.UserID == caller.GetUserId());

                    if (user == null)
                        throw new InvalidOperationException("Local user was not found in the expected room");

                    switch (request)
                    {
                        case StartMatchCountdownRequest startMatchCountdownRequest:
                            await startMatchCountdown(caller, room, startMatchCountdownRequest);
                            break;

                        case StopCountdownRequest stopCountdownRequest:
                            await stopCountdown(caller, room, stopCountdownRequest);
                            break;

                        default:
                            await room.Controller.HandleUserRequest(user, request);
                            break;
                    }
                }
            }
        }

        private async Task startMatchCountdown(HubCallerContext caller, ServerMultiplayerRoom room, StartMatchCountdownRequest request)
        {
            ensureIsHost(caller, room);

            if (room.State != MultiplayerRoomState.Open)
                throw new InvalidStateException("Cannot start a countdown during ongoing play.");

            if (room.Settings.AutoStartEnabled)
                throw new InvalidStateException("Cannot start manual countdown if auto-start is enabled.");

            await room.StartCountdown(new MatchStartCountdown { TimeRemaining = request.Duration }, r => ((IServerMultiplayerRoomController)this).StartMatch(r));
        }

        private async Task stopCountdown(HubCallerContext caller, ServerMultiplayerRoom room, StopCountdownRequest request)
        {
            ensureIsHost(caller, room);

            MultiplayerCountdown? countdown = room.FindCountdownById(request.ID);

            if (countdown == null)
                return;

            switch (countdown)
            {
                case MatchStartCountdown when room.Settings.AutoStartEnabled:
                case ForceGameplayStartCountdown:
                case ServerShuttingDownCountdown:
                    throw new InvalidStateException("Cannot stop the requested countdown.");
            }

            await room.StopCountdown(countdown);
        }

        async Task IMultiplayerUserHubContext.StartMatch(HubCallerContext caller)
        {
            using (var userUsage = await getOrCreateUserState(caller))
            {
                Debug.Assert(userUsage.Item != null);

                using (var roomUsage = await getUserRoom(userUsage.Item))
                {
                    var room = roomUsage.Item;

                    if (room == null)
                        throw new InvalidOperationException("Attempted to operate on a null room");

                    ensureIsHost(caller, room);

                    if (room.Host != null && room.Host.State != MultiplayerUserState.Spectating && room.Host.State != MultiplayerUserState.Ready)
                        throw new InvalidStateException("Can't start match when the host is not ready.");

                    if (room.Users.All(u => u.State != MultiplayerUserState.Ready))
                        throw new InvalidStateException("Can't start match when no users are ready.");

                    await ((IServerMultiplayerRoomController)this).StartMatch(room);
                }
            }
        }

        async Task IMultiplayerUserHubContext.AbortMatch(HubCallerContext caller)
        {
            using (var userUsage = await getOrCreateUserState(caller))
            {
                Debug.Assert(userUsage.Item != null);

                using (var roomUsage = await getUserRoom(userUsage.Item))
                {
                    var room = roomUsage.Item;
                    if (room == null)
                        throw new InvalidOperationException("Attempted to operate on a null room");

                    ensureIsHost(caller, room);

                    if (room.State != MultiplayerRoomState.WaitingForLoad && room.State != MultiplayerRoomState.Playing)
                        throw new InvalidStateException("Cannot abort a match that hasn't started.");

                    foreach (var user in room.Users)
                        await ((IServerMultiplayerRoomController)this).ChangeAndBroadcastUserState(room, user, MultiplayerUserState.Idle);

                    await context.Clients.Group(MultiplayerHub.GetGroupId(room.RoomID)).SendAsync(nameof(IMultiplayerClient.GameplayAborted), GameplayAbortReason.HostAbortedTheMatch);

                    await ((IServerMultiplayerRoomController)this).UpdateRoomStateIfRequired(room);
                }
            }
        }

        async Task IMultiplayerUserHubContext.AbortGameplay(HubCallerContext caller)
        {
            using (var userUsage = await getOrCreateUserState(caller))
            {
                Debug.Assert(userUsage.Item != null);

                using (var roomUsage = await getUserRoom(userUsage.Item))
                {
                    var room = roomUsage.Item;
                    if (room == null)
                        throw new InvalidOperationException("Attempted to operate on a null room");

                    var user = room.Users.FirstOrDefault(u => u.UserID == caller.GetUserId());
                    if (user == null)
                        throw new InvalidOperationException("Local user was not found in the expected room");

                    if (!IsGameplayState(user.State))
                        throw new InvalidStateException("Cannot abort gameplay while not in a gameplay state");

                    await ((IServerMultiplayerRoomController)this).ChangeAndBroadcastUserState(room, user, MultiplayerUserState.Idle);
                    await ((IServerMultiplayerRoomController)this).UpdateRoomStateIfRequired(room);
                }
            }
        }

        async Task IMultiplayerUserHubContext.VoteToSkipIntro(HubCallerContext caller)
        {
            using (var userUsage = await getOrCreateUserState(caller))
            {
                Debug.Assert(userUsage.Item != null);

                using (var roomUsage = await getUserRoom(userUsage.Item))
                {
                    var room = roomUsage.Item;
                    if (room == null)
                        throw new InvalidOperationException("Attempted to operate on a null room");

                    var user = room.Users.FirstOrDefault(u => u.UserID == caller.GetUserId());
                    if (user == null)
                        throw new InvalidOperationException("Local user was not found in the expected room");

                    if (!IsGameplayState(user.State))
                        throw new InvalidStateException("Cannot skip while not in a gameplay state");

                    await ((IServerMultiplayerRoomController)this).ChangeUserVoteToSkipIntro(room, user, true);
                    await ((IServerMultiplayerRoomController)this).CheckVotesToSkipPassed(room);
                }
            }
        }

        async Task IMultiplayerUserHubContext.AddPlaylistItem(HubCallerContext caller, MultiplayerPlaylistItem item)
        {
            using (var userUsage = await getOrCreateUserState(caller))
            {
                Debug.Assert(userUsage.Item != null);

                using (var roomUsage = await getUserRoom(userUsage.Item))
                {
                    var room = roomUsage.Item;
                    if (room == null)
                        throw new InvalidOperationException("Attempted to operate on a null room");

                    var user = room.Users.FirstOrDefault(u => u.UserID == caller.GetUserId());
                    if (user == null)
                        throw new InvalidOperationException("Local user was not found in the expected room");

                    log(room, caller, $"Adding playlist item for beatmap {item.BeatmapID}");
                    await room.Controller.AddPlaylistItem(item, user);

                    await ((IServerMultiplayerRoomController)this).UpdateRoomStateIfRequired(room);
                }
            }
        }

        async Task IMultiplayerUserHubContext.EditPlaylistItem(HubCallerContext caller, MultiplayerPlaylistItem item)
        {
            using (var userUsage = await getOrCreateUserState(caller))
            {
                Debug.Assert(userUsage.Item != null);

                using (var roomUsage = await getUserRoom(userUsage.Item))
                {
                    var room = roomUsage.Item;
                    if (room == null)
                        throw new InvalidOperationException("Attempted to operate on a null room");

                    var user = room.Users.FirstOrDefault(u => u.UserID == caller.GetUserId());
                    if (user == null)
                        throw new InvalidOperationException("Local user was not found in the expected room");

                    log(room, $"Editing playlist item {item.ID} for beatmap {item.BeatmapID}");
                    await room.Controller.EditPlaylistItem(item, user);
                }
            }
        }

        async Task IMultiplayerUserHubContext.RemovePlaylistItem(HubCallerContext caller, long playlistItemId)
        {
            using (var userUsage = await getOrCreateUserState(caller))
            {
                Debug.Assert(userUsage.Item != null);

                using (var roomUsage = await getUserRoom(userUsage.Item))
                {
                    var room = roomUsage.Item;
                    if (room == null)
                        throw new InvalidOperationException("Attempted to operate on a null room");

                    var user = room.Users.FirstOrDefault(u => u.UserID == caller.GetUserId());
                    if (user == null)
                        throw new InvalidOperationException("Local user was not found in the expected room");

                    log(room, $"Removing playlist item {playlistItemId}");
                    await room.Controller.RemovePlaylistItem(playlistItemId, user);

                    await ((IServerMultiplayerRoomController)this).UpdateRoomStateIfRequired(room);
                }
            }
        }

        async Task IMultiplayerUserHubContext.ChangeSettings(HubCallerContext caller, MultiplayerRoomSettings settings)
        {
            using (var userUsage = await getOrCreateUserState(caller))
            {
                Debug.Assert(userUsage.Item != null);

                using (var roomUsage = await getUserRoom(userUsage.Item))
                {
                    var room = roomUsage.Item;

                    if (room == null)
                        throw new InvalidOperationException("Attempted to operate on a null room");

                    if (room.State != MultiplayerRoomState.Open)
                        throw new InvalidStateException("Attempted to change settings while game is active");

                    ensureIsHost(caller, room);

                    log(room, "Settings updating");

                    await changeSettings(settings, room);
                }
            }
        }

        private async Task changeSettings(MultiplayerRoomSettings settings, ServerMultiplayerRoom room)
        {
            settings.Name = await chatFilters.FilterAsync(settings.Name);

            // Server is authoritative over the playlist item ID.
            // Todo: This needs to change for tournament mode.
            settings.PlaylistItemId = room.Settings.PlaylistItemId;

            if (room.Settings.Equals(settings))
                return;

            var previousSettings = room.Settings;

            if (settings.MatchType == MatchType.Playlists)
                throw new InvalidStateException("Invalid match type selected");

            try
            {
                room.Settings = settings;
                await updateDatabaseSettings(room);
            }
            catch
            {
                // rollback settings if an error occurred when updating the database.
                room.Settings = previousSettings;
                throw;
            }

            if (previousSettings.MatchType != settings.MatchType)
            {
                await room.ChangeMatchType(settings.MatchType);
                log(room, $"Switching room ruleset to {room.Controller}");
            }

            await room.Controller.HandleSettingsChanged();
            await ((IServerMultiplayerRoomController)this).NotifySettingsChanged(room, false);

            await ((IServerMultiplayerRoomController)this).UpdateRoomStateIfRequired(room);
        }

        async Task IMultiplayerUserHubContext.ChangeAndBroadcastUserState(HubCallerContext caller, MultiplayerUserState state)
        {
            using (var userUsage = await getOrCreateUserState(caller))
            {
                Debug.Assert(userUsage.Item != null);

                using (var roomUsage = await getUserRoom(userUsage.Item))
                {
                    var room = roomUsage.Item;

                    if (room == null)
                        throw new InvalidOperationException("Attempted to operate on a null room");

                    var user = room.Users.SingleOrDefault(u => u.UserID == userUsage.Item.UserId);

                    if (user == null)
                        throw new InvalidOperationException("User not in room");

                    await ((IServerMultiplayerRoomController)this).ChangeAndBroadcastUserState(room, user, state);
                }
            }
        }

        async Task IMultiplayerUserHubContext.ChangeAndBroadcastUserBeatmapAvailability(HubCallerContext caller, BeatmapAvailability beatmapAvailability)
        {
            using (var userUsage = await getOrCreateUserState(caller))
            {
                Debug.Assert(userUsage.Item != null);

                using (var roomUsage = await getUserRoom(userUsage.Item))
                {
                    var room = roomUsage.Item;

                    if (room == null)
                        throw new InvalidOperationException("Attempted to operate on a null room");

                    var user = room.Users.SingleOrDefault(u => u.UserID == userUsage.Item.UserId);

                    if (user == null)
                        throw new InvalidOperationException("User not in room");

                    await ((IServerMultiplayerRoomController)this).ChangeAndBroadcastUserBeatmapAvailability(room, user, beatmapAvailability);
                }
            }
        }

        #endregion

        #region IMultiplayerRefereeHubContext

        Task IMultiplayerRefereeHubContext.InitialiseUserState(HubCallerContext caller)
            => ((IMultiplayerUserHubContext)this).InitialiseUserState(caller);

        async Task IMultiplayerRefereeHubContext.CleanUpUserState(HubCallerContext caller)
        {
            // todo reconsider
            using (var usage = await getOrCreateUserState(caller))
                usage.Item = null;
        }

        async Task<MultiplayerRoom> IMultiplayerRefereeHubContext.CreateRoom(HubCallerContext caller, MultiplayerRoom room)
        {
            var created = await ((IMultiplayerUserHubContext)this).CreateRoom(caller, room);
            await ensureSpectating(caller);
            return created;
        }

        async Task<long> IMultiplayerRefereeHubContext.CloseRoom(HubCallerContext caller)
        {
            long roomId;

            using (var userUsage = await getOrCreateUserState(caller))
            {
                Debug.Assert(userUsage.Item != null);

                using (var roomUsage = await getUserRoom(userUsage.Item))
                {
                    var room = roomUsage.Item;

                    if (room == null)
                        throw new InvalidOperationException("Attempted to operate on a null room");

                    log(room, caller, "Closing room");
                    roomId = room.RoomID;

                    ensureIsHost(caller, room);

                    foreach (var user in room.Users.Where(u => u.UserID != room.Host?.UserID).ToArray())
                    {
                        using (var targetUserUsage = await users.GetForUse(user.UserID))
                        {
                            Debug.Assert(targetUserUsage.Item != null);

                            if (targetUserUsage.Item.CurrentRoomID == null)
                                throw new InvalidOperationException();

                            await ((IServerMultiplayerRoomController)this).LeaveRoom(caller.GetUserId(), targetUserUsage.Item, roomUsage, true);
                        }
                    }

                    await ((IServerMultiplayerRoomController)this).LeaveRoom(caller.GetUserId(), userUsage.Item, roomUsage, true);
                }
            }

            return roomId;
        }

        Task IMultiplayerRefereeHubContext.InvitePlayer(HubCallerContext caller, int userId)
            => ((IMultiplayerUserHubContext)this).InvitePlayer(caller, userId);

        Task IMultiplayerRefereeHubContext.TransferHost(HubCallerContext caller, int userId)
            => ((IMultiplayerUserHubContext)this).TransferHost(caller, userId);

        Task IMultiplayerRefereeHubContext.KickUser(HubCallerContext caller, int userId)
            => ((IMultiplayerUserHubContext)this).KickUser(caller, userId);

        async Task IMultiplayerRefereeHubContext.StartMatchCountdown(HubCallerContext caller, StartMatchCountdownRequest request)
        {
            using (var userUsage = await getOrCreateUserState(caller))
            {
                Debug.Assert(userUsage.Item != null);

                using (var roomUsage = await getUserRoom(userUsage.Item))
                {
                    var room = roomUsage.Item;

                    if (room == null)
                        throw new InvalidOperationException("Attempted to operate on a null room");

                    await startMatchCountdown(caller, room, request);
                }
            }
        }

        async Task IMultiplayerRefereeHubContext.StopMatchCountdown(HubCallerContext caller)
        {
            using (var userUsage = await getOrCreateUserState(caller))
            {
                Debug.Assert(userUsage.Item != null);

                using (var roomUsage = await getUserRoom(userUsage.Item))
                {
                    var room = roomUsage.Item;

                    if (room == null)
                        throw new InvalidOperationException("Attempted to operate on a null room");

                    foreach (var countdown in room.ActiveCountdowns.OfType<MatchStartCountdown>())
                        await room.StopCountdown(countdown);
                }
            }
        }

        Task IMultiplayerRefereeHubContext.StartMatch(HubCallerContext caller)
            => ((IMultiplayerUserHubContext)this).StartMatch(caller);

        async Task IMultiplayerRefereeHubContext.AbortMatch(HubCallerContext caller)
        {
            await ((IMultiplayerUserHubContext)this).AbortMatch(caller);
            await ensureSpectating(caller);
        }

        async Task IMultiplayerRefereeHubContext.EditCurrentPlaylistItem(HubCallerContext caller, Action<MultiplayerPlaylistItem> changeFunc)
        {
            using (var userUsage = await getOrCreateUserState(caller))
            {
                Debug.Assert(userUsage.Item != null);

                using (var roomUsage = await getUserRoom(userUsage.Item))
                {
                    var room = roomUsage.Item;
                    if (room == null)
                        throw new InvalidOperationException("Attempted to operate on a null room");

                    var user = room.Users.FirstOrDefault(u => u.UserID == caller.GetUserId());
                    if (user == null)
                        throw new InvalidOperationException("Local user was not found in the expected room");

                    var currentItem = room.Controller.CurrentItem.Clone();
                    changeFunc.Invoke(currentItem);

                    log(room, $"Editing playlist item {currentItem.ID} for beatmap {currentItem.BeatmapID}");
                    await room.Controller.EditPlaylistItem(currentItem, user);
                }
            }
        }

        async Task IMultiplayerRefereeHubContext.ChangeSettings(HubCallerContext caller, Action<MultiplayerRoomSettings> changeFunc)
        {
            using (var userUsage = await getOrCreateUserState(caller))
            {
                Debug.Assert(userUsage.Item != null);

                using (var roomUsage = await getUserRoom(userUsage.Item))
                {
                    var room = roomUsage.Item;

                    if (room == null)
                        throw new InvalidOperationException("Attempted to operate on a null room");

                    if (room.State != MultiplayerRoomState.Open)
                        throw new InvalidStateException("Attempted to change settings while game is active");

                    ensureIsHost(caller, room);

                    log(room, "Settings updating");

                    // TODO: this is ultra bad but can't create a copy ctor without changing game...
                    var settings = new MultiplayerRoomSettings
                    {
                        Name = room.Settings.Name,
                        PlaylistItemId = room.Settings.PlaylistItemId,
                        Password = room.Settings.Password,
                        MatchType = room.Settings.MatchType,
                        QueueMode = room.Settings.QueueMode,
                        AutoStartDuration = room.Settings.AutoStartDuration,
                        AutoSkip = room.Settings.AutoSkip,
                    };
                    changeFunc.Invoke(settings);
                    await changeSettings(settings, room);
                }
            }
        }

        private async Task ensureSpectating(HubCallerContext caller)
        {
            await ((IMultiplayerUserHubContext)this).ChangeAndBroadcastUserState(caller, MultiplayerUserState.Spectating);
            await ((IMultiplayerUserHubContext)this).ChangeAndBroadcastUserBeatmapAvailability(caller, BeatmapAvailability.NotDownloaded());
        }

        #endregion

        private async Task<ItemUsage<MultiplayerClientState>> getOrCreateUserState(HubCallerContext hubCallerContext)
        {
            var usage = await users.GetForUse(hubCallerContext.GetUserId(), true);

            if (usage.Item != null && usage.Item.ConnectionId != hubCallerContext.ConnectionId)
            {
                usage.Dispose();
                throw new InvalidOperationException("State is not valid for this connection");
            }

            return usage;
        }

        /// <summary>
        /// Retrieve the <see cref="MultiplayerRoom"/> for the local context user.
        /// </summary>
        private async Task<ItemUsage<ServerMultiplayerRoom>> getUserRoom(MultiplayerClientState state)
        {
            if (state.CurrentRoomID == null)
                throw new NotJoinedRoomException();

            return await rooms.GetForUse(state.CurrentRoomID.Value);
        }

        /// <summary>
        /// Ensure the local user is the host of the room, and throw if they are not.
        /// </summary>
        private void ensureIsHost(HubCallerContext caller, MultiplayerRoom room)
        {
            if (room.Host?.UserID != caller.GetUserId())
                throw new NotHostException();
        }

        public static bool IsGameplayState(MultiplayerUserState state)
        {
            switch (state)
            {
                default:
                    return false;

                case MultiplayerUserState.WaitingForLoad:
                case MultiplayerUserState.Loaded:
                case MultiplayerUserState.ReadyForGameplay:
                case MultiplayerUserState.Playing:
                    return true;
            }
        }

        private async Task setNewHost(MultiplayerRoom room, MultiplayerRoomUser newHost)
        {
            room.Host = newHost;
            await context.Clients.Group(MultiplayerHub.GetGroupId(room.RoomID)).SendAsync(nameof(IMultiplayerClient.HostChanged), newHost.UserID);

            await updateDatabaseHost(room);
        }

        private async Task addDatabaseUser(MultiplayerRoom room, MultiplayerRoomUser user)
        {
            using (var db = databaseFactory.GetInstance())
                await db.AddRoomParticipantAsync(room, user);
        }

        private async Task removeDatabaseUser(MultiplayerRoom room, MultiplayerRoomUser user)
        {
            using (var db = databaseFactory.GetInstance())
                await db.RemoveRoomParticipantAsync(room, user);
        }

        private async Task updateDatabaseHost(MultiplayerRoom room)
        {
            using (var db = databaseFactory.GetInstance())
                await db.UpdateRoomHostAsync(room);
        }

        private async Task endDatabaseMatch(int userId, MultiplayerRoom room)
        {
            using (var db = databaseFactory.GetInstance())
                await db.EndMatchAsync(room);

            await multiplayerEventLogger.LogRoomDisbandedAsync(room.RoomID, userId);
        }

        private async Task updateDatabaseSettings(MultiplayerRoom room)
        {
            var playlistItem = room.Playlist.FirstOrDefault(item => item.ID == room.Settings.PlaylistItemId);

            if (playlistItem == null)
                throw new InvalidStateException("Attempted to select a playlist item not contained by the room.");

            using (var db = databaseFactory.GetInstance())
                await db.UpdateRoomSettingsAsync(room);
        }

        private void log(HubCallerContext ctx, string message, LogLevel logLevel = LogLevel.Information)
        {
            logger.Log(logLevel, "[user:{userId}] {message}",
                ctx.GetUserId(),
                message.Trim());
        }

        private void log(ServerMultiplayerRoom room, string message, LogLevel logLevel = LogLevel.Information)
        {
            logger.Log(logLevel, "[room:{roomID}] {message}",
                room.RoomID,
                message.Trim());
        }

        private void log(ServerMultiplayerRoom room, HubCallerContext caller, string message, LogLevel logLevel = LogLevel.Information)
            => log(room, caller.GetUserId(), message, logLevel);

        private void log(ServerMultiplayerRoom room, int userId, string message, LogLevel logLevel = LogLevel.Information)
        {
            logger.Log(logLevel, "[user:{userId}] [room:{roomID}] {message}",
                userId,
                room.RoomID,
                message.Trim());
        }
        
    }
}
