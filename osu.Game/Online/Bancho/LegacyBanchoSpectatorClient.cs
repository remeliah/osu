// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using osu.Framework.Bindables;
using osu.Framework.Logging;
using osu.Game.Online.API;
using osu.Game.Online.Rooms;
using osu.Game.Online.Spectator;
using osu.Game.Replays.Legacy;
using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring.Legacy;
using osu.Game.Users;

namespace osu.Game.Online.Bancho
{
    /// <summary>
    /// Bridges lazer's spectator model to osu!stable's Bancho replay-frame packets.
    /// </summary>
    public sealed partial class LegacyBanchoSpectatorClient : SpectatorClient
    {
        private readonly LegacyBanchoAPIAccess api;
        private readonly BindableBool isConnected = new();
        private int watchedUserId;
        private bool receivedPlay;
        private ushort sequence;
        private SpectatorState? currentPlayState;
        private int? cantSpectateBeatmapId;
        private string? cantSpectateBeatmapHash;

        public override IBindable<bool> IsConnected => isConnected;

        public LegacyBanchoSpectatorClient(LegacyBanchoAPIAccess api)
        {
            this.api = api;
            api.State.BindValueChanged(state => isConnected.Value = state.NewValue == APIState.Online, true);
            api.BanchoSpectatorPacketReceived += handlePacket;
            api.BanchoPresenceChanged += handlePresenceChanged;
            api.BanchoMatchScoreFrameReceived += handleMatchScoreFrame;
        }

        protected override Task<bool> BeginPlayingInternal(long? scoreToken, SpectatorState state)
        {
            currentPlayState = state;
            api.UpdateLegacyGameplayMods(state);
            sendBundle(ReplayAction.NewSong, null);
            return Task.FromResult(IsConnected.Value);
        }

        protected override Task SendFramesInternal(long? scoreToken, FrameDataBundle bundle)
        {
            sendBundle(ReplayAction.Standard, bundle);
            if (api.IsInLegacyMultiplayerMatch)
            {
                byte[] scoreFrame = LegacySpectatorFrameCodec.EncodeScoreFrame(bundle, getClassicScore(bundle));
                api.EnqueueBanchoPacket(ClientPacket.MatchScoreUpdate, writer => writer.Write(scoreFrame));
            }
            return Task.CompletedTask;
        }

        protected override Task EndPlayingInternal(long? scoreToken, SpectatedUserState finalState)
        {
            ReplayAction action = finalState switch
            {
                SpectatedUserState.Passed => ReplayAction.Completion,
                SpectatedUserState.Failed => ReplayAction.Fail,
                _ => ReplayAction.SongSelect,
            };
            sendBundle(action, null);
            currentPlayState = null;
            return Task.CompletedTask;
        }

        protected override Task WatchUserInternal(int userId)
        {
            if (userId == api.LocalUser.Value.Id)
            {
                // Stable treats selecting yourself as a request to stop the
                // current spectator session and never sends StartSpectating.
                if (watchedUserId > 0)
                    api.EnqueueBanchoPacket(ClientPacket.StopSpectating);

                watchedUserId = 0;
                receivedPlay = false;
                clearCantSpectateState();
                return Task.CompletedTask;
            }

            if (api.IsInLegacyMultiplayerMatch)
            {
                SpectatorState multiplayerState = api.GetLegacyMultiplayerSpectatorState(userId);
                if (multiplayerState.BeatmapID != null)
                    ((ISpectatorClient)this).UserBeganPlaying(userId, multiplayerState);
                return Task.CompletedTask;
            }

            watchedUserId = userId;
            receivedPlay = false;
            clearCantSpectateState();
            api.EnqueueBanchoPacket(ClientPacket.StartSpectating, writer => writer.Write(userId));
            api.RequestUserStats(userId);

            // Stable does not send a separate "begin watching" state packet.
            // Seed lazer's spectator screen from the authoritative UserStats
            // snapshot so its Start Watching button is enabled immediately.
            SpectatorState state = api.GetSpectatorState(userId);
            if (state.State == SpectatedUserState.Playing && state.BeatmapID != null)
                ((ISpectatorClient)this).UserBeganPlaying(userId, state);

            return Task.CompletedTask;
        }

        private void handlePresenceChanged(int userId, UserPresence? presence)
        {
            if (userId != watchedUserId || !presence.HasValue)
                return;

            SpectatorState state = api.GetSpectatorState(userId);
            if (state.State == SpectatedUserState.Playing && state.BeatmapID != null)
                ((ISpectatorClient)this).UserBeganPlaying(userId, state);
        }

        protected override Task StopWatchingUserInternal(int userId)
        {
            if (watchedUserId == userId)
            {
                api.EnqueueBanchoPacket(ClientPacket.StopSpectating);
                watchedUserId = 0;
                receivedPlay = false;
                clearCantSpectateState();
            }

            return Task.CompletedTask;
        }

        internal void NotifyBeatmapUnavailable(int userId, SpectatorState state)
        {
            if (userId != watchedUserId || !IsConnected.Value)
                return;

            if (cantSpectateBeatmapId == state.BeatmapID
                && string.Equals(cantSpectateBeatmapHash, state.BeatmapHash, StringComparison.OrdinalIgnoreCase))
                return;

            cantSpectateBeatmapId = state.BeatmapID;
            cantSpectateBeatmapHash = state.BeatmapHash;
            api.EnqueueBanchoPacket(ClientPacket.CantSpectate);
        }

        internal void NotifyBeatmapAvailable(int userId)
        {
            if (userId != watchedUserId || cantSpectateBeatmapId == null && cantSpectateBeatmapHash == null)
                return;

            clearCantSpectateState();

            // Stable repeats StartSpectating after a missing map is imported.
            // bancho.py uses this to notify the host that frame delivery can resume.
            api.EnqueueBanchoPacket(ClientPacket.StartSpectating, writer => writer.Write(userId));
        }

        private void clearCantSpectateState()
        {
            cantSpectateBeatmapId = null;
            cantSpectateBeatmapHash = null;
        }

        protected override Task DisconnectInternal() => Task.CompletedTask;

        public override Task Reconnect() => Task.CompletedTask;

        private void sendBundle(ReplayAction action, FrameDataBundle? bundle)
        {
            if (!IsConnected.Value)
                return;

            byte[] payload = LegacySpectatorFrameCodec.Encode(action, bundle, sequence++, bundle == null ? null : getClassicScore(bundle));
            api.EnqueueBanchoPacket(ClientPacket.SpectateFrames, writer => writer.Write(payload));
        }

        private int getClassicScore(FrameDataBundle bundle)
        {
            if (bundle.Header.LegacyTotalScore is long exactScore)
                return checked((int)Math.Clamp(exactScore, 0, int.MaxValue));

            SpectatorState? state = currentPlayState;
            if (state?.RulesetID == null)
                return checked((int)Math.Clamp(bundle.Header.TotalScore, 0, int.MaxValue));

            var score = new MultiplayerScore
            {
                RulesetId = state.RulesetID.Value,
                TotalScore = bundle.Header.TotalScore,
                MaximumStatistics = state.MaximumStatistics,
            };
            return checked((int)Math.Clamp(score.GetDisplayScore(ScoringMode.Classic), 0, int.MaxValue));
        }

        private void handlePacket(ServerPacket type, byte[] payload)
        {
            try
            {
                switch (type)
                {
                    case ServerPacket.SpectatorJoined:
                    {
                        int id = readUserId(payload);
                        ((ISpectatorClient)this).UserStartedWatching([api.GetSpectatorUser(id)]);
                        break;
                    }

                    case ServerPacket.SpectatorLeft:
                        ((ISpectatorClient)this).UserEndedWatching(readUserId(payload));
                        break;

                    case ServerPacket.SpectatorCantSpectate:
                        // This identifies another spectator who is missing the
                        // host's map. It is not a state change for the host.
                        break;

                    case ServerPacket.SpectateFrames when watchedUserId > 0:
                        receiveFrames(payload);
                        break;
                }
            }
            catch (Exception ex) when (ex is EndOfStreamException or InvalidDataException or ArgumentException)
            {
                Logger.Error(ex, "Malformed stable spectator packet");
            }
        }

        private void handleMatchScoreFrame(int userId, byte[] payload)
        {
            try
            {
                SpectatorState state = api.GetLegacyMultiplayerSpectatorState(userId);
                FrameHeader header = LegacySpectatorFrameCodec.DecodeScoreFrame(payload, state, out int frameTime);

                // Multiplayer score frames do not contain cursor/key frames, but
                // lazer's live leaderboard uses the replay-frame timestamp to
                // order score headers. Supply a timing-only frame so the stable
                // 29-byte update can flow through the normal spectator pipeline.
                var timingFrame = new LegacyReplayFrame(frameTime, null, null, ReplayButtonState.None);
                ((ISpectatorClient)this).UserSentFrames(userId, new FrameDataBundle(header, [timingFrame]));
            }
            catch (Exception ex) when (ex is EndOfStreamException or InvalidDataException or ArgumentException)
            {
                Logger.Error(ex, "Malformed stable multiplayer score frame");
            }
        }

        private void receiveFrames(byte[] payload)
        {
            LegacySpectatorFrameBundle bundle = LegacySpectatorFrameCodec.Decode(payload, api.GetSpectatorState(watchedUserId));

            if (bundle.Action == ReplayAction.NewSong || (!receivedPlay && bundle.Action == ReplayAction.Standard))
            {
                receivedPlay = true;
                ((ISpectatorClient)this).UserBeganPlaying(watchedUserId, api.GetSpectatorState(watchedUserId));
            }

            if (bundle.Frames.Count > 0)
                ((ISpectatorClient)this).UserSentFrames(watchedUserId, new FrameDataBundle(bundle.Header, bundle.Frames));

            SpectatedUserState? finalState = bundle.Action switch
            {
                ReplayAction.Completion => SpectatedUserState.Passed,
                ReplayAction.Fail => SpectatedUserState.Failed,
                ReplayAction.SongSelect => SpectatedUserState.Quit,
                _ => null,
            };

            if (finalState != null)
            {
                receivedPlay = false;
                SpectatorState state = api.GetSpectatorState(watchedUserId);
                state.State = finalState.Value;
                ((ISpectatorClient)this).UserFinishedPlaying(watchedUserId, state);
            }
        }

        private static int readUserId(byte[] payload)
        {
            if (payload.Length < sizeof(int))
                throw new InvalidDataException("Spectator user packet is truncated.");
            return BitConverter.ToInt32(payload);
        }

        protected override void Dispose(bool isDisposing)
        {
            api.BanchoSpectatorPacketReceived -= handlePacket;
            api.BanchoPresenceChanged -= handlePresenceChanged;
            api.BanchoMatchScoreFrameReceived -= handleMatchScoreFrame;
            base.Dispose(isDisposing);
        }
    }

    internal enum ReplayAction : byte
    {
        Standard = 0,
        NewSong = 1,
        Skip = 2,
        Completion = 3,
        Fail = 4,
        Pause = 5,
        Unpause = 6,
        SongSelect = 7,
        WatchingOther = 8,
    }

    internal readonly record struct LegacySpectatorFrameBundle(ReplayAction Action, List<LegacyReplayFrame> Frames, FrameHeader Header);

    internal static class LegacySpectatorFrameCodec
    {
        public static byte[] Encode(ReplayAction action, FrameDataBundle? bundle, ushort sequence, int? totalScoreOverride = null)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
            writer.Write(0); // random seed (protocol 18+)
            writer.Write(checked((ushort)(bundle?.Frames.Count ?? 0)));

            foreach (LegacyReplayFrame frame in bundle?.Frames ?? Array.Empty<LegacyReplayFrame>())
            {
                writer.Write((byte)frame.ButtonState);
                writer.Write((byte)0); // pre-taiko compatibility byte
                writer.Write(frame.MouseX ?? 0);
                writer.Write(frame.MouseY ?? 0);
                writer.Write(checked((int)Math.Round(frame.Time)));
            }

            writer.Write((byte)action);
            writeScoreFrame(writer, bundle, totalScoreOverride);
            writer.Write(sequence);
            return stream.ToArray();
        }

        public static byte[] EncodeScoreFrame(FrameDataBundle bundle, int? totalScoreOverride = null)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
            writeScoreFrame(writer, bundle, totalScoreOverride);
            return stream.ToArray();
        }

        public static LegacySpectatorFrameBundle Decode(byte[] payload, SpectatorState state)
        {
            using var reader = new BinaryReader(new MemoryStream(payload, writable: false), Encoding.UTF8);
            reader.ReadInt32(); // random seed
            int count = reader.ReadUInt16();
            var frames = new List<LegacyReplayFrame>(count);
            for (int i = 0; i < count; i++)
            {
                var buttons = (ReplayButtonState)reader.ReadByte();
                reader.ReadByte();
                float x = reader.ReadSingle();
                float y = reader.ReadSingle();
                int time = reader.ReadInt32();
                frames.Add(new LegacyReplayFrame(time, x, y, buttons));
            }

            var action = (ReplayAction)reader.ReadByte();
            FrameHeader header = readScoreFrame(reader, state);
            reader.ReadUInt16(); // sequence

            return new LegacySpectatorFrameBundle(action, frames, header);
        }

        public static FrameHeader DecodeScoreFrame(byte[] payload, SpectatorState state, out int frameTime)
        {
            using var reader = new BinaryReader(new MemoryStream(payload, writable: false), Encoding.UTF8);
            return readScoreFrame(reader, state, out frameTime);
        }

        private static FrameHeader readScoreFrame(BinaryReader reader, SpectatorState state)
            => readScoreFrame(reader, state, out _);

        private static FrameHeader readScoreFrame(BinaryReader reader, SpectatorState state, out int frameTime)
        {
            frameTime = reader.ReadInt32();
            reader.ReadByte(); // multiplayer slot
            int n300 = reader.ReadUInt16();
            int n100 = reader.ReadUInt16();
            int n50 = reader.ReadUInt16();
            int ngeki = reader.ReadUInt16();
            int nkatu = reader.ReadUInt16();
            int nmiss = reader.ReadUInt16();
            int classicScore = reader.ReadInt32();
            int maxCombo = reader.ReadUInt16();
            int combo = reader.ReadUInt16();
            reader.ReadBoolean(); // perfect
            reader.ReadByte(); // hp
            reader.ReadByte(); // tag byte
            bool scoreV2 = reader.ReadBoolean();
            double comboPortion = scoreV2 ? reader.ReadDouble() : 0;
            double bonusPortion = scoreV2 ? reader.ReadDouble() : 0;

            int mode = state.RulesetID ?? 0;
            int objectCount = state.MaximumStatistics.Where(pair => pair.Key.IsBasic()).Sum(pair => pair.Value);
            long totalScore = LegacyScoreClient.ConvertClassicToStandardised(mode, classicScore, objectCount);
            Dictionary<HitResult, int> statistics = createStatistics(mode, n300, n100, n50, ngeki, nkatu, nmiss);
            (double baseScore, double maximumBaseScore, int judgementCount) = calculateAccuracyValues(mode, n300, n100, n50, ngeki, nkatu, nmiss);
            double accuracy = maximumBaseScore > 0 ? baseScore / maximumBaseScore : 0;
            var header = new FrameHeader(totalScore, accuracy, combo, maxCombo, statistics,
                new ScoreProcessorStatistics
                {
                    BaseScore = baseScore,
                    MaximumBaseScore = maximumBaseScore,
                    AccuracyJudgementCount = judgementCount,
                    ComboPortion = comboPortion,
                    BonusPortion = bonusPortion,
                }, DateTimeOffset.UtcNow, state.Mods.ToArray(), totalScore, Array.Empty<int>(), classicScore);

            return header;
        }

        private static void writeScoreFrame(BinaryWriter writer, FrameDataBundle? bundle, int? totalScoreOverride = null)
        {
            FrameHeader? header = bundle?.Header;
            Dictionary<HitResult, int>? statistics = header?.Statistics;
            int time = bundle?.Frames.LastOrDefault() is { } frame ? checked((int)Math.Round(frame.Time)) : 0;
            writer.Write(time);
            writer.Write((byte)0);
            writer.Write(toUShort(get(statistics, HitResult.Great)));
            writer.Write(toUShort(get(statistics, HitResult.Ok)));
            writer.Write(toUShort(get(statistics, HitResult.Meh)));
            writer.Write(toUShort(get(statistics, HitResult.Perfect)));
            writer.Write(toUShort(get(statistics, HitResult.Good)));
            writer.Write(toUShort(get(statistics, HitResult.Miss)));
            writer.Write(totalScoreOverride ?? checked((int)Math.Clamp(header?.TotalScore ?? 0, 0, int.MaxValue)));
            writer.Write(toUShort(header?.MaxCombo ?? 0));
            writer.Write(toUShort(header?.Combo ?? 0));
            writer.Write(false);
            writer.Write(byte.MaxValue);
            writer.Write((byte)0);
            writer.Write(false);
        }

        private static int get(Dictionary<HitResult, int>? statistics, HitResult result) => statistics?.GetValueOrDefault(result) ?? 0;

        private static ushort toUShort(int value) => checked((ushort)Math.Clamp(value, 0, ushort.MaxValue));

        private static Dictionary<HitResult, int> createStatistics(int mode, int n300, int n100, int n50, int ngeki, int nkatu, int nmiss)
        {
            var result = new Dictionary<HitResult, int> { [HitResult.Great] = n300, [HitResult.Miss] = nmiss };
            switch (mode)
            {
                case 0:
                    result[HitResult.Ok] = n100;
                    result[HitResult.Meh] = n50;
                    break;
                case 1:
                    result[HitResult.Ok] = n100;
                    result[HitResult.LargeBonus] = ngeki + nkatu;
                    break;
                case 2:
                    result[HitResult.LargeTickHit] = n100;
                    result[HitResult.SmallTickHit] = n50;
                    result[HitResult.SmallTickMiss] = nkatu;
                    break;
                case 3:
                    result[HitResult.Perfect] = ngeki;
                    result[HitResult.Good] = nkatu;
                    result[HitResult.Ok] = n100;
                    result[HitResult.Meh] = n50;
                    break;
            }
            return result;
        }

        private static (double Base, double Maximum, int Count) calculateAccuracyValues(int mode, int n300, int n100, int n50, int ngeki, int nkatu, int nmiss)
        {
            int count = mode switch
            {
                2 => n300 + n100 + n50 + nkatu + nmiss,
                3 => n300 + n100 + n50 + ngeki + nkatu + nmiss,
                _ => n300 + n100 + n50 + nmiss,
            };
            double score = mode switch
            {
                1 => n300 + n100 * 0.5,
                2 => n300 + n100 + n50,
                3 => n300 * 300d + ngeki * 300d + nkatu * 200d + n100 * 100d + n50 * 50d,
                _ => n300 * 300d + n100 * 100d + n50 * 50d,
            };
            return mode is 1 or 2 ? (score, count, count) : (score, count * 300d, count);
        }
    }
}
