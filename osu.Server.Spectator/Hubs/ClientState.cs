// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;

namespace osu.Server.Spectator.Hubs
{
    [Serializable]
    public class ClientState
    {
        /// <summary>
        /// The connection ID of the owner of this state.
        /// </summary>
        public readonly string ConnectionId;

        /// <summary>
        /// The user ID of the owner of this state.
        /// </summary>
        public readonly int UserId;

        /// <summary>
        /// The JWT token ID (<c>jti</c> claim) from the token that the owner of this state provided
        /// to establish this connection.
        /// This is used to identify clients potentially connecting from multiple devices.
        /// </summary>
        public readonly string TokenId;

        public ClientState(in string connectionId, in int userId, in string tokenId)
        {
            UserId = userId;
            ConnectionId = connectionId;
            TokenId = tokenId;
        }
    }
}
