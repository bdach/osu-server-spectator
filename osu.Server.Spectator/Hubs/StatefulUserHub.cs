// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Threading.Tasks;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using osu.Game.Online;
using osu.Game.Online.Multiplayer;
using osu.Server.Spectator.Authentication;
using osu.Server.Spectator.Database;
using osu.Server.Spectator.Entities;
using osu.Server.Spectator.Extensions;

namespace osu.Server.Spectator.Hubs
{
    [UsedImplicitly]
    [Authorize]
    public abstract class StatefulUserHub<TClient, TUserState> : LoggingHub<TClient>, IStatefulServer
        where TUserState : ClientState
        where TClient : class, IStatefulUserHubClient
    {
        protected readonly IDatabaseFactory DatabaseFactory;
        private readonly ILoggerFactory loggerFactory;
        protected readonly EntityStore<TUserState> UserStates;
        private readonly EntityStore<ConnectionState> connectionStates;

        protected StatefulUserHub(
            IDatabaseFactory databaseFactory,
            ILoggerFactory loggerFactory,
            IDistributedCache cache,
            EntityStore<TUserState> userStates,
            EntityStore<ConnectionState> connectionStates)
            : base(loggerFactory)
        {
            DatabaseFactory = databaseFactory;
            this.loggerFactory = loggerFactory;
            UserStates = userStates;
            this.connectionStates = connectionStates;
        }

        protected KeyValuePair<long, TUserState>[] GetAllStates() => UserStates.GetAllEntities();

        public override async Task OnConnectedAsync()
        {
            await base.OnConnectedAsync();

            try
            {
                // if a previous connection is still present for the current user, we need to clean it up.
                await cleanUpState(false);
            }
            catch
            {
                Log("State cleanup failed");

                // if any exception happened during clean-up, don't allow the user to reconnect.
                // this limits damage to the user in a bad state if their clean-up cannot occur (they will not be able to reconnect until the issue is resolved).
                Context.Abort();
                throw;
            }
        }

        public sealed override async Task OnDisconnectedAsync(Exception? exception)
        {
            await base.OnDisconnectedAsync(exception);
            await cleanUpState(true);
        }

        private async Task cleanUpState(bool isDisconnect)
        {
            ItemUsage<TUserState>? usage;

            try
            {
                usage = await UserStates.GetForUse(Context.GetUserId());
            }
            catch (KeyNotFoundException)
            {
                // no state to clean up.
                return;
            }

            Log($"Cleaning up state on {(isDisconnect ? "disconnect" : "connect")}");

            try
            {
                if (usage.Item != null)
                {
                    bool isOurState = usage.Item.ConnectionId == Context.ConnectionId;

                    if (isDisconnect && !isOurState)
                    {
                        // not our state, owned by a different connection.
                        Log("Disconnect state cleanup aborted due to newer connection owning state");
                        return;
                    }

                    try
                    {
                        await CleanUpState(usage.Item);
                    }
                    finally
                    {
                        usage.Destroy();
                        Log("State cleanup completed");
                    }
                }
            }
            finally
            {
                usage.Dispose();
            }
        }

        /// <summary>
        /// Perform any cleanup required on the provided state.
        /// </summary>
        protected virtual Task CleanUpState(TUserState state) => Task.CompletedTask;

        protected async Task<ItemUsage<TUserState>> GetOrCreateLocalUserState()
        {
            var usage = await UserStates.GetForUse(Context.GetUserId(), true);

            if (usage.Item != null && usage.Item.ConnectionId != Context.ConnectionId)
            {
                usage.Dispose();
                throw new InvalidOperationException("State is not valid for this connection");
            }

            return usage;
        }

        protected Task<ItemUsage<TUserState>> GetStateFromUser(int userId) => UserStates.GetForUse(userId);

        public async Task SendHeader(string key, string value)
        {
            switch (key)
            {
                case IStatefulServer.TOKEN_HEADER:
                {
                    using var connectionState = await connectionStates.GetForUse(Context.GetUserId());

                    if (connectionState.Item == null)
                        break;

                    var optionsConfigurator = new ConfigureJwtBearerOptions(DatabaseFactory, loggerFactory);
                    var options = new JwtBearerOptions();
                    optionsConfigurator.Configure(options);
                    var principal = new JwtSecurityTokenHandler().ValidateToken(value, options.TokenValidationParameters, out var validated);

                    if (principal == null || validated == null)
                    {
                        Log($"[{nameof(SendHeader)}] Token could not be parsed correctly. Rejecting update.");
                        break;
                    }

                    if (!await optionsConfigurator.IsTokenValid(new JsonWebToken(value)))
                    {
                        Log($"[{nameof(SendHeader)}] Token has been expired or has been revoked. Rejecting update.");
                        break;
                    }

                    if (principal.FindFirst("jti") is not Claim claim || string.IsNullOrEmpty(claim.Value))
                    {
                        Log($"[{nameof(SendHeader)}] Token does not contain \"jti\" claim. Rejecting update.");
                        break;
                    }

                    Log($"[{nameof(SendHeader)}] Updating current token ID on user request.");
                    connectionState.Item.TokenId = claim.Value;
                    break;
                }
            }
        }
    }
}
