// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Text.Json.Serialization;

namespace osu.Server.Spectator.Hubs.Referee.Models
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum CountdownType
    {
        [JsonStringEnumMemberName("match_start")]
        MatchStart,

        [JsonStringEnumMemberName("server_shutting_down")]
        ServerShuttingDown,
    }
}
