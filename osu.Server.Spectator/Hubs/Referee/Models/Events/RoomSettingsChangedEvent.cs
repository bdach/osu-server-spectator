// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Text.Json.Serialization;
using osu.Game.Online.Multiplayer;

namespace osu.Server.Spectator.Hubs.Referee.Models.Events
{
    public class RoomSettingsChangedEvent
    {
        [JsonPropertyName("room_id")]
        public long RoomId { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("password")]
        public string Password { get; set; } = string.Empty;

        [JsonPropertyName("type")]
        public MatchType Type { get; set; }

        [JsonPropertyName("playlist_item_id")]
        public long PlaylistItemId { get; set; }

        [JsonConstructor]
        public RoomSettingsChangedEvent()
        {
        }

        public RoomSettingsChangedEvent(long roomId, MultiplayerRoomSettings settings)
        {
            RoomId = roomId;
            Name = settings.Name;
            Password = settings.Password;
            Type = (MatchType)settings.MatchType;
            PlaylistItemId = settings.PlaylistItemId;
        }
    }
}
