// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using osu.Server.Spectator.Entities;

namespace osu.Server.Spectator.Hubs.Multiplayer
{
    public class MultiplayerRoomLifetimeBackgroundService : BackgroundService
    {
        private readonly EntityStore<ServerMultiplayerRoom> roomStore;

        public MultiplayerRoomLifetimeBackgroundService(
            EntityStore<ServerMultiplayerRoom> roomStore)
        {
            this.roomStore = roomStore;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var allRooms = roomStore.GetAllEntities();

                foreach (var (roomId, room) in allRooms)
                {
                    if (room.EndDate == null || DateTimeOffset.Now <= room.EndDate)
                        continue;

                    try
                    {
                        using (var roomUsage = await roomStore.GetForUse(roomId))
                        {
                            if (roomUsage.Item?.EndDate == null || DateTimeOffset.Now <= roomUsage.Item.EndDate)
                                continue;

                            roomUsage.Destroy();
                        }
                    }
                    catch (KeyNotFoundException)
                    {
                        // not fatal. probably log something fuck knows.
                    }
                }

                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }
    }
}
