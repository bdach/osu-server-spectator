// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Text.Json.Serialization;
using osu.Game.Online.API;

namespace osu.Server.Spectator.Hubs.Referee.Models.Events
{
    public class UserModsChangedEvent
    {
        [JsonPropertyName("room_id")]
        public long RoomId { get; set; }

        [JsonPropertyName("user_id")]
        public int UserId { get; set; }

        [JsonPropertyName("mods")]
        public APIMod[] UserMods { get; set; } = [];
    }
}
