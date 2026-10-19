// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using osu.Game.Beatmaps.Legacy;
using osu.Game.Beatmaps;
using osu.Game.Online;
using osu.Game.Online.API;
using osu.Game.Online.API.Requests;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Online.Bancho;
using osu.Game.Online.Chat;
using osu.Game.Online.Spectator;
using osu.Game.Rulesets.Osu;
using osu.Game.Screens.Play.Leaderboards;
using osu.Game.Users;

namespace osu.Game.Tests.Online.Bancho
{
    [TestFixture]
    public class BanchoPacketCodecTest
    {
        [Test]
        public void TestPacketHeaderAndPayload()
        {
            byte[] encoded = BanchoPacketCodec.Encode(ClientPacket.ChannelJoin, writer => BanchoPacketCodec.WriteString(writer, "#osu"));

            Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(encoded), Is.EqualTo((ushort)ClientPacket.ChannelJoin));
            Assert.That(encoded[2], Is.Zero);
            Assert.That(BinaryPrimitives.ReadInt32LittleEndian(encoded.AsSpan(3)), Is.EqualTo(encoded.Length - 7));

            using var reader = new BinaryReader(new MemoryStream(encoded[7..]), Encoding.UTF8);
            Assert.That(BanchoPacketCodec.ReadString(reader), Is.EqualTo("#osu"));
        }

        [TestCase("")]
        [TestCase("ascii")]
        [TestCase("日本語とemoji 🎵")]
        [TestCaseSource(nameof(longStrings))]
        public void TestStringRoundTrip(string value)
        {
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
                BanchoPacketCodec.WriteString(writer, value);

            stream.Position = 0;
            using var reader = new BinaryReader(stream, Encoding.UTF8);
            Assert.That(BanchoPacketCodec.ReadString(reader), Is.EqualTo(value));
        }

        [Test]
        public void TestDecodesMultiplePackets()
        {
            byte[] first = makeServerPacket(ServerPacket.UserId, BitConverter.GetBytes(123));
            byte[] second = makeServerPacket(ServerPacket.Ping, Array.Empty<byte>());

            var decoded = BanchoPacketCodec.Decode(first.Concat(second).ToArray());
            Assert.That(decoded, Has.Count.EqualTo(2));
            Assert.That(BinaryPrimitives.ReadInt32LittleEndian(decoded[0].Payload), Is.EqualTo(123));
            Assert.That(decoded[1].Type, Is.EqualTo(ServerPacket.Ping));
        }

        [Test]
        public void TestStableIntListEncoding()
        {
            byte[] encoded = BanchoPacketCodec.Encode(ClientPacket.RequestUserStats, writer =>
                BanchoPacketCodec.WriteIntList(writer, new[] { 12, 3456 }));

            using var reader = new BinaryReader(new MemoryStream(encoded[7..]), Encoding.UTF8);
            Assert.That(reader.ReadInt16(), Is.EqualTo(2));
            Assert.That(reader.ReadInt32(), Is.EqualTo(12));
            Assert.That(reader.ReadInt32(), Is.EqualTo(3456));
            Assert.That(reader.BaseStream.Position, Is.EqualTo(reader.BaseStream.Length));
        }

        [Test]
        public void TestRejectsTruncatedPacket() =>
            Assert.Throws<InvalidDataException>(() => BanchoPacketCodec.Decode(new byte[] { 5, 0, 0, 4, 0, 0, 0, 1 }));

        [TestCase("password", "5f4dcc3b5aa765d61d8327deb882cf99")]
        [TestCase("pásswörd", "1223d14386c6b7811f59c6b6b282bea1")]
        public void TestStablePasswordHash(string password, string expectedHash) =>
            Assert.That(LegacyBanchoAPIAccess.HashPassword(password), Is.EqualTo(expectedHash));

        [Test]
        public void TestStableClientIdentityIsInternallyConsistent()
        {
            string[] fields = BanchoClientIdentity.LoginHashes.Split(':');
            Assert.That(fields, Has.Length.EqualTo(6));
            Assert.That(fields[0], Does.Match("^[0-9a-f]{32}$"));
            Assert.That(fields[1], Is.Not.Empty.And.Not.EqualTo("lazer-private"));
            Assert.That(fields[2], Is.EqualTo(md5(fields[1])));
            Assert.That(fields[3], Is.EqualTo(md5(BanchoClientIdentity.UninstallId)));
            Assert.That(fields[4], Is.EqualTo(md5(BanchoClientIdentity.DiskId)));
            Assert.That(fields[5], Is.Empty);
            Assert.That(BanchoClientIdentity.UninstallId, Does.Match("^[0-9a-f]{32}$"));
            Assert.That(BanchoClientIdentity.DiskId, Does.Match("^[0-9a-f]{32}$"));
            Assert.That(BanchoClientIdentity.ScoreClientHash, Is.EqualTo(BanchoClientIdentity.LoginHashes));
        }

        [Test]
        public void TestStableClientVersionMatchesReferenceSnapshot() =>
            Assert.That(BanchoConnection.STABLE_VERSION, Is.EqualTo("b20260711"));

        [Test]
        public void TestStableLogoutCarriesReservedInteger()
        {
            byte[] encoded = BanchoConnection.CreateLogoutPacket();

            Assert.Multiple(() =>
            {
                Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(encoded), Is.EqualTo((ushort)ClientPacket.Logout));
                Assert.That(BinaryPrimitives.ReadInt32LittleEndian(encoded.AsSpan(3)), Is.EqualTo(sizeof(int)));
                Assert.That(BinaryPrimitives.ReadInt32LittleEndian(encoded.AsSpan(7)), Is.Zero);
            });
        }

        [Test]
        public void TestStableLobbyUsesMultiplayerAction()
        {
            Assert.That(LegacyBanchoAPIAccess.GetLegacyAction(new UserActivity.InLobby()), Is.EqualTo(5));
            Assert.That(LegacyBanchoAPIAccess.GetLegacyAction(new UserActivity.SearchingForLobby()), Is.EqualTo(5));
        }

        [Test]
        public void TestStableWatchingStatusSeparatesTargetAndBeatmap()
        {
            const string title = "artist - title [difficulty]";
            var activity = (UserActivity.SpectatingUser)LegacyBanchoAPIAccess.CreateLegacyActivity(6, $"target user play {title}", 1234, 0);

            Assert.Multiple(() =>
            {
                Assert.That(activity.PlayerName, Is.EqualTo("target user"));
                Assert.That(activity.BeatmapDisplayTitle, Is.EqualTo(title));
                Assert.That(activity.BeatmapID, Is.EqualTo(1234));
            });
        }

        [Test]
        public void TestStableMultiplayerStatusDoesNotExposeBeatmapAsRoomName()
        {
            var activity = (UserActivity.InLobby)LegacyBanchoAPIAccess.CreateLegacyActivity(5, "artist - title [difficulty]", 1234, 0);
            Assert.That(activity.RoomName, Is.Empty);
        }

        [TestCase(0, false)] // Idle can carry a selected map, but sends no frames.
        [TestCase(2, true)]  // Playing
        [TestCase(5, false)] // Multiplayer lobby
        [TestCase(6, true)]  // Watching
        [TestCase(8, true)]  // Testing
        [TestCase(10, true)] // Paused
        [TestCase(12, true)] // Multiplaying
        public void TestStableSpectatableActions(byte action, bool expected) =>
            Assert.That(LegacyBanchoAPIAccess.IsSpectatableAction(action), Is.EqualTo(expected));

        [Test]
        public void TestStableScoreRijndaelEncryption()
        {
            byte[] key = Encoding.UTF8.GetBytes("osu!-scoreburgr---------20260312");
            byte[] iv = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();

            string encrypted = Convert.ToBase64String(LegacyScoreClient.EncryptRijndael256(Encoding.UTF8.GetBytes("test:score:payload"), key, iv));
            Assert.That(encrypted, Is.EqualTo("rFtkkXhECBJ+maQc9GgBtg0KBmyOF0Pfl87v/Opkh1c="));
        }

        [Test]
        public void TestStableOnlineScoreChecksum()
        {
            string checksum = LegacyScoreClient.ComputeOnlineChecksum(300, 20, 5, 3, 2, 1, new string('a', 32), 400, false,
                "test user", 1234567, "A", (LegacyMods)72, true, 0, "260922123456", "client");

            Assert.That(checksum, Is.EqualTo("941b0ab9307de32803ac6a9229360857"));
        }

        [Test]
        public void TestStableScoreEndpointRetainsEncodedQuery()
        {
            Uri endpoint = LegacyScoreClient.ResolveRequestEndpoint(new Uri("https://osu.private.example/custom"), "web/osu-osz2-getscores.php?us=test+user&mods=72");

            Assert.That(endpoint.AbsoluteUri, Is.EqualTo("https://osu.private.example/custom/web/osu-osz2-getscores.php?us=test+user&mods=72"));
        }

        [TestCase("https://osu.local.sh/custom/path")]
        [TestCase("http://osu.local.sh")]
        public void TestNowPlayingMatchesBanchoParser(string websiteUrl)
        {
            string link = NowPlayingCommand.FormatBanchoBeatmapLink(websiteUrl, 123, 456, "Artist - Title [Difficulty]");
            string message = $"\u0001ACTION is listening to {link} +Hidden -NoFail\u0001";
            var banchoPattern = new Regex(
                @"^\x01ACTION is (?:playing|editing|watching|listening to) \[https?://osu\.(?:ppy\.sh|refx\.online)/beatmapsets/(?<sid>\d{1,10})#/?(?:osu|taiko|fruits|mania)?/(?<bid>\d{1,10})");

            Match match = banchoPattern.Match(message);
            Assert.That(match.Success, Is.True);
            Assert.That(match.Groups["sid"].Value, Is.EqualTo("123"));
            Assert.That(match.Groups["bid"].Value, Is.EqualTo("456"));
        }

        [TestCase("https://osu.ppy.sh")]
        [TestCase("https://c.ppy.sh")]
        [TestCase("https://OSU.PPY.SH./somewhere")]
        [TestCase("ftp://private.example.com")]
        [TestCase("https://private.example.com?redirect=https://osu.ppy.sh")]
        public void TestRejectsNonPrivateEndpoint(string url) =>
            Assert.Throws<ArgumentException>(() => PrivateServerEndpointValidator.Validate(url));

        [TestCase("https://bancho.example.com")]
        [TestCase("http://127.0.0.1:8080")]
        [TestCase("https://example.com/custom/bancho")]
        public void TestAcceptsPrivateEndpoint(string url) =>
            Assert.DoesNotThrow(() => PrivateServerEndpointValidator.Validate(url));

        [Test]
        public void TestInvalidConfiguredEndpointFailsClosed()
        {
            var endpoints = new PrivateServerEndpointConfiguration("https://private.example.com");

            Assert.Throws<ArgumentException>(() => endpoints.SetServerUrl("https://osu.ppy.sh"));
            Assert.That(endpoints.BanchoUrl, Is.EqualTo("https://c.private.example.com"));
            Assert.That(endpoints.APIUrl, Is.EqualTo("https://osu.private.example.com"));
            Assert.That(endpoints.ValidationError, Is.Not.Null);
        }

        [Test]
        public void TestInvalidSavedEndpointDoesNotPreventStartup()
        {
            PrivateServerEndpointConfiguration? endpoints = null;
            Assert.DoesNotThrow(() => endpoints = new PrivateServerEndpointConfiguration("https://c.ppy.sh"));
            Assert.That(endpoints!.BanchoUrl, Is.Empty);
            Assert.That(endpoints.ValidationError, Is.Not.Null);
        }

        [Test]
        public void TestConfigurableBanchoWebsiteAndResourceOrigins()
        {
            var endpoints = new PrivateServerEndpointConfiguration("local.sh");

            Assert.That(endpoints.Domain, Is.EqualTo("local.sh"));
            Assert.That(endpoints.BanchoUrl, Is.EqualTo("https://c.local.sh"));
            Assert.That(endpoints.WebsiteUrl, Is.EqualTo("https://osu.local.sh"));
            Assert.That(endpoints.APIUrl, Is.EqualTo("https://osu.local.sh"));
            Assert.That(endpoints.GetAvatarUrl(123), Is.EqualTo("https://a.local.sh/123"));
            Assert.That(endpoints.AllowedOrigins.Select(uri => uri.Host), Is.EquivalentTo(new[] { "c.local.sh", "osu.local.sh", "a.local.sh" }));
        }

        [Test]
        public void TestAdvancedServerEndpoints()
        {
            var endpoints = new PrivateServerEndpointConfiguration(
                "local.sh",
                "cho.example.net:8443",
                "https://web.example.net/osu",
                "avatars.example.net");

            Assert.That(endpoints.Domain, Is.EqualTo("local.sh"));
            Assert.That(endpoints.BanchoUrl, Is.EqualTo("https://cho.example.net:8443"));
            Assert.That(endpoints.WebsiteUrl, Is.EqualTo("https://web.example.net/osu"));
            Assert.That(endpoints.GetAvatarUrl(123), Is.EqualTo("https://avatars.example.net/123"));
            Assert.That(endpoints.AllowedOrigins.Select(uri => uri.GetLeftPart(UriPartial.Authority)), Is.EquivalentTo(new[]
            {
                "https://cho.example.net:8443",
                "https://web.example.net",
                "https://avatars.example.net",
            }));
        }

        [Test]
        public void TestAdvancedOfficialEndpointIsRejectedAtomically()
        {
            var endpoints = new PrivateServerEndpointConfiguration("local.sh");

            Assert.Throws<ArgumentException>(() => endpoints.SetServerUrls("other.example", "c.other.example", "osu.ppy.sh", "a.other.example"));
            Assert.That(endpoints.Domain, Is.EqualTo("local.sh"));
            Assert.That(endpoints.BanchoUrl, Is.EqualTo("https://c.local.sh"));
            Assert.That(endpoints.WebsiteUrl, Is.EqualTo("https://osu.local.sh"));
        }

        [Test]
        public void TestRankedStableLeaderboardResponseIsParsed()
        {
            var ruleset = new OsuRuleset();
            var beatmap = new BeatmapInfo
            {
                OnlineID = -1,
                MD5Hash = new string('a', 32),
                Ruleset = ruleset.RulesetInfo,
                Metadata = new BeatmapMetadata { Artist = "Artist", Title = "Title" },
            };
            var request = new GetScoresRequest(beatmap, ruleset.RulesetInfo, BeatmapLeaderboardScope.Global, allowMissingOnlineId: true);
            const string response = "2|false|123|456|1|0|\n0\nArtist - Title [Difficulty]\n0.0\n\n42|player|123456|100|0|0|100|0|0|0|1|0|7|1|2026-09-23 00:00:00|1";

            var scores = LegacyScoreClient.ParseScoresResponse(response, request, ruleset);

            Assert.That(scores.ScoresCount, Is.EqualTo(1));
            Assert.That(scores.Scores, Has.Count.EqualTo(1));
            Assert.That(scores.Scores.Single().BeatmapID, Is.EqualTo(123));
            Assert.That(scores.Scores.Single().LegacyScoreId, Is.EqualTo(42));
            Assert.That(request.LegacyBeatmapStatus, Is.EqualTo(BeatmapOnlineStatus.Ranked));
            Assert.That(request.LegacyBeatmapId, Is.EqualTo(123));
            Assert.That(request.LegacyBeatmapSetId, Is.EqualTo(456));
        }

        [Test]
        public void TestPendingStableLeaderboardResponseIsEmptyButUnavailable()
        {
            var ruleset = new OsuRuleset();
            var beatmap = new BeatmapInfo
            {
                OnlineID = -1,
                MD5Hash = new string('a', 32),
                Ruleset = ruleset.RulesetInfo,
                Metadata = new BeatmapMetadata { Artist = "Artist", Title = "Title" },
            };
            var request = new GetScoresRequest(beatmap, ruleset.RulesetInfo, BeatmapLeaderboardScope.Global, allowMissingOnlineId: true);

            APIScoresCollection scores = LegacyScoreClient.ParseScoresResponse("0|false|123|456", request, ruleset);

            Assert.That(scores.Scores, Is.Empty);
            Assert.That(scores.ScoresCount, Is.Zero);
            Assert.That(request.LegacyBeatmapStatus, Is.EqualTo(BeatmapOnlineStatus.Pending));
        }

        [Test]
        public void TestStableSubmissionOverallStatisticsAreParsed()
        {
            const string response = "beatmapId:123|\n"
                                    + "|chartId:beatmap|rankBefore:4|rankAfter:1|rankedScoreBefore:20000|rankedScoreAfter:26810|onlineScoreId:42|\n"
                                    + "|chartId:overall|rankBefore:15|rankAfter:12|rankedScoreBefore:100000|rankedScoreAfter:126810|totalScoreBefore:200000|totalScoreAfter:226810|maxComboBefore:50|maxComboAfter:52|accuracyBefore:80.12|accuracyAfter:81.94|ppBefore:9.5|ppAfter:11|";

            var user = new APIUser { Id = 6, Username = "test" };
            var (before, after) = LegacyScoreClient.ParseSubmissionStatistics(response, user);

            Assert.That(before, Is.Not.Null);
            Assert.That(after, Is.Not.Null);
            Assert.Multiple(() =>
            {
                Assert.That(before!.GlobalRank, Is.EqualTo(15));
                Assert.That(after!.GlobalRank, Is.EqualTo(12));
                Assert.That(before.RankedScore, Is.EqualTo(100000));
                Assert.That(after.RankedScore, Is.EqualTo(126810));
                Assert.That(before.TotalScore, Is.EqualTo(200000));
                Assert.That(after.TotalScore, Is.EqualTo(226810));
                Assert.That(before.MaxCombo, Is.EqualTo(50));
                Assert.That(after.MaxCombo, Is.EqualTo(52));
                Assert.That(before.Accuracy, Is.EqualTo(80.12));
                Assert.That(after.Accuracy, Is.EqualTo(81.94));
                Assert.That(before.PP, Is.EqualTo(9.5m));
                Assert.That(after.PP, Is.EqualTo(11m));
                Assert.That(after.User, Is.SameAs(user));
            });
        }

        [TestCase("local.sh")]
        [TestCase("https://c.local.sh")]
        [TestCase("https://osu.local.sh/")]
        public void TestNormalizesPrivateServerDomain(string value)
        {
            Assert.That(PrivateServerEndpointValidator.NormalizeDomain(value), Is.EqualTo("local.sh"));
        }

        [Test]
        public void TestStableSpectatorFrameBundleRoundTrip()
        {
            byte[] encoded = LegacySpectatorFrameCodec.Encode(ReplayAction.NewSong, null, 42);
            LegacySpectatorFrameBundle decoded = LegacySpectatorFrameCodec.Decode(encoded, new SpectatorState { RulesetID = 0 });

            Assert.That(decoded.Action, Is.EqualTo(ReplayAction.NewSong));
            Assert.That(decoded.Frames, Is.Empty);
            Assert.That(decoded.Header.TotalScore, Is.Zero);
        }

        [Test]
        public void TestStableMultiplayerScoreFrameRetainsTimestamp()
        {
            byte[] payload = new byte[29];
            BinaryPrimitives.WriteInt32LittleEndian(payload, 12345);
            payload[4] = 3;
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(5), 100);
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(17), 987654);
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(21), 321);
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(23), 123);

            var state = new SpectatorState
            {
                RulesetID = 0,
                MaximumStatistics = new Dictionary<osu.Game.Rulesets.Scoring.HitResult, int>
                {
                    [osu.Game.Rulesets.Scoring.HitResult.Great] = 1000,
                },
            };
            FrameHeader header = LegacySpectatorFrameCodec.DecodeScoreFrame(payload, state, out int frameTime);

            Assert.Multiple(() =>
            {
                Assert.That(frameTime, Is.EqualTo(12345));
                Assert.That(header.TotalScore, Is.EqualTo(LegacyScoreClient.ConvertClassicToStandardised(0, 987654, 1000)));
                Assert.That(header.LegacyTotalScore, Is.EqualTo(987654));
                Assert.That(header.MaxCombo, Is.EqualTo(321));
                Assert.That(header.Combo, Is.EqualTo(123));
                Assert.That(header.Statistics[osu.Game.Rulesets.Scoring.HitResult.Great], Is.EqualTo(100));
            });
        }

        private static readonly string[] longStrings = { new('a', 300) };

        private static string md5(string value) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

        private static byte[] makeServerPacket(ServerPacket type, byte[] payload)
        {
            byte[] result = new byte[7 + payload.Length];
            BinaryPrimitives.WriteUInt16LittleEndian(result, (ushort)type);
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(3), payload.Length);
            payload.CopyTo(result, 7);
            return result;
        }
    }
}
