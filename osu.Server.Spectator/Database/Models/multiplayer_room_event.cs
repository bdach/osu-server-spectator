// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;

namespace osu.Server.Spectator.Database.Models
{
    // ReSharper disable InconsistentNaming

    [Serializable]
    public enum multiplayer_room_event_type
    {
        player_left = 1,
        player_joined,
        player_kicked,
        room_created,
        room_disbanded,
        game_started,
        game_aborted,
        host_changed,
    }

    public class multiplayer_room_event
    {
        public long event_id { get; set; }
        public long room_id { get; set; }
        public multiplayer_room_event_type event_type { get; set; }
        public long? playlist_item_id { get; set; }
        public long? user_id { get; set; }
    }
}
