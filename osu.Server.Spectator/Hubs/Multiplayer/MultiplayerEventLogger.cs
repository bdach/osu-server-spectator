// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using osu.Game.Online.API;
using osu.Game.Online.Matchmaking;
using osu.Game.Online.Multiplayer;
using osu.Game.Online.Rooms;
using osu.Server.Spectator.Database;
using osu.Server.Spectator.Database.Models;
using osu.Server.Spectator.Hubs.Referee;

namespace osu.Server.Spectator.Hubs.Multiplayer
{
    public class MultiplayerEventNotifier : IMultiplayerRoomEventNotifier, IMatchmakingEventNotifier
    {
        private readonly IDatabaseFactory databaseFactory;
        private readonly IHubContext<MultiplayerHub> multiplayerHubContext;
        private readonly IHubContext<RefereeHub> refereeHubContext;
        private readonly ILogger<MultiplayerEventNotifier> logger;

        public MultiplayerEventNotifier(
            ILoggerFactory loggerFactory,
            IDatabaseFactory databaseFactory,
            IHubContext<MultiplayerHub> multiplayerHubContext,
            IHubContext<RefereeHub> refereeHubContext)
        {
            logger = loggerFactory.CreateLogger<MultiplayerEventNotifier>();
            this.databaseFactory = databaseFactory;
            this.multiplayerHubContext = multiplayerHubContext;
            this.refereeHubContext = refereeHubContext;
        }

        #region IMultiplayerRoomEventNotifier

        public async Task SubscribePlayer(long roomId, string connectionId)
        {
            await multiplayerHubContext.Groups.AddToGroupAsync(connectionId, GetGroupId(roomId));
        }

        public async Task UnsubscribePlayer(long roomId, string connectionId)
        {
            await multiplayerHubContext.Groups.RemoveFromGroupAsync(connectionId, GetGroupId(roomId));
        }

        public async Task SubscribeReferee(long roomId, string connectionId)
        {
            await refereeHubContext.Groups.AddToGroupAsync(connectionId, GetGroupId(roomId));
        }

        public async Task UnsubscribeReferee(long roomId, string connectionId)
        {
            await refereeHubContext.Groups.RemoveFromGroupAsync(connectionId, GetGroupId(roomId));
        }

        public async Task OnRoomCreatedAsync(long roomId, int userId)
        {
            var ev = new multiplayer_realtime_room_event
            {
                event_type = "room_created",
                room_id = roomId,
                user_id = userId,
            };

            await logDatabaseEvent(ev);
            await refereeHubContext.Clients.Group(GetGroupId(ev.room_id)).SendAsync("RoomEventLogged", ev);
        }

        public async Task OnRoomStateChangedAsync(long roomId, MultiplayerRoomState state)
        {
            await multiplayerHubContext.Clients.Group(GetGroupId(roomId)).SendAsync(nameof(IMultiplayerClient.RoomStateChanged), state);
        }

        public async Task OnPlayerJoinedAsync(long roomId, MultiplayerRoomUser user)
        {
            var ev = new multiplayer_realtime_room_event
            {
                event_type = "player_joined",
                room_id = roomId,
                user_id = user.UserID,
            };

            await logDatabaseEvent(ev);
            await multiplayerHubContext.Clients.Group(GetGroupId(roomId)).SendAsync(nameof(IMultiplayerClient.UserJoined), ev);
            await refereeHubContext.Clients.Group(GetGroupId(ev.room_id)).SendAsync("RoomEventLogged", ev);
        }

        public async Task OnPlayerLeftAsync(long roomId, MultiplayerRoomUser user)
        {
            var ev = new multiplayer_realtime_room_event
            {
                event_type = "player_left",
                room_id = roomId,
                user_id = user.UserID,
            };

            await logDatabaseEvent(ev);
            await multiplayerHubContext.Clients.Group(GetGroupId(roomId)).SendAsync(nameof(IMultiplayerClient.UserLeft), user);
            await refereeHubContext.Clients.Group(GetGroupId(ev.room_id)).SendAsync("RoomEventLogged", ev);
        }

        public async Task OnPlayerKickedAsync(long roomId, MultiplayerRoomUser user)
        {
            var ev = new multiplayer_realtime_room_event
            {
                event_type = "player_kicked",
                room_id = roomId,
                user_id = user.UserID,
            };

            await logDatabaseEvent(ev);
            // the target user has already been removed from the group, so send the message to them separately.
            // TODO: the group management should probably be here too
            await multiplayerHubContext.Clients.User(user.UserID.ToString()).SendAsync(nameof(IMultiplayerClient.UserKicked), user);
            await multiplayerHubContext.Clients.Group(GetGroupId(roomId)).SendAsync(nameof(IMultiplayerClient.UserKicked), user);
            await refereeHubContext.Clients.Group(GetGroupId(ev.room_id)).SendAsync("RoomEventLogged", ev);
        }

        public async Task OnPlayerInvitedAsync(long roomId, int userId, int invitedBy, string password)
        {
            await multiplayerHubContext.Clients.User(userId.ToString()).SendAsync(nameof(IMultiplayerClient.Invited), invitedBy, roomId, password);
        }

        public async Task OnHostChangedAsync(long roomId, int userId)
        {
            var ev = new multiplayer_realtime_room_event
            {
                event_type = "host_changed",
                room_id = roomId,
                user_id = userId,
            };

            await logDatabaseEvent(ev);
            await multiplayerHubContext.Clients.Group(GetGroupId(roomId)).SendAsync(nameof(IMultiplayerClient.HostChanged), userId);
            await refereeHubContext.Clients.Group(GetGroupId(ev.room_id)).SendAsync("RoomEventLogged", ev);
        }

        public async Task OnSettingsChangedAsync(long roomId, MultiplayerRoomSettings settings)
        {
            await multiplayerHubContext.Clients.Group(GetGroupId(roomId)).SendAsync(nameof(IMultiplayerClient.SettingsChanged), settings);
        }

        public async Task OnUserStateChangedAsync(long roomId, int userId, MultiplayerUserState state)
        {
            await multiplayerHubContext.Clients.Group(GetGroupId(roomId)).SendAsync(nameof(IMultiplayerClient.UserStateChanged), userId, state);
        }

        public async Task OnMatchUserStateChangedAsync(long roomId, int userId, MatchUserState? userState)
        {
            await multiplayerHubContext.Clients.Group(GetGroupId(roomId)).SendAsync(nameof(IMultiplayerClient.MatchUserStateChanged), userId, userState);
        }

        public async Task OnMatchRoomStateChangedAsync(long roomId, MatchRoomState? state)
        {
            await multiplayerHubContext.Clients.Group(GetGroupId(roomId)).SendAsync(nameof(IMultiplayerClient.MatchRoomStateChanged), state);
        }

        public async Task OnNewMatchEventAsync(long roomId, MatchServerEvent e)
        {
            await multiplayerHubContext.Clients.Group(GetGroupId(roomId)).SendAsync(nameof(IMultiplayerClient.MatchEvent), e);
        }

        public async Task OnUserBeatmapAvailabilityChangedAsync(long roomId, int userId, BeatmapAvailability availability)
        {
            await multiplayerHubContext.Clients.Group(GetGroupId(roomId)).SendAsync(nameof(IMultiplayerClient.UserBeatmapAvailabilityChanged), userId, availability);
        }

        public async Task OnUserStyleChangedAsync(long roomId, int userId, int? beatmapId, int? rulesetId)
        {
            await multiplayerHubContext.Clients.Group(GetGroupId(roomId)).SendAsync(nameof(IMultiplayerClient.UserStyleChanged), userId, beatmapId, rulesetId);
        }

        public async Task OnUserModsChangedAsync(long roomId, int userId, IEnumerable<APIMod> mods)
        {
            await multiplayerHubContext.Clients.Group(GetGroupId(roomId)).SendAsync(nameof(IMultiplayerClient.UserModsChanged), userId, mods);
        }

        public async Task OnGameStartedAsync(long roomId, long playlistItemId, MatchStartedEventDetail details)
        {
            var ev = new multiplayer_realtime_room_event
            {
                event_type = "game_started",
                room_id = roomId,
                playlist_item_id = playlistItemId,
                event_detail = JsonConvert.SerializeObject(details)
            };

            await logDatabaseEvent(ev);
            await multiplayerHubContext.Clients.Group(GetGroupId(roomId)).SendAsync(nameof(IMultiplayerClient.LoadRequested));
            await refereeHubContext.Clients.Group(GetGroupId(ev.room_id)).SendAsync("RoomEventLogged", ev);
        }

        public async Task OnGameplayStartedAsync(long roomId, int userId)
        {
            await multiplayerHubContext.Clients.User(userId.ToString()).SendAsync(nameof(IMultiplayerClient.GameplayStarted));
        }

        public async Task OnGameAbortedAsync(long roomId, long playlistItemId, GameplayAbortReason? reason)
        {
            var ev = new multiplayer_realtime_room_event
            {
                event_type = "game_aborted",
                room_id = roomId,
                playlist_item_id = playlistItemId,
            };

            await logDatabaseEvent(ev);
            if (reason != null)
                await multiplayerHubContext.Clients.Group(GetGroupId(roomId)).SendAsync(nameof(IMultiplayerClient.GameplayAborted), reason);
            await refereeHubContext.Clients.Group(GetGroupId(ev.room_id)).SendAsync("RoomEventLogged", ev);
        }

        public async Task OnGameplayAbortedAsync(long roomId, int userId, GameplayAbortReason reason)
        {
            await multiplayerHubContext.Clients.User(userId.ToString()).SendAsync(nameof(IMultiplayerClient.GameplayAborted), reason);
        }

        public async Task OnGameCompletedAsync(long roomId, long playlistItemId)
        {
            var ev = new multiplayer_realtime_room_event
            {
                event_type = "game_completed",
                room_id = roomId,
                playlist_item_id = playlistItemId,
            };
            await logDatabaseEvent(ev);
            await multiplayerHubContext.Clients.Group(GetGroupId(roomId)).SendAsync(nameof(IMultiplayerClient.ResultsReady));
            await refereeHubContext.Clients.Group(GetGroupId(ev.room_id)).SendAsync("RoomEventLogged", ev);
        }

        public async Task OnPlaylistItemAddedAsync(long roomId, MultiplayerPlaylistItem item)
        {
            await multiplayerHubContext.Clients.Group(GetGroupId(roomId)).SendAsync(nameof(IMultiplayerClient.PlaylistItemAdded), item);
        }

        public async Task OnPlaylistItemRemovedAsync(long roomId, long playlistItemId)
        {
            await multiplayerHubContext.Clients.Group(GetGroupId(roomId)).SendAsync(nameof(IMultiplayerClient.PlaylistItemRemoved), playlistItemId);
        }

        public async Task OnPlaylistItemChangedAsync(long roomId, MultiplayerPlaylistItem item)
        {
            await multiplayerHubContext.Clients.Group(GetGroupId(roomId)).SendAsync(nameof(IMultiplayerClient.PlaylistItemChanged), item);
        }

        public async Task OnUserVotedToSkipIntro(long roomId, int userId, bool voted)
        {
            await multiplayerHubContext.Clients.Group(GetGroupId(roomId)).SendAsync(nameof(IMultiplayerClient.UserVotedToSkipIntro), userId, voted);
        }

        public async Task OnVoteToSkipIntroPassed(long roomId)
        {
            await multiplayerHubContext.Clients.Group(GetGroupId(roomId)).SendAsync(nameof(IMultiplayerClient.VoteToSkipIntroPassed));
        }

        public async Task OnRoomDisbandedAsync(long roomId, int userId)
        {
            var ev = new multiplayer_realtime_room_event
            {
                event_type = "room_disbanded",
                room_id = roomId,
                user_id = userId,
            };

            await logDatabaseEvent(ev);
            await refereeHubContext.Clients.Group(GetGroupId(ev.room_id)).SendAsync("RoomEventLogged", ev);
        }

        #endregion

        #region IMatchmakingEventNotifier

        public Task OnRoomCreatedAsync(long roomId, MatchmakingRoomCreatedEventDetail details) => logDatabaseEvent(new matchmaking_room_event
        {
            event_type = "room_created",
            room_id = roomId,
            event_detail = JsonConvert.SerializeObject(details)
        });

        /// <summary>
        /// Records a user joining a matchmaking room.
        /// </summary>
        public Task OnPlayerJoinedMatchmakingAsync(long roomId, int userId) => logDatabaseEvent(new matchmaking_room_event
        {
            event_type = "user_join",
            room_id = roomId,
            user_id = userId
        });

        public async Task OnPlayerBeatmapSelectedAsync(long roomId, int userId, long playlistItemId)
        {
            await multiplayerHubContext.Clients.Group(GetGroupId(roomId)).SendAsync(nameof(IMatchmakingClient.MatchmakingItemSelected), userId, playlistItemId);
        }

        public async Task OnPlayerBeatmapDeselectedAsync(long roomId, int userId, long playlistItemId)
        {
            await multiplayerHubContext.Clients.Group(GetGroupId(roomId)).SendAsync(nameof(IMatchmakingClient.MatchmakingItemDeselected), userId, playlistItemId);
        }

        /// <summary>
        /// Records a user's individual beatmap selection.
        /// </summary>
        public Task OnPlayerBeatmapFinalisedAsync(long roomId, int userId, long playlistItemId) => logDatabaseEvent(new matchmaking_room_event
        {
            event_type = "user_pick",
            room_id = roomId,
            user_id = userId,
            playlist_item_id = playlistItemId
        });

        /// <summary>
        /// Records the final gameplay beatmap as selected by the server.
        /// </summary>
        public Task OnFinalBeatmapSelectedAsync(long roomId, long playlistItemId) => logDatabaseEvent(new matchmaking_room_event
        {
            event_type = "gameplay_beatmap",
            room_id = roomId,
            playlist_item_id = playlistItemId
        });

        #endregion

        private async Task logDatabaseEvent(multiplayer_realtime_room_event ev)
        {
            try
            {
                using var db = databaseFactory.GetInstance();
                await db.LogRoomEventAsync(ev);
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Failed to log multiplayer room event to database");
            }
        }

        private async Task logDatabaseEvent(matchmaking_room_event ev)
        {
            try
            {
                using var db = databaseFactory.GetInstance();
                await db.LogRoomEventAsync(ev);
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Failed to log multiplayer room event to database");
            }
        }

        /// <summary>
        /// Get the group ID to be used for multiplayer messaging.
        /// </summary>
        /// <param name="roomId">The databased room ID.</param>
        public static string GetGroupId(long roomId) => $"room:{roomId}";
    }
}
