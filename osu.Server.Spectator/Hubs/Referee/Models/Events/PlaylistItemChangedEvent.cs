// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
using System.Text.Json.Serialization;
using osu.Game.Online.Rooms;

namespace osu.Server.Spectator.Hubs.Referee.Models.Events
{
    public class PlaylistItemChangedEvent
    {
        [JsonPropertyName("room_id")]
        public long RoomId { get; set; }

        [JsonPropertyName("playlist_item_id")]
        public long PlaylistItemId { get; set; }

        [JsonPropertyName("ruleset_id")]
        public int RulesetId { get; set; }

        [JsonPropertyName("beatmap_id")]
        public int BeatmapId { get; set; }

        [JsonPropertyName("required_mods")]
        public Mod[] RequiredMods { get; set; } = [];

        [JsonPropertyName("allowed_mods")]
        public Mod[] AllowedMods { get; set; } = [];

        [JsonPropertyName("freestyle")]
        public bool Freestyle { get; set; }

        [JsonPropertyName("expired")]
        public bool Expired { get; set; }

        [JsonConstructor]
        public PlaylistItemChangedEvent()
        {
        }

        public PlaylistItemChangedEvent(long roomId, MultiplayerPlaylistItem item)
        {
            RoomId = roomId;
            PlaylistItemId = item.ID;
            RulesetId = item.RulesetID;
            BeatmapId = item.BeatmapID;
            RequiredMods = item.RequiredMods.Select(Mod.FromAPIMod).ToArray();
            AllowedMods = item.AllowedMods.Select(Mod.FromAPIMod).ToArray();
            Freestyle = item.Freestyle;
            Expired = item.Expired;
        }
    }
}
