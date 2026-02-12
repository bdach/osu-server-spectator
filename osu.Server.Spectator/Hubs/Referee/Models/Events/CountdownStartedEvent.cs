// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Text.Json.Serialization;

namespace osu.Server.Spectator.Hubs.Referee.Models.Events
{
    public class CountdownStartedEvent
    {
        [JsonPropertyName("room_id")]
        public long RoomId { get; set; }

        [JsonPropertyName("countdown_id")]
        public long CountdownId { get; set; }

        [JsonPropertyName("seconds")]
        public double Seconds { get; set; }

        [JsonConstructor]
        public CountdownStartedEvent()
        {
        }

        public CountdownStartedEvent(long roomId, Game.Online.Multiplayer.Countdown.CountdownStartedEvent ev)
        {
            RoomId = roomId;
            CountdownId = ev.Countdown.ID;
            Seconds = ev.Countdown.TimeRemaining.TotalSeconds;
        }
    }
}
