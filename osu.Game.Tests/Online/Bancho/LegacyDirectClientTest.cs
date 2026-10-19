// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
using NUnit.Framework;
using osu.Game.Beatmaps;
using osu.Game.Online.Bancho;

namespace osu.Game.Tests.Online.Bancho
{
    [TestFixture]
    public class LegacyDirectClientTest
    {
        [Test]
        public void TestParsesStableDirectResponse()
        {
            const string response = "1\n123.osz|Artist|Title|Mapper|1|10.0|2024-03-04 05:06:07|123|0|1|0|0|0|[5.32*] Insane {cs: 4 / od: 8.5 / ar: 9 / hp: 6}@0";

            var result = LegacyDirectClient.ParseSearchResponse(response, 0);
            var set = result.BeatmapSets.Single();
            var beatmap = set.Beatmaps.Single();

            Assert.Multiple(() =>
            {
                Assert.That(result.Total, Is.EqualTo(1));
                Assert.That(result.Cursor, Is.Null);
                Assert.That(set.OnlineID, Is.EqualTo(123));
                Assert.That(set.Status, Is.EqualTo(BeatmapOnlineStatus.Ranked));
                Assert.That(set.HasVideo, Is.True);
                Assert.That(beatmap.BeatmapSet, Is.SameAs(set));
                Assert.That(beatmap.DifficultyName, Is.EqualTo("Insane"));
                Assert.That(beatmap.StarRating, Is.EqualTo(5.32).Within(0.001));
                Assert.That(beatmap.OverallDifficulty, Is.EqualTo(8.5f));
                Assert.That(beatmap.RulesetID, Is.Zero);
            });
        }

        [Test]
        public void TestEmptyStableDirectResponseIsSuccessful()
        {
            var result = LegacyDirectClient.ParseSearchResponse("0", 0);

            Assert.That(result.BeatmapSets, Is.Empty);
            Assert.That(result.Total, Is.Zero);
            Assert.That(result.Cursor, Is.Null);
        }
    }
}
