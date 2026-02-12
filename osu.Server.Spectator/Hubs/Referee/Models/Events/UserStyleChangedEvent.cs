// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Text.Json.Serialization;

namespace osu.Server.Spectator.Hubs.Referee.Models.Events
{
    public class UserStyleChangedEvent
    {
        [JsonPropertyName("room_id")]
        public long RoomId { get; set; }

        [JsonPropertyName("user_id")]
        public int UserId { get; set; }

        [JsonPropertyName("beatmap_id")]
        public int? BeatmapId { get; set; }

        [JsonPropertyName("ruleset_id")]
        public int? RulesetId { get; set; }
    }
}
