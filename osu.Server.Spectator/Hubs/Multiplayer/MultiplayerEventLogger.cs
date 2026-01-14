// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using osu.Server.Spectator.Database;
using osu.Server.Spectator.Database.Models;
using osu.Server.Spectator.Hubs.Referee;

namespace osu.Server.Spectator.Hubs.Multiplayer
{
    public class MultiplayerEventNotifier : IMultiplayerRoomEventNotifier, IMatchmakingEventNotifier
    {
        private readonly IDatabaseFactory databaseFactory;
        private readonly RefereeHubContext refereeHubContext;
        private readonly ILogger<MultiplayerEventNotifier> logger;

        public MultiplayerEventNotifier(
            ILoggerFactory loggerFactory,
            IDatabaseFactory databaseFactory,
            RefereeHubContext refereeHubContext)
        {
            logger = loggerFactory.CreateLogger<MultiplayerEventNotifier>();
            this.databaseFactory = databaseFactory;
            this.refereeHubContext = refereeHubContext;
        }

        #region IMultiplayerRoomEventNotifier

        public async Task OnRoomCreatedAsync(long roomId, int userId)
        {
            var ev = new multiplayer_realtime_room_event
            {
                event_type = "room_created",
                room_id = roomId,
                user_id = userId,
            };

            await logDatabaseEvent(ev);
            await refereeHubContext.NotifyRoomEvent(ev);
        }

        public async Task OnRoomDisbandedAsync(long roomId, int userId)
        {
            var ev = new multiplayer_realtime_room_event
            {
                event_type = "room_disbanded",
                room_id = roomId,
                user_id = userId,
            };

            await logDatabaseEvent(ev);
            await refereeHubContext.NotifyRoomEvent(ev);
        }

        public async Task OnPlayerJoinedAsync(long roomId, int userId)
        {
            var ev = new multiplayer_realtime_room_event
            {
                event_type = "player_joined",
                room_id = roomId,
                user_id = userId,
            };

            await logDatabaseEvent(ev);
            await refereeHubContext.NotifyRoomEvent(ev);
        }

        public async Task OnPlayerLeftAsync(long roomId, int userId)
        {
            var ev = new multiplayer_realtime_room_event
            {
                event_type = "player_left",
                room_id = roomId,
                user_id = userId,
            };

            await logDatabaseEvent(ev);
            await refereeHubContext.NotifyRoomEvent(ev);
        }

        public async Task OnPlayerKickedAsync(long roomId, int userId)
        {
            var ev = new multiplayer_realtime_room_event
            {
                event_type = "player_kicked",
                room_id = roomId,
                user_id = userId,
            };

            await logDatabaseEvent(ev);
            await refereeHubContext.NotifyRoomEvent(ev);
        }

        public async Task OnHostChangedAsync(long roomId, int userId)
        {
            var ev = new multiplayer_realtime_room_event
            {
                event_type = "host_changed",
                room_id = roomId,
                user_id = userId,
            };

            await logDatabaseEvent(ev);
            await refereeHubContext.NotifyRoomEvent(ev);
        }

        public async Task OnGameStartedAsync(long roomId, long playlistItemId, MatchStartedEventDetail details)
        {
            var ev = new multiplayer_realtime_room_event
            {
                event_type = "game_started",
                room_id = roomId,
                playlist_item_id = playlistItemId,
                event_detail = JsonConvert.SerializeObject(details)
            };

            await logDatabaseEvent(ev);
            await refereeHubContext.NotifyRoomEvent(ev);
        }

        public async Task OnGameAbortedAsync(long roomId, long playlistItemId)
        {
            var ev = new multiplayer_realtime_room_event
            {
                event_type = "game_aborted",
                room_id = roomId,
                playlist_item_id = playlistItemId,
            };

            await logDatabaseEvent(ev);
            await refereeHubContext.NotifyRoomEvent(ev);
        }

        public async Task OnGameCompletedAsync(long roomId, long playlistItemId)
        {
            await logDatabaseEvent(new multiplayer_realtime_room_event
            {
                event_type = "game_completed",
                room_id = roomId,
                playlist_item_id = playlistItemId,
            });
        }

        #endregion

        #region IMatchmakingEventNotifier

        public Task OnRoomCreatedAsync(long roomId, MatchmakingRoomCreatedEventDetail details) => logDatabaseEvent(new matchmaking_room_event
        {
            event_type = "room_created",
            room_id = roomId,
            event_detail = JsonConvert.SerializeObject(details)
        });

        /// <summary>
        /// Records a user joining a matchmaking room.
        /// </summary>
        Task IMatchmakingEventNotifier.OnPlayerJoinedAsync(long roomId, int userId) => logDatabaseEvent(new matchmaking_room_event
        {
            event_type = "user_join",
            room_id = roomId,
            user_id = userId
        });

        /// <summary>
        /// Records a user's individual beatmap selection.
        /// </summary>
        Task IMatchmakingEventNotifier.OnPlayerBeatmapPickAsync(long roomId, int userId, long playlistItemId) => logDatabaseEvent(new matchmaking_room_event
        {
            event_type = "user_pick",
            room_id = roomId,
            user_id = userId,
            playlist_item_id = playlistItemId
        });

        /// <summary>
        /// Records the final gameplay beatmap as selected by the server.
        /// </summary>
        Task IMatchmakingEventNotifier.OnFinalBeatmapSelectedAsync(long roomId, long playlistItemId) => logDatabaseEvent(new matchmaking_room_event
        {
            event_type = "gameplay_beatmap",
            room_id = roomId,
            playlist_item_id = playlistItemId
        });

        #endregion

        private async Task logDatabaseEvent(multiplayer_realtime_room_event ev)
        {
            try
            {
                using var db = databaseFactory.GetInstance();
                await db.LogRoomEventAsync(ev);
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Failed to log multiplayer room event to database");
            }
        }

        private async Task logDatabaseEvent(matchmaking_room_event ev)
        {
            try
            {
                using var db = databaseFactory.GetInstance();
                await db.LogRoomEventAsync(ev);
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Failed to log multiplayer room event to database");
            }
        }
    }
}
