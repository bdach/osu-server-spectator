// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using osu.Game.Online.Multiplayer;
using osu.Game.Online.Multiplayer.Countdown;
using osu.Game.Online.Rooms;

namespace osu.Server.Spectator.Hubs.Multiplayer
{
    public interface IMultiplayerRefereeHubContext
    {
        Task InitialiseUserState(HubCallerContext caller);
        Task CleanUpUserState(HubCallerContext caller);
        Task<MultiplayerRoom> CreateRoom(HubCallerContext caller, MultiplayerRoom room);
        Task CloseRoom(HubCallerContext caller, long roomId);
        Task InvitePlayer(HubCallerContext caller, long roomId, int userId);
        Task TransferHost(HubCallerContext caller, long roomId, int userId);
        Task KickUser(HubCallerContext caller, long roomId, int userId);
        Task StartMatchCountdown(HubCallerContext caller, long roomId, StartMatchCountdownRequest request);
        Task StopMatchCountdown(HubCallerContext caller, long roomId);
        Task StartMatch(HubCallerContext caller, long roomId);
        Task AbortMatch(HubCallerContext caller, long roomId);
        Task EditCurrentPlaylistItem(HubCallerContext caller, long roomId, Action<MultiplayerPlaylistItem> changeFunc);
        Task ChangeSettings(HubCallerContext caller, long roomId, Action<MultiplayerRoomSettings> changeFunc);
    }
}
