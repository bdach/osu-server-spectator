// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Moq;
using osu.Game.Online.RealtimeMultiplayer;
using osu.Server.Spectator.Hubs;
using Xunit;

namespace osu.Server.Spectator.Tests
{
    public class MultiplayerFlowTests
    {
        private readonly MultiplayerHub hub;

        private const int user_id = 1234;
        private const int user_id_2 = 2345;

        private const long room_id = 8888;

        private readonly Mock<IMultiplayerClient> mockReceiver;
        private readonly Mock<IMultiplayerClient> mockGameplayReceiver;

        private readonly Mock<HubCallerContext> mockContextUser1;
        private readonly Mock<HubCallerContext> mockContextUser2;

        public MultiplayerFlowTests()
        {
            MultiplayerHub.Reset();

            MemoryDistributedCache cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));

            hub = new MultiplayerHub(cache);

            Mock<IGroupManager> mockGroups = new Mock<IGroupManager>();

            mockContextUser1 = new Mock<HubCallerContext>();
            mockContextUser1.Setup(context => context.UserIdentifier).Returns(user_id.ToString());

            mockContextUser2 = new Mock<HubCallerContext>();
            mockContextUser2.Setup(context => context.UserIdentifier).Returns(user_id_2.ToString());

            Mock<IHubCallerClients<IMultiplayerClient>> mockClients = new Mock<IHubCallerClients<IMultiplayerClient>>();

            mockReceiver = new Mock<IMultiplayerClient>();
            mockClients.Setup(clients => clients.Group(MultiplayerHub.GetGroupId(room_id, false))).Returns(mockReceiver.Object);

            mockGameplayReceiver = new Mock<IMultiplayerClient>();
            mockClients.Setup(clients => clients.Group(MultiplayerHub.GetGroupId(room_id, true))).Returns(mockGameplayReceiver.Object);

            hub.Groups = mockGroups.Object;
            hub.Clients = mockClients.Object;

            setUserContext(mockContextUser1);
        }

        #region Host assignment and transfer

        [Fact]
        public async Task FirstUserBecomesHost()
        {
            var room = await hub.JoinRoom(room_id);
            Assert.True(room.Host?.UserID == user_id);
        }

        [Fact]
        public async Task HostTransfer()
        {
            setUserContext(mockContextUser1);
            var room = await hub.JoinRoom(room_id);

            setUserContext(mockContextUser2);
            Assert.Equal(room, await hub.JoinRoom(room_id));

            setUserContext(mockContextUser1);
            await hub.TransferHost(user_id_2);

            mockReceiver.Verify(r => r.HostChanged(user_id_2), Times.Once);
            Assert.True(room.Host?.UserID == user_id_2);
        }

        [Fact]
        public async Task HostLeavingCausesHostTransfer()
        {
            setUserContext(mockContextUser1);
            var room = await hub.JoinRoom(room_id);

            setUserContext(mockContextUser2);
            await hub.JoinRoom(room_id);

            setUserContext(mockContextUser1);
            await hub.LeaveRoom();

            mockReceiver.Verify(r => r.HostChanged(user_id_2), Times.Once);
            Assert.True(room.Host?.UserID == user_id_2);
        }

        #endregion

        #region Joining and leaving

        [Fact]
        public async Task UserCantJoinWhenAlreadyJoined()
        {
            await hub.JoinRoom(room_id);

            // ensure the same user can't join a room if already in a room.
            await Assert.ThrowsAsync<UserAlreadyInMultiplayerRoom>(() => hub.JoinRoom(room_id));

            // but can join once first leaving.
            await hub.LeaveRoom();
            await hub.JoinRoom(room_id);

            await hub.LeaveRoom();
        }

        [Fact]
        public async Task LastUserLeavingCausesRoomDisband()
        {
            setUserContext(mockContextUser1);
            await hub.JoinRoom(room_id);

            setUserContext(mockContextUser2);
            await hub.JoinRoom(room_id);

            setUserContext(mockContextUser1);
            await hub.LeaveRoom();

            // room still exists even though the original host left
            Assert.True(hub.TryGetRoom(room_id, out var _));

            setUserContext(mockContextUser2);
            await hub.LeaveRoom();

            // room is gone.
            Assert.False(hub.TryGetRoom(room_id, out var _));
        }

        [Fact]
        public async Task UserCantLeaveWhenNotAlreadyJoined()
        {
            await Assert.ThrowsAsync<NotJoinedRoomException>(() => hub.LeaveRoom());
        }

        [Fact]
        public async Task UserJoinLeaveNotifiesOtherUsers()
        {
            await hub.JoinRoom(room_id); // join an arbitrary first user (listener).

            setUserContext(mockContextUser2);
            await hub.JoinRoom(room_id);
            await Assert.ThrowsAsync<UserAlreadyInMultiplayerRoom>(() => hub.JoinRoom(room_id)); // invalid join

            mockReceiver.Verify(r => r.UserJoined(new MultiplayerRoomUser(user_id_2)), Times.Once);

            await hub.LeaveRoom();
            mockReceiver.Verify(r => r.UserLeft(new MultiplayerRoomUser(user_id_2)), Times.Once);

            await hub.JoinRoom(room_id);
            mockReceiver.Verify(r => r.UserJoined(new MultiplayerRoomUser(user_id_2)), Times.Exactly(2));

            await hub.LeaveRoom();
            mockReceiver.Verify(r => r.UserLeft(new MultiplayerRoomUser(user_id_2)), Times.Exactly(2));
        }

        #endregion

        #region User State

        [Fact]
        public async Task UserStateChangeNotifiesOtherUsers()
        {
            await hub.JoinRoom(room_id);

            await hub.ChangeState(MultiplayerUserState.Ready);
            mockReceiver.Verify(r => r.UserStateChanged(user_id, MultiplayerUserState.Ready), Times.Once);
        }

        [Theory]
        [InlineData(MultiplayerUserState.WaitingForLoad)]
        [InlineData(MultiplayerUserState.Playing)]
        [InlineData(MultiplayerUserState.Results)]
        public async Task UserCantChangeStateToReservedStates(MultiplayerUserState reservedState)
        {
            await hub.JoinRoom(room_id);
            await Assert.ThrowsAsync<InvalidStateChange>(() => hub.ChangeState(reservedState));
        }

        [Fact]
        public async Task StandardMatchFlow()
        {
            var room = await hub.JoinRoom(room_id);
            Assert.All(room.Users, u => Assert.Equal(MultiplayerUserState.Idle, u.State));

            // some users enter a ready state.
            await hub.ChangeState(MultiplayerUserState.Ready);
            Assert.All(room.Users, u => Assert.Equal(MultiplayerUserState.Ready, u.State));

            // host requests the start of the match.
            await hub.StartMatch();

            // server requests the all users start loading.
            mockGameplayReceiver.Verify(r => r.LoadRequested(), Times.Once);
            Assert.All(room.Users, u => Assert.Equal(MultiplayerUserState.WaitingForLoad, u.State));

            // all users finish loading.
            await hub.ChangeState(MultiplayerUserState.Loaded);

            // server requests users start playing.
            mockReceiver.Verify(r => r.MatchStarted(), Times.Once);
            Assert.All(room.Users, u => Assert.Equal(MultiplayerUserState.Playing, u.State));

            // all users finish playing.
            await hub.ChangeState(MultiplayerUserState.FinishedPlay);

            // server lets players know that results are ready for consumption (all players have finished).
            mockReceiver.Verify(r => r.ResultsReady(), Times.Once);
            Assert.All(room.Users, u => Assert.Equal(MultiplayerUserState.Results, u.State));

            // players return back to idle state as they please.
            await hub.ChangeState(MultiplayerUserState.Idle);
            Assert.All(room.Users, u => Assert.Equal(MultiplayerUserState.Idle, u.State));
        }

        #endregion

        #region Room Settings

        [Fact]
        public async Task UserCantChangeSettingsWhenNotJoinedRoom()
        {
            await Assert.ThrowsAsync<NotJoinedRoomException>(() => hub.ChangeSettings(new MultiplayerRoomSettings()));
        }

        [Fact]
        public async Task RoomSettingsUpdateNotifiesOtherUsers()
        {
            MultiplayerRoomSettings testSettings = new MultiplayerRoomSettings
            {
                BeatmapID = 1234567,
                RulesetID = 2
            };

            await hub.JoinRoom(room_id);
            await hub.ChangeSettings(testSettings);

            mockReceiver.Verify(r => r.SettingsChanged(testSettings), Times.Once);
        }

        #endregion

        private void setUserContext(Mock<HubCallerContext> context) => hub.Context = context.Object;
    }
}
