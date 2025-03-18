// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using osu.Server.Spectator.Database;
using osu.Server.Spectator.Database.Models;

namespace osu.Server.Spectator.Hubs.Multiplayer
{
    public class MultiplayerEventLogger
    {
        private readonly IDatabaseFactory databaseFactory;
        private readonly ILogger<MultiplayerEventLogger> logger;

        public MultiplayerEventLogger(
            ILoggerFactory loggerFactory,
            IDatabaseFactory databaseFactory)
        {
            logger = loggerFactory.CreateLogger<MultiplayerEventLogger>();
            this.databaseFactory = databaseFactory;
        }

        public Task LogRoomCreatedAsync(long roomId, int userId) => logEvent(new multiplayer_room_event
        {
            event_type = multiplayer_room_event_type.room_created,
            room_id = roomId,
            user_id = userId,
        });

        public Task LogRoomDisbandedAsync(long roomId, int userId) => logEvent(new multiplayer_room_event
        {
            event_type = multiplayer_room_event_type.room_disbanded,
            room_id = roomId,
            user_id = userId,
        });

        public Task LogPlayerJoinedAsync(long roomId, int userId) => logEvent(new multiplayer_room_event
        {
            event_type = multiplayer_room_event_type.player_joined,
            user_id = userId,
            room_id = roomId,
        });

        public Task LogPlayerLeftAsync(long roomId, int userId) => logEvent(new multiplayer_room_event
        {
            event_type = multiplayer_room_event_type.player_left,
            user_id = userId,
            room_id = roomId,
        });

        public Task LogPlayerKickedAsync(long roomId, int userId) => logEvent(new multiplayer_room_event
        {
            event_type = multiplayer_room_event_type.player_kicked,
            user_id = userId,
            room_id = roomId,
        });

        public Task LogHostChangedAsync(long roomId, int userId) => logEvent(new multiplayer_room_event
        {
            event_type = multiplayer_room_event_type.host_changed,
            user_id = userId,
            room_id = roomId,
        });

        public Task LogGameStartedAsync(long roomId, long playlistItemId, RoomState roomState) => logEvent(new multiplayer_room_event
        {
            event_type = multiplayer_room_event_type.game_started,
            playlist_item_id = playlistItemId,
            room_id = roomId,
            RoomState = roomState
        });

        public Task LogGameAbortedAsync(long roomId, long playlistItemId) => logEvent(new multiplayer_room_event
        {
            event_type = multiplayer_room_event_type.game_aborted,
            playlist_item_id = playlistItemId,
            room_id = roomId,
        });

        private async Task logEvent(multiplayer_room_event ev)
        {
            try
            {
                using var db = databaseFactory.GetInstance();
                await db.RecordRoomEventAsync(ev);
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Failed to log multiplayer room event to database");
            }
        }
    }
}
