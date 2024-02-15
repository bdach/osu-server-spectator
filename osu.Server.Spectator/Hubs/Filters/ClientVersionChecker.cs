// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Memory;
using osu.Framework.Extensions.TypeExtensions;
using osu.Server.Spectator.Database;
using osu.Server.Spectator.Database.Models;
using osu.Server.Spectator.Entities;
using osu.Server.Spectator.Extensions;
using osu.Server.Spectator.Hubs.Metadata;

namespace osu.Server.Spectator.Hubs.Filters
{
    public class ClientVersionChecker : IHubFilter
    {
        private static readonly HashSet<Type> hubs_to_check;

        private readonly EntityStore<MetadataClientState> metadataStore;
        private readonly IDatabaseFactory databaseFactory;
        private readonly IMemoryCache memoryCache;

        public ClientVersionChecker(
            EntityStore<MetadataClientState> metadataStore,
            IDatabaseFactory databaseFactory,
            IMemoryCache memoryCache)
        {
            this.metadataStore = metadataStore;
            this.databaseFactory = databaseFactory;
            this.memoryCache = memoryCache;
        }

        static ClientVersionChecker()
        {
            hubs_to_check = Assembly.GetAssembly(typeof(ClientVersionChecker))!
                                    .GetTypes()
                                    .Where(t => t.EnumerateBaseTypes().Contains(typeof(Hub))
                                                && t.GetCustomAttribute<CheckClientVersionAttribute>() != null)
                                    .ToHashSet();
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

            if (!hubs_to_check.Contains(hub.GetType()))
                return;

            var clientMetadata = metadataStore.GetEntityUnsafe(hubCallerContext.GetUserId());
            if (string.IsNullOrEmpty(clientMetadata?.VersionHash))
                throw new InvalidVersionException();

            var build = await getBuildByHash(clientMetadata.VersionHash);
            if (build == null || !build.allow_bancho)
                throw new InvalidVersionException();
        }

        private Task<osu_build?> getBuildByHash(string hash) => memoryCache.GetOrCreateAsync<osu_build?>(hash, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
            using (var db = databaseFactory.GetInstance())
                return await db.GetLazerBuildByHashAsync(hash);
        });

        public class InvalidVersionException : HubException
        {
            public InvalidVersionException()
                : base("You cannot play online on this version of osu!. Please ensure that you are using the latest version of the official game releases.")
            {
            }
        }
    }
}
