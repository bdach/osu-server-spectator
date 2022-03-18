// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using osu.Game.Online.Multiplayer;
using osu.Game.Online.Multiplayer.Countdown;
using osu.Server.Spectator.Hubs;
using Xunit;

namespace osu.Server.Spectator.Tests.Multiplayer
{
    public class MultiplayerCountdownTest : MultiplayerTest
    {
        [Fact]
        public async Task CannotStartCountdownIfNotReady()
        {
            await Hub.JoinRoom(ROOM_ID);

            await Assert.ThrowsAsync<InvalidStateException>(async () => await Hub.SendMatchRequest(new StartMatchCountdownRequest { Delay = TimeSpan.FromMinutes(1) }));

            using (var usage = Hub.GetRoom(ROOM_ID))
            {
                var room = usage.Item;
                Debug.Assert(room != null);

                Assert.Null(room.Countdown);
                Receiver.Verify(r => r.MatchEvent(It.IsAny<CountdownChangedEvent>()), Times.Never);
            }
        }

        [Fact]
        public async Task GameplayStartsWhenCountdownEnds()
        {
            await Hub.JoinRoom(ROOM_ID);
            await Hub.ChangeState(MultiplayerUserState.Ready);

            await Hub.SendMatchRequest(new StartMatchCountdownRequest { Delay = TimeSpan.FromMinutes(1) });
            waitForCountingDown();

            using (var usage = Hub.GetRoom(ROOM_ID))
            {
                var room = usage.Item;
                Debug.Assert(room != null);

                Assert.NotNull(room.Countdown);
                Assert.InRange((room.Countdown!.EndTime - DateTimeOffset.Now).Seconds, 30, 60);
                GameplayReceiver.Verify(r => r.LoadRequested(), Times.Never);
            }

            finishCountdown();

            using (var usage = Hub.GetRoom(ROOM_ID))
            {
                var room = usage.Item;
                Debug.Assert(room != null);

                Assert.Null(room.Countdown);
                GameplayReceiver.Verify(r => r.LoadRequested(), Times.Once);
            }
        }

        [Fact]
        public async Task GameplayDoesNotStartWhenCountdownCancelled()
        {
            await Hub.JoinRoom(ROOM_ID);
            await Hub.ChangeState(MultiplayerUserState.Ready);

            await Hub.SendMatchRequest(new StartMatchCountdownRequest { Delay = TimeSpan.FromMinutes(1) });
            await Hub.SendMatchRequest(new StopCountdownRequest());

            finishCountdown();

            using (var usage = Hub.GetRoom(ROOM_ID))
            {
                var room = usage.Item;
                Debug.Assert(room != null);

                Assert.Null(room.Countdown);
                Receiver.Verify(r => r.MatchEvent(It.IsAny<CountdownChangedEvent>()), Times.Exactly(2));
                GameplayReceiver.Verify(r => r.LoadRequested(), Times.Never);
            }
        }

        [Fact]
        public async Task NewCountdownOverridesExisting()
        {
            await Hub.JoinRoom(ROOM_ID);
            await Hub.ChangeState(MultiplayerUserState.Ready);

            // Start first countdown.

            await Hub.SendMatchRequest(new StartMatchCountdownRequest { Delay = TimeSpan.FromMinutes(1) });
            waitForCountingDown();

            using (var usage = Hub.GetRoom(ROOM_ID))
            {
                var room = usage.Item;
                Debug.Assert(room != null);

                Assert.NotNull(room.Countdown);
                Receiver.Verify(r => r.MatchEvent(It.IsAny<CountdownChangedEvent>()), Times.Once);
            }

            // Start second countdown.

            MultiplayerCountdown? existingCountdown;

            using (var usage = Hub.GetRoom(ROOM_ID))
            {
                var room = usage.Item;
                Debug.Assert(room != null);

                existingCountdown = room.Countdown;
            }

            await Hub.SendMatchRequest(new StartMatchCountdownRequest { Delay = TimeSpan.FromMinutes(1) });

            // Wait for the second countdown to begin running.
            int attempts = 200;

            while (attempts-- > 0)
            {
                using (var usage = Hub.GetRoom(ROOM_ID))
                {
                    var room = usage.Item;
                    Debug.Assert(room != null);

                    if (room.Countdown != null && room.Countdown != existingCountdown)
                        break;
                }

                Thread.Sleep(10);
            }

            using (var usage = Hub.GetRoom(ROOM_ID))
            {
                var room = usage.Item;
                Debug.Assert(room != null);

                Assert.NotNull(room.Countdown);
                Receiver.Verify(r => r.MatchEvent(It.IsAny<CountdownChangedEvent>()), Times.Exactly(3));
                GameplayReceiver.Verify(r => r.LoadRequested(), Times.Never);
            }

            finishCountdown();

            using (var usage = Hub.GetRoom(ROOM_ID))
            {
                var room = usage.Item;
                Debug.Assert(room != null);

                Assert.Null(room.Countdown);
                Receiver.Verify(r => r.MatchEvent(It.IsAny<CountdownChangedEvent>()), Times.Exactly(4));
                GameplayReceiver.Verify(r => r.LoadRequested(), Times.Once);
            }
        }

        [Fact]
        public async Task CanNotStartCountdownDuringMatch()
        {
            await Hub.JoinRoom(ROOM_ID);
            await Hub.ChangeState(MultiplayerUserState.Ready);
            await Hub.StartMatch();

            await Assert.ThrowsAsync<InvalidStateException>(async () => await Hub.SendMatchRequest(new StartMatchCountdownRequest { Delay = TimeSpan.FromMinutes(1) }));
        }

        [Fact]
        public async Task CountdownStopsWhenAllUsersUnready()
        {
            await Hub.JoinRoom(ROOM_ID);
            await Hub.ChangeState(MultiplayerUserState.Ready);

            await Hub.SendMatchRequest(new StartMatchCountdownRequest { Delay = TimeSpan.FromMinutes(1) });
            waitForCountingDown();

            SetUserContext(ContextUser2);
            await Hub.JoinRoom(ROOM_ID);

            // Simulate a host transfer where no users remain ready.
            SetUserContext(ContextUser);
            await Hub.LeaveRoom();

            int attempts = 200;

            while (attempts-- > 0)
            {
                using (var usage = Hub.GetRoom(ROOM_ID))
                {
                    var room = usage.Item;
                    Debug.Assert(room != null);

                    if (!room.CountdownImplementation.IsRunning)
                        break;
                }
            }

            using (var usage = Hub.GetRoom(ROOM_ID))
            {
                var room = usage.Item;
                Debug.Assert(room != null);

                Assert.False(room.CountdownImplementation.IsRunning);
                Assert.Null(room.Countdown);
                Receiver.Verify(r => r.MatchEvent(It.IsAny<CountdownChangedEvent>()), Times.Exactly(2));
                GameplayReceiver.Verify(r => r.LoadRequested(), Times.Never);
            }
        }

        [Fact]
        public async Task CountdownStopsWhenHostUnreadies()
        {
            await Hub.JoinRoom(ROOM_ID);
            await Hub.ChangeState(MultiplayerUserState.Ready);
            await Hub.SendMatchRequest(new StartMatchCountdownRequest { Delay = TimeSpan.FromMinutes(1) });
            waitForCountingDown();

            SetUserContext(ContextUser2);
            await Hub.JoinRoom(ROOM_ID);
            await Hub.ChangeState(MultiplayerUserState.Ready);

            SetUserContext(ContextUser);
            await Hub.ChangeState(MultiplayerUserState.Idle);

            int attempts = 200;

            while (attempts-- > 0)
            {
                using (var usage = Hub.GetRoom(ROOM_ID))
                {
                    var room = usage.Item;
                    Debug.Assert(room != null);

                    if (!room.CountdownImplementation.IsRunning)
                        break;
                }
            }

            using (var usage = Hub.GetRoom(ROOM_ID))
            {
                var room = usage.Item;
                Debug.Assert(room != null);

                Assert.False(room.CountdownImplementation.IsRunning);
            }
        }

        [Fact]
        public async Task AutoStartCountdownDoesNotStartWithZeroDuration()
        {
            await Hub.JoinRoom(ROOM_ID);
            await Hub.ChangeState(MultiplayerUserState.Ready);

            using (var usage = Hub.GetRoom(ROOM_ID))
            {
                var room = usage.Item;
                Debug.Assert(room != null);

                Assert.False(room.CountdownImplementation.IsRunning);
            }
        }

        [Fact]
        public async Task AutoStartCountdownStartsAndStopsWithUserReadyStates()
        {
            await Hub.JoinRoom(ROOM_ID);
            await Hub.ChangeSettings(new MultiplayerRoomSettings { AutoStartDuration = TimeSpan.FromMinutes(1) });

            // First user readies up.
            await Hub.ChangeState(MultiplayerUserState.Ready);

            using (var usage = Hub.GetRoom(ROOM_ID))
            {
                var room = usage.Item;
                Debug.Assert(room != null);

                Assert.True(room.CountdownImplementation.IsRunning);
            }

            // Second user joins (not ready).
            SetUserContext(ContextUser2);
            await Hub.JoinRoom(ROOM_ID);

            using (var usage = Hub.GetRoom(ROOM_ID))
            {
                var room = usage.Item;
                Debug.Assert(room != null);

                Assert.True(room.CountdownImplementation.IsRunning);
            }

            // Second user readies up.
            await Hub.ChangeState(MultiplayerUserState.Ready);

            using (var usage = Hub.GetRoom(ROOM_ID))
            {
                var room = usage.Item;
                Debug.Assert(room != null);

                Assert.True(room.CountdownImplementation.IsRunning);
            }

            // First user unreadies.
            SetUserContext(ContextUser);
            await Hub.ChangeState(MultiplayerUserState.Idle);

            using (var usage = Hub.GetRoom(ROOM_ID))
            {
                var room = usage.Item;
                Debug.Assert(room != null);

                Assert.True(room.CountdownImplementation.IsRunning);
            }

            // Second user unreadies.
            SetUserContext(ContextUser2);
            await Hub.ChangeState(MultiplayerUserState.Idle);

            int attempts = 200;

            while (attempts-- > 0)
            {
                using (var usage = Hub.GetRoom(ROOM_ID))
                {
                    var room = usage.Item;
                    Debug.Assert(room != null);

                    if (!room.CountdownImplementation.IsRunning)
                        break;
                }
            }

            using (var usage = Hub.GetRoom(ROOM_ID))
            {
                var room = usage.Item;
                Debug.Assert(room != null);

                Assert.False(room.CountdownImplementation.IsRunning);
            }
        }

        [Fact]
        public async Task AutoStartCountdownCanNotBeCancelled()
        {
            await Hub.JoinRoom(ROOM_ID);
            await Hub.ChangeSettings(new MultiplayerRoomSettings { AutoStartDuration = TimeSpan.FromMinutes(1) });
            await Hub.ChangeState(MultiplayerUserState.Ready);
            waitForCountingDown();

            await Hub.SendMatchRequest(new StopCountdownRequest());

            int attempts = 200;

            while (attempts-- > 0)
            {
                using (var usage = Hub.GetRoom(ROOM_ID))
                {
                    var room = usage.Item;
                    Debug.Assert(room != null);

                    if (!room.CountdownImplementation.IsRunning)
                        break;
                }
            }

            using (var usage = Hub.GetRoom(ROOM_ID))
            {
                var room = usage.Item;
                Debug.Assert(room != null);

                Assert.True(room.CountdownImplementation.IsRunning);
            }
        }

        private void finishCountdown()
        {
            ServerMultiplayerRoom? room;

            using (var usage = Hub.GetRoom(ROOM_ID))
            {
                room = usage.Item;
                room?.CountdownImplementation.Finish();
            }

            Debug.Assert(room != null);

            int attempts = 200;
            while (attempts-- > 0 && room.CountdownImplementation.IsRunning)
                Thread.Sleep(10);
        }

        private void waitForCountingDown()
        {
            ServerMultiplayerRoom? room;

            using (var usage = Hub.GetRoom(ROOM_ID))
                room = usage.Item;

            Debug.Assert(room != null);

            int attempts = 200;
            while (attempts-- > 0 && room.Countdown == null)
                Thread.Sleep(10);
        }
    }
}
