// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Threading.Tasks;

namespace osu.Server.Spectator.Services
{
    public interface ISharedInterop
    {
        /// <summary>
        /// Creates a chat channel for the multiplayer room with the given <paramref name="roomId"/>.
        /// </summary>
        /// <param name="roomId">The ID of the room.</param>
        /// <param name="addHost">Whether the host of the room should be added to the channel.</param>
        Task CreateChatForRoomAsync(long roomId, bool addHost);

        /// <summary>
        /// Adds a user to an osu!web chat channel associated with the given multiplayer room.
        /// </summary>
        /// <param name="userId">The ID of the user wanting to join the room.</param>
        /// <param name="roomId">The ID of the room to join.</param>
        /// <param name="password">The room's password.</param>
        Task AddUserToRoomChatAsync(int userId, long roomId, string password);

        /// <summary>
        /// Removes a user from an osu!web chat channel associated with the given multiplayer room.
        /// </summary>
        /// <param name="userId">The ID of the user wanting to part the room.</param>
        /// <param name="roomId">The ID of the room to part.</param>
        Task RemoveUserFromRoomChatAsync(int userId, long roomId);
    }
}
