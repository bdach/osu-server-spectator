// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Primitives;
using Microsoft.Extensions.Logging;
using osu.Game.Online.Metadata;
using osu.Game.Users;
using osu.Server.Spectator.Database;
using osu.Server.Spectator.Entities;
using osu.Server.Spectator.Extensions;
using osu.Server.Spectator.Hubs.Spectator;

namespace osu.Server.Spectator.Hubs.Metadata
{
    public class MetadataHub : StatefulUserHub<IMetadataClient, MetadataClientState>, IMetadataServer
    {
        private readonly IDailyChallengeUpdater dailyChallengeUpdater;
        private readonly IScoreProcessedSubscriber scoreProcessedSubscriber;

        internal const string ONLINE_PRESENCE_WATCHERS_GROUP = "metadata:online-presence-watchers";

        internal static string MultiplayerRoomWatchersGroup(long roomId) => $"metadata:multiplayer-room-watchers:{roomId}";

        public MetadataHub(
            IDatabaseFactory databaseFactory,
            ILoggerFactory loggerFactory,
            IDistributedCache cache,
            EntityStore<MetadataClientState> userStates,
            EntityStore<ConnectionState> connectionStates,
            IDailyChallengeUpdater dailyChallengeUpdater,
            IScoreProcessedSubscriber scoreProcessedSubscriber)
            : base(databaseFactory, loggerFactory, cache, userStates, connectionStates)
        {
            this.dailyChallengeUpdater = dailyChallengeUpdater;
            this.scoreProcessedSubscriber = scoreProcessedSubscriber;
        }

        public override async Task OnConnectedAsync()
        {
            await base.OnConnectedAsync();

            using (var usage = await GetOrCreateLocalUserState())
            {
                string? versionHash = null;

                if (Context.GetHttpContext()?.Request.Headers.TryGetValue("OsuVersionHash", out StringValues headerValue) == true)
                {
                    versionHash = headerValue;

                    // The token is 82 chars long, and the clientHash is the first 32 of those.
                    // See: https://github.com/ppy/osu-web/blob/7be19a0fe0c9fa2f686e4bb686dbc8e9bf7bcf84/app/Libraries/ClientCheck.php#L92
                    if (versionHash?.Length >= 82)
                        versionHash = versionHash.Substring(versionHash.Length - 82, 32);
                }

                usage.Item = new MetadataClientState(Context.ConnectionId, Context.GetUserId(), versionHash);
                await broadcastUserPresenceUpdate(usage.Item.UserId, usage.Item.ToUserPresence());
                await Clients.Caller.DailyChallengeUpdated(dailyChallengeUpdater.Current);
            }
        }

        public async Task<BeatmapUpdates> GetChangesSince(int queueId)
        {
            using (var db = DatabaseFactory.GetInstance())
                return await db.GetUpdatedBeatmapSets(queueId);
        }

        public async Task BeginWatchingUserPresence()
        {
            foreach (var userState in GetAllStates())
            {
                if (userState.Value.UserStatus != UserStatus.Offline)
                    await Clients.Caller.UserPresenceUpdated(userState.Value.UserId, userState.Value.ToUserPresence());
            }

            await Groups.AddToGroupAsync(Context.ConnectionId, ONLINE_PRESENCE_WATCHERS_GROUP);
        }

        public Task EndWatchingUserPresence()
            => Groups.RemoveFromGroupAsync(Context.ConnectionId, ONLINE_PRESENCE_WATCHERS_GROUP);

        public async Task UpdateActivity(UserActivity? activity)
        {
            using (var usage = await GetOrCreateLocalUserState())
            {
                Debug.Assert(usage.Item != null);
                usage.Item.UserActivity = activity;

                await broadcastUserPresenceUpdate(usage.Item.UserId, usage.Item.ToUserPresence());
            }
        }

        public async Task UpdateStatus(UserStatus? status)
        {
            using (var usage = await GetOrCreateLocalUserState())
            {
                Debug.Assert(usage.Item != null);
                usage.Item.UserStatus = status;

                await broadcastUserPresenceUpdate(usage.Item.UserId, usage.Item.ToUserPresence());
            }
        }

        public async Task<MultiplayerPlaylistItemStats[]> BeginWatchingMultiplayerRoom(long id)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, MultiplayerRoomWatchersGroup(id));
            await scoreProcessedSubscriber.RegisterForMultiplayerRoomAsync(Context.GetUserId(), id);

            using var db = DatabaseFactory.GetInstance();
            return await db.GetMultiplayerRoomStatsAsync(id);
        }

        public async Task EndWatchingMultiplayerRoom(long id)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, MultiplayerRoomWatchersGroup(id));
            await scoreProcessedSubscriber.UnregisterFromMultiplayerRoomAsync(Context.GetUserId(), id);
        }

        protected override async Task CleanUpState(MetadataClientState state)
        {
            await base.CleanUpState(state);
            await broadcastUserPresenceUpdate(state.UserId, null);
            await scoreProcessedSubscriber.UnregisterFromAllMultiplayerRoomsAsync(state.UserId);
        }

        private Task broadcastUserPresenceUpdate(int userId, UserPresence? userPresence)
        {
            if (userPresence?.Status == UserStatus.Offline)
                userPresence = null;

            return Clients.Group(ONLINE_PRESENCE_WATCHERS_GROUP).UserPresenceUpdated(userId, userPresence);
        }
    }
}
