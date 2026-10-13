// SPDX-License-Identifier: MPL-2.0

using FluentAssertions;
using NUnit.Framework;
using NINA.GuideEngine.Algorithms;

namespace NINA.GuideEngine.Test.Algorithms;

[TestFixture]
public class ReversalGateTests
{
    private static double Gauss(Random rng)
    {
        double u1 = 1.0 - rng.NextDouble();
        double u2 = rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    [Test]
    public void EverythingIsAllowedUntilTheNoiseIsKnown()
    {
        var gate = new ReversalGate();
        gate.Allows(1).Should().BeTrue();
        gate.Observe(0.01, 0);
        gate.Observe(-0.01, 0);
        gate.IsWarm.Should().BeFalse();
        gate.Allows(-1).Should().BeTrue();
    }

    [Test]
    public void LearnsTheNoiseFromTheFrameToFrameChanges()
    {
        var rng = new Random(3);
        var gate = new ReversalGate();
        double sum = 0;
        for (int i = 0; i < 4000; i++)
        {
            gate.Observe(0.2 * Gauss(rng), 0);
            if (i >= 1000)
            {
                sum += gate.NoiseSigmaPx;
            }
        }

        // the estimate follows the noise with a memory of about 20 frames: on average it is the noise σ
        (sum / 3000).Should().BeApproximately(0.2, 0.02);
    }

    [Test]
    public void PureNoiseRarelyJustifiesAReversal()
    {
        var rng = new Random(7);
        var gate = new ReversalGate();
        int allowed = 0;
        const int frames = 20000;
        for (int i = 0; i < frames; i++)
        {
            gate.Observe(0.17 * Gauss(rng), 0);
            if (i > ReversalGate.WarmupFrames && (gate.Allows(1) || gate.Allows(-1)))
            {
                allowed++;
            }
        }

        // the estimated noise is a little low after quiet stretches, so the rate is above the textbook one, but rare
        allowed.Should().BeLessThan(frames / 100);
    }

    [TestCase(1.0, 25)]
    [TestCase(2.0, 8)]
    public void ARealOffsetGetsThroughWithinAFewFrames(double sigmas, int maxFrames)
    {
        var rng = new Random(11);
        var gate = new ReversalGate();
        for (int i = 0; i < 200; i++)
        {
            gate.Observe(0.17 * Gauss(rng), 0);
        }

        int frames = 0;
        while (!gate.Allows(1) && frames < 200)
        {
            gate.Observe(0.17 * (sigmas + Gauss(rng)), 0);
            frames++;
        }

        frames.Should().BeLessThanOrEqualTo(maxFrames);
    }

    [Test]
    public void ALargeOffsetGetsThroughAtOnce()
    {
        var rng = new Random(5);
        var gate = new ReversalGate();
        for (int i = 0; i < 200; i++)
        {
            gate.Observe(0.1 * Gauss(rng), 0);
        }

        gate.Observe(-0.1 * (ReversalGate.ImmediateSigmas + 1), 0);
        gate.Allows(-1).Should().BeTrue();
        gate.Allows(1).Should().BeFalse();
    }

    [Test]
    public void TheExpectedCorrectionIsNotTakenForNoise()
    {
        var gate = new ReversalGate();

        // a correction of 0.5 px brings the offset back exactly: the change is the correction, not noise
        gate.Observe(0.5, 0);
        gate.Observe(0.0, 0.5);
        for (int i = 0; i < 10; i++)
        {
            gate.Observe(0.0, 0);
        }

        gate.NoiseSigmaPx.Should().Be(ReversalGate.MinNoisePx);
    }
}
