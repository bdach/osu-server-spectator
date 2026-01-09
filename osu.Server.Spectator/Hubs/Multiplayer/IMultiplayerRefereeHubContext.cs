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
        Task<long> CloseRoom(HubCallerContext caller);
        Task InvitePlayer(HubCallerContext caller, int userId);
        Task TransferHost(HubCallerContext caller, int userId);
        Task KickUser(HubCallerContext caller, int userId);
        Task StartMatchCountdown(HubCallerContext caller, StartMatchCountdownRequest request);
        Task StopMatchCountdown(HubCallerContext caller);
        Task StartMatch(HubCallerContext caller);
        Task AbortMatch(HubCallerContext caller);
        Task EditCurrentPlaylistItem(HubCallerContext caller, Action<MultiplayerPlaylistItem> changeFunc);
        Task ChangeSettings(HubCallerContext caller, Action<MultiplayerRoomSettings> changeFunc);
    }
}
