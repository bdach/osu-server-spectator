// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Caching.Distributed;
using osu.Game.Online.Multiplayer;
using osu.Server.Spectator.Entities;
using osu.Server.Spectator.Extensions;

namespace osu.Server.Spectator.Hubs
{
    [UsedImplicitly]
    [Authorize]
    public abstract class StatefulUserHub<TClient, TUserState> : LoggingHub<TClient>
        where TUserState : ClientState
        where TClient : class
    {
        protected readonly EntityStore<TUserState> UserStates;

        protected StatefulUserHub(IDistributedCache cache, EntityStore<TUserState> userStates)
        {
            UserStates = userStates;
        }

        protected KeyValuePair<long, TUserState>[] GetAllStates() => UserStates.GetAllEntities();

        public override async Task OnConnectedAsync()
        {
            await base.OnConnectedAsync();

            var previousState = UserStates.GetEntityUnsafe(CurrentContextUserId);

            if (previousState != null && previousState.TokenId != Context.User!.GetJwtId())
            {
                // user has an existing state but token doesn't match.
                // this indicates a second separate connection from another client that is not allowed.
                Log($"Dropping connection {Context.ConnectionId} from user {CurrentContextUserId} as another already exists");
                Context.Abort();
                return;
            }

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
                usage = await UserStates.GetForUse(CurrentContextUserId);
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
                    if (isDisconnect && !isLocalUserState(usage))
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
            var usage = await UserStates.GetForUse(CurrentContextUserId, true);

            if (usage.Item != null && !isLocalUserState(usage))
            {
                usage.Dispose();
                throw new InvalidStateException("State is not valid for this connection");
            }

            return usage;
        }

        protected Task<ItemUsage<TUserState>> GetStateFromUser(int userId) => UserStates.GetForUse(userId);

        private bool isLocalUserState(ItemUsage<TUserState> usage)
            => usage.Item != null
               && usage.Item.ConnectionId == Context.ConnectionId
               && usage.Item.TokenId == Context.User!.GetJwtId();
    }
}
