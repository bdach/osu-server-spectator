// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Distributed;
using Newtonsoft.Json;
using osu.Server.Spectator.Database;
using osu.Server.Spectator.Database.Models;
using osu.Server.Spectator.Entities;
using osu.Server.Spectator.Extensions;
using osu.Server.Spectator.Hubs.Metadata;

namespace osu.Server.Spectator.Hubs.Filters
{
    public class ClientVersionChecker : StatefulHubConnectionController
    {
        private static readonly ConcurrentDictionary<Type, bool> hub_requires_version_check = new ConcurrentDictionary<Type, bool>();

        private readonly EntityStore<MetadataClientState> metadataStore;
        private readonly IDatabaseFactory databaseFactory;
        private readonly IDistributedCache distributedCache;

        public ClientVersionChecker(
            EntityStore<ConnectionState> connectionStates,
            IServiceProvider serviceProvider,
            EntityStore<MetadataClientState> metadataStore,
            IDatabaseFactory databaseFactory,
            IDistributedCache distributedCache)
            : base(connectionStates, serviceProvider)
        {
            this.metadataStore = metadataStore;
            this.databaseFactory = databaseFactory;
            this.distributedCache = distributedCache;
        }

        public override async Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
        {
            await checkVersion(context.Hub, context.Context);
            await next(context);
        }

        public override async ValueTask<object?> InvokeMethodAsync(HubInvocationContext invocationContext, Func<HubInvocationContext, ValueTask<object?>> next)
        {
            await checkVersion(invocationContext.Hub, invocationContext.Context);
            return await next(invocationContext);
        }

        private async Task checkVersion(Hub hub, HubCallerContext hubCallerContext)
        {
            if (!AppSettings.CheckClientVersion)
                return;

            if (!shouldCheckVersionFor(hub))
                return;

            int userId = hubCallerContext.GetUserId();
            var clientMetadata = metadataStore.GetEntityUnsafe(userId);

            if (string.IsNullOrEmpty(clientMetadata?.VersionHash))
            {
                await DisconnectClientFromAllStatefulHubsAsync(userId);
                return;
            }

            var build = await getBuildByHash(clientMetadata.VersionHash);

            if (build == null || !build.allow_bancho)
            {
                await DisconnectClientFromAllStatefulHubsAsync(userId);
                return;
            }
        }

        private static bool shouldCheckVersionFor(Hub hub)
            => hub_requires_version_check.GetOrAdd(hub.GetType(), h => h.GetCustomAttribute<CheckClientVersionAttribute>() != null);

        private async Task<osu_build?> getBuildByHash(string hash)
        {
            string cacheKey = $"build:{hash}";
            string? buildJson = await distributedCache.GetStringAsync(cacheKey);

            if (!string.IsNullOrEmpty(buildJson))
                return JsonConvert.DeserializeObject<osu_build>(buildJson);

            using var db = databaseFactory.GetInstance();
            var build = await db.GetLazerBuildByHashAsync(hash);
            await distributedCache.SetStringAsync(cacheKey, JsonConvert.SerializeObject(build), new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5)
            });
            return build;
        }
    }
}
