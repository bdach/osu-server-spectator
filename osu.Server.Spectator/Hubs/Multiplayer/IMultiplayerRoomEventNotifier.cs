// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Threading.Tasks;
using osu.Game.Online.API;
using osu.Game.Online.Multiplayer;
using osu.Game.Online.Rooms;
using osu.Server.Spectator.Database.Models;

namespace osu.Server.Spectator.Hubs.Multiplayer
{
    public interface IMultiplayerRoomEventNotifier
    {
        Task OnRoomCreatedAsync(long roomId, int userId);
        Task OnRoomStateChangedAsync(long roomId, MultiplayerRoomState state);
        Task OnPlayerJoinedAsync(long roomId, MultiplayerRoomUser user);
        Task OnPlayerLeftAsync(long roomId, MultiplayerRoomUser user);
        Task OnPlayerKickedAsync(long roomId, MultiplayerRoomUser user);
        Task OnPlayerInvitedAsync(long roomId, int userId, int invitedBy, string password);
        Task OnHostChangedAsync(long roomId, int userId);
        Task OnSettingsChangedAsync(long roomId, MultiplayerRoomSettings settings);
        Task OnUserStateChangedAsync(long roomId, int userId, MultiplayerUserState state);
        Task OnMatchUserStateChangedAsync(long roomId, int userId, MatchUserState? userState);
        Task OnMatchRoomStateChangedAsync(long roomId, MatchRoomState? state);
        Task OnNewMatchEventAsync(long roomId, MatchServerEvent e);
        Task OnUserBeatmapAvailabilityChangedAsync(long roomId, int userId, BeatmapAvailability availability);
        Task OnUserStyleChangedAsync(long roomId, int userId, int? beatmapId, int? rulesetId);
        Task OnUserModsChangedAsync(long roomId, int userId, IEnumerable<APIMod> mods);
        Task OnGameStartedAsync(long roomId, long playlistItemId, MatchStartedEventDetail details);
        Task OnGameplayStartedAsync(long roomId, int userId);
        Task OnGameAbortedAsync(long roomId, long playlistItemId, GameplayAbortReason? reason);
        Task OnGameplayAbortedAsync(long roomId, int userId, GameplayAbortReason reason);
        Task OnGameCompletedAsync(long roomId, long playlistItemId);
        Task OnPlaylistItemAddedAsync(long roomId, MultiplayerPlaylistItem item);
        Task OnPlaylistItemRemovedAsync(long roomId, long playlistItemId);
        Task OnPlaylistItemChangedAsync(long roomId, MultiplayerPlaylistItem item);
        Task OnUserVotedToSkipIntro(long roomId, int userId, bool voted);
        Task OnVoteToSkipIntroPassed(long roomId);
        Task OnRoomDisbandedAsync(long roomId, int userId);
    }
}
