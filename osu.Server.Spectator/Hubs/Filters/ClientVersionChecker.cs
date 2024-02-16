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
    public class ClientVersionChecker : IHubFilter
    {
        private static readonly ConcurrentDictionary<Type, bool> hub_requires_version_check = new ConcurrentDictionary<Type, bool>();

        private readonly EntityStore<MetadataClientState> metadataStore;
        private readonly IDatabaseFactory databaseFactory;
        private readonly IDistributedCache distributedCache;

        public ClientVersionChecker(
            EntityStore<MetadataClientState> metadataStore,
            IDatabaseFactory databaseFactory,
            IDistributedCache distributedCache)
        {
            this.metadataStore = metadataStore;
            this.databaseFactory = databaseFactory;
            this.distributedCache = distributedCache;
        }

        public async Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
        {
            await checkVersion(context.Hub, context.Context);
            await next(context);
        }

        public async ValueTask<object?> InvokeMethodAsync(HubInvocationContext invocationContext, Func<HubInvocationContext, ValueTask<object?>> next)
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

            var clientMetadata = metadataStore.GetEntityUnsafe(hubCallerContext.GetUserId());
            if (string.IsNullOrEmpty(clientMetadata?.VersionHash))
                throw new InvalidVersionException();

            var build = await getBuildByHash(clientMetadata.VersionHash);
            if (build == null || !build.allow_bancho)
                throw new InvalidVersionException();
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

        public class InvalidVersionException : HubException
        {
            public InvalidVersionException()
                : base("You cannot play online on this version of osu!. Please ensure that you are using the latest version of the official game releases.")
            {
            }
        }
    }
}
