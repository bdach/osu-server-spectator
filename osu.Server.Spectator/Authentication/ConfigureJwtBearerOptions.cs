// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;
using osu.Server.Spectator.Database;

namespace osu.Server.Spectator.Authentication
{
    public class ConfigureJwtBearerOptions : IConfigureNamedOptions<JwtBearerOptions>
    {
        // TODO: specify this as the authentication method in the other hubs
        public const string LAZER_CLIENT_SCHEME = "lazer";
        public const string REFEREE_DELEGATION_SCHEME = "referee-delegation";
        public const string REFEREE_AUTH_CODE_SCHEME = "referee-auth-code";

        private readonly IDatabaseFactory databaseFactory;
        private readonly ILogger<ConfigureJwtBearerOptions> logger;

        public ConfigureJwtBearerOptions(IDatabaseFactory databaseFactory, ILoggerFactory loggerFactory)
        {
            this.databaseFactory = databaseFactory;
            logger = loggerFactory.CreateLogger<ConfigureJwtBearerOptions>();
        }

        public void Configure(JwtBearerOptions options)
            => throw new NotSupportedException();

        public void Configure(string? name, JwtBearerOptions options)
        {
            switch (name)
            {
                case LAZER_CLIENT_SCHEME:
                    configureLazerScheme(options);
                    return;

                case REFEREE_DELEGATION_SCHEME:
                    configureRefereeDelegationScheme(options);
                    return;

                case REFEREE_AUTH_CODE_SCHEME:
                    configureRefereeAuthenticationCodeScheme(options);
                    return;

                default:
                    throw new NotSupportedException($"Scheme {name} is not supported");
            }
        }

        private void configureLazerScheme(JwtBearerOptions options)
        {
            var rsa = getKeyProvider();

            options.TokenValidationParameters = new TokenValidationParameters
            {
                IssuerSigningKey = new RsaSecurityKey(rsa),
                ValidAudience = "5", // should match the client ID assigned to osu! in the osu-web target deploy.
                // TODO: figure out why this isn't included in the token.
                ValidateIssuer = false,
                ValidIssuer = "https://osu.ppy.sh/",
            };

            options.Events = new JwtBearerEvents
            {
                OnTokenValidated = async context =>
                {
                    var jwtToken = (JsonWebToken)context.SecurityToken;
                    int tokenUserId = int.Parse(jwtToken.Subject);

                    using (var db = databaseFactory.GetInstance())
                    {
                        // check expiry/revocation against database
                        var userId = await db.GetUserIdFromTokenAsync(jwtToken);

                        if (userId != tokenUserId)
                        {
                            logger.LogInformation("Token revoked or expired");
                            context.Fail("Token has expired or been revoked");
                        }
                    }
                },
            };
        }

        private void configureRefereeDelegationScheme(JwtBearerOptions options)
        {
            var rsa = getKeyProvider();

            options.TokenValidationParameters = new TokenValidationParameters
            {
                IssuerSigningKey = new RsaSecurityKey(rsa),
                // there could be multiple valid audiences here, so we're not checking.
                // TODO: maybe rethink that later.
                ValidateAudience = false,
                // TODO: figure out why this isn't included in the token.
                ValidateIssuer = false,
                ValidIssuer = "https://osu.ppy.sh/",
            };

            options.Events = new JwtBearerEvents
            {
                OnAuthenticationFailed = e =>
                {
                    return Task.CompletedTask;
                },
                OnTokenValidated = async context =>
                {
                    var jwtToken = (JsonWebToken)context.SecurityToken;

                    using (var db = databaseFactory.GetInstance())
                    {
                        // check expiry/revocation against database
                        var userId = await db.GetResourceOwnerIdFromDelegationTokenAsync(jwtToken);

                        if (userId == null)
                        {
                            // TODO: this is metadata that is not in the token and has to be fetched externally.
                            // because of this the token is rejected by the authorization middleware *before* we ever arrive here.
                            // to fix this we either have to bolt on the user ID in `OnMessageReceived`,
                            // or get web to put a claim in the token that will indicate that the token gives permission to act on behalf of the resource owner.
                            logger.LogInformation("Token revoked or expired");
                            context.Fail("Token has expired or been revoked");
                        }
                    }
                },
            };
        }

        private void configureRefereeAuthenticationCodeScheme(JwtBearerOptions options)
        {
            var rsa = getKeyProvider();

            options.TokenValidationParameters = new TokenValidationParameters
            {
                IssuerSigningKey = new RsaSecurityKey(rsa),
                // there could be multiple valid audiences here, so we're not checking.
                // TODO: maybe rethink that later.
                ValidateAudience = false,
                // TODO: figure out why this isn't included in the token.
                ValidateIssuer = false,
                ValidIssuer = "https://osu.ppy.sh/",
            };

            options.Events = new JwtBearerEvents
            {
                OnTokenValidated = async context =>
                {
                    var jwtToken = (JsonWebToken)context.SecurityToken;
                    int tokenUserId = int.Parse(jwtToken.Subject);

                    using (var db = databaseFactory.GetInstance())
                    {
                        // check expiry/revocation against database
                        var userId = await db.GetUserIdFromTokenAsync(jwtToken);

                        if (userId != tokenUserId)
                        {
                            logger.LogInformation("Token revoked or expired");
                            context.Fail("Token has expired or been revoked");
                        }
                    }
                },
            };
        }

        /// <summary>
        /// borrowed from https://stackoverflow.com/a/54323524
        /// </summary>
        private static RSACryptoServiceProvider getKeyProvider()
        {
            string key = File.ReadAllText("oauth-public.key");

            key = key.Replace("-----BEGIN PUBLIC KEY-----", "");
            key = key.Replace("-----END PUBLIC KEY-----", "");
            key = key.Replace("\n", "");

            var keyBytes = Convert.FromBase64String(key);

            var asymmetricKeyParameter = PublicKeyFactory.CreateKey(keyBytes);
            var rsaKeyParameters = (RsaKeyParameters)asymmetricKeyParameter;
            var rsaParameters = new RSAParameters
            {
                Modulus = rsaKeyParameters.Modulus.ToByteArrayUnsigned(),
                Exponent = rsaKeyParameters.Exponent.ToByteArrayUnsigned()
            };

            var rsa = new RSACryptoServiceProvider();
            rsa.ImportParameters(rsaParameters);

            return rsa;
        }
    }
}
