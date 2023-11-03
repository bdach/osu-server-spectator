// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Security.Claims;

namespace osu.Server.Spectator.Extensions
{
    public static class JsonWebTokenExtensions
    {
        /// <summary>
        /// Returns the JWT ID (value of <c>jti</c> claim) from the JWT represented by the
        /// <paramref name="claimsPrincipal"/> instance.
        /// </summary>
        /// <exception cref="InvalidOperationException">The <c>jti</c> claim was not found.</exception>
        public static string GetJwtId(this ClaimsPrincipal claimsPrincipal)
            => claimsPrincipal.FindFirst(claim => claim.Type == "jti")?.Value
               ?? throw new InvalidOperationException("The jti claim could not be found.");
    }
}
