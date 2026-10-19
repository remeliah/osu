// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using NUnit.Framework;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Catch;
using osu.Game.Rulesets.Catch.Beatmaps;
using osu.Game.Rulesets.Catch.Objects;
using osu.Game.Rulesets.Judgements;
using osu.Game.Rulesets.Mania;
using osu.Game.Rulesets.Mania.Beatmaps;
using osu.Game.Rulesets.Mania.Mods;
using osu.Game.Rulesets.Mania.Objects;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Osu.Beatmaps;
using osu.Game.Rulesets.Osu.Judgements;
using osu.Game.Rulesets.Osu.Objects;
using osu.Game.Rulesets.Osu.Scoring;
using osu.Game.Rulesets.Scoring;
using osu.Game.Rulesets.Scoring.Legacy;
using osu.Game.Scoring;
using osu.Game.Rulesets.Taiko;
using osu.Game.Rulesets.Taiko.Beatmaps;
using osu.Game.Rulesets.Taiko.Mods;
using osu.Game.Rulesets.Taiko.Objects;
using osu.Game.Tests.Beatmaps;
using TaikoHit = osu.Game.Rulesets.Taiko.Objects.Hit;

namespace osu.Game.Tests.Rulesets.Scoring
{
    [TestFixture]
    public class LegacyScoreV1ProcessorTest
    {
        [Test]
        public void TestOsuScoreV1GoldenVector()
        {
            var beatmap = new OsuBeatmap();
            for (int i = 0; i < 3; i++)
                beatmap.HitObjects.Add(new HitCircle { StartTime = i * 1000 });

            ILegacyScoreProcessor processor = createProcessor(new OsuRuleset(), beatmap, 3);
            foreach (HitCircle circle in beatmap.HitObjects)
                processor.ApplyResult(new JudgementResult(circle, circle.Judgement) { Type = HitResult.Great });

            Assert.That(processor.TotalScore, Is.EqualTo(948));
        }

        [Test]
        public void TestTaikoScoreV1GoldenVector()
        {
            var beatmap = new TaikoBeatmap();
            for (int i = 0; i < 12; i++)
                beatmap.HitObjects.Add(new TaikoHit { StartTime = i * 1000 });

            ILegacyScoreProcessor processor = createProcessor(new TaikoRuleset(), beatmap, 12);
            foreach (TaikoHit hit in beatmap.HitObjects)
                processor.ApplyResult(new JudgementResult(hit, hit.Judgement) { Type = HitResult.Great });

            Assert.That(processor.TotalScore, Is.EqualTo(3728));
        }

        [Test]
        public void TestTaikoStrongDrumRollUsesStableScore()
        {
            var beatmap = new TaikoBeatmap();
            beatmap.HitObjects.Add(new TaikoHit());

            var drumRoll = new DrumRoll();
            var tick = new DrumRollTick(drumRoll) { IsStrong = true };
            var strong = new DrumRollTick.StrongNestedHit(tick);
            ILegacyScoreProcessor processor = createProcessor(new TaikoRuleset(), beatmap, 1);

            processor.ApplyResult(new JudgementResult(tick, tick.Judgement) { Type = HitResult.SmallBonus });
            processor.ApplyResult(new JudgementResult(strong, strong.Judgement) { Type = HitResult.LargeBonus });

            // Stable applies 120% for each side of a large roll tick, then
            // doubles the result: (300 + 60) * 2 = 720.
            Assert.Multiple(() =>
            {
                Assert.That(processor.TotalScore, Is.EqualTo(720));
                Assert.That(processor.CountGeki, Is.Zero);
                Assert.That(processor.CountKatu, Is.Zero);
            });
        }

        [TestCase(HitResult.Great, 1, 0)]
        [TestCase(HitResult.Ok, 0, 1)]
        public void TestTaikoStrongHitUsesStableBonusCounters(HitResult resultType, int expectedGeki, int expectedKatu)
        {
            var beatmap = new TaikoBeatmap();
            var hit = new TaikoHit { IsStrong = true };
            beatmap.HitObjects.Add(hit);

            var strong = new TaikoHit.StrongNestedHit(hit);
            ILegacyScoreProcessor processor = createProcessor(new TaikoRuleset(), beatmap, 1);
            processor.ApplyResult(new JudgementResult(hit, hit.Judgement) { Type = resultType });
            processor.ApplyResult(new JudgementResult(strong, strong.Judgement) { Type = HitResult.LargeBonus });

            Assert.Multiple(() =>
            {
                Assert.That(processor.CountGeki, Is.EqualTo(expectedGeki));
                Assert.That(processor.CountKatu, Is.EqualTo(expectedKatu));
            });
        }

        [TestCase(false, 11)]
        [TestCase(true, 8)]
        public void TestTaikoSwellUsesStableHitRequirement(bool doubleTime, int expectedHits)
        {
            var beatmap = new TaikoBeatmap();
            var swell = new Swell { Duration = 1000, RequiredHits = 10 };
            beatmap.HitObjects.Add(swell);
            swell.ApplyDefaults(beatmap.ControlPointInfo, beatmap.Difficulty);

            var mods = new List<Mod>();
            if (doubleTime)
                mods.Add(new TaikoModDoubleTime());

            ILegacyScoreProcessor processor = createProcessor(new TaikoRuleset(), beatmap, 1, mods);

            Assert.That(swell.NestedHitObjects.Count, Is.EqualTo(expectedHits));

            foreach (SwellTick tick in swell.NestedHitObjects)
                processor.ApplyResult(new JudgementResult(tick, tick.Judgement) { Type = HitResult.SmallBonus });
            processor.ApplyResult(new JudgementResult(swell, swell.Judgement) { Type = HitResult.Great });

            Assert.That(processor.TotalScore, Is.EqualTo(expectedHits * 300 + 600));
        }

        [Test]
        public void TestCatchScoreV1GoldenVector()
        {
            var beatmap = new CatchBeatmap();
            for (int i = 0; i < 3; i++)
                beatmap.HitObjects.Add(new Fruit { StartTime = i * 1000 });

            ILegacyScoreProcessor processor = createProcessor(new CatchRuleset(), beatmap, 3);
            foreach (Fruit fruit in beatmap.HitObjects)
                processor.ApplyResult(new JudgementResult(fruit, fruit.Judgement) { Type = HitResult.Great });

            Assert.That(processor.TotalScore, Is.EqualTo(948));
        }

        [Test]
        public void TestManiaScoreV1GoldenVector()
        {
            var beatmap = new ManiaBeatmap(new StageDefinition(4));
            for (int i = 0; i < 2; i++)
                beatmap.HitObjects.Add(new Note { StartTime = i * 1000 });

            ILegacyScoreProcessor processor = createProcessor(new ManiaRuleset(), beatmap, 2);
            foreach (Note note in beatmap.HitObjects)
                processor.ApplyResult(new JudgementResult(note, note.Judgement) { Type = HitResult.Perfect });

            Assert.That(processor.TotalScore, Is.EqualTo(1_000_000));
        }

        [Test]
        public void TestManiaComboPenaltyAccountsForStableScoreIncreaseMods()
        {
            var beatmap = new ManiaBeatmap(new StageDefinition(4));
            var first = new Note();
            var second = new Note { StartTime = 1000 };
            beatmap.HitObjects.Add(first);
            beatmap.HitObjects.Add(second);

            ILegacyScoreProcessor processor = createProcessor(new ManiaRuleset(), beatmap, 2, new Mod[] { new ManiaModHidden() });
            processor.ApplyResult(new JudgementResult(first, first.Judgement) { Type = HitResult.Perfect });
            processor.ApplyResult(new JudgementResult(second, second.Judgement) { Type = HitResult.Good });

            // Mania removes score-increase mods from its total multiplier, but
            // stable still divides the 200 penalty by HD's 1.06 multiplier.
            Assert.That(processor.TotalScore, Is.EqualTo(776_440));
        }

        [Test]
        public void TestManiaHoldNoteIsOneStableJudgement()
        {
            var beatmap = new ManiaBeatmap(new StageDefinition(4));
            var hold = new HoldNote { Duration = 250 };
            beatmap.HitObjects.Add(hold);
            hold.ApplyDefaults(beatmap.ControlPointInfo, beatmap.Difficulty);

            ILegacyScoreProcessor processor = createProcessor(new ManiaRuleset(), beatmap, 3);
            processor.ApplyResult(new JudgementResult(hold.Head, hold.Head.Judgement) { Type = HitResult.Perfect });

            // The head starts a stable LN but is not independently scored.
            Assert.That(processor.TotalScore, Is.Zero);

            processor.ApplyResult(new JudgementResult(hold.Tail, hold.Tail.Judgement) { Type = HitResult.Perfect });

            Assert.Multiple(() =>
            {
                Assert.That(processor.TotalScore, Is.EqualTo(1_000_000));
                // Stable adds one combo every 100 ms while holding, followed by
                // one combo for the combined LN judgement.
                Assert.That(processor.Combo, Is.EqualTo(3));
                Assert.That(processor.MaxCombo, Is.EqualTo(3));
                Assert.That(processor.Statistics!.GetValueOrDefault(HitResult.Perfect), Is.EqualTo(1));
                Assert.That(processor.MaximumStatistics!.GetValueOrDefault(HitResult.Perfect), Is.EqualTo(1));
            });
        }

        [Test]
        public void TestManiaHoldBreakCapsStableJudgementAndBreaksCombo()
        {
            var beatmap = new ManiaBeatmap(new StageDefinition(4));
            var hold = new HoldNote { Duration = 250 };
            beatmap.HitObjects.Add(hold);
            hold.ApplyDefaults(beatmap.ControlPointInfo, beatmap.Difficulty);

            ILegacyScoreProcessor processor = createProcessor(new ManiaRuleset(), beatmap, 1);
            processor.ApplyResult(new JudgementResult(hold.Head, hold.Head.Judgement) { Type = HitResult.Perfect });
            processor.ApplyResult(new JudgementResult(hold.Body, hold.Body.Judgement)
            {
                Type = HitResult.ComboBreak,
                TimeOffset = -100
            });
            processor.ApplyResult(new JudgementResult(hold.Tail, hold.Tail.Judgement) { Type = HitResult.Perfect });

            Assert.Multiple(() =>
            {
                Assert.That(processor.TotalScore, Is.LessThan(1_000_000));
                Assert.That(processor.Combo, Is.EqualTo(1));
                Assert.That(processor.MaxCombo, Is.EqualTo(1));
                Assert.That(processor.Statistics!.GetValueOrDefault(HitResult.Good), Is.EqualTo(1));
            });
        }

        [Test]
        public void TestRevertRestoresExactScore()
        {
            var beatmap = new OsuBeatmap();
            var circle = new HitCircle();
            beatmap.HitObjects.Add(circle);
            ILegacyScoreProcessor processor = createProcessor(new OsuRuleset(), beatmap, 1);
            var result = new JudgementResult(circle, circle.Judgement) { Type = HitResult.Great };

            processor.ApplyResult(result);
            Assert.That(processor.TotalScore, Is.EqualTo(300));
            processor.RevertResult(result);
            Assert.That(processor.TotalScore, Is.Zero);
        }

        [Test]
        public void TestPopulateScoreUsesStableStatistics()
        {
            var processor = new OsuScoreProcessor
            {
                LegacyStatistics = new Dictionary<HitResult, int> { [HitResult.Ok] = 1 },
                LegacyMaximumStatistics = new Dictionary<HitResult, int> { [HitResult.Great] = 1 }
            };
            processor.LegacyTotalScore.Value = 123;
            processor.LegacyCombo.Value = 4;
            processor.LegacyMaxCombo.Value = 7;

            var score = new ScoreInfo();
            processor.PopulateScore(score);

            Assert.Multiple(() =>
            {
                Assert.That(score.LegacyTotalScore, Is.EqualTo(123));
                Assert.That(score.Combo, Is.EqualTo(4));
                Assert.That(score.MaxCombo, Is.EqualTo(7));
                Assert.That(score.Accuracy, Is.EqualTo(1d / 3).Within(1e-10));
                Assert.That(score.Rank, Is.EqualTo(ScoreRank.D));
                Assert.That(score.Statistics.GetValueOrDefault(HitResult.Ok), Is.EqualTo(1));
            });
        }

        [Test]
        public void TestPopulateScoreUsesStableRankRules()
        {
            var processor = new OsuScoreProcessor
            {
                LegacyStatistics = new Dictionary<HitResult, int>
                {
                    [HitResult.Great] = 81,
                    [HitResult.Ok] = 19
                },
                LegacyMaximumStatistics = new Dictionary<HitResult, int> { [HitResult.Great] = 100 }
            };
            processor.LegacyTotalScore.Value = 123;

            var score = new ScoreInfo(ruleset: new OsuRuleset().RulesetInfo);
            processor.PopulateScore(score);

            // Stable awards A for >80% 300s with no misses. The equivalent
            // accuracy (87.33%) would only be a B under lazer's generic rules.
            Assert.That(score.Rank, Is.EqualTo(ScoreRank.A));
        }

        [Test]
        public void TestOsuMissedSliderTailPreservesStableCombo()
        {
            var beatmap = new OsuBeatmap();
            var slider = new Slider();
            var head = new SliderHeadCircle();
            var tail = new SliderTailCircle(slider);
            var circle = new HitCircle();
            beatmap.HitObjects.Add(slider);
            beatmap.HitObjects.Add(circle);

            ILegacyScoreProcessor processor = createProcessor(new OsuRuleset(), beatmap, 2);
            processor.ApplyResult(new JudgementResult(head, head.Judgement) { Type = HitResult.SmallTickHit });
            processor.ApplyResult(new JudgementResult(tail, tail.Judgement) { Type = HitResult.SmallTickMiss });

            Assert.Multiple(() =>
            {
                Assert.That(processor.Combo, Is.EqualTo(1));
                Assert.That(processor.MaxCombo, Is.EqualTo(1));
            });

            processor.ApplyResult(new JudgementResult(circle, circle.Judgement) { Type = HitResult.Great });

            Assert.Multiple(() =>
            {
                Assert.That(processor.Combo, Is.EqualTo(2));
                Assert.That(processor.MaxCombo, Is.EqualTo(2));
            });
        }

        [Test]
        public void TestOsuSpinnerUsesStableHalfSpinThresholds()
        {
            var beatmap = new OsuBeatmap();
            beatmap.Difficulty.OverallDifficulty = 0;
            var spinner = new Spinner { Duration = 1000 };
            beatmap.HitObjects.Add(spinner);

            ILegacyScoreProcessor processor = createProcessor(new OsuRuleset(), beatmap, 1);
            var result = new OsuSpinnerJudgementResult(spinner, spinner.Judgement) { Type = HitResult.Great };
            result.History.ReportDelta(500, 720);
            processor.ApplyResult(result);

            // OD0 requires three half-spins on stable. Four half-spins yield
            // two 100-point spins and an Ok (100) object judgement.
            Assert.Multiple(() =>
            {
                Assert.That(processor.TotalScore, Is.EqualTo(300));
                Assert.That(processor.Combo, Is.EqualTo(1));
                Assert.That(processor.MaxCombo, Is.EqualTo(1));
                Assert.That(processor.Statistics!.GetValueOrDefault(HitResult.Ok), Is.EqualTo(1));
                Assert.That(processor.MaximumStatistics!.GetValueOrDefault(HitResult.Great), Is.EqualTo(1));
            });
        }

        private static ILegacyScoreProcessor createProcessor(ILegacyRuleset ruleset, osu.Game.Beatmaps.IBeatmap beatmap, int maximumJudgements, IReadOnlyList<Mod>? mods = null)
        {
            var working = new TestWorkingBeatmap(beatmap);
            return ruleset.CreateLegacyScoreProcessor(working, beatmap, mods ?? new List<Mod>(), maximumJudgements);
        }
    }
}
