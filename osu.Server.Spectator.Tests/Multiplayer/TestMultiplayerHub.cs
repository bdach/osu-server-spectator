// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using osu.Server.Spectator.Database;
using osu.Server.Spectator.Entities;
using osu.Server.Spectator.Hubs.Multiplayer;

namespace osu.Server.Spectator.Tests.Multiplayer
{
    public class TestMultiplayerHub : MultiplayerHub
    {
        public new MultiplayerHubContext HubContext => base.HubContext;

        public TestMultiplayerHub(
            IDatabaseFactory databaseFactory,
            ILoggerFactory loggerFactory,
            IDistributedCache cache,
            EntityStore<ConnectionState> connectionState,
            EntityStore<ServerMultiplayerRoom> rooms,
            EntityStore<MultiplayerClientState> users,
            ChatFilters chatFilters,
            IHubContext<MultiplayerHub> hubContext)
            : base(databaseFactory, loggerFactory, cache, connectionState, rooms, users, chatFilters, hubContext)
        {
        }

        public bool CheckRoomExists(long roomId)
        {
            try
            {
                using (var usage = Rooms.GetForUse(roomId).Result)
                    return usage.Item != null;
            }
            catch
            {
                // probably not tracked.
                return false;
            }
        }
    }
}
