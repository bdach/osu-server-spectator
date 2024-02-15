// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Memory;
using Moq;
using osu.Server.Spectator.Database;
using osu.Server.Spectator.Database.Models;
using osu.Server.Spectator.Entities;
using osu.Server.Spectator.Hubs.Filters;
using osu.Server.Spectator.Hubs.Metadata;
using Xunit;

namespace osu.Server.Spectator.Tests
{
    public class ClientVersionCheckerTests
    {
        private readonly EntityStore<MetadataClientState> metadataStore;
        private readonly Mock<IDatabaseFactory> databaseFactoryMock;
        private readonly Mock<IMemoryCache> memoryCacheMock;

        public ClientVersionCheckerTests()
        {
            metadataStore = new EntityStore<MetadataClientState>();

            databaseFactoryMock = new Mock<IDatabaseFactory>();
            databaseFactoryMock.Setup(db => db.GetInstance())
                               .Returns(() => new Mock<DatabaseAccess>().Object);

            memoryCacheMock = new Mock<IMemoryCache>();

            AppSettings.CheckClientVersion = false;
        }

        [Fact]
        public async Task TestVersionsNotCheckedByDefault()
        {
            var hubCallerContextMock = new Mock<HubCallerContext>();
            hubCallerContextMock.Setup(ctx => ctx.UserIdentifier).Returns("2222");

            // extension methods can't be mocked, so this mocks the underlying `TryGetValue(object, out object?)` method.
            object? hubChecksVersion = true;
            memoryCacheMock.Setup(cache => cache.TryGetValue(It.Is<object>(s => ((string)s).StartsWith("hub:", StringComparison.Ordinal)), out hubChecksVersion))
                           .Returns(true);

            var lifetimeContext = new HubLifetimeContext(hubCallerContextMock.Object, new Mock<IServiceProvider>().Object, new Mock<Hub>().Object);
            var filter = new ClientVersionChecker(metadataStore, databaseFactoryMock.Object, memoryCacheMock.Object);

            bool connectionEstablished = false;
            await filter.OnConnectedAsync(lifetimeContext, _ =>
            {
                connectionEstablished = true;
                return Task.CompletedTask;
            });

            Assert.True(connectionEstablished);
        }

        [Fact]
        public async Task TestVersionsNotCheckedOnExcludedHubs()
        {
            var hubCallerContextMock = new Mock<HubCallerContext>();
            hubCallerContextMock.Setup(ctx => ctx.UserIdentifier).Returns("2222");

            AppSettings.CheckClientVersion = true;
            // extension methods can't be mocked, so this mocks the underlying `TryGetValue(object, out object?)` method.
            object? hubChecksVersion = false;
            memoryCacheMock.Setup(cache => cache.TryGetValue(It.Is<object>(s => ((string)s).StartsWith("hub:", StringComparison.Ordinal)), out hubChecksVersion))
                           .Returns(true);

            var lifetimeContext = new HubLifetimeContext(hubCallerContextMock.Object, new Mock<IServiceProvider>().Object, new Mock<Hub>().Object);
            var filter = new ClientVersionChecker(metadataStore, databaseFactoryMock.Object, memoryCacheMock.Object);

            bool connectionEstablished = false;
            await filter.OnConnectedAsync(lifetimeContext, _ =>
            {
                connectionEstablished = true;
                return Task.CompletedTask;
            });

            Assert.True(connectionEstablished);
        }

        [Fact]
        public async Task TestNoUserState()
        {
            var hubCallerContextMock = new Mock<HubCallerContext>();
            hubCallerContextMock.Setup(ctx => ctx.UserIdentifier).Returns("2222");

            AppSettings.CheckClientVersion = true;
            // extension methods can't be mocked, so this mocks the underlying `TryGetValue(object, out object?)` method.
            object? hubChecksVersion = true;
            memoryCacheMock.Setup(cache => cache.TryGetValue(It.Is<object>(s => ((string)s).StartsWith("hub:", StringComparison.Ordinal)), out hubChecksVersion))
                           .Returns(true);

            var lifetimeContext = new HubLifetimeContext(hubCallerContextMock.Object, new Mock<IServiceProvider>().Object, new Mock<Hub>().Object);
            var filter = new ClientVersionChecker(metadataStore, databaseFactoryMock.Object, memoryCacheMock.Object);

            await Assert.ThrowsAsync<ClientVersionChecker.InvalidVersionException>(
                async () => await filter.OnConnectedAsync(lifetimeContext, _ => Task.CompletedTask));
        }

        [Fact]
        public async Task TestUserStateHasEmptyHash()
        {
            var hubCallerContextMock = new Mock<HubCallerContext>();
            hubCallerContextMock.Setup(ctx => ctx.UserIdentifier).Returns("2222");

            using (var usage = await metadataStore.GetForUse(2222, true))
                usage.Item = new MetadataClientState("deadbeef", 2222, null);

            AppSettings.CheckClientVersion = true;
            // extension methods can't be mocked, so this mocks the underlying `TryGetValue(object, out object?)` method.
            object? hubChecksVersion = true;
            memoryCacheMock.Setup(cache => cache.TryGetValue(It.Is<object>(s => ((string)s).StartsWith("hub:", StringComparison.Ordinal)), out hubChecksVersion))
                           .Returns(true);

            var lifetimeContext = new HubLifetimeContext(hubCallerContextMock.Object, new Mock<IServiceProvider>().Object, new Mock<Hub>().Object);
            var filter = new ClientVersionChecker(metadataStore, databaseFactoryMock.Object, memoryCacheMock.Object);

            await Assert.ThrowsAsync<ClientVersionChecker.InvalidVersionException>(
                async () => await filter.OnConnectedAsync(lifetimeContext, _ => Task.CompletedTask));
        }

        [Fact]
        public async Task TestUserIsOnBuildWithUnrecognisedHash()
        {
            var hubCallerContextMock = new Mock<HubCallerContext>();
            hubCallerContextMock.Setup(ctx => ctx.UserIdentifier).Returns("2222");

            using (var usage = await metadataStore.GetForUse(2222, true))
                usage.Item = new MetadataClientState("deadbeef", 2222, "cafebabe");

            AppSettings.CheckClientVersion = true;
            // extension methods can't be mocked, so this mocks the underlying `TryGetValue(object, out object?)` method.
            object? hubChecksVersion = true;
            memoryCacheMock.Setup(cache => cache.TryGetValue(It.Is<object>(s => ((string)s).StartsWith("hub:", StringComparison.Ordinal)), out hubChecksVersion))
                           .Returns(true);
            object? build = null;
            memoryCacheMock.Setup(cache => cache.TryGetValue("build:cafebabe", out build))
                           .Returns(true);

            var lifetimeContext = new HubLifetimeContext(hubCallerContextMock.Object, new Mock<IServiceProvider>().Object, new Mock<Hub>().Object);
            var filter = new ClientVersionChecker(metadataStore, databaseFactoryMock.Object, memoryCacheMock.Object);

            await Assert.ThrowsAsync<ClientVersionChecker.InvalidVersionException>(
                async () => await filter.OnConnectedAsync(lifetimeContext, _ => Task.CompletedTask));
            memoryCacheMock.Verify(cache => cache.TryGetValue("build:cafebabe", out build), Times.Once);
        }

        [Fact]
        public async Task TestUserIsOnBuildWithDisallowedBancho()
        {
            var hubCallerContextMock = new Mock<HubCallerContext>();
            hubCallerContextMock.Setup(ctx => ctx.UserIdentifier).Returns("2222");

            using (var usage = await metadataStore.GetForUse(2222, true))
                usage.Item = new MetadataClientState("deadbeef", 2222, "cafebabe");

            AppSettings.CheckClientVersion = true;
            // extension methods can't be mocked, so this mocks the underlying `TryGetValue(object, out object?)` method.
            object? hubChecksVersion = true;
            memoryCacheMock.Setup(cache => cache.TryGetValue(It.Is<object>(s => ((string)s).StartsWith("hub:", StringComparison.Ordinal)), out hubChecksVersion))
                           .Returns(true);
            object? build = new osu_build { allow_bancho = false };
            memoryCacheMock.Setup(cache => cache.TryGetValue("build:cafebabe", out build))
                           .Returns(true);

            var lifetimeContext = new HubLifetimeContext(hubCallerContextMock.Object, new Mock<IServiceProvider>().Object, new Mock<Hub>().Object);
            var filter = new ClientVersionChecker(metadataStore, databaseFactoryMock.Object, memoryCacheMock.Object);

            await Assert.ThrowsAsync<ClientVersionChecker.InvalidVersionException>(
                async () => await filter.OnConnectedAsync(lifetimeContext, _ => Task.CompletedTask));
            memoryCacheMock.Verify(cache => cache.TryGetValue("build:cafebabe", out build), Times.Once);
        }

        [Fact]
        public async Task TestUserIsOnBuildWithAllowedBancho()
        {
            var hubCallerContextMock = new Mock<HubCallerContext>();
            hubCallerContextMock.Setup(ctx => ctx.UserIdentifier).Returns("2222");

            using (var usage = await metadataStore.GetForUse(2222, true))
                usage.Item = new MetadataClientState("deadbeef", 2222, "cafebabe");

            AppSettings.CheckClientVersion = true;
            // extension methods can't be mocked, so this mocks the underlying `TryGetValue(object, out object?)` method.
            object? hubChecksVersion = true;
            memoryCacheMock.Setup(cache => cache.TryGetValue(It.Is<object>(s => ((string)s).StartsWith("hub:", StringComparison.Ordinal)), out hubChecksVersion))
                           .Returns(true);
            object? build = new osu_build { allow_bancho = true };
            memoryCacheMock.Setup(cache => cache.TryGetValue("build:cafebabe", out build))
                           .Returns(true);

            var lifetimeContext = new HubLifetimeContext(hubCallerContextMock.Object, new Mock<IServiceProvider>().Object, new Mock<Hub>().Object);
            var filter = new ClientVersionChecker(metadataStore, databaseFactoryMock.Object, memoryCacheMock.Object);

            bool connectionEstablished = false;
            await filter.OnConnectedAsync(lifetimeContext, _ =>
            {
                connectionEstablished = true;
                return Task.CompletedTask;
            });

            Assert.True(connectionEstablished);
            memoryCacheMock.Verify(cache => cache.TryGetValue("build:cafebabe", out build), Times.Once);
        }
    }
}
