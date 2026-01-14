// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using osu.Game.Online.Multiplayer;
using osu.Server.Spectator.Entities;

namespace osu.Server.Spectator.Hubs.Multiplayer
{
    public interface IServerMultiplayerRoomController
    {
        /// <summary>
        /// Retrieves a <see cref="ServerMultiplayerRoom"/> usage.
        /// </summary>
        /// <param name="roomId">The ID of the room to retrieve.</param>
        Task<ItemUsage<ServerMultiplayerRoom>?> TryGetRoom(long roomId);

        Task<MultiplayerRoom> JoinOrCreateRoom(long roomId, ItemUsage<ServerMultiplayerRoom> roomUsage, ItemUsage<MultiplayerClientState> userUsage, string password, bool isNewRoom, MultiplayerRoomUserRole role);

        Task RemoveUserFromRoom(int removingUserId, MultiplayerClientState removedUserState, ItemUsage<ServerMultiplayerRoom> roomUsage, bool wasKick);

        void Log(ServerMultiplayerRoom room, MultiplayerRoomUser? user, string message, LogLevel logLevel = LogLevel.Information);
    }

    public enum MultiplayerRoomUserRole
    {
        Player,
        Referee,
    }
}
