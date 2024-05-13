// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using osu.Game.Online.Metadata;
using osu.Server.Spectator.Database;

namespace osu.Server.Spectator.Hubs.Metadata
{
    public interface IBeatmapOfTheDayUpdater : IHostedService
    {
        BeatmapOfTheDayInfo Current { get; }
    }

    public class BeatmapOfTheDayUpdater : BackgroundService, IBeatmapOfTheDayUpdater
    {
        /// <summary>
        /// Amount of time (in milliseconds) between subsequent polls for the current beatmap of the day.
        /// </summary>
        public int UpdateInterval = 300_000;

        public BeatmapOfTheDayInfo Current { get; private set; } = new BeatmapOfTheDayInfo();

        private readonly ILogger logger;
        private readonly IDatabaseFactory databaseFactory;
        private readonly IHubContext<MetadataHub> hubContext;

        public BeatmapOfTheDayUpdater(
            ILoggerFactory loggerFactory,
            IDatabaseFactory databaseFactory,
            IHubContext<MetadataHub> hubContext)
        {
            logger = loggerFactory.CreateLogger(nameof(BeatmapOfTheDayUpdater));
            this.databaseFactory = databaseFactory;
            this.hubContext = hubContext;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (stoppingToken.IsCancellationRequested == false)
            {
                try
                {
                    await updateBeatmapOfTheDayInfo(stoppingToken);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to update beatmap of the day");
                }

                await Task.Delay(UpdateInterval, stoppingToken);
            }
        }

        private async Task updateBeatmapOfTheDayInfo(CancellationToken cancellationToken)
        {
            using var db = databaseFactory.GetInstance();

            var activeRooms = (await db.GetActiveBeatmapOfTheDayRoomsAsync()).ToList();

            if (activeRooms.Count > 1)
            {
                logger.LogWarning("More than one active 'beatmap of the day' room detected (ids: {0}). Will only use the first one.",
                    string.Join(',', activeRooms.Select(room => room.id)));
            }

            var activeRoom = activeRooms.FirstOrDefault();

            if (Current.RoomID != activeRoom?.id)
            {
                logger.LogInformation("Broadcasting 'beatmap of the day' room change from id {0} to {1}", Current.RoomID, activeRoom?.id);
                Current = new BeatmapOfTheDayInfo { RoomID = activeRoom?.id };
                await hubContext.Clients.All.SendAsync(nameof(IMetadataClient.BeatmapOfTheDayUpdated), Current, cancellationToken);
            }
        }
    }
}
