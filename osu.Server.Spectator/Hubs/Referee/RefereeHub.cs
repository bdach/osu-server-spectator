// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Online.Multiplayer;
using osu.Game.Online.Rooms;
using osu.Server.Spectator.Hubs.Multiplayer;

namespace osu.Server.Spectator.Hubs.Referee
{
    public class RefereeHub : Hub
    {
        private readonly IMultiplayerHubContext multiplayerHubContext;

        public RefereeHub(IMultiplayerHubContext multiplayerHubContext)
        {
            this.multiplayerHubContext = multiplayerHubContext;
        }

        public async Task Ping(string payload)
        {
            await Clients.Caller.SendAsync("Pong", payload);
        }

        public async Task<long> MakeRoom(string roomName)
        {
            var room = new MultiplayerRoom(new Room
            {
                Name = roomName,
                Password = Guid.NewGuid().ToString(),
                Type = MatchType.HeadToHead,
                QueueMode = QueueMode.HostOnly,
                Playlist =
                [
                    new PlaylistItem(new APIBeatmap { OnlineID = 75 })
                ]
            });
            var created = await multiplayerHubContext.CreateRoom(Context, room);
            await Groups.AddToGroupAsync(Context.ConnectionId, MultiplayerHub.GetGroupId(created.RoomID));
            return created.RoomID;
        }

        // TODO: currently this is all still beholden to the "user can only be in one room at a time" principle
        // rolling with it for now just to see all of this do something, but eventually this will have to change
        public async Task<long> CloseRoom()
        {
            long roomId = await multiplayerHubContext.CloseRoom(Context);
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, MultiplayerHub.GetGroupId(roomId));
            return roomId;
        }

        public async Task InvitePlayer(int userId)
            => await multiplayerHubContext.InvitePlayer(Context, userId);

        public async Task KickUser(int userId)
            => await multiplayerHubContext.KickUser(Context, userId);
    }
}
