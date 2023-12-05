// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Distributed;
using osu.Game.Online.Metadata;
using osu.Game.Users;
using osu.Server.Spectator.Database;
using osu.Server.Spectator.Entities;
using osu.Server.Spectator.Extensions;

namespace osu.Server.Spectator.Hubs.Metadata
{
    public class MetadataHub : StatefulUserHub<IMetadataClient, MetadataClientState>, IMetadataServer
    {
        private readonly IDatabaseFactory databaseFactory;

        public MetadataHub(
            IDistributedCache cache,
            EntityStore<MetadataClientState> userStates,
            IDatabaseFactory databaseFactory)
            : base(cache, userStates)
        {
            this.databaseFactory = databaseFactory;
        }

        public override async Task OnConnectedAsync()
        {
            await base.OnConnectedAsync();

            foreach (var userState in GetAllStates())
                await Clients.Caller.UserPresenceUpdated(userState.Value.UserId, userState.Value.ToUserPresence());

            using (var usage = await GetOrCreateLocalUserState())
            {
                usage.Item = new MetadataClientState(Context.ConnectionId, Context.GetUserId());
                await Clients.Others.UserPresenceUpdated(usage.Item.UserId, usage.Item.ToUserPresence());
            }
        }

        public async Task<BeatmapUpdates> GetChangesSince(int queueId)
        {
            using (var db = databaseFactory.GetInstance())
                return await db.GetUpdatedBeatmapSets(queueId);
        }

        public async Task UpdateActivity(UserActivity? activity)
        {
            using (var usage = await GetOrCreateLocalUserState())
            {
                Debug.Assert(usage.Item != null);
                usage.Item.UserActivity = activity;

                await Clients.Others.UserPresenceUpdated(Context.GetUserId(), usage.Item.ToUserPresence());
            }
        }

        public async Task UpdateStatus(UserStatus? status)
        {
            using (var usage = await GetOrCreateLocalUserState())
            {
                Debug.Assert(usage.Item != null);
                usage.Item.UserStatus = status;

                await Clients.Others.UserPresenceUpdated(Context.GetUserId(), usage.Item.ToUserPresence());
            }
        }

        protected override async Task CleanUpState(MetadataClientState state)
        {
            await base.CleanUpState(state);
            await Clients.AllExcept(new[] { state.ConnectionId }).UserPresenceUpdated(Context.GetUserId(), null);
        }
    }
}
