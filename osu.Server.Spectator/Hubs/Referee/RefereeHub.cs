// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using osu.Game.Online.API;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Online.Multiplayer;
using osu.Game.Online.Multiplayer.Countdown;
using osu.Game.Online.Rooms;
using osu.Server.Spectator.Authentication;
using osu.Server.Spectator.Database;
using osu.Server.Spectator.Hubs.Multiplayer;

namespace osu.Server.Spectator.Hubs.Referee
{
    [Authorize(ConfigureJwtBearerOptions.REFEREE_AUTH_CODE_SCHEME)]
    public class RefereeHub : Hub
    {
        private readonly IDatabaseFactory db;
        private readonly IMultiplayerRefereeHubContext multiplayerHubContext;

        public RefereeHub(IMultiplayerRefereeHubContext multiplayerHubContext, IDatabaseFactory db)
        {
            this.multiplayerHubContext = multiplayerHubContext;
            this.db = db;
        }

        public async Task Ping(string payload)
        {
            await Clients.Caller.SendAsync("Pong", payload);
        }

        public override async Task OnConnectedAsync()
        {
            await base.OnConnectedAsync();
            await multiplayerHubContext.InitialiseUserState(Context);
        }

        public async Task<long> MakeRoom(int rulesetId, int beatmapId, string roomName)
        {
            var room = new MultiplayerRoom(new Room
            {
                Name = roomName,
                Password = Guid.NewGuid().ToString(),
                Type = MatchType.HeadToHead,
                QueueMode = QueueMode.HostOnly,
                Playlist =
                [
                    new PlaylistItem(new APIBeatmap { OnlineID = beatmapId }).With(ruleset: rulesetId)
                ]
            });
            var created = await multiplayerHubContext.CreateRoom(Context, room);
            return created.RoomID;
        }

        public async Task CloseRoom(long roomId)
        {
            await multiplayerHubContext.CloseRoom(Context, roomId);
        }

        public async Task SetRoomName(long roomId, string roomName)
            => await multiplayerHubContext.ChangeSettings(Context, roomId, room => room.Name = roomName);

        public async Task SetRoomPassword(long roomId, string password)
            => await multiplayerHubContext.ChangeSettings(Context, roomId, room => room.Password = password);

        public async Task SetMatchType(long roomId, MatchType matchType)
            => await multiplayerHubContext.ChangeSettings(Context, roomId, room => room.MatchType = matchType);

        public async Task InvitePlayer(long roomId, int userId)
            => await multiplayerHubContext.InvitePlayer(Context, roomId, userId);

        public async Task SetHost(long roomId, int userId)
            => await multiplayerHubContext.TransferHost(Context, roomId, userId);

        public async Task KickUser(long roomId, int userId)
            => await multiplayerHubContext.KickUser(Context, roomId, userId);

        public async Task SetBeatmap(long roomId, int beatmapId, int? rulesetId)
        {
            using var connection = db.GetInstance();
            var databaseBeatmap = await connection.GetBeatmapAsync(beatmapId);

            if (databaseBeatmap == null)
                throw new HubException($"Beatmap with id {beatmapId} not found.");

            await multiplayerHubContext.EditCurrentPlaylistItem(Context, roomId, item =>
            {
                item.BeatmapID = beatmapId;
                // TODO: this is a bit stupid...
                item.BeatmapChecksum = databaseBeatmap.checksum ?? string.Empty;
                item.RulesetID = rulesetId ?? item.RulesetID;
            });
        }

        // TODO: mod settings don't work as they should here, likely due to serialisation foibles.
        // maybe we want to be accepting string blobs here and only deserialising on c# side....
        public async Task SetRequiredMods(long roomId, APIMod[] mods)
            => await multiplayerHubContext.EditCurrentPlaylistItem(Context, roomId, item => item.RequiredMods = mods);

        public async Task SetAllowedMods(long roomId, APIMod[] mods)
            => await multiplayerHubContext.EditCurrentPlaylistItem(Context, roomId, item => item.AllowedMods = mods);

        public async Task SetFreestyle(long roomId, bool enabled)
            => await multiplayerHubContext.EditCurrentPlaylistItem(Context, roomId, item => item.Freestyle = enabled);

        public async Task StartGameplay(long roomId, int? countdown)
        {
            if (countdown == null)
                await multiplayerHubContext.StartMatch(Context, roomId);
            else
                await multiplayerHubContext.StartMatchCountdown(Context, roomId, new StartMatchCountdownRequest { Duration = TimeSpan.FromSeconds(countdown.Value) });
        }

        public async Task AbortGameplayCountdown(long roomId)
            => await multiplayerHubContext.StopMatchCountdown(Context, roomId);

        public async Task AbortGameplay(long roomId)
            => await multiplayerHubContext.AbortMatch(Context, roomId);

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            await multiplayerHubContext.CleanUpUserState(Context);
            await base.OnDisconnectedAsync(exception);
        }
    }
}
