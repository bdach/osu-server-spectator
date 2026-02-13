// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Text.Json.Serialization;
using osu.Server.Spectator.Database.Models;

namespace osu.Server.Spectator.Hubs.Referee.Models
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum MatchTeam
    {
        [JsonStringEnumMemberName("blue")]
        Blue = room_team.blue,

        [JsonStringEnumMemberName("red")]
        Red = room_team.red,
    }
}
