// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using osu.Game.Online.API;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Online.Multiplayer;
using osu.Game.Online.Multiplayer.Countdown;
using osu.Game.Online.Rooms;
using osu.Server.Spectator.Database;
using osu.Server.Spectator.Hubs.Multiplayer;

namespace osu.Server.Spectator.Hubs.Referee
{
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
            await Groups.AddToGroupAsync(Context.ConnectionId, MultiplayerHub.GetGroupId(created.RoomID));
            return created.RoomID;
        }

        // TODO: currently this is all still beholden to the "user can only be in one room at a time" principle
        // rolling with it for now just to see all of this do something, but eventually this will have to change
        public async Task<long> CloseRoom()
        {
            long roomId = await multiplayerHubContext.CloseRoom(Context);
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, MultiplayerHub.GetGroupId(roomId));
            return roomId;
        }

        public async Task SetRoomName(string roomName)
            => await multiplayerHubContext.ChangeSettings(Context, room => room.Name = roomName);

        public async Task SetRoomPassword(string password)
            => await multiplayerHubContext.ChangeSettings(Context, room => room.Password = password);

        public async Task SetMatchType(MatchType matchType)
            => await multiplayerHubContext.ChangeSettings(Context, room => room.MatchType = matchType);

        public async Task InvitePlayer(int userId)
            => await multiplayerHubContext.InvitePlayer(Context, userId);

        public async Task SetHost(int userId)
            => await multiplayerHubContext.TransferHost(Context, userId);

        public async Task KickUser(int userId)
            => await multiplayerHubContext.KickUser(Context, userId);

        public async Task SetBeatmap(int beatmapId, int? rulesetId)
        {
            using var connection = db.GetInstance();
            var databaseBeatmap = await connection.GetBeatmapAsync(beatmapId);

            if (databaseBeatmap == null)
                throw new HubException($"Beatmap with id {beatmapId} not found.");

            await multiplayerHubContext.EditCurrentPlaylistItem(Context, item =>
            {
                item.BeatmapID = beatmapId;
                // TODO: this is a bit stupid...
                item.BeatmapChecksum = databaseBeatmap.checksum ?? string.Empty;
                item.RulesetID = rulesetId ?? item.RulesetID;
            });
        }

        // TODO: mod settings don't work as they should here, likely due to serialisation foibles.
        // maybe we want to be accepting string blobs here and only deserialising on c# side....
        public async Task SetRequiredMods(APIMod[] mods)
            => await multiplayerHubContext.EditCurrentPlaylistItem(Context, item => item.RequiredMods = mods);

        public async Task SetAllowedMods(APIMod[] mods)
            => await multiplayerHubContext.EditCurrentPlaylistItem(Context, item => item.AllowedMods = mods);

        public async Task SetFreestyle(bool enabled)
            => await multiplayerHubContext.EditCurrentPlaylistItem(Context, item => item.Freestyle = enabled);

        public async Task StartGameplay(int? countdown)
        {
            if (countdown == null)
                await multiplayerHubContext.StartMatch(Context);
            else
                await multiplayerHubContext.StartMatchCountdown(Context, new StartMatchCountdownRequest { Duration = TimeSpan.FromSeconds(countdown.Value) });
        }

        public async Task AbortGameplayCountdown()
            => await multiplayerHubContext.StopMatchCountdown(Context);

        public async Task AbortGameplay()
            => await multiplayerHubContext.AbortMatch(Context);

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            await multiplayerHubContext.CleanUpUserState(Context);
            await base.OnDisconnectedAsync(exception);
        }
    }
}
