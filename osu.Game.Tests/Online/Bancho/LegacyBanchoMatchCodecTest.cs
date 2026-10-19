// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.IO;
using System.Text;
using NUnit.Framework;
using osu.Game.Online.Bancho;

namespace osu.Game.Tests.Online.Bancho
{
    [TestFixture]
    public class LegacyBanchoMatchCodecTest
    {
        [Test]
        public void TestRoundTripReferenceLayout()
        {
            var expected = new LegacyBanchoMatch
            {
                ID = 42,
                InProgress = true,
                Mods = 72,
                Name = "refx multi",
                Password = "secret",
                HasPassword = true,
                BeatmapName = "artist - title [difficulty]",
                BeatmapID = 1234,
                BeatmapChecksum = "0123456789abcdef0123456789abcdef",
                HostID = 1001,
                RulesetID = 0,
                WinCondition = 1,
                TeamType = 2,
                FreeMods = true,
                Seed = 9876,
            };
            for (int i = 0; i < LegacyBanchoMatch.SLOT_COUNT; i++)
                expected.SlotStatuses[i] = LegacyMatchSlotStatus.Open;
            expected.SlotStatuses[0] = LegacyMatchSlotStatus.Ready;
            expected.SlotStatuses[5] = LegacyMatchSlotStatus.NoMap;
            expected.SlotUserIDs[0] = 1001;
            expected.SlotUserIDs[5] = 1002;
            expected.SlotTeams[0] = LegacyMatchTeam.Red;
            expected.SlotTeams[5] = LegacyMatchTeam.Blue;
            expected.SlotMods[0] = 8;
            expected.SlotMods[5] = 16;

            byte[] encoded = encode(expected);
            LegacyBanchoMatch actual = decode(encoded);

            Assert.Multiple(() =>
            {
                Assert.That(actual.ID, Is.EqualTo(expected.ID));
                Assert.That(actual.InProgress, Is.True);
                Assert.That(actual.Name, Is.EqualTo(expected.Name));
                Assert.That(actual.Password, Is.EqualTo(expected.Password));
                Assert.That(actual.HasPassword, Is.True);
                Assert.That(actual.BeatmapID, Is.EqualTo(expected.BeatmapID));
                Assert.That(actual.BeatmapChecksum, Is.EqualTo(expected.BeatmapChecksum));
                Assert.That(actual.SlotStatuses, Is.EqualTo(expected.SlotStatuses));
                Assert.That(actual.SlotTeams, Is.EqualTo(expected.SlotTeams));
                Assert.That(actual.SlotUserIDs, Is.EqualTo(expected.SlotUserIDs));
                Assert.That(actual.SlotMods, Is.EqualTo(expected.SlotMods));
                Assert.That(actual.HostID, Is.EqualTo(expected.HostID));
                Assert.That(actual.TeamType, Is.EqualTo(expected.TeamType));
                Assert.That(actual.Seed, Is.EqualTo(expected.Seed));
            });
        }

        [Test]
        public void TestHiddenPasswordUsesStableSentinel()
        {
            var match = new LegacyBanchoMatch { HasPassword = true, Password = "secret" };
            for (int i = 0; i < LegacyBanchoMatch.SLOT_COUNT; i++)
                match.SlotStatuses[i] = LegacyMatchSlotStatus.Open;

            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
                LegacyBanchoMatchCodec.Write(writer, match, hidePassword: true);

            LegacyBanchoMatch decoded = decode(stream.ToArray());
            Assert.That(decoded.HasPassword, Is.True);
            Assert.That(decoded.Password, Is.Empty);
        }

        private static byte[] encode(LegacyBanchoMatch match)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
            LegacyBanchoMatchCodec.Write(writer, match);
            return stream.ToArray();
        }

        private static LegacyBanchoMatch decode(byte[] data)
        {
            using var reader = new BinaryReader(new MemoryStream(data), Encoding.UTF8);
            return LegacyBanchoMatchCodec.Read(reader);
        }
    }
}
