// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.SignalR;
using osu.Game.Online.Multiplayer;
using osu.Server.Spectator.Extensions;

namespace osu.Server.Spectator.Entities
{
    /// <summary>
    /// Maintains the connection state of a single client (notably, client, not user) across multiple hubs.
    /// </summary>
    public class ConnectionState
    {
        /// <summary>
        /// The unique IDs of the JWTs the user is using to authenticate.
        /// This is used to control user uniqueness.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Multiple tokens per user are allowed, as the tokens' expiration is managed by web
        /// and it is feasible that the token may be refreshed client-side while the game is running.
        /// </para>
        /// <para>
        /// Because the token is generally only sent to spectator server when hub connections are being established via HTTP requests,
        /// the server would remain blissfully unaware of this fact.
        /// Due to this, users could get forcibly logged out when connection to any of the hubs drops out and comes back again,
        /// as the server would see a new token and assume that it was a new client instance.
        /// </para>
        /// <para>
        /// This is mitigated by introducing <see cref="IStatefulServer.SendHeader"/> as a means to inform the server
        /// that the new token has arrived...
        /// except when doing so, <i>overwriting</i> the token would lead to <i>existing connections</i> ceasing to work,
        /// because <see cref="ConcurrentConnectionLimiter"/> would check <i>the principal it stored when connection was established</i>
        /// (<see cref="HubCallerContext.User"/>, to be precise) against the updated token.
        /// Therefore we must continue to store <i>all</i> possibly-valid tokens until the connection fully drops.
        /// </para>
        /// </remarks>
        public readonly HashSet<string> TokenIds = new HashSet<string>();

        /// <summary>
        /// The connection IDs of the user for each hub type.
        /// </summary>
        /// <remarks>
        /// In SignalR, connection IDs are unique per connection.
        /// Because we use multiple hubs and a user is expected to be connected to each hub individually,
        /// we use a dictionary to track connections across all hubs for a specific user.
        /// </remarks>
        public readonly Dictionary<Type, string> ConnectionIds = new Dictionary<Type, string>();

        public ConnectionState(HubLifetimeContext context)
        {
            TokenIds.Add(context.Context.GetTokenId());

            RegisterConnectionId(context);
        }

        /// <summary>
        /// Registers the provided hub/connection context, replacing any existing connection for the hub type.
        /// </summary>
        /// <param name="context">The hub context to retrieve information from.</param>
        public void RegisterConnectionId(HubLifetimeContext context)
            => ConnectionIds[context.Hub.GetType()] = context.Context.ConnectionId;
    }
}
