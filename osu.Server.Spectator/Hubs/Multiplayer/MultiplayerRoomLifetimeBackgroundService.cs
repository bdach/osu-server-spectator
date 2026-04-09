// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using osu.Server.Spectator.Entities;

namespace osu.Server.Spectator.Hubs.Multiplayer
{
    public class MultiplayerRoomLifetimeBackgroundService : BackgroundService
    {
        private readonly EntityStore<ServerMultiplayerRoom> roomStore;
        private readonly ILogger<MultiplayerRoomLifetimeBackgroundService> logger;

        public MultiplayerRoomLifetimeBackgroundService(
            EntityStore<ServerMultiplayerRoom> roomStore,
            ILoggerFactory loggerFactory)
        {
            this.roomStore = roomStore;
            logger = loggerFactory.CreateLogger<MultiplayerRoomLifetimeBackgroundService>();
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                logger.LogDebug("Running clean-up of rooms with set end dates.");

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
                            {
                                logger.LogDebug("Skipping attempt to destroy usage of room ID:{RoomId} due as its end date has changed to {EndDate}", roomId, roomUsage.Item?.EndDate);
                                continue;
                            }

                            logger.LogDebug("Destroying usage of room ID:{RoomId} as its end date of {EndDate} has passed", roomId, roomUsage.Item.EndDate);
                            roomUsage.Destroy();
                        }
                    }
                    catch (KeyNotFoundException)
                    {
                        logger.LogDebug("Skipping attempt to destroy usage of room ID:{RoomId} as it is no longer present in the room store", roomId);
                    }
                }

                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }
    }
}
