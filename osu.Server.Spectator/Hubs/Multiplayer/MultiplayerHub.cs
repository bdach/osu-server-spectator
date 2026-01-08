// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using MessagePack;
using Microsoft.Extensions.Logging;
using osu.Game.Online;
using osu.Game.Online.API;
using osu.Game.Online.Multiplayer;
using osu.Game.Online.Multiplayer.Countdown;
using osu.Game.Online.Rooms;
using osu.Server.Spectator.Database;
using osu.Server.Spectator.Entities;
using osu.Server.Spectator.Extensions;
using osu.Server.Spectator.Hubs.Multiplayer.Matchmaking.Queue;

namespace osu.Server.Spectator.Hubs.Multiplayer
{
    public partial class MultiplayerHub : StatefulUserHub<IMultiplayerClient, MultiplayerClientState>, IMultiplayerServer
    {
        public const string STATSD_PREFIX = "multiplayer";

        public static readonly MessagePackSerializerOptions MESSAGE_PACK_OPTIONS = new MessagePackSerializerOptions(new SignalRUnionWorkaroundResolver());

        protected readonly EntityStore<ServerMultiplayerRoom> Rooms;
        protected readonly IMultiplayerHubContext HubContext;
        private readonly IDatabaseFactory databaseFactory;
        private readonly IMatchmakingQueueBackgroundService matchmakingQueueService;

        public MultiplayerHub(
            ILoggerFactory loggerFactory,
            EntityStore<ServerMultiplayerRoom> rooms,
            EntityStore<MultiplayerClientState> users,
            IDatabaseFactory databaseFactory,
            IMultiplayerHubContext hubContext,
            IMatchmakingQueueBackgroundService matchmakingQueueService)
            : base(loggerFactory, users)
        {
            this.databaseFactory = databaseFactory;
            this.matchmakingQueueService = matchmakingQueueService;

            Rooms = rooms;
            HubContext = hubContext;
        }

        public override async Task OnConnectedAsync()
        {
            await base.OnConnectedAsync();
            await HubContext.InitialiseUserState(Context);
        }

        public async Task<MultiplayerRoom> CreateRoom(MultiplayerRoom room)
            => await HubContext.CreateRoom(Context, room);

        public async Task<MultiplayerRoom> JoinRoom(long roomId)
            => await HubContext.JoinRoomWithPassword(Context, roomId, string.Empty);

        public async Task<MultiplayerRoom> JoinRoomWithPassword(long roomId, string password)
            => await HubContext.JoinRoomWithPassword(Context, roomId, password);

        public async Task LeaveRoom()
            => await HubContext.LeaveRoom(Context);

        public async Task InvitePlayer(int userId)
            => await HubContext.InvitePlayer(Context, userId);

        public async Task TransferHost(int userId)
            => await HubContext.TransferHost(Context, userId);

        public async Task KickUser(int userId)
            => await HubContext.KickUser(Context, userId);

        public async Task ChangeState(MultiplayerUserState newState)
        {
            using (var userUsage = await GetOrCreateLocalUserState())
            {
                Debug.Assert(userUsage.Item != null);

                using (var roomUsage = await getLocalUserRoom(userUsage.Item))
                {
                    var room = roomUsage.Item;

                    if (room == null)
                        throw new InvalidOperationException("Attempted to operate on a null room");

                    var user = room.Users.FirstOrDefault(u => u.UserID == Context.GetUserId());

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

                    Log(room, $"User changing state from {user.State} to {newState}");

                    ensureValidStateSwitch(room, user.State, newState);

                    await HubContext.ChangeAndBroadcastUserState(room, user, newState);

                    // Signal newly-spectating users to load gameplay if currently in the middle of play.
                    if (newState == MultiplayerUserState.Spectating
                        && (room.State == MultiplayerRoomState.WaitingForLoad || room.State == MultiplayerRoomState.Playing))
                    {
                        await Clients.Caller.LoadRequested();
                    }

                    await HubContext.UpdateRoomStateIfRequired(room);
                }
            }
        }

        public async Task ChangeBeatmapAvailability(BeatmapAvailability newBeatmapAvailability)
        {
            using (var userUsage = await GetOrCreateLocalUserState())
            {
                Debug.Assert(userUsage.Item != null);

                using (var roomUsage = await getLocalUserRoom(userUsage.Item))
                {
                    var room = roomUsage.Item;

                    if (room == null)
                        throw new InvalidOperationException("Attempted to operate on a null room");

                    var user = room.Users.FirstOrDefault(u => u.UserID == Context.GetUserId());

                    if (user == null)
                        throw new InvalidOperationException("Local user was not found in the expected room");

                    await HubContext.ChangeAndBroadcastUserBeatmapAvailability(room, user, newBeatmapAvailability);
                }
            }
        }

        public async Task ChangeUserStyle(int? beatmapId, int? rulesetId)
        {
            using (var userUsage = await GetOrCreateLocalUserState())
            {
                Debug.Assert(userUsage.Item != null);

                using (var roomUsage = await getLocalUserRoom(userUsage.Item))
                {
                    var room = roomUsage.Item;

                    if (room == null)
                        throw new InvalidOperationException("Attempted to operate on a null room");

                    var user = room.Users.FirstOrDefault(u => u.UserID == Context.GetUserId());

                    if (user == null)
                        throw new InvalidOperationException("Local user was not found in the expected room");

                    await HubContext.ChangeUserStyle(beatmapId, rulesetId, room, user);
                }
            }
        }

        public async Task ChangeUserMods(IEnumerable<APIMod> newMods)
        {
            using (var userUsage = await GetOrCreateLocalUserState())
            {
                Debug.Assert(userUsage.Item != null);

                using (var roomUsage = await getLocalUserRoom(userUsage.Item))
                {
                    var room = roomUsage.Item;

                    if (room == null)
                        throw new InvalidOperationException("Attempted to operate on a null room");

                    var user = room.Users.FirstOrDefault(u => u.UserID == Context.GetUserId());

                    if (user == null)
                        throw new InvalidOperationException("Local user was not found in the expected room");

                    await HubContext.ChangeUserMods(newMods, room, user);
                }
            }
        }

        public async Task SendMatchRequest(MatchUserRequest request)
        {
            using (var userUsage = await GetOrCreateLocalUserState())
            {
                Debug.Assert(userUsage.Item != null);

                using (var roomUsage = await getLocalUserRoom(userUsage.Item))
                {
                    var room = roomUsage.Item;

                    if (room == null)
                        throw new InvalidOperationException("Attempted to operate on a null room");

                    var user = room.Users.FirstOrDefault(u => u.UserID == Context.GetUserId());

                    if (user == null)
                        throw new InvalidOperationException("Local user was not found in the expected room");

                    switch (request)
                    {
                        case StartMatchCountdownRequest startMatchCountdownRequest:
                            await HubContext.StartMatchCountdown(Context, room, startMatchCountdownRequest);
                            break;

                        case StopCountdownRequest stopCountdownRequest:
                            await HubContext.StopCountdown(Context, room, stopCountdownRequest);
                            break;

                        default:
                            await room.Controller.HandleUserRequest(user, request);
                            break;
                    }
                }
            }
        }

        public async Task StartMatch()
            => await HubContext.StartMatch(Context);

        public async Task AbortMatch()
            => await HubContext.AbortMatch(Context);

        public async Task AbortGameplay()
        {
            using (var userUsage = await GetOrCreateLocalUserState())
            {
                Debug.Assert(userUsage.Item != null);

                using (var roomUsage = await getLocalUserRoom(userUsage.Item))
                {
                    var room = roomUsage.Item;
                    if (room == null)
                        throw new InvalidOperationException("Attempted to operate on a null room");

                    var user = room.Users.FirstOrDefault(u => u.UserID == Context.GetUserId());
                    if (user == null)
                        throw new InvalidOperationException("Local user was not found in the expected room");

                    if (!IsGameplayState(user.State))
                        throw new InvalidStateException("Cannot abort gameplay while not in a gameplay state");

                    await HubContext.ChangeAndBroadcastUserState(room, user, MultiplayerUserState.Idle);
                    await HubContext.UpdateRoomStateIfRequired(room);
                }
            }
        }

        public async Task VoteToSkipIntro()
        {
            using (var userUsage = await GetOrCreateLocalUserState())
            {
                Debug.Assert(userUsage.Item != null);

                using (var roomUsage = await getLocalUserRoom(userUsage.Item))
                {
                    var room = roomUsage.Item;
                    if (room == null)
                        throw new InvalidOperationException("Attempted to operate on a null room");

                    var user = room.Users.FirstOrDefault(u => u.UserID == Context.GetUserId());
                    if (user == null)
                        throw new InvalidOperationException("Local user was not found in the expected room");

                    if (!IsGameplayState(user.State))
                        throw new InvalidStateException("Cannot skip while not in a gameplay state");

                    await HubContext.ChangeUserVoteToSkipIntro(room, user, true);
                    await HubContext.CheckVotesToSkipPassed(room);
                }
            }
        }

        public async Task AddPlaylistItem(MultiplayerPlaylistItem item)
            => await HubContext.AddPlaylistItem(Context, item);

        public async Task EditPlaylistItem(MultiplayerPlaylistItem item)
            => await HubContext.EditPlaylistItem(Context, item);

        public async Task RemovePlaylistItem(long playlistItemId)
            => await HubContext.RemovePlaylistItem(Context, playlistItemId);

        public async Task ChangeSettings(MultiplayerRoomSettings settings)
            => await HubContext.ChangeSettings(Context, settings);

        /// <summary>
        /// Get the group ID to be used for multiplayer messaging.
        /// </summary>
        /// <param name="roomId">The databased room ID.</param>
        public static string GetGroupId(long roomId) => $"room:{roomId}";

        protected override async Task CleanUpState(MultiplayerClientState state)
        {
            await base.CleanUpState(state);
            await matchmakingQueueService.RemoveFromQueueAsync(state);

            if (state.CurrentRoomID != null)
            {
                using (var roomUsage = await getLocalUserRoom(state))
                    await HubContext.LeaveRoom(Context, state, roomUsage, false);
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

        /// <summary>
        /// Ensure the local user is the host of the room, and throw if they are not.
        /// </summary>
        private void ensureIsHost(MultiplayerRoom room)
        {
            if (room.Host?.UserID != Context.GetUserId())
                throw new NotHostException();
        }

        /// <summary>
        /// Retrieve the <see cref="MultiplayerRoom"/> for the local context user.
        /// </summary>
        private async Task<ItemUsage<ServerMultiplayerRoom>> getLocalUserRoom(MultiplayerClientState state)
        {
            if (state.CurrentRoomID == null)
                throw new NotJoinedRoomException();

            return await Rooms.GetForUse(state.CurrentRoomID.Value);
        }

        internal Task<ItemUsage<ServerMultiplayerRoom>> GetRoom(long roomId) => Rooms.GetForUse(roomId);

        protected void Log(ServerMultiplayerRoom room, string message, LogLevel logLevel = LogLevel.Information) => base.Log($"[room:{room.RoomID}] {message}", logLevel);
    }
}
