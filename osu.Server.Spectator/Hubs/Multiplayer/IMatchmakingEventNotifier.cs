// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Threading.Tasks;
using osu.Server.Spectator.Database.Models;

namespace osu.Server.Spectator.Hubs.Multiplayer
{
    public interface IMatchmakingEventNotifier
    {
        Task OnRoomCreatedAsync(long roomId, MatchmakingRoomCreatedEventDetail details);
        Task OnPlayerJoinedAsync(long roomId, int userId);
        Task OnPlayerBeatmapPickAsync(long roomId, int userId, long playlistItemId);
        Task OnFinalBeatmapSelectedAsync(long roomId, long playlistItemId);
    }
}
