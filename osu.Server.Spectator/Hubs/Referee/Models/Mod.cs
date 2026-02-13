// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using osu.Game.Online.API;

namespace osu.Server.Spectator.Hubs.Referee.Models
{
    public class Mod
    {
        [JsonPropertyName("acronym")]
        public string Acronym { get; set; } = string.Empty;

        [JsonPropertyName("settings")]
        public Dictionary<string, object> Settings { get; set; } = [];

        public static Mod FromAPIMod(APIMod mod) => new Mod
        {
            Acronym = mod.Acronym,
            Settings = mod.Settings
        };

        public APIMod ToAPIMod() => new APIMod
        {
            Acronym = Acronym,
            Settings = Settings.ToDictionary<KeyValuePair<string, object>, string, object>(
                kv => kv.Key,
                kv =>
                {
                    switch (kv.Value)
                    {
                        case string s: return s;

                        case bool b: return b;

                        case int i: return i;

                        case double d: return d;

                        case null: return null!;

                        case JsonElement element:
                        {
                            switch (element.ValueKind)
                            {
                                case JsonValueKind.String: return element.GetString()!;

                                case JsonValueKind.Number: return element.GetDouble();

                                case JsonValueKind.True: return true;

                                case JsonValueKind.False: return false;

                                case JsonValueKind.Null: return null!;
                            }

                            break;
                        }
                    }

                    throw new ArgumentOutOfRangeException(nameof(kv.Value), kv.Value, "Unsupported value");
                })
        };
    }
}
