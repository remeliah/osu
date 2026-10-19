// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Extensions;
using osu.Game.Beatmaps.Legacy;
using osu.Game.Online.API;
using osu.Game.Online.Matchmaking;
using osu.Game.Online.Matchmaking.Requests;
using osu.Game.Online.Matchmaking.Responses;
using osu.Game.Online.Multiplayer;
using osu.Game.Online.Multiplayer.Countdown;
using osu.Game.Online.Multiplayer.MatchTypes.RankedPlay;
using osu.Game.Online.Multiplayer.MatchTypes.TeamVersus;
using osu.Game.Online.RankedPlay;
using osu.Game.Online.Rooms;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Mods;
using RoomMatchType = osu.Game.Online.Rooms.MatchType;

namespace osu.Game.Online.Bancho
{
    /// <summary>
    /// Adapts lazer's single-item realtime rooms to osu!stable's Bancho match protocol.
    /// </summary>
    public sealed partial class LegacyBanchoMultiplayerClient : MultiplayerClient
    {
        private readonly LegacyBanchoAPIAccess bancho;
        private readonly BindableBool isConnected = new();
        private readonly object joinLock = new();

        private TaskCompletionSource<LegacyBanchoMatch>? pendingJoin;
        private LegacyBanchoMatch? currentMatch;
        private bool waitingForAllPlayersLoaded;
        private bool allPlayersLoaded;

        public override IBindable<bool> IsConnected => isConnected;

        public LegacyBanchoMultiplayerClient(LegacyBanchoAPIAccess bancho)
        {
            this.bancho = bancho;
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            bancho.BanchoMultiplayerPacketReceived += onPacket;
            bancho.State.BindValueChanged(state => Schedule(() =>
            {
                isConnected.Value = state.NewValue == APIState.Online;
                if (isConnected.Value)
                    joinLobby();
                else
                {
                    currentMatch = null;
                    completePendingJoin(new OperationCanceledException("The Bancho session disconnected."));
                }
            }), true);
        }

        protected override async Task<MultiplayerRoom> CreateRoomInternal(MultiplayerRoom room)
        {
            ensureConnected();

            LegacyBanchoMatch match = createMatch(room);
            Task<LegacyBanchoMatch> response = beginJoin();
            partLobby();
            bancho.EnqueueBanchoPacket(ClientPacket.CreateMatch, writer => LegacyBanchoMatchCodec.Write(writer, match));

            try
            {
                LegacyBanchoMatch joined = await awaitJoin(response).ConfigureAwait(false);
                return createRoom(joined);
            }
            catch
            {
                if (isConnected.Value)
                    joinLobby();
                throw;
            }
        }

        protected override async Task<MultiplayerRoom> JoinRoomInternal(long roomId, string? password = null)
        {
            ensureConnected();

            Task<LegacyBanchoMatch> response = beginJoin();
            partLobby();
            bancho.EnqueueBanchoPacket(ClientPacket.JoinMatch, writer =>
            {
                writer.Write(checked((int)roomId));
                BanchoPacketCodec.WriteString(writer, password ?? string.Empty);
            });

            try
            {
                LegacyBanchoMatch joined = await awaitJoin(response).ConfigureAwait(false);
                return createRoom(joined);
            }
            catch
            {
                if (isConnected.Value)
                    joinLobby();
                throw;
            }
        }

        protected override Task LeaveRoomInternal()
        {
            if (isConnected.Value)
                bancho.EnqueueBanchoPacket(ClientPacket.PartMatch);

            bancho.LeftLegacyMultiplayerMatch();
            currentMatch = null;
            waitingForAllPlayersLoaded = false;
            allPlayersLoaded = false;
            if (isConnected.Value)
                joinLobby();
            return Task.CompletedTask;
        }

        public override Task InvitePlayer(int userId)
        {
            if (isConnected.Value && currentMatch != null)
                bancho.EnqueueBanchoPacket(ClientPacket.MatchInvite, writer => writer.Write(userId));
            return Task.CompletedTask;
        }

        public override Task TransferHost(int userId)
        {
            if (currentMatch?.HostID != API.LocalUser.Value.Id)
                return Task.CompletedTask;

            int slot = getSlotForUser(userId);
            if (slot >= 0)
                bancho.EnqueueBanchoPacket(ClientPacket.MatchTransferHost, writer => writer.Write(slot));
            return Task.CompletedTask;
        }

        public override Task KickUser(int userId)
        {
            if (currentMatch?.HostID != API.LocalUser.Value.Id)
                return Task.CompletedTask;

            MultiplayerRoomUser? user = Room?.Users.FirstOrDefault(candidate => candidate.UserID == userId);
            if (user?.User != null)
                bancho.SendLegacyMultiplayerCommand($"!mp kick {user.User.Username}");
            return Task.CompletedTask;
        }

        public override Task ChangeSettings(MultiplayerRoomSettings settings)
        {
            if (currentMatch == null || currentMatch.HostID != API.LocalUser.Value.Id)
                return Task.CompletedTask;

            LegacyBanchoMatch changed = currentMatch.Clone();
            changed.Name = settings.Name[..Math.Min(settings.Name.Length, 50)];
            changed.TeamType = settings.MatchType == RoomMatchType.TeamVersus ? (byte)2 : (byte)0;
            bancho.EnqueueBanchoPacket(ClientPacket.MatchChangeSettings, writer => LegacyBanchoMatchCodec.Write(writer, changed));

            if (settings.Password != "*" && !string.Equals(settings.Password, currentMatch.Password, StringComparison.Ordinal))
            {
                LegacyBanchoMatch passwordChanged = changed.Clone();
                passwordChanged.Password = settings.Password;
                passwordChanged.HasPassword = !string.IsNullOrEmpty(settings.Password);
                bancho.EnqueueBanchoPacket(ClientPacket.MatchChangePassword,
                    writer => LegacyBanchoMatchCodec.Write(writer, passwordChanged));
            }


            if (settings.MaxParticipants is byte maximum)
            {
                for (int i = 0; i < LegacyBanchoMatch.SLOT_COUNT; i++)
                {
                    if (currentMatch.SlotUserIDs[i] != 0)
                        continue;

                    bool shouldLock = i >= maximum;
                    bool isLocked = currentMatch.SlotStatuses[i] == LegacyMatchSlotStatus.Locked;
                    if (shouldLock != isLocked)
                    {
                        int slot = i;
                        bancho.EnqueueBanchoPacket(ClientPacket.MatchLock, writer => writer.Write(slot));
                    }
                }
            }

            return Task.CompletedTask;
        }

        public override Task ChangeState(MultiplayerUserState newState)
        {
            if (!isConnected.Value || currentMatch == null)
                return Task.CompletedTask;

            switch (newState)
            {
                case MultiplayerUserState.Ready:
                    bancho.EnqueueBanchoPacket(ClientPacket.MatchReady);
                    break;

                case MultiplayerUserState.Idle:
                    bancho.EnqueueBanchoPacket(ClientPacket.MatchNotReady);
                    break;

                case MultiplayerUserState.Loaded:
                    bancho.EnqueueBanchoPacket(ClientPacket.MatchLoadComplete);
                    break;

                case MultiplayerUserState.ReadyForGameplay:
                    if (allPlayersLoaded)
                        beginGameplay();
                    break;

                case MultiplayerUserState.FinishedPlay:
                    bancho.EnqueueBanchoPacket(ClientPacket.MatchComplete);
                    break;
            }

            if (LocalUser != null)
                _ = ((IMultiplayerClient)this).UserStateChanged(LocalUser.UserID, newState);
            return Task.CompletedTask;
        }

        public override Task ChangeBeatmapAvailability(BeatmapAvailability newBeatmapAvailability)
        {
            if (!isConnected.Value || currentMatch == null)
                return Task.CompletedTask;

            switch (newBeatmapAvailability.State)
            {
                case DownloadState.LocallyAvailable:
                    bancho.EnqueueBanchoPacket(ClientPacket.MatchHasBeatmap);
                    break;

                case DownloadState.NotDownloaded:
                    bancho.EnqueueBanchoPacket(ClientPacket.MatchNoBeatmap);
                    break;
            }

            return Task.CompletedTask;
        }

        public override Task ChangeUserStyle(int? beatmapId, int? rulesetId) => Task.CompletedTask;

        public override Task ChangeUserMods(IEnumerable<APIMod> newMods)
        {
            if (currentMatch == null)
                return Task.CompletedTask;

            if (!currentMatch.FreeMods && currentMatch.HostID != API.LocalUser.Value.Id)
                return Task.CompletedTask;

            Ruleset ruleset = getRuleset(currentMatch.RulesetID);
            uint mods = (uint)ruleset.ConvertToLegacyMods(newMods.Select(mod => mod.ToMod(ruleset)).ToArray());

            // In stable freemod rooms DT/NC/HT remain global match mods and only
            // the host is allowed to update them. Lazer's user-mod callback only
            // supplies the locally selected mods, so preserve the current global
            // speed selection when the host changes their per-slot mods.
            if (currentMatch.FreeMods && API.LocalUser.Value.Id == currentMatch.HostID)
            {
                const LegacyMods speed_changing_mods = LegacyMods.DoubleTime | LegacyMods.Nightcore | LegacyMods.HalfTime;
                mods |= currentMatch.Mods & (uint)speed_changing_mods;
            }

            bancho.EnqueueBanchoPacket(ClientPacket.MatchChangeMods, writer => writer.Write(mods));
            return Task.CompletedTask;
        }

        public override Task SendMatchRequest(MatchUserRequest request)
        {
            switch (request)
            {
                case ChangeSlotRequest changeSlot:
                    bancho.EnqueueBanchoPacket(ClientPacket.MatchChangeSlot, writer => writer.Write((int)changeSlot.SlotID));
                    break;

                case ChangeTeamRequest changeTeam when currentMatch != null:
                {
                    int localSlot = getSlotForUser(API.LocalUser.Value.Id);
                    LegacyMatchTeam desired = changeTeam.TeamID == 0 ? LegacyMatchTeam.Red : LegacyMatchTeam.Blue;
                    if (localSlot >= 0 && currentMatch.SlotTeams[localSlot] != desired)
                        bancho.EnqueueBanchoPacket(ClientPacket.MatchChangeTeam);
                    break;
                }

                case SetLockStateRequest lockState when currentMatch?.HostID == API.LocalUser.Value.Id:
                    for (int i = 0; i < LegacyBanchoMatch.SLOT_COUNT; i++)
                    {
                        bool isLocked = currentMatch.SlotStatuses[i] == LegacyMatchSlotStatus.Locked;
                        if (currentMatch.SlotUserIDs[i] == 0 && isLocked != lockState.Locked)
                        {
                            int slot = i;
                            bancho.EnqueueBanchoPacket(ClientPacket.MatchLock, writer => writer.Write(slot));
                        }
                    }
                    break;

                case StartMatchCountdownRequest countdown when currentMatch?.HostID == API.LocalUser.Value.Id:
                    bancho.SendLegacyMultiplayerCommand($"!mp start {(int)Math.Ceiling(countdown.Duration.TotalSeconds)}");
                    break;

                case StopCountdownRequest when currentMatch?.HostID == API.LocalUser.Value.Id:
                    bancho.SendLegacyMultiplayerCommand("!mp start cancel");
                    break;
            }

            return Task.CompletedTask;
        }

        public override Task StartMatch()
        {
            if (isConnected.Value && currentMatch?.HostID == API.LocalUser.Value.Id && !currentMatch.InProgress)
                bancho.EnqueueBanchoPacket(ClientPacket.MatchStart);
            return Task.CompletedTask;
        }

        public override Task AbortGameplay()
        {
            if (isConnected.Value && currentMatch?.InProgress == true)
                bancho.EnqueueBanchoPacket(ClientPacket.MatchFailed);
            return Task.CompletedTask;
        }

        internal void NotifyPlayerFailed()
        {
            if (isConnected.Value && currentMatch?.InProgress == true)
                bancho.EnqueueBanchoPacket(ClientPacket.MatchFailed);
        }

        public override Task AbortMatch()
        {
            if (currentMatch?.HostID == API.LocalUser.Value.Id)
                bancho.SendLegacyMultiplayerCommand("!mp abort");
            return Task.CompletedTask;
        }

        public override Task AddPlaylistItem(MultiplayerPlaylistItem item) => updatePlaylistItem(item);

        public override Task EditPlaylistItem(MultiplayerPlaylistItem item) => updatePlaylistItem(item);

        public override Task RemovePlaylistItem(long playlistItemId) => Task.CompletedTask;

        public override Task VoteToSkipIntro()
        {
            if (isConnected.Value && currentMatch?.InProgress == true)
                bancho.EnqueueBanchoPacket(ClientPacket.MatchSkipRequest);
            return Task.CompletedTask;
        }

        private Task updatePlaylistItem(MultiplayerPlaylistItem item)
        {
            if (currentMatch == null || currentMatch.HostID != API.LocalUser.Value.Id)
                return Task.CompletedTask;

            LegacyBanchoMatch changed = currentMatch.Clone();
            (int selectedId, string checksum, string title) = bancho.GetLegacySelection();
            changed.BeatmapID = item.BeatmapID > 0 ? item.BeatmapID : selectedId;
            changed.BeatmapChecksum = !string.IsNullOrEmpty(item.BeatmapChecksum) ? item.BeatmapChecksum : checksum;
            changed.BeatmapName = title;
            changed.RulesetID = checked((byte)Math.Clamp(item.RulesetID, 0, 3));

            Ruleset ruleset = getRuleset(changed.RulesetID);
            changed.Mods = (uint)ruleset.ConvertToLegacyMods(item.RequiredMods.Select(mod => mod.ToMod(ruleset)).ToArray());
            changed.FreeMods = item.AllowedMods.Any();
            bancho.EnqueueBanchoPacket(ClientPacket.MatchChangeSettings, writer => LegacyBanchoMatchCodec.Write(writer, changed));
            return Task.CompletedTask;
        }

        private LegacyBanchoMatch createMatch(MultiplayerRoom room)
        {
            MultiplayerPlaylistItem item = room.Playlist.Single();
            Ruleset ruleset = getRuleset(item.RulesetID);
            (int selectedId, string checksum, string title) = bancho.GetLegacySelection();
            var match = new LegacyBanchoMatch
            {
                Name = room.Settings.Name[..Math.Min(room.Settings.Name.Length, 50)],
                Password = room.Settings.Password,
                HasPassword = !string.IsNullOrEmpty(room.Settings.Password),
                BeatmapName = title,
                BeatmapID = item.BeatmapID > 0 ? item.BeatmapID : selectedId,
                BeatmapChecksum = !string.IsNullOrEmpty(item.BeatmapChecksum) ? item.BeatmapChecksum : checksum,
                HostID = API.LocalUser.Value.Id,
                RulesetID = checked((byte)Math.Clamp(item.RulesetID, 0, 3)),
                TeamType = room.Settings.MatchType == RoomMatchType.TeamVersus ? (byte)2 : (byte)0,
                FreeMods = item.AllowedMods.Any(),
                Mods = (uint)ruleset.ConvertToLegacyMods(item.RequiredMods.Select(mod => mod.ToMod(ruleset)).ToArray()),
            };

            Array.Fill(match.SlotStatuses, LegacyMatchSlotStatus.Open);
            int maximum = Math.Clamp((int?)room.Settings.MaxParticipants ?? LegacyBanchoMatch.SLOT_COUNT, 1, LegacyBanchoMatch.SLOT_COUNT);
            for (int i = maximum; i < LegacyBanchoMatch.SLOT_COUNT; i++)
                match.SlotStatuses[i] = LegacyMatchSlotStatus.Locked;
            match.SlotStatuses[0] = LegacyMatchSlotStatus.NotReady;
            match.SlotUserIDs[0] = API.LocalUser.Value.Id;
            match.SlotTeams[0] = match.TeamType >= 2 ? LegacyMatchTeam.Red : LegacyMatchTeam.Neutral;
            return match;
        }

        private MultiplayerRoom createRoom(LegacyBanchoMatch match)
        {
            MultiplayerPlaylistItem item = createPlaylistItem(match);
            List<MultiplayerRoomUser> users = createUsers(match).ToList();
            var room = new MultiplayerRoom(match.ID)
            {
                State = match.InProgress ? MultiplayerRoomState.Playing : MultiplayerRoomState.Open,
                ChannelID = bancho.GetLegacyChannelId("#multiplayer"),
                Settings = createSettings(match, item.ID),
                Users = users,
                Host = users.FirstOrDefault(user => user.UserID == match.HostID),
                MatchState = createMatchState(match),
                Playlist = [item],
            };

            currentMatch = match.Clone();
            return room;
        }

        private MultiplayerRoomSettings createSettings(LegacyBanchoMatch match, long playlistItemId) => new()
        {
            Name = match.Name,
            Password = match.Password,
            PlaylistItemId = playlistItemId,
            MatchType = match.TeamType is 2 or 3 ? RoomMatchType.TeamVersus : RoomMatchType.HeadToHead,
            QueueMode = QueueMode.HostOnly,
            MaxParticipants = getMaxParticipants(match),
        };

        private MultiplayerPlaylistItem createPlaylistItem(LegacyBanchoMatch match)
        {
            Ruleset ruleset = getRuleset(match.RulesetID);
            var requiredMods = ruleset.ConvertFromLegacyMods((LegacyMods)match.Mods).Select(mod => new APIMod(mod)).ToList();
            if (ruleset.CreateMod<ModClassic>() is ModClassic classic)
                requiredMods.Add(new APIMod(classic));

            return new MultiplayerPlaylistItem
            {
                ID = match.ID,
                OwnerID = match.HostID,
                BeatmapID = match.BeatmapID,
                BeatmapChecksum = match.BeatmapChecksum,
                RulesetID = match.RulesetID,
                RequiredMods = requiredMods,
                AllowedMods = match.FreeMods
                    ? ruleset.AllMods.Where(mod => mod.ValidForMultiplayerAsFreeMod)
                             .Select(mod => mod.CreateInstance())
                             .Where(mod => ruleset.ConvertToLegacyMods([mod]) != LegacyMods.None)
                             .Select(mod => new APIMod(mod)).ToArray()
                    : [],
            };
        }

        private IEnumerable<MultiplayerRoomUser> createUsers(LegacyBanchoMatch match)
        {
            Ruleset ruleset = getRuleset(match.RulesetID);
            for (int i = 0; i < LegacyBanchoMatch.SLOT_COUNT; i++)
            {
                int userId = match.SlotUserIDs[i];
                if (userId <= 0)
                    continue;

                yield return new MultiplayerRoomUser(userId)
                {
                    State = convertUserState(match.SlotStatuses[i]),
                    BeatmapAvailability = match.SlotStatuses[i] == LegacyMatchSlotStatus.NoMap
                        ? BeatmapAvailability.NotDownloaded()
                        : BeatmapAvailability.LocallyAvailable(),
                    Mods = match.FreeMods
                        ? ruleset.ConvertFromLegacyMods((LegacyMods)match.SlotMods[i]).Select(mod => new APIMod(mod)).ToArray()
                        : [],
                    MatchState = match.TeamType is 2 or 3
                        ? new TeamVersusUserState { TeamID = match.SlotTeams[i] == LegacyMatchTeam.Red ? 0 : 1 }
                        : null,
                    Role = MultiplayerRoomUserRole.Player,
                };
            }
        }

        private MatchRoomState createMatchState(LegacyBanchoMatch match)
        {
            byte maximum = getMaxParticipants(match);
            int?[] slots = match.SlotUserIDs.Take(maximum).Select(id => id > 0 ? (int?)id : null).ToArray();
            bool locked = match.SlotStatuses.Where((_, index) => match.SlotUserIDs[index] == 0)
                               .Any()
                          && match.SlotStatuses.Where((_, index) => match.SlotUserIDs[index] == 0)
                                  .All(status => status == LegacyMatchSlotStatus.Locked);
            if (match.TeamType is 2 or 3)
            {
                return new TeamVersusRoomState
                {
                    Teams =
                    [
                        new MultiplayerTeam { ID = 0, Name = "Team Red" },
                        new MultiplayerTeam { ID = 1, Name = "Team Blue" },
                    ],
                    Slots = slots,
                    Locked = locked,
                };
            }

            return new StandardMatchRoomState
            {
                Slots = slots,
                Locked = locked,
            };
        }

        private static byte getMaxParticipants(LegacyBanchoMatch match)
        {
            int maximum = LegacyBanchoMatch.SLOT_COUNT;
            while (maximum > 1
                   && match.SlotUserIDs[maximum - 1] == 0
                   && match.SlotStatuses[maximum - 1] == LegacyMatchSlotStatus.Locked)
                maximum--;

            return checked((byte)maximum);
        }

        private MultiplayerUserState convertUserState(LegacyMatchSlotStatus status)
        {
            return status switch
            {
                LegacyMatchSlotStatus.Ready => MultiplayerUserState.Ready,
                LegacyMatchSlotStatus.Playing => waitingForAllPlayersLoaded ? MultiplayerUserState.WaitingForLoad : MultiplayerUserState.Playing,
                LegacyMatchSlotStatus.Complete => MultiplayerUserState.Results,
                _ => MultiplayerUserState.Idle,
            };
        }

        private void onPacket(ServerPacket type, byte[] payload) => Schedule(() => processPacket(type, payload));

        private void processPacket(ServerPacket type, byte[] payload)
        {
            using var reader = new BinaryReader(new MemoryStream(payload, false), Encoding.UTF8);

            switch (type)
            {
                case ServerPacket.MatchJoinSuccess:
                {
                    LegacyBanchoMatch joined = LegacyBanchoMatchCodec.Read(reader);
                    currentMatch = joined.Clone();
                    completePendingJoin(joined);
                    break;
                }

                case ServerPacket.MatchJoinFail:
                    completePendingJoin(new InvalidPasswordException());
                    break;

                case ServerPacket.UpdateMatch:
                case ServerPacket.NewMatch:
                {
                    LegacyBanchoMatch updated = LegacyBanchoMatchCodec.Read(reader);
                    if (currentMatch?.ID == updated.ID)
                        applyMatchUpdate(updated);
                    break;
                }

                case ServerPacket.DisposeMatch:
                    if (reader.ReadInt32() == currentMatch?.ID)
                        _ = ((IMultiplayerClient)this).RoomStateChanged(MultiplayerRoomState.Closed);
                    break;

                case ServerPacket.MatchStart:
                {
                    LegacyBanchoMatch started = LegacyBanchoMatchCodec.Read(reader);
                    if (currentMatch?.ID != started.ID)
                        break;

                    waitingForAllPlayersLoaded = true;
                    allPlayersLoaded = false;
                    applyMatchUpdate(started);
                    _ = ((IMultiplayerClient)this).RoomStateChanged(MultiplayerRoomState.WaitingForLoad);
                    _ = ((IMultiplayerClient)this).LoadRequested();
                    break;
                }

                case ServerPacket.AllPlayersLoaded:
                case ServerPacket.MatchAllPlayersLoaded:
                    allPlayersLoaded = true;
                    if (LocalUser?.State == MultiplayerUserState.ReadyForGameplay)
                        beginGameplay();
                    break;

                case ServerPacket.MatchComplete:
                    waitingForAllPlayersLoaded = false;
                    allPlayersLoaded = false;
                    _ = ((IMultiplayerClient)this).RoomStateChanged(MultiplayerRoomState.Open);
                    if (Room != null)
                    {
                        foreach (MultiplayerRoomUser user in Room.Users.Where(user => user.State is MultiplayerUserState.Playing or MultiplayerUserState.FinishedPlay).ToArray())
                            _ = ((IMultiplayerClient)this).UserStateChanged(user.UserID, MultiplayerUserState.Results);
                    }
                    _ = ((IMultiplayerClient)this).ResultsReady();
                    break;

                case ServerPacket.MatchAbort:
                    waitingForAllPlayersLoaded = false;
                    allPlayersLoaded = false;
                    _ = ((IMultiplayerClient)this).GameplayAborted(GameplayAbortReason.HostAbortedTheMatch);
                    _ = ((IMultiplayerClient)this).RoomStateChanged(MultiplayerRoomState.Open);
                    break;

                case ServerPacket.MatchTransferHost:
                    // The following UpdateMatch packet carries the authoritative
                    // new host id. This packet is only a prompt for stable clients.
                    break;

                case ServerPacket.MatchPlayerFailed:
                    // Stable keeps failed multiplayer users in the playing state;
                    // this packet only changes their displayed grade to F. Lazer's
                    // multiplayer leaderboard has no equivalent failure flag.
                    break;

                case ServerPacket.MatchSkip:
                    _ = ((IMultiplayerClient)this).VoteToSkipIntroPassed();
                    break;

                case ServerPacket.MatchPlayerSkipped:
                    _ = ((IMultiplayerClient)this).UserVotedToSkipIntro(reader.ReadInt32(), true);
                    break;

                case ServerPacket.MatchInvite:
                {
                    BanchoPacketCodec.ReadString(reader); // sender name
                    string text = BanchoPacketCodec.ReadString(reader);
                    BanchoPacketCodec.ReadString(reader); // target name
                    int senderId = reader.ReadInt32();
                    Match match = Regex.Match(text, @"osump://(?<id>\d+)/(?<password>[^\s\]]*)", RegexOptions.CultureInvariant);
                    if (match.Success && long.TryParse(match.Groups["id"].Value, out long roomId))
                        _ = ((IMultiplayerClient)this).Invited(senderId, roomId, match.Groups["password"].Value);
                    break;
                }

                case ServerPacket.MatchChangePassword:
                    if (currentMatch != null)
                        currentMatch.Password = BanchoPacketCodec.ReadString(reader);
                    break;
            }
        }

        private void beginGameplay()
        {
            if (!waitingForAllPlayersLoaded)
                return;

            waitingForAllPlayersLoaded = false;
            if (Room != null)
            {
                foreach (MultiplayerRoomUser user in Room.Users.Where(user => user.State is MultiplayerUserState.WaitingForLoad or MultiplayerUserState.Loaded or MultiplayerUserState.ReadyForGameplay).ToArray())
                    _ = ((IMultiplayerClient)this).UserStateChanged(user.UserID, MultiplayerUserState.Playing);
            }
            _ = ((IMultiplayerClient)this).RoomStateChanged(MultiplayerRoomState.Playing);
            _ = ((IMultiplayerClient)this).GameplayStarted();
        }

        private void applyMatchUpdate(LegacyBanchoMatch updated)
        {
            LegacyBanchoMatch? previous = currentMatch;
            currentMatch = updated.Clone();
            if (Room == null)
                return;

            Dictionary<int, MultiplayerRoomUser> newUsers = createUsers(updated).ToDictionary(user => user.UserID);
            Dictionary<int, MultiplayerRoomUser> oldUsers = Room.Users.ToDictionary(user => user.UserID);

            foreach (MultiplayerRoomUser removed in oldUsers.Values.Where(user => !newUsers.ContainsKey(user.UserID)).ToArray())
                _ = ((IMultiplayerClient)this).UserLeft(removed);
            foreach (MultiplayerRoomUser added in newUsers.Values.Where(user => !oldUsers.ContainsKey(user.UserID)).ToArray())
                _ = ((IMultiplayerClient)this).UserJoined(added);

            foreach ((int userId, MultiplayerRoomUser incoming) in newUsers)
            {
                if (!oldUsers.TryGetValue(userId, out MultiplayerRoomUser? existing))
                    continue;

                if (existing.State != incoming.State)
                    _ = ((IMultiplayerClient)this).UserStateChanged(userId, incoming.State);
                if (!existing.BeatmapAvailability.Equals(incoming.BeatmapAvailability))
                    _ = ((IMultiplayerClient)this).UserBeatmapAvailabilityChanged(userId, incoming.BeatmapAvailability);
                if (!existing.Mods.SequenceEqual(incoming.Mods))
                    _ = ((IMultiplayerClient)this).UserModsChanged(userId, incoming.Mods);
                if (incoming.MatchState != null)
                    _ = ((IMultiplayerClient)this).MatchUserStateChanged(userId, incoming.MatchState);
            }

            if (previous == null || previous.HostID != updated.HostID)
                _ = ((IMultiplayerClient)this).HostChanged(updated.HostID);

            MultiplayerPlaylistItem item = createPlaylistItem(updated);
            _ = ((IMultiplayerClient)this).SettingsChanged(createSettings(updated, item.ID));
            _ = PlaylistItemChanged(item);
            _ = ((IMultiplayerClient)this).MatchRoomStateChanged(createMatchState(updated));
            _ = ((IMultiplayerClient)this).RoomStateChanged(updated.InProgress
                ? waitingForAllPlayersLoaded ? MultiplayerRoomState.WaitingForLoad : MultiplayerRoomState.Playing
                : MultiplayerRoomState.Open);
        }

        private int getSlotForUser(int userId)
        {
            if (currentMatch == null)
                return -1;
            return Array.IndexOf(currentMatch.SlotUserIDs, userId);
        }

        private Ruleset getRuleset(int rulesetId) =>
            Rulesets.GetRuleset(Math.Clamp(rulesetId, 0, 3))?.CreateInstance()
            ?? throw new InvalidOperationException($"Legacy ruleset {rulesetId} is not installed.");

        private Task<LegacyBanchoMatch> beginJoin()
        {
            lock (joinLock)
            {
                if (pendingJoin != null)
                    throw new InvalidOperationException("A stable multiplayer join is already in progress.");

                pendingJoin = new TaskCompletionSource<LegacyBanchoMatch>(TaskCreationOptions.RunContinuationsAsynchronously);
                return pendingJoin.Task;
            }
        }

        private async Task<LegacyBanchoMatch> awaitJoin(Task<LegacyBanchoMatch> response)
        {
            try
            {
                return await response.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            }
            finally
            {
                lock (joinLock)
                {
                    if (ReferenceEquals(pendingJoin?.Task, response))
                        pendingJoin = null;
                }
            }
        }

        private void completePendingJoin(LegacyBanchoMatch match)
        {
            lock (joinLock)
            {
                pendingJoin?.TrySetResult(match);
                pendingJoin = null;
            }
        }

        private void completePendingJoin(Exception exception)
        {
            lock (joinLock)
            {
                pendingJoin?.TrySetException(exception);
                pendingJoin = null;
            }
        }

        private void ensureConnected()
        {
            if (!isConnected.Value)
                throw new OperationCanceledException("The Bancho multiplayer connection is offline.");
        }

        public override Task DiscardCards(RankedPlayCardItem[] cards) => Task.CompletedTask;
        public override Task PlayCard(RankedPlayCardItem card) => Task.CompletedTask;
        public override Task<MatchmakingPool[]> GetMatchmakingPoolsOfType(MatchmakingPoolType type) => Task.FromResult(Array.Empty<MatchmakingPool>());
        public override Task<MatchmakingJoinLobbyResponse> MatchmakingJoinLobbyWithParams(MatchmakingJoinLobbyRequest request) => Task.FromResult(new MatchmakingJoinLobbyResponse());
        public override Task MatchmakingLeaveLobby() => Task.CompletedTask;
        public override Task MatchmakingJoinQueue(int poolId) => Task.CompletedTask;
        public override Task MatchmakingLeaveQueue() => Task.CompletedTask;
        public override Task MatchmakingAcceptInvitation() => Task.CompletedTask;
        public override Task<MatchmakingIssueDuelResponse> MatchmakingIssueDuel(MatchmakingIssueDuelRequest request) => Task.FromResult(new MatchmakingIssueDuelResponse());
        public override Task<MatchmakingAcceptDuelResponse> MatchmakingAcceptDuel(MatchmakingAcceptDuelRequest request) => Task.FromResult(new MatchmakingAcceptDuelResponse());
        public override Task MatchmakingDeclineInvitation() => Task.CompletedTask;
        public override Task MatchmakingToggleSelection(long playlistItemId) => Task.CompletedTask;
        public override Task MatchmakingSkipToNextStage() => Task.CompletedTask;

        public override Task Reconnect()
        {
            if (bancho.IsLoggedIn)
                joinLobby();
            return Task.CompletedTask;
        }

        protected override Task DisconnectInternal()
        {
            if (bancho.IsLoggedIn)
                partLobby();
            return Task.CompletedTask;
        }

        private void joinLobby()
        {
            bancho.EnqueueBanchoPacket(ClientPacket.JoinLobby);
            bancho.EnqueueBanchoPacket(ClientPacket.ChannelJoin, writer => BanchoPacketCodec.WriteString(writer, "#lobby"));
        }

        private void partLobby()
        {
            bancho.EnqueueBanchoPacket(ClientPacket.PartLobby);
            bancho.EnqueueBanchoPacket(ClientPacket.ChannelLeave, writer => BanchoPacketCodec.WriteString(writer, "#lobby"));
        }

        protected override void Dispose(bool isDisposing)
        {
            bancho.BanchoMultiplayerPacketReceived -= onPacket;
            base.Dispose(isDisposing);
        }
    }
}
