// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Threading.Tasks;
using osu.Game.Online.Multiplayer;

namespace osu.Server.Spectator.Hubs.Multiplayer
{
    public interface IMultiplayerRoomFactory
    {
        Task<long> CreateRoomAsync(int hostId, MultiplayerRoom room);
    }
}
