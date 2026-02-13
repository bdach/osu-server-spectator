// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Text.Json.Serialization;
using osu.Game.Online.Multiplayer;

namespace osu.Server.Spectator.Hubs.Referee.Models.Events
{
    public class CountdownStoppedEvent
    {
        [JsonPropertyName("room_id")]
        public long RoomId { get; set; }

        [JsonPropertyName("countdown_id")]
        public long CountdownId { get; set; }

        [JsonPropertyName("type")]
        public CountdownType Type { get; set; }

        [JsonConstructor]
        public CountdownStoppedEvent()
        {
        }

        public static CountdownStoppedEvent? Create(long roomId, MultiplayerCountdown countdown)
        {
            var result = new CountdownStoppedEvent
            {
                RoomId = roomId,
                CountdownId = countdown.ID,
            };

            switch (countdown)
            {
                case MatchStartCountdown:
                    result.Type = CountdownType.MatchStart;
                    return result;

                case ServerShuttingDownCountdown:
                    result.Type = CountdownType.ServerShuttingDown;
                    return result;

                default:
                    return null;
            }
        }
    }
}
