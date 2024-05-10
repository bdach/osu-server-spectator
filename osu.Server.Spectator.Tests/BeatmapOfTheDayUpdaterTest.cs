// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Moq;
using osu.Game.Online.Metadata;
using osu.Server.Spectator.Database;
using osu.Server.Spectator.Database.Models;
using osu.Server.Spectator.Hubs.Metadata;
using Xunit;

namespace osu.Server.Spectator.Tests
{
    public class BeatmapOfTheDayUpdaterTest
    {
        private readonly Mock<ILoggerFactory> loggerFactoryMock;
        private readonly Mock<IDatabaseFactory> databaseFactoryMock;
        private readonly Mock<IDatabaseAccess> databaseAccessMock;
        private readonly Mock<IHubContext<MetadataHub>> metadataHubContextMock;
        private readonly Mock<IClientProxy> allClientsProxy;

        public BeatmapOfTheDayUpdaterTest()
        {
            loggerFactoryMock = new Mock<ILoggerFactory>();
            loggerFactoryMock.Setup(factory => factory.CreateLogger(It.IsAny<string>()))
                             .Returns(new Mock<ILogger>().Object);

            databaseFactoryMock = new Mock<IDatabaseFactory>();
            databaseAccessMock = new Mock<IDatabaseAccess>();
            databaseFactoryMock.Setup(factory => factory.GetInstance()).Returns(databaseAccessMock.Object);

            metadataHubContextMock = new Mock<IHubContext<MetadataHub>>();
            allClientsProxy = new Mock<IClientProxy>();
            metadataHubContextMock.Setup(ctx => ctx.Clients.All).Returns(allClientsProxy.Object);
        }

        [Fact]
        public async Task TestChangeTracking()
        {
            databaseAccessMock.Setup(db => db.GetActiveBeatmapOfTheDayRoomsAsync())
                              .ReturnsAsync([new multiplayer_room { id = 4, category = room_category.beatmap_of_the_day }]);

            var updater = new BeatmapOfTheDayUpdater(
                loggerFactoryMock.Object,
                databaseFactoryMock.Object,
                metadataHubContextMock.Object)
            {
                UpdateInterval = 50
            };

            var task = updater.StartAsync(default);

            allClientsProxy.Verify(proxy => proxy.SendCoreAsync(
                    nameof(IMetadataClient.BeatmapOfTheDayUpdated),
                    It.Is<object[]>(args => ((BeatmapOfTheDayInfo)args![0]).RoomID == 4),
                    It.IsAny<CancellationToken>()),
                Times.Once);

            databaseAccessMock.Setup(db => db.GetActiveBeatmapOfTheDayRoomsAsync())
                              .ReturnsAsync([]);
            await Task.Delay(100);

            allClientsProxy.Verify(proxy => proxy.SendCoreAsync(
                    nameof(IMetadataClient.BeatmapOfTheDayUpdated),
                    It.Is<object[]>(args => ((BeatmapOfTheDayInfo)args![0]).RoomID == null),
                    It.IsAny<CancellationToken>()),
                Times.Once);

            databaseAccessMock.Setup(db => db.GetActiveBeatmapOfTheDayRoomsAsync())
                              .ReturnsAsync([new multiplayer_room { id = 5, category = room_category.beatmap_of_the_day }]);
            await Task.Delay(100);

            allClientsProxy.Verify(proxy => proxy.SendCoreAsync(
                    nameof(IMetadataClient.BeatmapOfTheDayUpdated),
                    It.Is<object[]>(args => ((BeatmapOfTheDayInfo)args![0]).RoomID == 5),
                    It.IsAny<CancellationToken>()),
                Times.Once);

            await updater.StopAsync(default);
            await task;
        }
    }
}
