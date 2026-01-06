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
    public class MultiplayerHubContext : IMultiplayerHubContext
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

        public MultiplayerHubContext(
            IHubContext<MultiplayerHub> context,
            EntityStore<ServerMultiplayerRoom> rooms,
            EntityStore<MultiplayerClientState> users,
            ILoggerFactory loggerFactory,
            IDatabaseFactory databaseFactory,
            MultiplayerEventLogger multiplayerEventLogger,
            ISharedInterop sharedInterop)
        {
            this.context = context;
            this.rooms = rooms;
            this.users = users;
            this.databaseFactory = databaseFactory;
            this.multiplayerEventLogger = multiplayerEventLogger;
            this.sharedInterop = sharedInterop;

            logger = loggerFactory.CreateLogger(nameof(MultiplayerHub).Replace("Hub", string.Empty));
        }

        public Task NotifyNewMatchEvent(ServerMultiplayerRoom room, MatchServerEvent e)
        {
            return context.Clients.Group(MultiplayerHub.GetGroupId(room.RoomID)).SendAsync(nameof(IMultiplayerClient.MatchEvent), e);
        }

        public Task NotifyMatchRoomStateChanged(ServerMultiplayerRoom room)
        {
            return context.Clients.Group(MultiplayerHub.GetGroupId(room.RoomID)).SendAsync(nameof(IMultiplayerClient.MatchRoomStateChanged), room.MatchState);
        }

        public Task NotifyMatchUserStateChanged(ServerMultiplayerRoom room, MultiplayerRoomUser user)
        {
            return context.Clients.Group(MultiplayerHub.GetGroupId(room.RoomID)).SendAsync(nameof(IMultiplayerClient.MatchUserStateChanged), user.UserID, user.MatchState);
        }

        public Task NotifyPlaylistItemAdded(ServerMultiplayerRoom room, MultiplayerPlaylistItem item)
        {
            return context.Clients.Group(MultiplayerHub.GetGroupId(room.RoomID)).SendAsync(nameof(IMultiplayerClient.PlaylistItemAdded), item);
        }

        public Task NotifyPlaylistItemRemoved(ServerMultiplayerRoom room, long playlistItemId)
        {
            return context.Clients.Group(MultiplayerHub.GetGroupId(room.RoomID)).SendAsync(nameof(IMultiplayerClient.PlaylistItemRemoved), playlistItemId);
        }

        public async Task NotifyPlaylistItemChanged(ServerMultiplayerRoom room, MultiplayerPlaylistItem item, bool beatmapChanged)
        {
            if (item.ID == room.Settings.PlaylistItemId)
            {
                await EnsureAllUsersValidStyle(room);
                await UnreadyAllUsers(room, beatmapChanged);
            }

            await context.Clients.Group(MultiplayerHub.GetGroupId(room.RoomID)).SendAsync(nameof(IMultiplayerClient.PlaylistItemChanged), item);
        }

        public async Task NotifySettingsChanged(ServerMultiplayerRoom room, bool playlistItemChanged)
        {
            await EnsureAllUsersValidStyle(room);

            // this should probably only happen for gameplay-related changes, but let's just keep things simple for now.
            await UnreadyAllUsers(room, playlistItemChanged);

            await context.Clients.Group(MultiplayerHub.GetGroupId(room.RoomID)).SendAsync(nameof(IMultiplayerClient.SettingsChanged), room.Settings);
        }

        public Task<ItemUsage<ServerMultiplayerRoom>?> TryGetRoom(long roomId)
        {
            return rooms.TryGetForUse(roomId);
        }

        public async Task<MultiplayerRoom> CreateRoom(HubCallerContext caller, MultiplayerRoom room)
        {
            Log(caller, "Attempting to create room");

            using (var db = databaseFactory.GetInstance())
            {
                if (await db.IsUserRestrictedAsync(caller.GetUserId()))
                    throw new InvalidStateException("Can't join a room when restricted.");
            }

            long roomId = await sharedInterop.CreateRoomAsync(caller.GetUserId(), room);
            await multiplayerEventLogger.LogRoomCreatedAsync(roomId, caller.GetUserId());

            return await joinOrCreateRoom(caller, roomId, room.Settings.Password, true);
        }

        public async Task<MultiplayerRoom> JoinRoomWithPassword(HubCallerContext caller, long roomId, string password)
        {
            Log(caller, $"Attempting to join room {roomId}");

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

            using (var userUsage = await GetOrCreateUserState(caller))
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

                            Log(room, roomUser, "User joined");
                        }
                        catch
                        {
                            try
                            {
                                if (userUsage.Item.CurrentRoomID != null)
                                {
                                    // the user was joined to the room, so we can run the standard leaveRoom method.
                                    // this will handle closing the room if this was the only user.
                                    await leaveRoom(caller, userUsage.Item, roomUsage, false);
                                }
                                else if (isNewRoom)
                                {
                                    if (room != null)
                                    {
                                        // the room was retrieved and associated to the usage, but something failed before the user (host) could join.
                                        // for now, let's mark the room as ended if this happens.
                                        await endDatabaseMatch(caller, room);
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
                    Log(caller, "Dropping attempt to join room before the host.", LogLevel.Error);
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

        public async Task LeaveRoom(HubCallerContext caller)
        {
            Log(caller, "Requesting to leave room");
            long roomId;

            using (var userUsage = await GetOrCreateUserState(caller))
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
                await leaveRoom(caller, state, roomUsage, wasKick);
        }

        private async Task leaveRoom(HubCallerContext caller, MultiplayerClientState state, ItemUsage<ServerMultiplayerRoom> roomUsage, bool wasKick)
        {
            if (state.CurrentRoomID == null)
                return;

            try
            {
                var room = roomUsage.Item;
                if (room == null)
                    throw new InvalidOperationException("Attempted to operate on a null room");

                var user = room.Users.FirstOrDefault(u => u.UserID == state.UserId);

                Log(room, user, wasKick ? "User kicked" : "User left");

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
                    await endDatabaseMatch(caller, room);

                    // only destroy the usage after the database operation succeeds.
                    Log(room, null, "Stopping tracking of room (all users left).");
                    roomUsage.Destroy();
                    return;
                }

                await UpdateRoomStateIfRequired(room);

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

        public async Task InvitePlayer(HubCallerContext caller, int userId)
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

            using (var userUsage = await GetOrCreateUserState(caller))
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

        public async Task KickUser(HubCallerContext caller, int userId)
        {
            long roomId;

            using (var userUsage = await GetOrCreateUserState(caller))
            {
                Debug.Assert(userUsage.Item != null);

                using (var roomUsage = await getUserRoom(userUsage.Item))
                {
                    var room = roomUsage.Item;

                    if (room == null)
                        throw new InvalidOperationException("Attempted to operate on a null room");

                    Log(room, null, $"Kicking user {userId}");
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

                        await leaveRoom(caller, targetUserUsage.Item, roomUsage, true);
                    }
                }
            }

            await multiplayerEventLogger.LogPlayerKickedAsync(roomId, userId);
        }

        public async Task<long> CloseRoom(HubCallerContext caller)
        {
            long roomId;

            using (var userUsage = await GetOrCreateUserState(caller))
            {
                Debug.Assert(userUsage.Item != null);

                using (var roomUsage = await getUserRoom(userUsage.Item))
                {
                    var room = roomUsage.Item;

                    if (room == null)
                        throw new InvalidOperationException("Attempted to operate on a null room");

                    Log(room, null, "Closing room");
                    roomId = room.RoomID;

                    ensureIsHost(caller, room);

                    foreach (var user in room.Users.OrderBy(u => u.UserID != room.Host?.UserID).ToArray())
                    {
                        using (var targetUserUsage = await users.GetForUse(user.UserID))
                        {
                            Debug.Assert(targetUserUsage.Item != null);

                            if (targetUserUsage.Item.CurrentRoomID == null)
                                throw new InvalidOperationException();

                            await leaveRoom(caller, targetUserUsage.Item, roomUsage, true);
                        }
                    }
                }
            }

            await multiplayerEventLogger.LogRoomDisbandedAsync(roomId, caller.GetUserId());
            return roomId;
        }

        public async Task UnreadyAllUsers(ServerMultiplayerRoom room, bool resetBeatmapAvailability)
        {
            Log(room, null, "Unreadying all users");

            foreach (var u in room.Users.Where(u => u.State == MultiplayerUserState.Ready).ToArray())
                await ChangeAndBroadcastUserState(room, u, MultiplayerUserState.Idle);

            if (resetBeatmapAvailability)
            {
                Log(room, null, "Resetting all users' beatmap availability");

                foreach (var user in room.Users)
                    await ChangeAndBroadcastUserBeatmapAvailability(room, user, new BeatmapAvailability(DownloadState.Unknown));
            }

            // Assume some destructive operation took place to warrant unreadying all users, and pre-emptively stop any match start countdown.
            // For example, gameplay-specific changes to the match settings or the current playlist item.
            await room.StopAllCountdowns<MatchStartCountdown>();
        }

        public async Task EnsureAllUsersValidStyle(ServerMultiplayerRoom room)
        {
            if (!room.Controller.CurrentItem.Freestyle)
            {
                // Reset entire style when freestyle is disabled.
                foreach (var user in room.Users)
                    await ChangeUserStyle(null, null, room, user);
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

                    await ChangeUserStyle(userBeatmapId, userRulesetId, room, user);
                }
            }

            foreach (var user in room.Users)
            {
                if (!room.Controller.CurrentItem.ValidateUserMods(user, user.Mods, out var validMods))
                    await ChangeUserMods(validMods, room, user);
            }
        }

        public async Task ChangeUserStyle(int? beatmapId, int? rulesetId, ServerMultiplayerRoom room, MultiplayerRoomUser user)
        {
            if (user.BeatmapId == beatmapId && user.RulesetId == rulesetId)
                return;

            Log(room, user, $"User style changing from (b:{user.BeatmapId}, r:{user.RulesetId}) to (b:{beatmapId}, r:{rulesetId})");

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

        public async Task ChangeUserMods(IEnumerable<APIMod> newMods, ServerMultiplayerRoom room, MultiplayerRoomUser user)
        {
            var newModList = newMods.ToList();

            if (!room.Controller.CurrentItem.ValidateUserMods(user, newModList, out var validMods))
                throw new InvalidStateException($"Incompatible mods were selected: {string.Join(',', newModList.Except(validMods).Select(m => m.Acronym))}");

            if (user.Mods.SequenceEqual(newModList))
                return;

            user.Mods = newModList;

            await context.Clients.Group(MultiplayerHub.GetGroupId(room.RoomID)).SendAsync(nameof(IMultiplayerClient.UserModsChanged), user.UserID, newModList);
        }

        public async Task ChangeAndBroadcastUserState(ServerMultiplayerRoom room, MultiplayerRoomUser user, MultiplayerUserState state)
        {
            Log(room, user, $"User state changed from {user.State} to {state}");

            user.State = state;

            await context.Clients.Group(MultiplayerHub.GetGroupId(room.RoomID)).SendAsync(nameof(IMultiplayerClient.UserStateChanged), user.UserID, user.State);

            await room.Controller.HandleUserStateChanged(user);
        }

        public async Task ChangeAndBroadcastUserBeatmapAvailability(ServerMultiplayerRoom room, MultiplayerRoomUser user, BeatmapAvailability newBeatmapAvailability)
        {
            if (user.BeatmapAvailability.Equals(newBeatmapAvailability))
                return;

            user.BeatmapAvailability = newBeatmapAvailability;

            await context.Clients.Group(MultiplayerHub.GetGroupId(room.RoomID)).SendAsync(nameof(IMultiplayerClient.UserBeatmapAvailabilityChanged), user.UserID, user.BeatmapAvailability);
        }

        public async Task ChangeRoomState(ServerMultiplayerRoom room, MultiplayerRoomState newState)
        {
            Log(room, null, $"Room state changing from {room.State} to {newState}");

            room.State = newState;
            using (var db = databaseFactory.GetInstance())
                await db.UpdateRoomStatusAsync(room);

            await context.Clients.Group(MultiplayerHub.GetGroupId(room.RoomID)).SendAsync(nameof(IMultiplayerClient.RoomStateChanged), newState);
        }

        public async Task ChangeUserVoteToSkipIntro(ServerMultiplayerRoom room, MultiplayerRoomUser user, bool voted)
        {
            if (user.VotedToSkipIntro == voted)
                return;

            Log(room, user, $"Changing user vote to skip intro => {voted}");

            user.VotedToSkipIntro = voted;
            await context.Clients.Group(MultiplayerHub.GetGroupId(room.RoomID)).SendAsync(nameof(IMultiplayerClient.UserVotedToSkipIntro), user.UserID, voted);
        }

        public async Task StartMatch(ServerMultiplayerRoom room)
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
                await ChangeUserVoteToSkipIntro(room, user, false);

            var readyUsers = room.Users.Where(u =>
                u.BeatmapAvailability.State == DownloadState.LocallyAvailable
                && (u.State == MultiplayerUserState.Ready || u.State == MultiplayerUserState.Idle)
            ).ToArray();

            foreach (var u in readyUsers)
                await ChangeAndBroadcastUserState(room, u, MultiplayerUserState.WaitingForLoad);

            await ChangeRoomState(room, MultiplayerRoomState.WaitingForLoad);

            await context.Clients.Group(MultiplayerHub.GetGroupId(room.RoomID)).SendAsync(nameof(IMultiplayerClient.LoadRequested));

            await room.StartCountdown(new ForceGameplayStartCountdown { TimeRemaining = gameplay_load_timeout }, StartOrStopGameplay);

            await multiplayerEventLogger.LogGameStartedAsync(room.RoomID, room.Controller.CurrentItem.ID, room.Controller.GetMatchDetails());
        }

        /// <summary>
        /// Starts gameplay for all users in the <see cref="MultiplayerUserState.Loaded"/> or <see cref="MultiplayerUserState.ReadyForGameplay"/> states,
        /// and aborts gameplay for any others in the <see cref="MultiplayerUserState.WaitingForLoad"/> state.
        /// </summary>
        public async Task StartOrStopGameplay(ServerMultiplayerRoom room)
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
                    await ChangeAndBroadcastUserState(room, user, MultiplayerUserState.Playing);
                    await context.Clients.Client(connectionId).SendAsync(nameof(IMultiplayerClient.GameplayStarted));
                    anyUserPlaying = true;
                }
                else if (user.State == MultiplayerUserState.WaitingForLoad)
                {
                    await ChangeAndBroadcastUserState(room, user, MultiplayerUserState.Idle);
                    await context.Clients.Client(connectionId).SendAsync(nameof(IMultiplayerClient.GameplayAborted), GameplayAbortReason.LoadTookTooLong);
                    Log(room, user, "Gameplay aborted because this user took too long to load.");
                }
            }

            if (anyUserPlaying)
                await ChangeRoomState(room, MultiplayerRoomState.Playing);
            else
            {
                await ChangeRoomState(room, MultiplayerRoomState.Open);
                await multiplayerEventLogger.LogGameAbortedAsync(room.RoomID, room.CurrentPlaylistItem.ID);
                await room.Controller.HandleGameplayCompleted();
            }
        }

        public async Task UpdateRoomStateIfRequired(ServerMultiplayerRoom room)
        {
            //check whether a room state change is required.
            switch (room.State)
            {
                case MultiplayerRoomState.Open:
                    if (room.Settings.AutoStartEnabled)
                    {
                        bool shouldHaveCountdown = !room.Controller.CurrentItem.Expired && room.Users.Any(u => u.State == MultiplayerUserState.Ready);

                        if (shouldHaveCountdown && !room.ActiveCountdowns.Any(c => c is MatchStartCountdown))
                            await room.StartCountdown(new MatchStartCountdown { TimeRemaining = room.Settings.AutoStartDuration }, StartMatch);
                    }

                    break;

                case MultiplayerRoomState.WaitingForLoad:
                    int countGameplayUsers = room.Users.Count(u => MultiplayerHub.IsGameplayState(u.State));
                    int countReadyUsers = room.Users.Count(u => u.State == MultiplayerUserState.ReadyForGameplay);

                    // Attempt to start gameplay when no more users need to change states. If all users have aborted, this will abort the match.
                    if (countReadyUsers == countGameplayUsers)
                        await StartOrStopGameplay(room);

                    break;

                case MultiplayerRoomState.Playing:
                    if (room.Users.All(u => u.State != MultiplayerUserState.Playing))
                    {
                        bool anyUserFinishedPlay = false;

                        foreach (var u in room.Users.Where(u => u.State == MultiplayerUserState.FinishedPlay))
                        {
                            anyUserFinishedPlay = true;
                            await ChangeAndBroadcastUserState(room, u, MultiplayerUserState.Results);
                        }

                        await ChangeRoomState(room, MultiplayerRoomState.Open);
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

        public async Task NotifyMatchmakingItemSelected(ServerMultiplayerRoom room, int userId, long playlistItemId)
        {
            await context.Clients.Group(MultiplayerHub.GetGroupId(room.RoomID)).SendAsync(nameof(IMatchmakingClient.MatchmakingItemSelected), userId, playlistItemId);
        }

        public async Task NotifyMatchmakingItemDeselected(ServerMultiplayerRoom room, int userId, long playlistItemId)
        {
            await context.Clients.Group(MultiplayerHub.GetGroupId(room.RoomID)).SendAsync(nameof(IMatchmakingClient.MatchmakingItemDeselected), userId, playlistItemId);
        }

        public async Task CheckVotesToSkipPassed(ServerMultiplayerRoom room)
        {
            int countVotedUsers = room.Users.Count(u => u.State == MultiplayerUserState.Playing && u.VotedToSkipIntro);
            int countGameplayUsers = room.Users.Count(u => u.State == MultiplayerUserState.Playing);

            if (countVotedUsers >= countGameplayUsers / 2 + 1)
                await context.Clients.Group(MultiplayerHub.GetGroupId(room.RoomID)).SendAsync(nameof(IMultiplayerClient.VoteToSkipIntroPassed));
        }

        protected async Task<ItemUsage<MultiplayerClientState>> GetOrCreateUserState(HubCallerContext hubCallerContext)
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

        private async Task endDatabaseMatch(HubCallerContext caller, MultiplayerRoom room)
        {
            using (var db = databaseFactory.GetInstance())
                await db.EndMatchAsync(room);

            await multiplayerEventLogger.LogRoomDisbandedAsync(room.RoomID, caller.GetUserId());
        }

        protected void Log(HubCallerContext ctx, string message, LogLevel logLevel = LogLevel.Information) => logger.Log(logLevel, "[user:{userId}] {message}",
            ctx.GetUserId(),
            message.Trim());

        public void Log(ServerMultiplayerRoom room, MultiplayerRoomUser? user, string message, LogLevel logLevel = LogLevel.Information)
        {
            logger.Log(logLevel, "[user:{userId}] [room:{roomID}] {message}",
                getLoggableUserIdentifier(user),
                room.RoomID,
                message.Trim());
        }

        public void Error(MultiplayerRoomUser? user, string message, Exception exception)
        {
            logger.LogError(exception, "[user:{userId}] {message}",
                getLoggableUserIdentifier(user),
                message.Trim());
        }

        private string getLoggableUserIdentifier(MultiplayerRoomUser? user)
        {
            return user?.UserID.ToString() ?? "???";
        }
    }
}
