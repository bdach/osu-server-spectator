// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using osu.Server.Spectator.Authentication;

namespace osu.Server.Spectator.Hubs.Referee
{
    [Authorize(ConfigureJwtBearerOptions.REFEREE_DELEGATION_SCHEME, AuthenticationSchemes = ConfigureJwtBearerOptions.REFEREE_DELEGATION_SCHEME)]
    public class RefereeHub : Hub
    {
        public async Task Ping(string payload)
        {
            await Clients.Caller.SendAsync("Pong", payload);
        }

        public async Task StartWatching(long roomId)
        {
            // TODO: check validity of the id probably maybe
            // TODO: also don't permit watching matchmaking rooms
            await Groups.AddToGroupAsync(Context.ConnectionId, GetGroupId(roomId));
        }

        public async Task StopWatching(long roomId)
        {
            // TODO: check validity of the id probably maybe
            // TODO: also don't permit watching matchmaking rooms
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, GetGroupId(roomId));
        }

        public static string GetGroupId(long roomId) => $"room:{roomId}";
    }
}
