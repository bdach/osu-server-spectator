// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IdentityModel.Tokens.Jwt;
using System.Threading.Tasks;
using osu.Game.Online.RealtimeMultiplayer;
using osu.Server.Spectator.Database.Models;

namespace osu.Server.Spectator.Database
{
    public interface IOsuDatabase : IDisposable
    {
        Task<int?> GetUserIdFromTokenAsync(JwtSecurityToken jwtToken);

        Task<bool> IsUserRestrictedAsync(int userId);

        Task<multiplayer_room> GetRoomAsync(long roomId);
        Task<multiplayer_playlist_item> GetCurrentPlaylistItemAsync(long roomId);
        Task<string> GetBeatmapChecksumAsync(int beatmapId);
        Task MarkRoomActiveAsync(MultiplayerRoom room);
        Task UpdateRoomSettingsAsync(MultiplayerRoom room);
        Task UpdateRoomHostAsync(MultiplayerRoom room);
        Task UpdateRoomParticipantsAsync(MultiplayerRoom room);
        Task ClearRoomScoresAsync(MultiplayerRoom room);
        Task EndMatchAsync(MultiplayerRoom room);
    }
}
