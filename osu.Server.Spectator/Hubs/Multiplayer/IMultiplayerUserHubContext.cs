// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using osu.Game.Online.API;
using osu.Game.Online.Multiplayer;
using osu.Game.Online.Rooms;

namespace osu.Server.Spectator.Hubs.Multiplayer
{
    public interface IMultiplayerUserHubContext
    {
        Task InitialiseUserState(HubCallerContext caller);
        Task CleanUpUserState(MultiplayerClientState state);
        Task<MultiplayerRoom> CreateRoom(HubCallerContext caller, MultiplayerRoom room);
        Task<MultiplayerRoom> JoinRoomWithPassword(HubCallerContext caller, long roomId, string password);
        Task LeaveRoom(HubCallerContext caller);
        Task InvitePlayer(HubCallerContext caller, int userId);
        Task TransferHost(HubCallerContext caller, int userId);
        Task KickUser(HubCallerContext caller, int userId);
        Task ChangeState(HubCallerContext caller, MultiplayerUserState newState);
        Task ChangeBeatmapAvailability(HubCallerContext caller, BeatmapAvailability beatmapAvailability);
        Task ChangeUserStyle(HubCallerContext caller, int? beatmapId, int? rulesetId);
        Task ChangeUserMods(HubCallerContext caller, IEnumerable<APIMod> newMods);
        Task SendMatchRequest(HubCallerContext caller, MatchUserRequest request);
        Task StartMatch(HubCallerContext caller);
        Task AbortMatch(HubCallerContext caller);
        Task AbortGameplay(HubCallerContext caller);
        Task VoteToSkipIntro(HubCallerContext caller);
        Task AddPlaylistItem(HubCallerContext caller, MultiplayerPlaylistItem item);
        Task EditPlaylistItem(HubCallerContext caller, MultiplayerPlaylistItem item);
        Task RemovePlaylistItem(HubCallerContext caller, long playlistItemId);
        Task ChangeSettings(HubCallerContext caller, MultiplayerRoomSettings settings);
        Task ChangeAndBroadcastUserState(HubCallerContext caller, MultiplayerUserState state);
        Task ChangeAndBroadcastUserBeatmapAvailability(HubCallerContext caller, BeatmapAvailability beatmapAvailability);
    }
}
