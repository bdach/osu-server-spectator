// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Threading.Tasks;
using osu.Server.Spectator.Database.Models;

namespace osu.Server.Spectator.Hubs.Multiplayer
{
    public interface IMultiplayerRoomEventNotifier
    {
        Task OnRoomCreatedAsync(long roomId, int userId);
        Task OnRoomDisbandedAsync(long roomId, int userId);
        Task OnPlayerJoinedAsync(long roomId, int userId);
        Task OnPlayerLeftAsync(long roomId, int userId);
        Task OnPlayerKickedAsync(long roomId, int userId);
        Task OnHostChangedAsync(long roomId, int userId);
        Task OnGameStartedAsync(long roomId, long playlistItemId, MatchStartedEventDetail details);
        Task OnGameAbortedAsync(long roomId, long playlistItemId);
        Task OnGameCompletedAsync(long roomId, long playlistItemId);
    }
}
