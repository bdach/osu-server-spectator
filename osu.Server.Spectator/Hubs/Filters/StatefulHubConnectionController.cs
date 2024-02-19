// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using osu.Game.Online;
using osu.Server.Spectator.Entities;

namespace osu.Server.Spectator.Hubs.Filters
{
    public abstract class StatefulHubConnectionController : IHubFilter
    {
        private static readonly IEnumerable<Type> stateful_user_hubs
            = typeof(IStatefulUserHub).Assembly.GetTypes().Where(type => typeof(IStatefulUserHub).IsAssignableFrom(type) && typeof(Hub).IsAssignableFrom(type) && !type.IsInterface && !type.IsAbstract).ToArray();

        protected EntityStore<ConnectionState> ConnectionStates { get; private set; }

        private readonly IServiceProvider serviceProvider;

        protected StatefulHubConnectionController(
            EntityStore<ConnectionState> connectionStates,
            IServiceProvider serviceProvider)
        {
            ConnectionStates = connectionStates;

            this.serviceProvider = serviceProvider;
        }

        protected async Task DisconnectClientFromAllStatefulHubsAsync(int userId)
        {
            var userState = ConnectionStates.GetEntityUnsafe(userId);

            if (userState == null)
                return;

            foreach (var hubType in stateful_user_hubs)
            {
                var hubContextType = typeof(IHubContext<>).MakeGenericType(hubType);
                var hubContext = (serviceProvider.GetRequiredService(hubContextType) as IHubContext)!;

                if (userState.ConnectionIds.TryGetValue(hubType, out string? connectionId))
                {
                    await hubContext.Clients.Client(connectionId)
                                    .SendCoreAsync(nameof(IStatefulUserHubClient.DisconnectRequested), Array.Empty<object>());
                }
            }
        }

        public virtual ValueTask<object?> InvokeMethodAsync(
            HubInvocationContext invocationContext,
            Func<HubInvocationContext, ValueTask<object?>> next)
        {
            return next(invocationContext);
        }

        public virtual Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
        {
            return next(context);
        }

        public virtual Task OnDisconnectedAsync(
            HubLifetimeContext context,
            Exception? exception,
            Func<HubLifetimeContext, Exception?, Task> next)
        {
            return next(context, exception);
        }
    }
}
