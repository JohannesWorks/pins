// SPDX-License-Identifier: MPL-2.0

using FluentAssertions;
using NUnit.Framework;
using NINA.GuideEngine.Algorithms;

namespace NINA.GuideEngine.Test.Algorithms;

/// <summary>
/// Closed-loop comparison of the Dec backlash compensations on mounts like the OnStepX of the 2026-10-12 guide log
/// (<see cref="DecPlayLoop"/>): no compensation, PHD2's fixed 900 ms pulse (the log's setting), and the play model, each
/// with the Predictive Dec algorithm the log used. The full table is in docs/notes/PLAY-MODEL-BACKLASH.md.
/// </summary>
[TestFixture]
public class PlayModelBacklashLoopTests
{
    private const int Hour = 2770; // 1.3 s frames

    internal static readonly (string Name, Func<int, DecPlayLoop> Loop)[] Scenarios =
    [
        ("OnStepX", seed => new DecPlayLoop { Seed = seed }),
        ("OnStepX+dither", seed => new DecPlayLoop { Seed = seed, DitherEveryFrames = 230 }),
        ("OnStepX varying+dither", seed => new DecPlayLoop { Seed = seed, PlayVariation = 0.5, DitherEveryFrames = 230 }),
        ("drift 0.006+dither", seed => new DecPlayLoop { Seed = seed, DriftPxPerSec = 0.006, DitherEveryFrames = 230 }),
        ("hard 600+dither", seed => new DecPlayLoop { Seed = seed, Stages = [(600, 1.0)], DitherEveryFrames = 230 }),
        ("hard 2000 drift+dither", seed => new DecPlayLoop { Seed = seed, Stages = [(2000, 1.0)], DriftPxPerSec = 0.004, DitherEveryFrames = 230 }),
        ("no backlash+dither", seed => new DecPlayLoop { Seed = seed, Stages = [(0, 1.0)], DitherEveryFrames = 230 }),

        // the log's Dec: frame-to-frame noise 0.08 px and a slow wander of 0.21 px σ, correlated over about 8 s
        ("logged wander+dither", seed => new DecPlayLoop { Seed = seed, SeeingSigmaPx = 0.06, WanderPxPerFrame = 0, SlowWanderPx = 0.2, DitherEveryFrames = 230 }),
        ("logged wander hard 600", seed => new DecPlayLoop { Seed = seed, SeeingSigmaPx = 0.06, WanderPxPerFrame = 0, SlowWanderPx = 0.2, Stages = [(600, 1.0)], DitherEveryFrames = 230 }),
        ("logged wander no backlash", seed => new DecPlayLoop { Seed = seed, SeeingSigmaPx = 0.06, WanderPxPerFrame = 0, SlowWanderPx = 0.2, Stages = [(0, 1.0)], DitherEveryFrames = 230 }),
    ];

    private static DecPlayLoop.Result Run(DecPlayLoop loop, string mode, GuideAlgorithmKind kind, int frames = Hour)
    {
        IBacklashCompensation? backlash = mode switch
        {
            "off" => null,
            "fixed" => new BacklashCompensation(900, 0, 0, enabled: true),
            "play" => new PlayModelBacklash(900),
            _ => throw new ArgumentException(mode),
        };
        return loop.Run(DecPlayLoop.Corrector(backlash, kind), frames);
    }

    private sealed record Summary(double Rms, double Guiding, double Settle, double Reversals, double PulseSec);

    private static Summary Average(Func<int, DecPlayLoop> scenario, string mode, GuideAlgorithmKind kind = GuideAlgorithmKind.Predictive, int seeds = 3)
    {
        var results = Enumerable.Range(1, seeds).Select(seed => Run(scenario(seed), mode, kind)).ToList();
        double Avg(Func<DecPlayLoop.Result, double> v) => results.Select(v).Where(double.IsFinite).DefaultIfEmpty(double.NaN).Average();
        return new Summary(Avg(r => r.TrueRmsPx), Avg(r => r.GuidingRmsPx), Avg(r => r.SettleFramesMean), Avg(r => r.ReversalsPerHour), Avg(r => r.PulseSecPerHour));
    }

    private static Func<int, DecPlayLoop> Scenario(string name) => Scenarios.Single(s => s.Name == name).Loop;

    [Test]
    public void OnTheLoggedMountItStopsThePingPongAndGuidesAtLeastAsWellAsWithout()
    {
        var off = Average(Scenario("OnStepX+dither"), "off");
        var fixedPulse = Average(Scenario("OnStepX+dither"), "fixed");
        var play = Average(Scenario("OnStepX+dither"), "play");

        play.Reversals.Should().BeLessThan(0.1 * fixedPulse.Reversals);
        play.PulseSec.Should().BeLessThan(0.5 * fixedPulse.PulseSec);
        play.Rms.Should().BeLessThan(off.Rms);
        play.Rms.Should().BeLessThan(fixedPulse.Rms);
        play.Guiding.Should().BeLessThan(1.15 * off.Guiding);
    }

    [TestCase("hard 600+dither")]
    [TestCase("hard 2000 drift+dither")]
    public void AHardPlayIsGuidedClearlyBetter(string scenario)
    {
        var off = Average(Scenario(scenario), "off");
        var fixedPulse = Average(Scenario(scenario), "fixed");
        var play = Average(Scenario(scenario), "play");

        play.Guiding.Should().BeLessThan(0.8 * Math.Min(off.Guiding, fixedPulse.Guiding));
        play.Rms.Should().BeLessThan(Math.Min(off.Rms, fixedPulse.Rms));
    }

    [TestCase("logged wander+dither")]
    [TestCase("logged wander hard 600")]
    public void TheSlowDecWanderOfTheLogIsCorrectedBetter(string scenario)
    {
        var off = Average(Scenario(scenario), "off");
        var fixedPulse = Average(Scenario(scenario), "fixed");
        var play = Average(Scenario(scenario), "play");

        play.Rms.Should().BeLessThan(Math.Min(off.Rms, fixedPulse.Rms));
        play.Guiding.Should().BeLessThan(Math.Min(off.Guiding, fixedPulse.Guiding));
        play.Reversals.Should().BeLessThan(0.6 * fixedPulse.Reversals);
    }

    [Test]
    public void AMountWithoutBacklashLosesNothing()
    {
        var off = Average(Scenario("no backlash+dither"), "off");
        var play = Average(Scenario("no backlash+dither"), "play");

        play.Rms.Should().BeLessThan(1.05 * off.Rms);
        play.Settle.Should().BeLessThan(off.Settle + 0.5);
    }

    [Test]
    [Explicit("prints the comparison table of docs/notes/PLAY-MODEL-BACKLASH.md")]
    public void PrintComparison()
    {
        foreach (var kind in new[] { GuideAlgorithmKind.Predictive, GuideAlgorithmKind.Hysteresis, GuideAlgorithmKind.ResistSwitch })
        {
            foreach (var (name, scenario) in Scenarios)
            {
                foreach (var mode in new[] { "off", "fixed", "play" })
                {
                    var a = Average(scenario, mode, kind, seeds: 5);
                    TestContext.Out.WriteLine(
                        $"| {kind} | {name} | {mode} | {a.Rms:F3} | {a.Guiding:F3} | {a.Settle:F1} | {a.Reversals:F0} | {a.PulseSec:F0} |");
                }
            }
        }
    }
}
