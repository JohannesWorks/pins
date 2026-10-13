// SPDX-License-Identifier: MPL-2.0

using FluentAssertions;
using NUnit.Framework;
using NINA.GuideEngine.Algorithms;
using NINA.GuideEngine.Core;

namespace NINA.GuideEngine.Test.Algorithms;

[TestFixture]
public class PlayModelBacklashTests
{
    private const double Rate = 0.00309; // px/ms, the OnStepX of the 2026-10-12 guide log

    /// <summary>
    /// Drives the model open loop with a hard play of <paramref name="playMs"/>: alternating runs of <paramref name="runPulses"/>
    /// pulses of <paramref name="pulseMs"/>, without noise, so every take-up is measured exactly.
    /// </summary>
    private static PlayModelBacklash Learned(double playMs, int pulseMs = 200, int runPulses = 6, int frames = 400, double prior = 900)
    {
        var play = new PlayModelBacklash(prior);
        double gap = 0, moved = 0;
        var direction = GuideDirection.South;
        for (int f = 0; f < frames; f++)
        {
            play.TrackResults(MoveOptions.GuideStep, -moved, 0, Rate);
            if (f % runPulses == 0)
            {
                direction = direction == GuideDirection.South ? GuideDirection.North : GuideDirection.South;
            }

            double motor = (direction == GuideDirection.South ? 1 : -1) * pulseMs;
            double before = gap;
            gap = Math.Clamp(gap + motor, -playMs / 2, playMs / 2);
            play.PulseSent(direction, pulseMs);
            moved += (motor - (gap - before)) * Rate; // a South motion reduces a positive offset
        }

        return play;
    }

    private static int Compensation(PlayModelBacklash play, GuideDirection direction, int requestedMs)
    {
        int amount = requestedMs;
        return play.Apply(MoveOptions.GuideStep, direction == GuideDirection.South ? 1 : -1, ref amount);
    }

    // pulses shorter than the play cross it step by step, so the shape of the take-up is seen, not only its total
    [TestCase(300, 100)]
    [TestCase(600, 200)]
    [TestCase(1500, 400)]
    public void LearnsTheSizeOfAHardPlayFromItsTakeUps(double playMs, int pulseMs)
    {
        var play = Learned(playMs, pulseMs);
        play.Values.FullReversalMs.Should().BeApproximately(playMs, 0.1 * playMs);
        play.Values.Shape.Should().Be("Hard");
        play.LearnCount.Should().BeGreaterThan(10);
    }

    [Test]
    public void LearnsThatThereIsNoPlay()
    {
        var play = Learned(0);
        play.Values.FullReversalMs.Should().BeLessThan(PlayModelBacklash.GateMinSlackMs);
        play.Values.Shape.Should().Be("None");
    }

    [Test]
    public void APlayCrossedInOnePulseIsLearnedByItsTotal()
    {
        // 600 ms pulses cross a 300 ms play at once: its total is seen, not its shape
        var play = Learned(300, pulseMs: 600);
        play.Values.FullReversalMs.Should().BeApproximately(300, 60);
    }

    [Test]
    public void APlayWiderThanTheTravelIsOnlyKnownToBeWider()
    {
        // runs of 6 × 200 ms never cross a 2500 ms play: the star never moves
        var play = Learned(2500, pulseMs: 200);
        play.Values.FullReversalMs.Should().BeGreaterThan(1200);
    }

    [Test]
    public void AGearEngagedThatWayNeedsNoCompensation()
    {
        var play = Learned(600);
        play.GearEngaged(GuideDirection.South);
        Compensation(play, GuideDirection.South, 100).Should().Be(0);
        play.EffectiveMs(GuideDirection.South, 100, 0).Should().BeApproximately(100, 1);
    }

    [Test]
    public void AReversalFromAnEngagedGearTakesUpThePlay()
    {
        var play = Learned(600);
        play.GearEngaged(GuideDirection.South);

        // cautious: a little less than the whole play rather than an overshoot
        int comp = Compensation(play, GuideDirection.North, 100);
        comp.Should().BeInRange(480, 620);
        play.EffectiveMs(GuideDirection.North, 100 + comp, comp).Should().BeInRange(0, 120);
    }

    [Test]
    public void AGearThatWobbledInsideThePlayNeedsLessAtTheNextReversal()
    {
        var play = Learned(600);
        play.GearEngaged(GuideDirection.South);
        play.PulseSent(GuideDirection.North, 200);

        // 200 ms of the play were crossed: South has 200 ms to take up again, North the remaining 400 ms
        Compensation(play, GuideDirection.South, 100).Should().BeInRange(150, 220);
        Compensation(play, GuideDirection.North, 100).Should().BeInRange(330, 420);
    }

    [Test]
    public void AnUnknownGearGetsHalfThePlayAndNoPulseIsHeld()
    {
        var play = Learned(600);
        play.ResetState();
        play.Values.StateKnown.Should().BeFalse();
        Compensation(play, GuideDirection.North, 100).Should().BeInRange(220, 320);
        play.HoldMove(MoveOptions.GuideStep, -0.1, Rate).Should().BeFalse();
    }

    [Test]
    public void TheCompensationNeverExceedsTheMaxPulse()
    {
        var limiter = new PulseLimiter();
        limiter.SetMaxDecDuration(400);
        var play = Learned(1500);
        play.Limiter = limiter;
        play.GearEngaged(GuideDirection.South);
        Compensation(play, GuideDirection.North, 100).Should().Be(300);
    }

    [Test]
    public void NoiseInsideThePlayIsHeldARealOffsetIsNot()
    {
        var rng = new Random(2);
        var play = Learned(600);
        play.GearEngaged(GuideDirection.South);
        for (int i = 0; i < 50; i++)
        {
            play.TrackResults(MoveOptions.GuideStep, 0.15 * Gauss(rng), 0, Rate);
        }

        // a small North request is noise: it would only cross the play
        play.TrackResults(MoveOptions.GuideStep, -0.1, 0, Rate);
        play.HoldMove(MoveOptions.GuideStep, -0.06, Rate).Should().BeTrue();
        play.HeldMoves.Should().Be(1);

        // South, the way the gear is engaged, is guided as usual
        play.HoldMove(MoveOptions.GuideStep, 0.06, Rate).Should().BeFalse();

        // a large North offset reverses at once
        play.TrackResults(MoveOptions.GuideStep, -1.5, 0, Rate);
        play.HoldMove(MoveOptions.GuideStep, -0.9, Rate).Should().BeFalse();
    }

    [Test]
    public void OnlyGuideStepsAreHeldAndOnlyWhileEnabled()
    {
        var play = Learned(600);
        play.GearEngaged(GuideDirection.South);
        for (int i = 0; i < 50; i++)
        {
            play.TrackResults(MoveOptions.GuideStep, 0.01 * (i % 3 - 1), 0, Rate);
        }

        play.HoldMove(MoveOptions.RecoveryMove, -0.05, Rate).Should().BeFalse("a recovery move bypasses the algorithms");
        play.HoldMove(MoveOptions.GuideStep, -0.05, Rate).Should().BeTrue();
        play.Enable(false);
        play.HoldMove(MoveOptions.GuideStep, -0.05, Rate).Should().BeFalse();
        int amount = 100;
        play.Apply(MoveOptions.GuideStep, -0.05, ref amount).Should().Be(0);
    }

    [Test]
    public void ACalibrationMeasuresTheShapeOfASoftPlay()
    {
        var loop = new DecPlayLoop();
        var play = new PlayModelBacklash(900);
        play.SeedFromCalibration(loop.CalibrationTakeUp(800, 8), loop.YRatePxPerMs).Should().BeTrue();

        double full = loop.Stages.Sum(s => s.Share * s.PlayMs);
        play.ShapeFromCalibration.Should().BeTrue();
        play.PriorFullReversalMs.Should().BeApproximately(full, 0.15 * full);
        play.Values.Shape.Should().Be("Calibrated");
    }

    [Test]
    public void TooFewCalibrationPointsChangeNothing()
    {
        var play = new PlayModelBacklash(900);
        play.SeedFromCalibration([new(500, 0.5), new(1000, 2.0)], Rate).Should().BeFalse();
        play.SeedFromCalibration([new(500, 0.5), new(1000, 2.0), new(1500, 3.5)], 0).Should().BeFalse();
        play.ShapeFromCalibration.Should().BeFalse();
    }

    [Test]
    public void WhatWasLearnedSurvivesAStoreAndRestore()
    {
        var learned = Learned(600);
        var state = learned.State(DateTimeOffset.UnixEpoch);

        var restored = new PlayModelBacklash(2500);
        restored.Restore(state).Should().BeTrue();
        restored.Values.FullReversalMs.Should().BeApproximately(learned.Values.FullReversalMs, 1);
        restored.Values.StateKnown.Should().BeFalse("the gear's position is not stored");
    }

    [Test]
    public void AnInvalidStoredStateIsIgnored()
    {
        var play = new PlayModelBacklash(900);
        double before = play.Values.FullReversalMs;
        play.Restore(null).Should().BeFalse();
        play.Restore(new PlayModelState { LogPosterior = [0, 1, 2] }).Should().BeFalse();
        play.Restore(new PlayModelState { LogPosterior = new double[play.HypothesisCount], CalibratedWeights = [1, 2] }).Should().BeFalse();
        play.Values.FullReversalMs.Should().Be(before);
    }

    [Test]
    public void TheCorrectorHoldsANoiseReversalAndSendsNoDecPulse()
    {
        var play = Learned(600);
        var corrector = DecPlayLoop.Corrector(play, GuideAlgorithmKind.Hysteresis);
        play.GearEngaged(GuideDirection.South);
        var rng = new Random(4);
        var t = DateTimeOffset.UnixEpoch;
        GuideCorrection? held = null;
        for (int i = 0; i < 60 && held is null; i++)
        {
            var corr = corrector.GuideStep(0, 0.1 * Gauss(rng), 1, Rate, t.AddSeconds(i));
            if (corr.DecHeldByBacklash)
            {
                held = corr;
            }
        }

        held.Should().NotBeNull();
        held!.Pulses.Should().NotContain(p => p.Direction.Axis() == GuideAxis.Dec);
        held.DecCorrectionPx.Should().Be(0);
    }

    private static double Gauss(Random rng)
    {
        double u1 = 1.0 - rng.NextDouble();
        double u2 = rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
