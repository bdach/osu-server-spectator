// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Text.Json.Serialization;

namespace osu.Server.Spectator.Hubs.Referee.Models
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum MatchType
    {
        [JsonStringEnumMemberName("head_to_head")]
        HeadToHead = Game.Online.Rooms.MatchType.HeadToHead,

        [JsonStringEnumMemberName("team_versus")]
        TeamVersus = Game.Online.Rooms.MatchType.TeamVersus,
    }
}
