// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;

namespace osu.Server.Spectator.Hubs.Referee
{
    public class RefereeHub : Hub
    {
        public async Task Ping(string payload)
        {
            await Clients.Caller.SendAsync("Pong", payload);
        }
    }
}
