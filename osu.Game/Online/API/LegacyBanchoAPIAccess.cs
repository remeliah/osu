// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics.Containers;
using osu.Framework.Logging;
using osu.Game.Beatmaps;
using osu.Game.Configuration;
using osu.Game.Database;
using osu.Game.Localisation;
using osu.Game.Online.API.Requests;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Online.Bancho;
using osu.Game.Online.Chat;
using osu.Game.Online.Notifications.WebSocket;
using osu.Game.Online.Rooms;
using osu.Game.Online.Multiplayer;
using osu.Game.Online.Solo;
using osu.Game.Online.Spectator;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Scoring;
using osu.Game.Users;

namespace osu.Game.Online.API
{
    /// <summary>
    /// Bridges the parts of lazer's online API used by login and chat to the legacy Bancho protocol.
    /// Unsupported lazer-only API v2 operations fail locally and are never redirected to an official endpoint.
    /// </summary>
    public partial class LegacyBanchoAPIAccess : CompositeComponent, IAPIProvider
    {
        private const string stored_hash_prefix = "legacy-md5:";

        private readonly OsuConfigManager config;
        private readonly PrivateServerEndpointConfiguration endpoints;
        private readonly Bindable<APIState> state = new(APIState.Offline);
        private readonly Bindable<string> outageMessage = new();
        private readonly LegacyLocalUserState localUserState = new();
        private readonly LegacyBanchoChatClient chatClient;
        private readonly LegacyNotificationsClient notificationsClient = new();
        private readonly Dictionary<int, APIUser> users = new();
        private readonly Dictionary<int, UserPresence> userPresences = new();
        private readonly Dictionary<int, LegacySpectatorStatus> spectatorStatuses = new();
        private readonly Dictionary<int, LegacyBanchoMatch> multiplayerMatches = new();
        private readonly HashSet<int> friendIds = new();
        private readonly HashSet<int> requestStatsAfterPresence = new();
        private readonly Dictionary<long, Channel> channelsById = new();
        private readonly Dictionary<string, Channel> channelsByName = new(StringComparer.OrdinalIgnoreCase);
        private readonly Queue<(string Target, string Content, DateTimeOffset SentAt)> sentMessages = new();
        private readonly object dataLock = new();
        private readonly object connectionLock = new();

        private static readonly string[] stableCountryCodes =
        [
            "oc", "eu", "ad", "ae", "af", "ag", "ai", "al", "am", "an", "ao", "aq", "ar", "as",
            "at", "au", "aw", "az", "ba", "bb", "bd", "be", "bf", "bg", "bh", "bi", "bj", "bm",
            "bn", "bo", "br", "bs", "bt", "bv", "bw", "by", "bz", "ca", "cc", "cd", "cf", "cg",
            "ch", "ci", "ck", "cl", "cm", "cn", "co", "cr", "cu", "cv", "cx", "cy", "cz", "de",
            "dj", "dk", "dm", "do", "dz", "ec", "ee", "eg", "eh", "er", "es", "et", "fi", "fj",
            "fk", "fm", "fo", "fr", "fx", "ga", "gb", "gd", "ge", "gf", "gh", "gi", "gl", "gm",
            "gn", "gp", "gq", "gr", "gs", "gt", "gu", "gw", "gy", "hk", "hm", "hn", "hr", "ht",
            "hu", "id", "ie", "il", "in", "io", "iq", "ir", "is", "it", "jm", "jo", "jp", "ke",
            "kg", "kh", "ki", "km", "kn", "kp", "kr", "kw", "ky", "kz", "la", "lb", "lc", "li",
            "lk", "lr", "ls", "lt", "lu", "lv", "ly", "ma", "mc", "md", "mg", "mh", "mk", "ml",
            "mm", "mn", "mo", "mp", "mq", "mr", "ms", "mt", "mu", "mv", "mw", "mx", "my", "mz",
            "na", "nc", "ne", "nf", "ng", "ni", "nl", "no", "np", "nr", "nu", "nz", "om", "pa",
            "pe", "pf", "pg", "ph", "pk", "pl", "pm", "pn", "pr", "ps", "pt", "pw", "py", "qa",
            "re", "ro", "ru", "rw", "sa", "sb", "sc", "sd", "se", "sg", "sh", "si", "sj", "sk",
            "sl", "sm", "sn", "so", "sr", "st", "sv", "sy", "sz", "tc", "td", "tf", "tg", "th",
            "tj", "tk", "tm", "tn", "to", "tl", "tr", "tt", "tv", "tw", "tz", "ua", "ug", "um",
            "us", "uy", "uz", "va", "vc", "ve", "vg", "vi", "vn", "vu", "wf", "ws", "ye", "yt",
            "rs", "za", "zm", "me", "zw", "xx", "a2", "o1", "ax", "gg", "im", "je", "bl", "mf"
        ];

        private CancellationTokenSource connectionCancellation = new();
        private BanchoConnection connection;
        private Task pendingConnectionShutdown = Task.CompletedTask;
        private int connectionGeneration;
        private long nextMessageId;
        private long nextScoreToken = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        private string currentPasswordHash = string.Empty;
        private byte currentLegacyAction;
        private UserActivity currentLegacyActivity;
        private string currentLegacyStatusText = string.Empty;
        private string currentLegacyBeatmapTitle = string.Empty;
        private string currentLegacyBeatmapHash = string.Empty;
        private int currentLegacyBeatmapId;
        private int currentLegacyMode;
        private uint currentLegacyMods;
        private int currentBanchoPrivileges;
        private DateTimeOffset silenceEnd;
        // Stable multiplayer match id 0 is valid, so it cannot be used as the
        // disconnected sentinel.
        private int currentLegacyMatchId = -1;

        [Resolved]
        private RulesetStore rulesets { get; set; }

        [Resolved]
        private RealmAccess realm { get; set; }

        internal event Action<int, UserPresence?> BanchoPresenceChanged;
        internal event Action<ServerPacket, byte[]> BanchoSpectatorPacketReceived;
        internal event Action<ServerPacket, byte[]> BanchoMultiplayerPacketReceived;
        internal event Action<int, byte[]> BanchoMatchScoreFrameReceived;

        internal bool IsAccountRestricted { get; private set; }

        public LegacyBanchoAPIAccess(OsuGameBase game, OsuConfigManager config, PrivateServerEndpointConfiguration endpoints)
        {
            this.config = config;
            this.endpoints = endpoints;
            chatClient = new LegacyBanchoChatClient(() => IsLoggedIn);

            // This provider is a stable-only session. Keep every score surface
            // (HUD, result screen and leaderboards) on the authoritative ScoreV1
            // value rather than lazer's standardised display score.
            config.SetValue(OsuSetting.ScoreDisplayMode, ScoringMode.Classic);

            ProvidedUsername = config.Get<string>(OsuSetting.Username);

            if (!string.IsNullOrEmpty(endpoints.Domain) && !string.Equals(config.Get<string>(OsuSetting.PrivateServerUrl), endpoints.Domain, StringComparison.OrdinalIgnoreCase))
                config.SetValue(OsuSetting.PrivateServerUrl, endpoints.Domain);

            config.GetBindable<string>(OsuSetting.PrivateServerUrl).BindValueChanged(_ => queueServerConfigurationChange());
            config.GetBindable<bool>(OsuSetting.PrivateServerAdvancedEndpoints).BindValueChanged(_ => queueServerConfigurationChange());
            config.GetBindable<string>(OsuSetting.PrivateServerBanchoUrl).BindValueChanged(_ => queueServerConfigurationChange());
            config.GetBindable<string>(OsuSetting.PrivateServerWebsiteUrl).BindValueChanged(_ => queueServerConfigurationChange());
            config.GetBindable<string>(OsuSetting.PrivateServerAvatarUrl).BindValueChanged(_ => queueServerConfigurationChange());

            string savedHash = config.Get<string>(OsuSetting.Token);
            if (!string.IsNullOrWhiteSpace(ProvidedUsername) && savedHash.StartsWith(stored_hash_prefix, StringComparison.Ordinal))
                startLogin(ProvidedUsername, savedHash[stored_hash_prefix.Length..], saveCredentials: false);
        }

        private void queueServerConfigurationChange() => Scheduler.AddOnce(applyServerConfiguration);

        public Exception SwitchServer(string serverDomain, bool useAdvancedEndpoints, string banchoUrl, string websiteUrl, string avatarUrl)
        {
            var updated = new PrivateServerEndpointConfiguration(
                serverDomain,
                useAdvancedEndpoints ? banchoUrl : null,
                useAdvancedEndpoints ? websiteUrl : null,
                useAdvancedEndpoints ? avatarUrl : null);

            if (updated.ValidationError != null)
            {
                LastLoginError = updated.ValidationError;
                return updated.ValidationError;
            }

            config.SetValue(OsuSetting.PrivateServerAdvancedEndpoints, useAdvancedEndpoints);
            config.SetValue(OsuSetting.PrivateServerBanchoUrl, banchoUrl.Trim());
            config.SetValue(OsuSetting.PrivateServerWebsiteUrl, websiteUrl.Trim());
            config.SetValue(OsuSetting.PrivateServerAvatarUrl, avatarUrl.Trim());
            config.SetValue(OsuSetting.PrivateServerUrl, updated.Domain);

            // Apply synchronously. Relying solely on configuration change callbacks here
            // leaves the old connection alive until this component's scheduler next runs.
            applyServerConfiguration(updated);
            return null;
        }

        private void applyServerConfiguration() => applyServerConfiguration(PrivateServerEndpointConfiguration.FromConfig(config));

        private void applyServerConfiguration(PrivateServerEndpointConfiguration updated)
        {
            if (updated.ValidationError != null)
            {
                LastLoginError = updated.ValidationError;
                return;
            }

            bool endpointsChanged = !string.Equals(endpoints.BanchoUrl, updated.BanchoUrl, StringComparison.OrdinalIgnoreCase)
                                    || !string.Equals(endpoints.WebsiteRootUrl, updated.WebsiteRootUrl, StringComparison.OrdinalIgnoreCase)
                                    || !string.Equals(endpoints.AvatarRootUrl, updated.AvatarRootUrl, StringComparison.OrdinalIgnoreCase);

            if (!endpointsChanged)
            {
                LastLoginError = null;
                return;
            }

            bool reconnect = !string.IsNullOrWhiteSpace(ProvidedUsername)
                             && !string.IsNullOrWhiteSpace(currentPasswordHash);
            string username = ProvidedUsername;
            string passwordHash = currentPasswordHash;

            Logger.Log($"Switching private server endpoints: Bancho={updated.BanchoUrl}, Web={updated.WebsiteRootUrl}, Avatar={updated.AvatarRootUrl}",
                LoggingTarget.Network, LogLevel.Important);

            disconnect(clearCredentials: false);
            endpoints.SetServerUrls(updated.Domain, updated.BanchoUrl, updated.WebsiteRootUrl, updated.AvatarRootUrl);
            MessageFormatter.WebsiteRootUrl = endpoints.WebsiteUrl;
            LastLoginError = null;
            IsAccountRestricted = false;
            currentBanchoPrivileges = 0;

            if (reconnect)
                startLogin(username, passwordHash, saveCredentials: false);
        }

        public IBindable<APIUser> LocalUser => localUserState.User;
        public ILocalUserState LocalUserState => localUserState;
        public string ScoreProcessingNoticeUrl => string.Empty;
        public Language Language => gameLanguage;
        private Language gameLanguage => Language.en;
        public string AccessToken => string.Empty;
        public Guid SessionIdentifier { get; } = Guid.NewGuid();
        public bool IsLoggedIn => state.Value == APIState.Online;
        public string ProvidedUsername { get; private set; }
        public EndpointConfiguration Endpoints => endpoints;
        public int APIVersion => 0;
        public Exception LastLoginError { get; private set; }
        public IBindable<APIState> State => state;
        public IBindable<string> UserFacingOutageMessage => outageMessage;
        public SessionVerificationMethod? SessionVerificationMethod => null;
        public INotificationsClient NotificationsClient => notificationsClient;

        public void Login(string username, string password)
        {
            string passwordHash = HashPassword(password);
            startLogin(username, passwordHash, saveCredentials: true);
        }

        internal static string HashPassword(string password) =>
            Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(password))).ToLowerInvariant();

        private void startLogin(string username, string passwordHash, bool saveCredentials)
        {
            Task previousShutdown = disconnect(clearCredentials: false);
            state.Value = APIState.Connecting;
            LastLoginError = null;
            ProvidedUsername = username;
            currentPasswordHash = passwordHash;

            if (saveCredentials)
            {
                config.SetValue(OsuSetting.Username, config.Get<bool>(OsuSetting.SaveUsername) ? username : string.Empty);
                config.SetValue(OsuSetting.Token, config.Get<bool>(OsuSetting.SavePassword) ? stored_hash_prefix + passwordHash : string.Empty);
            }

            try
            {
                PrivateServerEndpointValidator.Validate(endpoints.BanchoUrl);
            }
            catch (Exception ex)
            {
                LastLoginError = endpoints.ValidationError ?? (string.IsNullOrWhiteSpace(endpoints.BanchoUrl)
                    ? new InvalidOperationException("Set a private stable server URL in Settings > Online before signing in.", ex)
                    : ex);
                state.Value = APIState.Offline;
                return;
            }

            localUserState.SetUser(new APIUser { Username = username, Id = 1 });
            var cancellation = new CancellationTokenSource();
            int generation;
            lock (connectionLock)
            {
                connectionCancellation = cancellation;
                generation = connectionGeneration;
            }

            _ = Task.Run(async () =>
            {
                await previousShutdown.ConfigureAwait(false);
                await runConnection(username, passwordHash, generation, cancellation.Token).ConfigureAwait(false);
            });
        }

        private async Task runConnection(string username, string passwordHash, int generation, CancellationToken cancellationToken)
        {
            BanchoConnection activeConnection = null;

            try
            {
                activeConnection = new BanchoConnection(endpoints.BanchoUrl);
                lock (connectionLock)
                {
                    if (generation != connectionGeneration || cancellationToken.IsCancellationRequested)
                        return;

                    connection = activeConnection;
                }

                IReadOnlyList<BanchoPacket> initialPackets = await activeConnection.LoginAsync(username, passwordHash, cancellationToken).ConfigureAwait(false);
                if (!isCurrentConnection(generation, activeConnection, cancellationToken))
                    return;

                int userId = await scheduleOnUpdateThread(() => processPackets(initialPackets, activeConnection)).ConfigureAwait(false);

                if (userId <= 0)
                    throw new InvalidOperationException(getLoginError(userId));

                await scheduleOnUpdateThread(() =>
                {
                    updateLocalUser(userId, username);
                    state.Value = APIState.Online;
                    chatClient.NotifyPresenceReceived();
                }).ConfigureAwait(false);

                while (isCurrentConnection(generation, activeConnection, cancellationToken))
                {
                    try
                    {
                        IReadOnlyList<BanchoPacket> packets = await activeConnection.PollAsync(cancellationToken).ConfigureAwait(false);
                        if (!isCurrentConnection(generation, activeConnection, cancellationToken))
                            break;

                        await scheduleOnUpdateThread(() =>
                        {
                            processPackets(packets, activeConnection);
                            if (state.Value == APIState.Failing)
                                state.Value = APIState.Online;
                        }).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        if (!isCurrentConnection(generation, activeConnection, cancellationToken))
                            break;

                        await scheduleOnUpdateThread(() =>
                        {
                            LastLoginError = ex;
                            state.Value = APIState.Failing;
                        }).ConfigureAwait(false);
                        await Task.Delay(3000, cancellationToken).ConfigureAwait(false);
                    }

                    await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                if (isCurrentConnection(generation, activeConnection, cancellationToken))
                {
                    Logger.Error(ex, "Failed to connect to Bancho server");
                    await scheduleOnUpdateThread(() =>
                    {
                        if (!isCurrentConnection(generation, activeConnection, cancellationToken))
                            return;

                        LastLoginError = ex;
                        state.Value = APIState.Offline;
                        localUserState.ClearUser();
                    }).ConfigureAwait(false);
                }
            }
            finally
            {
                bool shouldDispose = false;
                lock (connectionLock)
                {
                    if (generation == connectionGeneration && ReferenceEquals(connection, activeConnection))
                    {
                        connection = null;
                        shouldDispose = true;
                    }
                }

                if (shouldDispose)
                    activeConnection?.Dispose();
            }
        }

        private Task scheduleOnUpdateThread(Action action)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Schedule(() =>
            {
                try
                {
                    action();
                    completion.SetResult();
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
            });
            return completion.Task;
        }

        private Task<T> scheduleOnUpdateThread<T>(Func<T> action)
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            Schedule(() =>
            {
                try
                {
                    completion.SetResult(action());
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
            });
            return completion.Task;
        }

        private bool isCurrentConnection(int generation, BanchoConnection activeConnection, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
                return false;

            lock (connectionLock)
                return generation == connectionGeneration && ReferenceEquals(connection, activeConnection);
        }

        private int processPackets(IReadOnlyList<BanchoPacket> packets, BanchoConnection activeConnection)
        {
            int loginUserId = int.MinValue;

            foreach (BanchoPacket packet in packets)
            {
                using var stream = new MemoryStream(packet.Payload, writable: false);
                using var reader = new BinaryReader(stream, Encoding.UTF8, false);

                try
                {
                    switch (packet.Type)
                    {
                        case ServerPacket.UserId:
                            loginUserId = reader.ReadInt32();
                            break;

                        case ServerPacket.Ping:
                            activeConnection.Enqueue(ClientPacket.Pong);
                            break;

                        case ServerPacket.SendMessage:
                            receiveMessage(reader);
                            break;

                        case ServerPacket.ChannelInfo:
                            registerChannel(readChannel(reader), false);
                            break;

                        case ServerPacket.ChannelAutoJoin:
                        case ServerPacket.ChannelJoinSuccess:
                            registerChannel(readChannel(reader), true);
                            break;

                        case ServerPacket.ChannelKicked:
                            partChannel(BanchoPacketCodec.ReadString(reader));
                            break;

                        case ServerPacket.UserPresence:
                            readUserPresence(reader);
                            break;

                        case ServerPacket.UserPresenceSingle:
                            requestUserPresences([reader.ReadInt32()]);
                            break;

                        case ServerPacket.UserStats:
                            readUserStats(reader);
                            break;

                        case ServerPacket.SpectatorJoined:
                        case ServerPacket.SpectatorLeft:
                        case ServerPacket.SpectateFrames:
                        case ServerPacket.SpectatorCantSpectate:
                        case ServerPacket.FellowSpectatorJoined:
                        case ServerPacket.FellowSpectatorLeft:
                            BanchoSpectatorPacketReceived?.Invoke(packet.Type, packet.Payload);
                            break;

                        case ServerPacket.UpdateMatch:
                        case ServerPacket.NewMatch:
                        case ServerPacket.MatchJoinSuccess:
                        case ServerPacket.MatchStart:
                        {
                            LegacyBanchoMatch match = LegacyBanchoMatchCodec.Read(reader);
                            lock (dataLock)
                                multiplayerMatches[match.ID] = match;
                            if (packet.Type == ServerPacket.MatchJoinSuccess)
                                currentLegacyMatchId = match.ID;
                            BanchoMultiplayerPacketReceived?.Invoke(packet.Type, packet.Payload);
                            break;
                        }

                        case ServerPacket.DisposeMatch:
                        {
                            int matchId = reader.ReadInt32();
                            lock (dataLock)
                                multiplayerMatches.Remove(matchId);
                            if (currentLegacyMatchId == matchId)
                                currentLegacyMatchId = -1;
                            BanchoMultiplayerPacketReceived?.Invoke(packet.Type, packet.Payload);
                            break;
                        }

                        case ServerPacket.MatchJoinFail:
                        case ServerPacket.AllPlayersLoaded:
                        case ServerPacket.MatchTransferHost:
                        case ServerPacket.MatchAllPlayersLoaded:
                        case ServerPacket.MatchPlayerFailed:
                        case ServerPacket.MatchComplete:
                        case ServerPacket.MatchSkip:
                        case ServerPacket.MatchPlayerSkipped:
                        case ServerPacket.MatchInvite:
                        case ServerPacket.MatchChangePassword:
                        case ServerPacket.MatchAbort:
                            BanchoMultiplayerPacketReceived?.Invoke(packet.Type, packet.Payload);
                            break;

                        case ServerPacket.MatchScoreUpdate:
                        {
                            int userId = 0;
                            if (packet.Payload.Length >= 5)
                            {
                                int slot = packet.Payload[4];
                                lock (dataLock)
                                {
                                    if (slot < LegacyBanchoMatch.SLOT_COUNT
                                        && multiplayerMatches.TryGetValue(currentLegacyMatchId, out LegacyBanchoMatch match))
                                        userId = match.SlotUserIDs[slot];
                                }
                            }

                            if (userId > 0)
                                BanchoMatchScoreFrameReceived?.Invoke(userId, packet.Payload);
                            BanchoMultiplayerPacketReceived?.Invoke(packet.Type, packet.Payload);
                            break;
                        }

                        case ServerPacket.UserLogout:
                        {
                            int userId = reader.ReadInt32();
                            byte newState = reader.BaseStream.Position < reader.BaseStream.Length ? reader.ReadByte() : (byte)0;

                            // State 1 means IRC left while the native osu! client remains connected.
                            if (newState != 1)
                                removeUserPresence(userId);
                            break;
                        }

                        case ServerPacket.UserPresenceBundle:
                            requestBundledUserPresences(reader);
                            break;

                        case ServerPacket.FriendsList:
                            readFriendsList(reader);
                            break;

                        case ServerPacket.Privileges:
                            currentBanchoPrivileges = reader.ReadInt32();
                            applyPrivileges(LocalUser.Value, currentBanchoPrivileges);
                            break;

                        case ServerPacket.ChannelInfoEnd:
                            chatClient.NotifyPresenceReceived();
                            break;

                        case ServerPacket.SilenceEnd:
                        {
                            int remainingSeconds = Math.Max(0, reader.ReadInt32());
                            silenceEnd = DateTimeOffset.UtcNow.AddSeconds(remainingSeconds);
                            break;
                        }

                        case ServerPacket.UserSilenced:
                            if (reader.ReadInt32() == LocalUser.Value.Id && silenceEnd <= DateTimeOffset.UtcNow)
                                silenceEnd = DateTimeOffset.MaxValue;
                            break;

                        case ServerPacket.UserDmBlocked:
                        case ServerPacket.TargetIsSilenced:
                        {
                            BanchoPacketCodec.ReadString(reader); // sender
                            BanchoPacketCodec.ReadString(reader); // message
                            string target = BanchoPacketCodec.ReadString(reader);
                            reader.ReadInt32(); // sender id
                            Logger.Log(packet.Type == ServerPacket.UserDmBlocked
                                ? $"Private message to {target} was blocked."
                                : $"Private message to {target} was rejected because the target is silenced.",
                                LoggingTarget.Network, LogLevel.Important);
                            break;
                        }

                        case ServerPacket.Unauthorized:
                            throw new InvalidOperationException("The server rejected this Bancho session.");

                        case ServerPacket.AccountRestricted:
                            // Stable remains connected in restricted mode. The
                            // server filters unavailable packets for this session.
                            IsAccountRestricted = true;
                            Logger.Log("The private Bancho account is restricted.", LoggingTarget.Network, LogLevel.Important);
                            break;

                        case ServerPacket.Notification:
                            Logger.Log($"Server notification: {BanchoPacketCodec.ReadString(reader)}", LoggingTarget.Network);
                            break;
                    }
                }
                catch (Exception ex) when (ex is EndOfStreamException or InvalidDataException)
                {
                    Logger.Error(ex, $"Malformed Bancho packet {(ushort)packet.Type}");
                }
            }

            return loginUserId;
        }

        private static string getLoginError(int userId) => userId switch
        {
            -1 => "Invalid username or password.",
            -2 => "The server rejected this stable client version.",
            -3 => "This account is banned.",
            -4 => "This account is not activated.",
            -5 => "The server failed to complete login.",
            -7 => "The server requires a password reset.",
            -8 => "The server requires account verification, which is not supported by this adapter.",
            _ => "The server returned an invalid login response.",
        };

        private void updateLocalUser(int id, string username)
        {
            APIUser user;
            lock (dataLock)
            {
                if (!users.TryGetValue(id, out user))
                    users[id] = user = new APIUser { Id = id, Username = username };
                else if (string.IsNullOrEmpty(user.Username))
                    user.Username = username;
            }

            applyPrivileges(user, currentBanchoPrivileges);
            localUserState.SetUser(user);
        }

        private void readUserPresence(BinaryReader reader)
        {
            int rawId = reader.ReadInt32();
            if (rawId == int.MinValue)
                throw new InvalidDataException("Invalid Bancho user ID.");

            int id = Math.Abs(rawId);
            string username = BanchoPacketCodec.ReadString(reader);
            reader.ReadByte(); // UTC offset + 24
            byte countryCode = reader.ReadByte();
            byte privilegesAndMode = reader.ReadByte();
            reader.ReadSingle();
            reader.ReadSingle();
            int rank = reader.ReadInt32();

            if (id <= 0 || string.IsNullOrWhiteSpace(username))
            {
                Logger.Log($"Ignoring invalid Bancho presence for user {id}.", LoggingTarget.Network, LogLevel.Important);
                return;
            }

            var user = new APIUser
            {
                Id = id,
                Username = username,
                WasRecentlyOnline = true,
                IsSupporter = (privilegesAndMode & 4) != 0,
                IsAdmin = (privilegesAndMode & (2 | 8 | 16)) != 0,
                CountryCode = convertStableCountryCode(countryCode),
                PlayMode = ((privilegesAndMode & 0xe0) >> 5) switch
                {
                    1 => "taiko",
                    2 => "fruits",
                    3 => "mania",
                    _ => "osu",
                },
                Rank = rank > 0 ? new APIUser.GlobalRank { Rank = rank, RulesetId = (privilegesAndMode & 0xe0) >> 5 } : null,
            };

            UserPresence presence;
            bool needsStats;
            lock (dataLock)
            {
                users[id] = user;
                if (!userPresences.TryGetValue(id, out presence))
                    userPresences[id] = presence = new UserPresence { Status = UserStatus.Online };

                needsStats = requestStatsAfterPresence.Remove(id);

                if (friendIds.Contains(id))
                    refreshFriends();
            }

            if (id == LocalUser.Value.Id)
                localUserState.SetUser(user);

            if (needsStats)
            {
                connection?.Enqueue(ClientPacket.RequestUserStats, writer =>
                    BanchoPacketCodec.WriteIntList(writer, new[] { id }));
            }
            BanchoPresenceChanged?.Invoke(id, presence);
        }

        private static void applyPrivileges(APIUser user, int privileges)
        {
            if (user == null)
                return;

            user.IsSupporter = (privileges & 4) != 0;
            user.IsAdmin = (privileges & (2 | 8 | 16)) != 0;
        }

        private void readUserStats(BinaryReader reader)
        {
            int id = reader.ReadInt32();
            byte action = reader.ReadByte();
            string statusText = BanchoPacketCodec.ReadString(reader);
            string beatmapHash = BanchoPacketCodec.ReadString(reader);
            uint stableMods = reader.ReadUInt32();
            int stableMode = reader.ReadByte();
            int mode = getBaseRulesetId(stableMode);
            int beatmapId = reader.ReadInt32();
            long rankedScore = reader.ReadInt64();
            float accuracy = reader.ReadSingle();
            int playCount = reader.ReadInt32();
            long totalScore = reader.ReadInt64();
            int rank = reader.ReadInt32();
            short performancePoints = reader.ReadInt16();

            UserPresence presence = new UserPresence
            {
                Status = UserStatus.Online,
                Activity = CreateLegacyActivity(action, statusText, beatmapId, mode),
            };

            bool hasUser;
            lock (dataLock)
            {
                hasUser = users.TryGetValue(id, out APIUser user) && !string.IsNullOrWhiteSpace(user.Username);
                if (hasUser)
                {
                    userPresences[id] = presence;
                    spectatorStatuses[id] = new LegacySpectatorStatus(action, beatmapId, beatmapHash, mode, stableMods);
                    user.Rank = rank > 0 ? new APIUser.GlobalRank { Rank = rank, RulesetId = mode } : null;
                    user.Statistics = new UserStatistics
                    {
                        User = user,
                        IsRanked = rank > 0,
                        GlobalRank = rank > 0 ? rank : null,
                        PP = performancePoints,
                        RankedScore = rankedScore,
                        // Bancho serialises accuracy in the 0..1 range while
                        // lazer's UserStatistics model stores percentage points.
                        Accuracy = accuracy * 100,
                        PlayCount = playCount,
                        TotalScore = totalScore,
                    };
                }
            }

            if (!hasUser)
            {
                lock (dataLock)
                    requestStatsAfterPresence.Add(id);
                requestUserPresences([id]);
                return;
            }

            BanchoPresenceChanged?.Invoke(id, presence);
        }

        internal static UserActivity CreateLegacyActivity(byte action, string text, int beatmapId, int mode)
        {
            string verb = mode switch
            {
                1 => "Playing osu!taiko",
                2 => "Playing osu!catch",
                3 => "Playing osu!mania",
                _ => "Playing osu!",
            };

            return action switch
            {
                2 => new UserActivity.InSoloGame { BeatmapID = beatmapId, BeatmapDisplayTitle = text, RulesetID = mode, RulesetPlayingVerb = verb },
                3 => new UserActivity.EditingBeatmap { BeatmapID = beatmapId, BeatmapDisplayTitle = text },
                4 => new UserActivity.ModdingBeatmap { BeatmapID = beatmapId, BeatmapDisplayTitle = text },
                // Stable keeps beatmap metadata in Multiplayer status packets,
                // but does not display it as the lobby name.
                5 or 11 => new UserActivity.InLobby { RoomID = -1, RoomName = string.Empty },
                6 => createSpectatingActivity(text, beatmapId),
                8 => new UserActivity.TestingBeatmap { BeatmapID = beatmapId, BeatmapDisplayTitle = text },
                10 => new UserActivity.InSoloGame { BeatmapID = beatmapId, BeatmapDisplayTitle = text, RulesetID = mode, RulesetPlayingVerb = verb },
                12 => new UserActivity.InMultiplayerGame { BeatmapID = beatmapId, BeatmapDisplayTitle = text, RulesetID = mode, RulesetPlayingVerb = verb },
                _ => null,
            };
        }

        private static UserActivity.SpectatingUser createSpectatingActivity(string text, int beatmapId)
        {
            // osu!stable serialises Watching as "<target> play <beatmap title>".
            // The target is not the player whose UserStats packet this is; using
            // that username would make every watcher appear to spectate themself.
            const string separator = " play ";
            int separatorIndex = text.IndexOf(separator, StringComparison.Ordinal);

            return new UserActivity.SpectatingUser
            {
                PlayerName = separatorIndex > 0 ? text[..separatorIndex] : string.Empty,
                BeatmapID = beatmapId,
                BeatmapDisplayTitle = separatorIndex > 0 ? text[(separatorIndex + separator.Length)..] : text,
            };
        }

        private void removeUserPresence(int id)
        {
            lock (dataLock)
            {
                userPresences.Remove(id);
                spectatorStatuses.Remove(id);
            }
            BanchoPresenceChanged?.Invoke(id, null);
        }

        private static CountryCode convertStableCountryCode(byte value)
        {
            if (value == 0 || value > stableCountryCodes.Length)
                return CountryCode.Unknown;

            string code = stableCountryCodes[value - 1].ToUpperInvariant();
            return code switch
            {
                "OC" => CountryCode.A1,
                "EU" or "A2" => CountryCode.A2,
                "O1" => CountryCode.Unknown,
                "XX" => CountryCode.Unknown,
                _ when Enum.TryParse(code, out CountryCode parsed) => parsed,
                _ => CountryCode.Unknown,
            };
        }

        internal void EnqueueBanchoPacket(ClientPacket type, Action<BinaryWriter> writePayload = null) => connection?.Enqueue(type, writePayload);

        internal void SendLegacyMultiplayerCommand(string command) =>
            connection?.Enqueue(ClientPacket.SendPublicMessage,
                writer => BanchoPacketCodec.WriteMessage(writer, ProvidedUsername, command, "#multiplayer", LocalUser.Value.Id));

        internal bool IsInLegacyMultiplayerMatch => currentLegacyMatchId >= 0;

        internal void LeftLegacyMultiplayerMatch() => currentLegacyMatchId = -1;

        internal void RequestUserStats(int id) => connection?.Enqueue(ClientPacket.RequestUserStats, writer =>
            BanchoPacketCodec.WriteIntList(writer, new[] { id }));

        internal SpectatorUser GetSpectatorUser(int id)
        {
            lock (dataLock)
            {
                APIUser user = users.GetValueOrDefault(id);
                return new SpectatorUser { OnlineID = id, Username = user?.Username ?? id.ToString() };
            }
        }

        internal bool HasPlayableSpectatorState(int id)
        {
            lock (dataLock)
                return spectatorStatuses.TryGetValue(id, out LegacySpectatorStatus status)
                       && IsSpectatableAction(status.Action)
                       && (status.BeatmapId != 0 || !string.IsNullOrEmpty(status.BeatmapHash));
        }

        internal SpectatorState GetSpectatorState(int id)
        {
            lock (dataLock)
            {
                if (!spectatorStatuses.TryGetValue(id, out LegacySpectatorStatus status))
                    return new SpectatorState { State = SpectatedUserState.Idle };

                Ruleset ruleset = getRuleset(status.RulesetId);
                var apiMods = ruleset.ConvertFromLegacyMods((osu.Game.Beatmaps.Legacy.LegacyMods)status.Mods).Select(mod => new APIMod(mod)).ToList();
                if (ruleset.CreateMod<ModClassic>() is ModClassic classic)
                    apiMods.Add(new APIMod(classic));

                return new SpectatorState
                {
                    // Preserve 0/-1 so the spectator screen can fall back to the
                    // checksum for local or otherwise unsubmitted beatmaps.
                    BeatmapID = status.BeatmapId,
                    BeatmapHash = status.BeatmapHash,
                    RulesetID = status.RulesetId,
                    Mods = apiMods,
                    State = IsSpectatableAction(status.Action)
                            && (status.BeatmapId != 0 || !string.IsNullOrEmpty(status.BeatmapHash))
                        ? SpectatedUserState.Playing
                        : SpectatedUserState.Idle,
                };
            }
        }

        internal static bool IsSpectatableAction(byte action) => action is 2 or 6 or 8 or 10 or 12;

        internal SpectatorState GetLegacyMultiplayerSpectatorState(int userId)
        {
            LegacyBanchoMatch match;
            lock (dataLock)
            {
                if (!multiplayerMatches.TryGetValue(currentLegacyMatchId, out LegacyBanchoMatch storedMatch))
                    return GetSpectatorState(userId);

                match = storedMatch.Clone();
            }

            int slot = Array.IndexOf(match.SlotUserIDs, userId);
            uint mods = match.Mods | (slot >= 0 && match.FreeMods ? match.SlotMods[slot] : 0);
            Ruleset ruleset = getRuleset(match.RulesetID);
            var apiMods = ruleset.ConvertFromLegacyMods((osu.Game.Beatmaps.Legacy.LegacyMods)mods).Select(mod => new APIMod(mod)).ToList();
            if (ruleset.CreateMod<ModClassic>() is ModClassic classic)
                apiMods.Add(new APIMod(classic));

            int objectCount = realm.Run(r =>
            {
                BeatmapInfo beatmap = r.All<BeatmapInfo>()
                                        .FirstOrDefault(b => b.OnlineID == match.BeatmapID || b.MD5Hash == match.BeatmapChecksum);
                return beatmap?.TotalObjectCount ?? 0;
            });
            var maximumStatistics = new Dictionary<osu.Game.Rulesets.Scoring.HitResult, int>();
            if (objectCount > 0)
                maximumStatistics[osu.Game.Rulesets.Scoring.HitResult.Great] = objectCount;

            return new SpectatorState
                {
                    BeatmapID = match.BeatmapID > 0 ? match.BeatmapID : null,
                    BeatmapHash = match.BeatmapChecksum,
                    RulesetID = match.RulesetID,
                    Mods = apiMods,
                    State = match.InProgress ? SpectatedUserState.Playing : SpectatedUserState.Idle,
                    MaximumStatistics = maximumStatistics,
                };
        }

        private void requestBundledUserPresences(BinaryReader reader)
        {
            int count = reader.ReadInt16();
            if (count < 0 || count > 100_000)
                throw new InvalidDataException("Invalid Bancho user presence bundle size.");

            int[] userIds = new int[count];
            for (int i = 0; i < count; i++)
                userIds[i] = reader.ReadInt32();

            requestUserPresences(userIds);
        }

        private void requestUserPresences(IEnumerable<int> requestedUserIds)
        {
            int[] userIds = requestedUserIds.Where(id => id > 0).Distinct().ToArray();
            const int users_per_request = 256;
            foreach (int[] batch in userIds.Chunk(users_per_request))
            {
                connection?.Enqueue(ClientPacket.UserPresenceRequest, writer =>
                    BanchoPacketCodec.WriteIntList(writer, batch));
            }
        }

        private void readFriendsList(BinaryReader reader)
        {
            int count = reader.ReadInt16();
            if (count < 0 || count > 100_000)
                throw new InvalidDataException("Invalid Bancho friends list size.");

            List<KeyValuePair<int, UserPresence>> onlineFriends;
            lock (dataLock)
            {
                friendIds.Clear();
                for (int i = 0; i < count; i++)
                    friendIds.Add(reader.ReadInt32());

                refreshFriends();
                onlineFriends = userPresences.Where(pair => friendIds.Contains(pair.Key)).ToList();
            }

            foreach ((int userId, UserPresence presence) in onlineFriends)
                BanchoPresenceChanged?.Invoke(userId, presence);
        }

        private Channel readChannel(BinaryReader reader)
        {
            string name = BanchoPacketCodec.ReadString(reader);
            string topic = reader.BaseStream.Position < reader.BaseStream.Length ? BanchoPacketCodec.ReadString(reader) : string.Empty;
            if (reader.BaseStream.Position + sizeof(int) <= reader.BaseStream.Length)
                reader.ReadInt32(); // user count

            return getOrCreateChannel(name, topic);
        }

        private Channel getOrCreateChannel(string name, string topic = "")
        {
            lock (dataLock)
            {
                if (channelsByName.TryGetValue(name, out Channel channel))
                {
                    if (!string.IsNullOrEmpty(topic))
                        channel.Topic = topic;
                    return channel;
                }

                long id = stableChannelId(name);
                channel = new Channel
                {
                    Id = id,
                    Name = name,
                    Topic = topic,
                    Type = name.Equals("#spectator", StringComparison.OrdinalIgnoreCase)
                        ? ChannelType.Spectator
                        : name.Equals("#multiplayer", StringComparison.OrdinalIgnoreCase)
                            ? ChannelType.Multiplayer
                            : ChannelType.Public,
                    MessageLengthLimit = 450,
                };
                channelsByName[name] = channel;
                channelsById[id] = channel;
                return channel;
            }
        }

        private void registerChannel(Channel channel, bool joined)
        {
            if (!joined || channel.Joined.Value)
                return;

            channel.Joined.Value = true;
            chatClient.NotifyChannelJoined(channel);
        }

        private void refreshFriends() =>
            localUserState.SetFriends(friendIds
                                           .Select(id => users.GetValueOrDefault(id))
                                           .Where(user => user != null && !string.IsNullOrWhiteSpace(user.Username)));

        private void partChannel(string name)
        {
            Channel channel;
            lock (dataLock)
                channelsByName.TryGetValue(name, out channel);

            if (channel == null)
                return;

            if (channel.Joined.Value)
            {
                channel.Joined.Value = false;
                chatClient.NotifyChannelParted(channel);
            }
        }

        private void receiveMessage(BinaryReader reader)
        {
            string senderName = BanchoPacketCodec.ReadString(reader);
            string content = BanchoPacketCodec.ReadString(reader);
            string target = BanchoPacketCodec.ReadString(reader);
            int senderId = reader.ReadInt32();

            if (senderId == LocalUser.Value.Id && consumeSentMessage(target, content))
                return;

            bool isAction = content.Length > 8 && content.StartsWith("\u0001ACTION ", StringComparison.Ordinal) && content.EndsWith('\u0001');
            if (isAction)
                content = content[8..^1];

            APIUser sender;
            lock (dataLock)
            {
                if (!users.TryGetValue(senderId, out sender))
                    users[senderId] = sender = new APIUser { Id = senderId, Username = senderName };
            }

            Channel channel;
            if (target.StartsWith('#'))
                channel = getOrCreateChannel(target);
            else
            {
                string counterpart = senderId == LocalUser.Value.Id ? target : senderName;
                channel = getOrCreatePrivateChannel(senderId == LocalUser.Value.Id ? findUserByName(counterpart) : sender);
            }

            if (!channel.Joined.Value)
                registerChannel(channel, true);

            chatClient.NotifyMessages(new List<Message>
            {
                new(Interlocked.Increment(ref nextMessageId))
                {
                    ChannelId = channel.Id,
                    Content = content,
                    DisplayContent = content,
                    IsAction = isAction,
                    Sender = sender,
                    Timestamp = DateTimeOffset.Now,
                },
            });
        }

        private APIUser findUserByName(string username)
        {
            lock (dataLock)
                return users.Values.FirstOrDefault(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase))
                       ?? new APIUser { Id = -Math.Abs(username.GetHashCode(StringComparison.OrdinalIgnoreCase)), Username = username };
        }

        private Channel getOrCreatePrivateChannel(APIUser user)
        {
            string key = $"@{user.Username}";
            lock (dataLock)
            {
                if (channelsByName.TryGetValue(key, out Channel existing))
                    return existing;

                var channel = new Channel(user)
                {
                    Id = 1_000_000_000L + Math.Abs((long)user.Id),
                    MessageLengthLimit = 450,
                };
                channelsByName[key] = channel;
                channelsById[channel.Id] = channel;
                return channel;
            }
        }

        private static long stableChannelId(string name)
        {
            uint hash = 2166136261;
            foreach (byte value in Encoding.UTF8.GetBytes(name.ToLowerInvariant()))
            {
                hash ^= value;
                hash *= 16777619;
            }
            // Room.ChannelId is a signed 32-bit value. Keep public channel IDs
            // in the same positive range so #multiplayer chat resolves to the
            // exact channel created from Bancho's join packet.
            long id = hash & int.MaxValue;
            return id == 0 ? 1 : id;
        }

        internal IReadOnlyDictionary<int, UserPresence> GetPresenceSnapshot()
        {
            lock (dataLock)
                return userPresences
                       .Where(pair => users.TryGetValue(pair.Key, out APIUser user) && !string.IsNullOrWhiteSpace(user.Username))
                       .ToDictionary(pair => pair.Key, pair => pair.Value);
        }

        internal bool IsFriend(int userId)
        {
            lock (dataLock)
                return friendIds.Contains(userId);
        }

        internal void UpdateLegacyStatus(UserActivity activity, UserStatus? status)
        {
            currentLegacyActivity = activity;
            // Stable's AFK status is driven by its idle timer, not by a user-facing
            // presence preference. Lazer's Do Not Disturb therefore stays Idle.
            currentLegacyAction = GetLegacyAction(activity);
            int? activityBeatmapId = activity?.GetBeatmapID();
            if (activityBeatmapId.HasValue)
                currentLegacyBeatmapId = activityBeatmapId.Value;
            if (activity is UserActivity.InGame inGame)
                currentLegacyMode = inGame.RulesetID;
            currentLegacyStatusText = getStableStatusText(activity);

            sendLegacyStatus();
        }

        internal void UpdateLegacySelection(RulesetInfo rulesetInfo, IReadOnlyList<Mod> mods, WorkingBeatmap beatmap)
        {
            Ruleset ruleset = rulesetInfo.CreateInstance();
            currentLegacyMode = rulesetInfo.OnlineID;
            currentLegacyMods = (uint)ruleset.ConvertToLegacyMods(mods.Where(mod => mod is not ModClassic and not ModNoMod && mod.UsesDefaultConfiguration).ToArray());

            (int beatmapId, _) = beatmap.GetOnlineIds();
            // Stable sends 0/-1 when the selected map has no online ID. Retaining
            // the previous map ID makes both status and spectating point at a
            // completely unrelated beatmap.
            currentLegacyBeatmapId = beatmapId;
            currentLegacyBeatmapTitle = getStableBeatmapDisplayTitle(beatmap.BeatmapInfo);
            currentLegacyBeatmapHash = beatmap.BeatmapInfo.MD5Hash ?? string.Empty;
            currentLegacyStatusText = getStableStatusText(currentLegacyActivity);
            sendLegacyStatus();
        }

        /// <summary>
        /// osu!stable sends <c>BeatmapManager.Current.DisplayTitle</c> in ChangeAction.
        /// Unlike lazer's <see cref="BeatmapInfoExtensions.GetDisplayTitle(IBeatmapInfo)"/>,
        /// that title never includes the beatmap creator.
        /// </summary>
        private static string getStableBeatmapDisplayTitle(IBeatmapInfo beatmapInfo)
        {
            string artist = beatmapInfo.Metadata.Artist;
            string title = beatmapInfo.Metadata.Title;
            string displayTitle = !string.IsNullOrEmpty(artist)
                ? $"{artist} - {title}"
                : title;

            if (!string.IsNullOrEmpty(beatmapInfo.DifficultyName))
                displayTitle += $" [{beatmapInfo.DifficultyName}]";

            return displayTitle;
        }

        private string getStableStatusText(UserActivity activity)
        {
            if (activity == null || currentLegacyAction is not (2 or 3 or 4 or 5 or 6 or 8 or 10 or 11 or 12 or 13))
                return string.Empty;

            string title = !string.IsNullOrEmpty(currentLegacyBeatmapTitle)
                ? currentLegacyBeatmapTitle
                : activity.GetDetails() ?? string.Empty;

            // Stable prefixes replay/spectator statuses with "<player> play ".
            // The drawable then prepends the action name ("Watching").
            if (activity is UserActivity.WatchingReplay replay && !string.IsNullOrEmpty(replay.PlayerName))
                return $"{replay.PlayerName} play {title}".TrimEnd();

            return title;
        }

        internal void UpdateLegacyGameplayMods(SpectatorState state)
        {
            if (state.RulesetID is not int rulesetId)
                return;

            Ruleset ruleset = getRuleset(rulesetId);
            Mod[] mods = state.Mods.Select(mod => mod.ToMod(ruleset)).ToArray();
            currentLegacyMods = (uint)ruleset.ConvertToLegacyMods(mods);
            currentLegacyAction = 2;
            currentLegacyMode = rulesetId;
            if (state.BeatmapID.HasValue)
                currentLegacyBeatmapId = state.BeatmapID.Value;
            if (!string.IsNullOrEmpty(state.BeatmapHash))
                currentLegacyBeatmapHash = state.BeatmapHash;
            sendLegacyStatus();
        }

        private void sendLegacyStatus()
        {
            connection?.Enqueue(ClientPacket.ChangeAction, writer =>
            {
                bool sendsBeatmap = currentLegacyAction is 2 or 3 or 4 or 5 or 6 or 8 or 10 or 11 or 12 or 13;
                writer.Write(currentLegacyAction);
                BanchoPacketCodec.WriteString(writer, sendsBeatmap ? currentLegacyStatusText : string.Empty);
                BanchoPacketCodec.WriteString(writer, sendsBeatmap ? currentLegacyBeatmapHash : string.Empty);
                // Stable still includes the selected map id and mods while Idle;
                // only its title/checksum are cleared on song-select/menu screens.
                writer.Write(currentLegacyAction == 1 ? 0u : currentLegacyMods);
                writer.Write((byte)Math.Clamp(currentLegacyMode, 0, 3));
                writer.Write(currentLegacyAction == 1 ? 0 : currentLegacyBeatmapId);
            });
        }

        internal static byte GetLegacyAction(UserActivity activity) => activity switch
        {
            UserActivity.InMultiplayerGame => 12,
            // The stable client uses Multiplayer (5) for Lobby, MatchSetup and
            // SelectMulti. Lobby (11) exists in the enum but is not emitted here.
            UserActivity.InLobby or UserActivity.SearchingForLobby => 5,
            UserActivity.TestingBeatmap => 8,
            UserActivity.SpectatingUser or UserActivity.WatchingReplay => 6,
            UserActivity.ModdingBeatmap => 4,
            UserActivity.EditingBeatmap => 3,
            UserActivity.InGame => 2,
            _ => 0,
        };

        public void Queue(APIRequest request)
        {
            request.AttachAPI(this);
            Schedule(() => handleRequest(request));
        }

        public void Perform(APIRequest request)
        {
            request.AttachAPI(this);
            handleRequest(request);
        }

        public Task PerformAsync(APIRequest request)
        {
            Perform(request);
            return Task.CompletedTask;
        }

        private void handleRequest(APIRequest request)
        {
            try
            {
                if (!IsLoggedIn)
                {
                    request.Fail(new InvalidOperationException("The server session is offline."));
                    return;
                }

                switch (request)
                {
                    case ListChannelsRequest list:
                    {
                        List<Channel> available;
                        lock (dataLock)
                            available = channelsByName.Values.Where(c => c.Type != ChannelType.PM).Distinct().ToList();

                        // Most stable servers explicitly send auto-join packets. For minimal private
                        // implementations which only send channel info, keep chat usable by joining one
                        // sensible public channel rather than leaving the whole listing inactive/grey.
                        if (!available.Any(channel => channel.Joined.Value))
                        {
                            Channel fallback = available.FirstOrDefault(channel =>
                                                   channel.Name.Equals("#osu", StringComparison.OrdinalIgnoreCase)
                                                   || channel.Name.Equals("#general", StringComparison.OrdinalIgnoreCase)
                                                   || channel.Name.Equals("#lobby", StringComparison.OrdinalIgnoreCase))
                                               ?? available.FirstOrDefault(channel => channel.Type == ChannelType.Public);

                            if (fallback != null)
                            {
                                connection?.Enqueue(ClientPacket.ChannelJoin, writer => BanchoPacketCodec.WriteString(writer, fallback.Name));
                                registerChannel(fallback, true);
                            }
                        }

                        list.TriggerSuccess(available);
                        break;
                    }

                    case JoinChannelRequest join:
                        connection?.Enqueue(ClientPacket.ChannelJoin, writer => BanchoPacketCodec.WriteString(writer, join.Channel.Name));
                        registerChannel(getOrCreateChannel(join.Channel.Name, join.Channel.Topic), true);
                        join.TriggerSuccess();
                        break;

                    case LeaveChannelRequest leave:
                        connection?.Enqueue(ClientPacket.ChannelLeave, writer => BanchoPacketCodec.WriteString(writer, leave.Channel.Name));
                        partChannel(leave.Channel.Name);
                        leave.TriggerSuccess();
                        break;

                    case PostMessageRequest post:
                    {
                        if (!sendMessage(post.Message, getChannelTarget(post.Message.ChannelId)))
                        {
                            post.Fail(new InvalidOperationException("Chat is unavailable while this account is silenced."));
                            break;
                        }
                        Message response = createEchoResponse(post.Message);
                        chatClient.NotifyMessages(new List<Message> { response });
                        post.TriggerSuccess(response);
                        break;
                    }

                    case CreateChannelRequest create:
                    {
                        Channel channel = getOrCreatePrivateChannel(create.Channel.Users.First());
                        create.TriggerSuccess(new APIChatChannel { ChannelID = checked((int)channel.Id), RecentMessages = new List<Message>() });
                        break;
                    }

                    case CreateNewPrivateMessageRequest createMessage:
                    {
                        Channel channel = getOrCreatePrivateChannel(createMessage.TargetUser);
                        if (!sendMessage(createMessage.OutgoingMessage, createMessage.TargetUser.Username))
                        {
                            createMessage.Fail(new InvalidOperationException("Chat is unavailable while this account is silenced."));
                            break;
                        }
                        Message response = createEchoResponse(createMessage.OutgoingMessage, channel.Id);
                        chatClient.NotifyChannelJoined(channel);
                        chatClient.NotifyMessages(new List<Message> { response });
                        createMessage.TriggerSuccess(new CreateNewPrivateMessageResponse { ChannelID = checked((int)channel.Id), Message = response });
                        break;
                    }

                    case GetMessagesRequest messages:
                        messages.TriggerSuccess(new List<Message>());
                        break;

                    case ChatAckRequest ack:
                        ack.TriggerSuccess(new ChatAckResponse());
                        break;

                    case MarkChannelAsReadRequest markRead:
                        markRead.TriggerSuccess();
                        break;

                    case LookupUsersRequest lookupUsers:
                        lock (dataLock)
                        {
                            lookupUsers.TriggerSuccess(new GetUsersResponse
                            {
                                Users = lookupUsers.UserIds.Select(findUserById).Where(user => user.Id != 0).ToList(),
                            });
                        }
                        break;

                    case GetUsersRequest getUsers:
                        lock (dataLock)
                        {
                            getUsers.TriggerSuccess(new GetUsersResponse
                            {
                                Users = getUsers.UserIds.Select(findUserById).Where(user => user.Id != 0).ToList(),
                            });
                        }
                        break;

                    case CreateSoloScoreRequest createScore:
                    {
                        Ruleset ruleset = getRuleset(createScore.RulesetId);
                        if (!LegacyModConversion.TryConvertForSubmission(ruleset, createScore.Mods, out _, out string error))
                            throw new InvalidOperationException(error);

                        createScore.TriggerSuccess(new APIScoreToken { ID = Interlocked.Increment(ref nextScoreToken) });
                        break;
                    }

                    case GetRoomsRequest getRooms:
                        getRooms.TriggerSuccess(getLegacyRooms());
                        break;

                    case GetRoomRequest getRoom:
                    {
                        Room room = getLegacyRoom(checked((int)getRoom.RoomId));
                        if (room == null)
                            getRoom.Fail(new InvalidOperationException($"Stable multiplayer room {getRoom.RoomId} does not exist."));
                        else
                            getRoom.TriggerSuccess(room);
                        break;
                    }

                    case GetScoresRequest getScores:
                        _ = fetchStableScores(getScores);
                        break;

                    case SearchBeatmapSetsRequest searchBeatmaps:
                        _ = searchStableBeatmaps(searchBeatmaps);
                        break;

                    case DownloadBeatmapSetRequest downloadBeatmapSet:
                        _ = downloadStableBeatmapSet(downloadBeatmapSet);
                        break;

                    case SubmitSoloScoreRequest submitScore:
                        _ = submitStableScore(submitScore);
                        break;

                    case GetUserRequest getUser:
                    {
                        APIUser user = int.TryParse(getUser.Lookup, out int id) ? findUserById(id) : findUserByName(getUser.Lookup);
                        if (user.Id == 0)
                            getUser.Fail(new InvalidOperationException($"User '{getUser.Lookup}' is not present on Bancho."));
                        else
                            getUser.TriggerSuccess(user);
                        break;
                    }

                    default:
                        request.Fail(new NotSupportedException($"{request.GetType().Name} requires lazer API v2 and is unavailable on a stable-only private server."));
                        break;
                }
            }
            catch (Exception ex)
            {
                request.Fail(ex);
            }
        }

        private APIUser findUserById(int id)
        {
            lock (dataLock)
                return users.TryGetValue(id, out APIUser user) ? user : new APIUser { Id = 0 };
        }

        private Ruleset getRuleset(int onlineId) =>
            rulesets.GetRuleset(getBaseRulesetId(onlineId))?.CreateInstance()
            ?? throw new InvalidOperationException($"No legacy ruleset with online ID {onlineId} is installed.");

        /// <summary>
        /// Stable uses extended mode IDs for Relax/Autopilot/cheat variants. Lazer's
        /// rulesets remain the four vanilla IDs, while the variant is represented by
        /// the legacy mod bitmask.
        /// </summary>
        private static int getBaseRulesetId(int stableMode) => stableMode switch
        {
            >= 4 and <= 7 => stableMode - 4,
            8 => 0,
            >= 12 and <= 15 => stableMode - 12,
            >= 16 and <= 19 => stableMode - 16,
            20 => 0,
            _ => Math.Clamp(stableMode, 0, 3),
        };

        private async Task fetchStableScores(GetScoresRequest request)
        {
            try
            {
                using var client = new LegacyScoreClient(endpoints.WebsiteUrl);
                APIScoresCollection result = await client.FetchScoresAsync(request, getRuleset(request.Ruleset.OnlineID), ProvidedUsername, currentPasswordHash, connectionCancellation.Token).ConfigureAwait(false);
                Schedule(() =>
                {
                    updateBeatmapMetadata(request);
                    request.TriggerSuccess(result);
                });
            }
            catch (Exception ex)
            {
                Schedule(() => request.Fail(ex));
            }
        }

        private async Task submitStableScore(SubmitSoloScoreRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(connection?.SessionToken))
                    throw new InvalidOperationException("The bancho session has no score submission token.");

                using var client = new LegacyScoreClient(endpoints.WebsiteUrl);
                MultiplayerScore result = await client.SubmitScoreAsync(request, getRuleset(request.Score.RulesetID), ProvidedUsername, currentPasswordHash,
                    connection.SessionToken, connectionCancellation.Token).ConfigureAwait(false);
                Schedule(() => request.TriggerSuccess(result));
            }
            catch (Exception ex)
            {
                Schedule(() => request.Fail(ex));
            }
        }

        private string getChannelTarget(long channelId)
        {
            lock (dataLock)
            {
                if (!channelsById.TryGetValue(channelId, out Channel channel))
                    throw new InvalidOperationException("The target Bancho channel is unknown.");
                return channel.Type == ChannelType.PM ? channel.Users.First().Username : channel.Name;
            }
        }

        private bool sendMessage(Message message, string target)
        {
            if (silenceEnd > DateTimeOffset.UtcNow)
                return false;

            ClientPacket packet = target.StartsWith('#') ? ClientPacket.SendPublicMessage : ClientPacket.SendPrivateMessage;
            string content = message.IsAction ? $"\u0001ACTION {message.Content}\u0001" : message.Content;
            lock (dataLock)
            {
                DateTimeOffset expiry = DateTimeOffset.UtcNow.AddMinutes(-1);
                while (sentMessages.TryPeek(out var pending) && pending.SentAt < expiry)
                    sentMessages.Dequeue();
                sentMessages.Enqueue((target, content, DateTimeOffset.UtcNow));
            }
            connection?.Enqueue(packet, writer => BanchoPacketCodec.WriteMessage(writer, ProvidedUsername, content, target, LocalUser.Value.Id));
            return true;
        }

        private bool consumeSentMessage(string target, string content)
        {
            lock (dataLock)
            {
                int count = sentMessages.Count;
                for (int i = 0; i < count; i++)
                {
                    var pending = sentMessages.Dequeue();
                    if (pending.Target.Equals(target, StringComparison.OrdinalIgnoreCase) && pending.Content == content)
                        return true;
                    sentMessages.Enqueue(pending);
                }
                return false;
            }
        }

        private Message createEchoResponse(Message original, long? channelId = null) => new(Interlocked.Increment(ref nextMessageId))
        {
            ChannelId = channelId ?? original.ChannelId,
            Content = original.Content,
            DisplayContent = original.Content,
            IsAction = original.IsAction,
            Sender = LocalUser.Value,
            Timestamp = DateTimeOffset.Now,
            Uuid = original.Uuid,
        };

        public void AuthenticateSecondFactor(string code) =>
            LastLoginError = new NotSupportedException("Stable Bancho authentication does not expose lazer's second-factor flow.");

        public void Logout() => disconnect(clearCredentials: true);

        private Task disconnect(bool clearCredentials)
        {
            CancellationTokenSource cancellation;
            BanchoConnection activeConnection;
            lock (connectionLock)
            {
                connectionGeneration++;
                cancellation = connectionCancellation;
                activeConnection = connection;
                connection = null;
            }

            cancellation.Cancel();

            if (activeConnection != null)
                pendingConnectionShutdown = sendLogoutAndDispose(activeConnection);

            state.Value = APIState.Offline;
            localUserState.ClearUser();
            IsAccountRestricted = false;
            currentBanchoPrivileges = 0;
            silenceEnd = default;

            int[] onlineUsers;
            lock (dataLock)
            {
                onlineUsers = userPresences.Keys.ToArray();
                userPresences.Clear();
                spectatorStatuses.Clear();
                multiplayerMatches.Clear();
                currentLegacyMatchId = -1;
                users.Clear();
                friendIds.Clear();
                requestStatsAfterPresence.Clear();
                sentMessages.Clear();
                channelsById.Clear();
                channelsByName.Clear();
            }

            foreach (int userId in onlineUsers)
                BanchoPresenceChanged?.Invoke(userId, null);

            if (clearCredentials)
            {
                currentPasswordHash = string.Empty;
                config.SetValue(OsuSetting.Token, string.Empty);
            }

            return pendingConnectionShutdown;
        }

        private async Task searchStableBeatmaps(SearchBeatmapSetsRequest request)
        {
            try
            {
                using var client = new LegacyDirectClient(endpoints.WebsiteUrl);
                SearchBeatmapSetsResponse result = await client.SearchAsync(request, ProvidedUsername, currentPasswordHash, connectionCancellation.Token).ConfigureAwait(false);
                Schedule(() => request.TriggerSuccess(result));
            }
            catch (Exception ex)
            {
                Schedule(() => request.Fail(ex));
            }
        }

        private async Task downloadStableBeatmapSet(DownloadBeatmapSetRequest request)
        {
            try
            {
                using var client = new LegacyDirectClient(endpoints.WebsiteUrl);
                string filename = await client.DownloadAsync(request, connectionCancellation.Token).ConfigureAwait(false);
                Schedule(() => request.TriggerLegacySuccess(filename));
            }
            catch (Exception ex)
            {
                Schedule(() => request.Fail(ex));
            }
        }

        void IAPIProvider.Schedule(Action action) => Schedule(action);

        public IHubClientConnector GetHubConnector(string clientName, string endpoint) => null;
        public IChatClient GetChatClient() => chatClient;

        internal LegacyBanchoMatch GetLegacyMatch(int id)
        {
            lock (dataLock)
                return multiplayerMatches.TryGetValue(id, out LegacyBanchoMatch match) ? match.Clone() : null;
        }

        internal IReadOnlyList<LegacyBanchoMatch> GetLegacyMatches()
        {
            lock (dataLock)
                return multiplayerMatches.Values.Select(match => match.Clone()).ToArray();
        }

        internal (int BeatmapID, string Checksum, string DisplayTitle) GetLegacySelection() =>
            (currentLegacyBeatmapId, currentLegacyBeatmapHash, currentLegacyBeatmapTitle);

        internal int GetLegacyChannelId(string name) => (int)stableChannelId(name);

        private List<Room> getLegacyRooms()
        {
            lock (dataLock)
                return multiplayerMatches.Values.Select(createLegacyRoom).ToList();
        }

        private Room getLegacyRoom(int id)
        {
            lock (dataLock)
                return multiplayerMatches.TryGetValue(id, out LegacyBanchoMatch match) ? createLegacyRoom(match) : null;
        }

        private Room createLegacyRoom(LegacyBanchoMatch match)
        {
            var beatmap = new APIBeatmap
            {
                OnlineID = match.BeatmapID,
                Checksum = match.BeatmapChecksum,
                RulesetID = Math.Clamp(match.RulesetID, (byte)0, (byte)3),
            };
            Ruleset ruleset = getRuleset(beatmap.RulesetID);
            var requiredMods = ruleset.ConvertFromLegacyMods((osu.Game.Beatmaps.Legacy.LegacyMods)match.Mods)
                                      .Select(mod => new APIMod(mod)).ToList();
            if (ruleset.CreateMod<ModClassic>() is ModClassic classic)
                requiredMods.Add(new APIMod(classic));

            var item = new PlaylistItem(beatmap)
            {
                ID = match.ID,
                OwnerID = match.HostID,
                RulesetID = beatmap.RulesetID,
                RequiredMods = requiredMods.ToArray(),
            };

            APIUser[] participants = match.SlotUserIDs.Where(id => id > 0)
                                                     .Select(findUserById)
                                                     .Where(user => user.Id > 0)
                                                     .ToArray();
            APIUser host = findUserById(match.HostID);
            return new Room
            {
                RoomID = match.ID,
                Name = match.Name,
                Password = match.HasPassword ? "*" : null,
                Host = host.Id > 0 ? host : null,
                Category = RoomCategory.Normal,
                MaxParticipants = LegacyBanchoMatch.SLOT_COUNT,
                ParticipantCount = match.SlotUserIDs.Count(id => id > 0),
                RecentParticipants = participants,
                Type = match.TeamType is 2 or 3 ? osu.Game.Online.Rooms.MatchType.TeamVersus : osu.Game.Online.Rooms.MatchType.HeadToHead,
                QueueMode = QueueMode.HostOnly,
                Status = match.InProgress ? RoomStatus.Playing : RoomStatus.Idle,
                Availability = RoomAvailability.Public,
                ChannelId = GetLegacyChannelId("#multiplayer"),
                Playlist = [item],
                CurrentPlaylistItem = item,
            };
        }

        public RegistrationRequest.RegistrationRequestErrors CreateAccount(string email, string username, string password) => new()
        {
            Message = "Account creation is not part of the stable Bancho protocol. Use your private server's website.",
        };

        protected override void Dispose(bool isDisposing)
        {
            connectionCancellation.Cancel();
            connection?.Dispose();
            base.Dispose(isDisposing);
        }

        private void updateBeatmapMetadata(GetScoresRequest request)
        {
            if (request.LegacyBeatmapStatus is not BeatmapOnlineStatus status)
                return;

            if (request.BeatmapInfo is BeatmapInfo detached && !detached.IsManaged)
            {
                if (detached.Status != BeatmapOnlineStatus.LocallyModified)
                    detached.Status = status;
                if (request.LegacyBeatmapId is int beatmapId)
                    detached.OnlineID = beatmapId;
                if (detached.BeatmapSet != null && request.LegacyBeatmapSetId is int beatmapSetId)
                    detached.BeatmapSet.OnlineID = beatmapSetId;
            }
            else if (request.BeatmapInfo is APIBeatmap apiBeatmap)
            {
                apiBeatmap.Status = status;
                if (request.LegacyBeatmapId is int beatmapId)
                    apiBeatmap.OnlineID = beatmapId;
                if (request.LegacyBeatmapSetId is int beatmapSetId)
                    apiBeatmap.OnlineBeatmapSetID = beatmapSetId;
            }

            string checksum = request.BeatmapInfo.MD5Hash;
            if (string.IsNullOrEmpty(checksum))
                return;

            realm.Write(r =>
            {
                BeatmapInfo managed = r.All<BeatmapInfo>().FirstOrDefault(b => b.MD5Hash == checksum);
                if (managed == null)
                    return;

                if (managed.Status != BeatmapOnlineStatus.LocallyModified)
                    managed.Status = status;
                if (request.LegacyBeatmapId is int beatmapId)
                    managed.OnlineID = beatmapId;
                if (managed.BeatmapSet != null)
                {
                    if (managed.BeatmapSet.Status != BeatmapOnlineStatus.LocallyModified)
                        managed.BeatmapSet.Status = status;
                    if (request.LegacyBeatmapSetId is int beatmapSetId)
                        managed.BeatmapSet.OnlineID = beatmapSetId;
                }
            });
        }

        private static async Task sendLogoutAndDispose(BanchoConnection activeConnection)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await activeConnection.LogoutAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to notify the private Bancho server of logout");
            }
            finally
            {
                activeConnection.Dispose();
            }
        }

        private readonly record struct LegacySpectatorStatus(byte Action, int BeatmapId, string BeatmapHash, int RulesetId, uint Mods);

        private sealed class LegacyLocalUserState : ILocalUserState
        {
            private readonly Bindable<APIUser> user = new(new GuestUser());
            private readonly BindableList<APIRelation> friends = new();
            private readonly BindableList<APIRelation> blocks = new();
            private readonly BindableList<int> favourites = new();

            public IBindable<APIUser> User => user;
            public IBindableList<APIRelation> Friends => friends;
            public IBindableList<APIRelation> Blocks => blocks;
            public IBindableList<int> FavouriteBeatmapSets => favourites;

            public void SetUser(APIUser value) => user.Value = value;

            public void SetFriends(IEnumerable<APIUser> users)
            {
                friends.Clear();
                friends.AddRange(users.Select(target => new APIRelation
                {
                    TargetID = target.Id,
                    TargetUser = target,
                    RelationType = RelationType.Friend,
                }));
            }
            public void ClearUser()
            {
                user.Value = new GuestUser();
                friends.Clear();
                blocks.Clear();
                favourites.Clear();
            }

            public void UpdateFriends()
            {
            }

            public void UpdateBlocks()
            {
            }

            public void UpdateFavouriteBeatmapSets()
            {
            }
        }
    }
}
