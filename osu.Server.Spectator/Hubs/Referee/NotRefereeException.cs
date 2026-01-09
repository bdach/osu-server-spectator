// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using Microsoft.AspNetCore.SignalR;

namespace osu.Server.Spectator.Hubs.Referee
{
    [Serializable]
    public class NotRefereeException : HubException
    {
        public NotRefereeException()
            : base("User is attempting to perform a referee level operation while not a referee of the room")
        {
        }
    }
}
