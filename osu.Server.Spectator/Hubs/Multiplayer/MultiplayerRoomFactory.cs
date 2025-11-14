// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using osu.Game.Online.Multiplayer;
using osu.Server.Spectator.Database;
using osu.Server.Spectator.Database.Models;

namespace osu.Server.Spectator.Hubs.Multiplayer
{
    public class MultiplayerRoomFactory : IMultiplayerRoomFactory
    {
        private readonly IDatabaseFactory databaseFactory;
        private readonly ChatFilters filters;

        public MultiplayerRoomFactory(
            IDatabaseFactory databaseFactory,
            ChatFilters filters)
        {
            this.databaseFactory = databaseFactory;
            this.filters = filters;
        }

        public async Task<long> CreateRoomAsync(int hostId, MultiplayerRoom room)
        {
            var dbRoom = new multiplayer_room
            {
                name = await filters.FilterAsync(room.Settings.Name),
                starts_at = DateTimeOffset.Now,
                type = room.Settings.MatchType.ToDatabaseMatchType(),
                queue_mode = room.Settings.QueueMode.ToDatabaseQueueMode(),
                auto_start_duration = (ushort)room.Settings.AutoStartDuration.TotalSeconds,
                auto_skip = room.Settings.AutoSkip,
                user_id = hostId,
                password = room.Settings.Password,
                ends_at = DateTimeOffset.Now.AddSeconds(30)
            };

            if (dbRoom.type == database_match_type.playlists)
                throw new InvalidOperationException("Cannot create playlists via spectator server.");

            // TODO: port the entirety of `assertValidStartGame()`

            var playlistItems = new List<multiplayer_playlist_item>();

            foreach (var item in room.Playlist)
            {
                // use -1 as room ID for now - will be associated correctly with the room in the `IDatabaseAccess` operation below
                var dbItem = new multiplayer_playlist_item(-1, item) { owner_id = hostId };
                playlistItems.Add(dbItem);
            }

            if (playlistItems.Count < 1)
                throw new InvalidStateException("Room must have at least one playlist item.");

            if (dbRoom.type != database_match_type.matchmaking && playlistItems.Count != 1)
                throw new InvalidStateException("Realtime room must have exactly one playlist item.");

            if (dbRoom.name.Length > 100)
                throw new InvalidStateException("Room name is too long.");

            // TODO: port `PlaylistItem::assertBeatmapsExist()`

            using (var db = databaseFactory.GetInstance())
                dbRoom.id = await db.CreateRoomAsync(dbRoom, playlistItems);
            // TODO: figure out what about multiplayer channels
            // (probably have them created by LIO still, just outside of criticality?)

            return dbRoom.id;
        }
    }
}
