// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Annotations;
using Moq;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using osu.Game.Beatmaps;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Osu.Beatmaps;

namespace osu.Game.Tests.Beatmaps
{
    [TestFixture]
    public class WorkingBeatmapTest
    {
        [Test]
        public void TestGetPlayableSuccess()
        {
            var working = new TestNeverLoadsWorkingBeatmap();

            working.ResetEvent.Set();

            ClassicAssert.NotNull(working.GetPlayableBeatmap(new OsuRuleset().RulesetInfo));
        }

        [Test]
        public void TestGetPlayableCancellationToken()
        {
            var working = new TestNeverLoadsWorkingBeatmap();

            var cts = new CancellationTokenSource();
            var loadStarted = new ManualResetEventSlim();
            var loadCompleted = new ManualResetEventSlim();

            Task.Factory.StartNew(() =>
            {
                loadStarted.Set();
                Assert.Throws<OperationCanceledException>(() => working.GetPlayableBeatmap(new OsuRuleset().RulesetInfo, Array.Empty<Mod>(), cts.Token));
                loadCompleted.Set();
            }, TaskCreationOptions.LongRunning);

            ClassicAssert.True(loadStarted.Wait(10000));

            cts.Cancel();

            ClassicAssert.True(loadCompleted.Wait(10000));

            working.ResetEvent.Set();
        }

        [Test]
        public void TestGetPlayableDefaultTimeout()
        {
            var working = new TestNeverLoadsWorkingBeatmap();

            Assert.Throws(Is.InstanceOf<TimeoutException>(), () => working.GetPlayableBeatmap(new OsuRuleset().RulesetInfo));

            working.ResetEvent.Set();
        }

        [Test]
        public void TestGetPlayableRulesetLoadFailure()
        {
            var working = new TestWorkingBeatmap(new Beatmap());

            // by default mocks return nulls if not set up, which is actually desired here to simulate a ruleset load failure scenario.
            var ruleset = new Mock<IRulesetInfo>();

            Assert.Throws<RulesetLoadException>(() => working.GetPlayableBeatmap(ruleset.Object));
        }

        [Test]
        public void TestGetOnlineIdsFallsBackToSourceMetadata()
        {
            var storedInfo = new BeatmapInfo
            {
                OnlineID = -1,
                BeatmapSet = new BeatmapSetInfo { OnlineID = 2587618 },
            };
            var decoded = new Beatmap
            {
                BeatmapInfo = new BeatmapInfo
                {
                    OnlineID = 5772355,
                    BeatmapSet = new BeatmapSetInfo { OnlineID = 2587618 },
                },
            };

            var working = new SourceMetadataWorkingBeatmap(storedInfo, decoded);

            Assert.That(working.GetOnlineIds(), Is.EqualTo((5772355, 2587618)));
        }

        private class SourceMetadataWorkingBeatmap : WorkingBeatmap
        {
            private readonly IBeatmap decoded;

            public SourceMetadataWorkingBeatmap(BeatmapInfo storedInfo, IBeatmap decoded)
                : base(storedInfo, null)
            {
                this.decoded = decoded;
            }

            protected override IBeatmap GetBeatmap() => decoded;
            public override osu.Framework.Graphics.Textures.Texture GetBackground() => throw new NotImplementedException();
            protected override osu.Framework.Audio.Track.Track GetBeatmapTrack() => throw new NotImplementedException();
            protected internal override osu.Game.Skinning.ISkin GetSkin() => throw new NotImplementedException();
            public override Stream GetStream(string storagePath) => Stream.Null;
        }

        public class TestNeverLoadsWorkingBeatmap : TestWorkingBeatmap
        {
            public ManualResetEventSlim ResetEvent = new ManualResetEventSlim();

            public TestNeverLoadsWorkingBeatmap()
                : base(new Beatmap())
            {
            }

            protected override IBeatmapConverter CreateBeatmapConverter(IBeatmap beatmap, Ruleset ruleset) => new TestConverter(beatmap, ResetEvent);

            public class TestConverter : IBeatmapConverter
            {
                private readonly ManualResetEventSlim resetEvent;

                public TestConverter(IBeatmap beatmap, ManualResetEventSlim resetEvent)
                {
                    this.resetEvent = resetEvent;
                    Beatmap = beatmap;
                }

#pragma warning disable CS0067
                [CanBeNull]
                public event Action<HitObject, IEnumerable<HitObject>> ObjectConverted;
#pragma warning restore CS0067

                public IBeatmap Beatmap { get; }

                public bool CanConvert() => true;

                public IBeatmap Convert(CancellationToken cancellationToken = default)
                {
                    resetEvent.Wait(cancellationToken);
                    return new OsuBeatmap();
                }
            }
        }
    }
}
