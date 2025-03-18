// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Game.Online.Multiplayer;
using osu.Server.Spectator.Database.Models;

namespace osu.Server.Spectator.Hubs.Multiplayer
{
    public abstract class MatchTypeImplementation
    {
        protected readonly ServerMultiplayerRoom Room;
        protected readonly IMultiplayerHubContext Hub;

        protected MatchTypeImplementation(ServerMultiplayerRoom room, IMultiplayerHubContext hub)
        {
            Room = room;
            Hub = hub;
        }

        /// <summary>
        /// Called when a user has requested a match type specific action.
        /// </summary>
        /// <param name="user">The user requesting the action.</param>
        /// <param name="request">The nature of the action.</param>
        public virtual void HandleUserRequest(MultiplayerRoomUser user, MatchUserRequest request)
        {
        }

        /// <summary>
        /// Called once for each user which joins the room. Will be run once for each user after initial construction.
        /// </summary>
        /// <param name="user">The user which joined the room.</param>
        public virtual void HandleUserJoined(MultiplayerRoomUser user)
        {
        }

        /// <summary>
        /// Called once for each user leaving the room.
        /// </summary>
        /// <param name="user">The user which left the room.</param>
        public virtual void HandleUserLeft(MultiplayerRoomUser user)
        {
        }

        public abstract RoomState GetRoomState();
    }
}
