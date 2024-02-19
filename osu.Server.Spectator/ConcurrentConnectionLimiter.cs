// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using osu.Framework.Extensions.TypeExtensions;
using osu.Framework.Logging;
using osu.Server.Spectator.Entities;
using osu.Server.Spectator.Extensions;
using osu.Server.Spectator.Hubs.Filters;

namespace osu.Server.Spectator
{
    public class ConcurrentConnectionLimiter : StatefulHubConnectionController
    {
        public ConcurrentConnectionLimiter(
            EntityStore<ConnectionState> connectionStates,
            IServiceProvider serviceProvider)
            : base(connectionStates, serviceProvider)
        {
        }

        public override async Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
        {
            await registerConnection(context);
            await next(context);
        }

        private async Task registerConnection(HubLifetimeContext context)
        {
            int userId = context.Context.GetUserId();

            using (var userState = await ConnectionStates.GetForUse(userId, true))
            {
                if (userState.Item == null)
                {
                    log(context, "connection from first client instance");
                    userState.Item = new ConnectionState(context);
                    return;
                }

                if (context.Context.GetTokenId() == userState.Item.TokenId)
                {
                    // The assumption is that the client has already dropped the old connection,
                    // so we don't bother to ask for a disconnection.

                    log(context, "subsequent connection from same client instance, registering");
                    // Importantly, this will replace the old connection, ensuring it cannot be
                    // used to communicate on anymore.
                    userState.Item.RegisterConnectionId(context);
                    return;
                }

                log(context, "connection from new client instance, dropping existing state");

                await DisconnectClientFromAllStatefulHubsAsync(userId);

                log(context, "existing state dropped");
                userState.Item = new ConnectionState(context);
            }
        }

        private static void log(HubLifetimeContext context, string message)
            => Logger.Log($"[user:{context.Context.GetUserId()}] [connection:{context.Context.ConnectionId}] [hub:{context.Hub.GetType().ReadableName()}] {message}");

        public override async ValueTask<object?> InvokeMethodAsync(HubInvocationContext invocationContext, Func<HubInvocationContext, ValueTask<object?>> next)
        {
            int userId = invocationContext.Context.GetUserId();

            using (var userState = await ConnectionStates.GetForUse(userId))
            {
                string? registeredConnectionId = null;

                bool tokenIdMatches = invocationContext.Context.GetTokenId() == userState.Item?.TokenId;
                bool hubRegistered = userState.Item?.ConnectionIds.TryGetValue(invocationContext.Hub.GetType(), out registeredConnectionId) == true;
                bool connectionIdMatches = registeredConnectionId == invocationContext.Context.ConnectionId;

                bool connectionIsValid = tokenIdMatches && hubRegistered && connectionIdMatches;

                if (!connectionIsValid)
                    throw new InvalidOperationException($"State is not valid for this connection, context: {LoggingHubFilter.GetMethodCallDisplayString(invocationContext)})");
            }

            return await next(invocationContext);
        }

        public override async Task OnDisconnectedAsync(HubLifetimeContext context, Exception? exception, Func<HubLifetimeContext, Exception?, Task> next)
        {
            // if `exception` isn't null then the disconnection is not clean,
            // so don't unregister yet in hopes that the user will return after a transient network failure or similar.
            if (exception == null)
                await unregisterConnection(context, exception);
            await next(context, exception);
        }

        private async Task unregisterConnection(HubLifetimeContext context, Exception? exception)
        {
            int userId = context.Context.GetUserId();

            using (var userState = await ConnectionStates.GetForUse(userId, true))
            {
                string? registeredConnectionId = null;

                bool tokenIdMatches = context.Context.GetTokenId() == userState.Item?.TokenId;
                bool hubRegistered = userState.Item?.ConnectionIds.TryGetValue(context.Hub.GetType(), out registeredConnectionId) == true;
                bool connectionIdMatches = registeredConnectionId == context.Context.ConnectionId;

                bool connectionCanBeCleanedUp = tokenIdMatches && hubRegistered && connectionIdMatches;

                if (connectionCanBeCleanedUp)
                {
                    log(context, "disconnected from hub");
                    userState.Item!.ConnectionIds.Remove(context.Hub.GetType());
                }

                if (userState.Item?.ConnectionIds.Count == 0)
                {
                    log(context, "all connections closed, destroying state");
                    userState.Destroy();
                }
            }
        }
    }
}
