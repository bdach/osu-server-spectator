// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Threading.Tasks;
using MessagePack;
using Microsoft.Extensions.Logging;
using osu.Game.Online;
using osu.Game.Online.API;
using osu.Game.Online.Multiplayer;
using osu.Game.Online.Rooms;
using osu.Server.Spectator.Database;
using osu.Server.Spectator.Entities;
using osu.Server.Spectator.Hubs.Multiplayer.Matchmaking.Queue;

namespace osu.Server.Spectator.Hubs.Multiplayer
{
    public partial class MultiplayerHub : StatefulUserHub<IMultiplayerClient, MultiplayerClientState>, IMultiplayerServer
    {
        public const string STATSD_PREFIX = "multiplayer";

        public static readonly MessagePackSerializerOptions MESSAGE_PACK_OPTIONS = new MessagePackSerializerOptions(new SignalRUnionWorkaroundResolver());

        protected readonly EntityStore<ServerMultiplayerRoom> Rooms;
        protected readonly IMultiplayerUserHubContext HubContext;
        private readonly IDatabaseFactory databaseFactory;
        private readonly IMatchmakingQueueBackgroundService matchmakingQueueService;

        public MultiplayerHub(
            ILoggerFactory loggerFactory,
            EntityStore<ServerMultiplayerRoom> rooms,
            EntityStore<MultiplayerClientState> users,
            IDatabaseFactory databaseFactory,
            IMultiplayerUserHubContext hubContext,
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
            => await HubContext.ChangeState(Context, newState);

        public async Task ChangeBeatmapAvailability(BeatmapAvailability newBeatmapAvailability)
            => await HubContext.ChangeBeatmapAvailability(Context, newBeatmapAvailability);

        public async Task ChangeUserStyle(int? beatmapId, int? rulesetId)
            => await HubContext.ChangeUserStyle(Context, beatmapId, rulesetId);

        public async Task ChangeUserMods(IEnumerable<APIMod> newMods)
            => await HubContext.ChangeUserMods(Context, newMods);

        public async Task SendMatchRequest(MatchUserRequest request)
            => await HubContext.SendMatchRequest(Context, request);

        public async Task StartMatch()
            => await HubContext.StartMatch(Context);

        public async Task AbortMatch()
            => await HubContext.AbortMatch(Context);

        public async Task AbortGameplay()
            => await HubContext.AbortGameplay(Context);

        public async Task VoteToSkipIntro()
            => await HubContext.VoteToSkipIntro(Context);

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
            await HubContext.CleanUpUserState(state);
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
    }
}
