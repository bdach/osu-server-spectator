// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using osu.Server.Spectator.Database.Models;
using osu.Server.Spectator.Hubs.Multiplayer;

namespace osu.Server.Spectator.Hubs.Referee
{
    public class RefereeHubContext
    {
        private readonly IHubContext<RefereeHub> context;

        public RefereeHubContext(IHubContext<RefereeHub> context)
        {
            this.context = context;
        }

        public async Task NotifyRoomEvent(multiplayer_realtime_room_event ev)
        {
            await context.Clients.Group(MultiplayerHub.GetGroupId(ev.room_id)).SendAsync("RoomEventLogged", ev);
        }
    }
}
