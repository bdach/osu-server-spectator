// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Text.Json.Serialization;

namespace osu.Server.Spectator.Hubs.Referee.Models.Events
{
    public class CountdownStoppedEvent
    {
        [JsonPropertyName("room_id")]
        public long RoomId { get; set; }

        [JsonPropertyName("countdown_id")]
        public long CountdownId { get; set; }

        [JsonConstructor]
        public CountdownStoppedEvent()
        {
        }

        public CountdownStoppedEvent(long roomId, Game.Online.Multiplayer.Countdown.CountdownStoppedEvent ev)
        {
            RoomId = roomId;
            CountdownId = ev.ID;
        }
    }
}
