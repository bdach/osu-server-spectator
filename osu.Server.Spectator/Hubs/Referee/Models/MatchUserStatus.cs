// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Text.Json.Serialization;
using osu.Game.Online.Multiplayer;

namespace osu.Server.Spectator.Hubs.Referee.Models
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum MatchUserStatus
    {
        [JsonStringEnumMemberName("idle")]
        Idle,

        [JsonStringEnumMemberName("ready")]
        Ready,

        [JsonStringEnumMemberName("playing")]
        Playing,

        [JsonStringEnumMemberName("finished_play")]
        FinishedPlay,

        [JsonStringEnumMemberName("spectating")]
        Spectating
    }

    public static class MatchUserStatusExtensions
    {
        public static MatchUserStatus? ToMatchUserStatus(this MultiplayerUserState state)
        {
            switch (state)
            {
                case MultiplayerUserState.Idle:
                    return MatchUserStatus.Idle;

                case MultiplayerUserState.Ready:
                    return MatchUserStatus.Ready;

                case MultiplayerUserState.Playing:
                    return MatchUserStatus.Playing;

                case MultiplayerUserState.FinishedPlay:
                    return MatchUserStatus.FinishedPlay;

                case MultiplayerUserState.Spectating:
                    return MatchUserStatus.Spectating;
            }

            return null;
        }
    }
}
