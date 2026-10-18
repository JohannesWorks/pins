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

        // the wander is followed, so there are reversals, but no ping-pong beyond the fixed pulse's
        play.Reversals.Should().BeLessThan(fixedPulse.Reversals);
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
    public void AReversalTheMountReportsNotCarriedOutTeachesNoPlay()
    {
        // a hard 150 ms play; reversals of 300 ms or more are rejected, as the guider learns from PulseRejectedException
        var runs = Enumerable.Range(1, 3).Select(seed =>
        {
            var play = new PlayModelBacklash(900);
            var loop = new DecPlayLoop
            {
                Seed = seed,
                SeeingSigmaPx = 0.06,
                WanderPxPerFrame = 0,
                SlowWanderPx = 0.2,
                Stages = [(150, 1.0)],
                DitherEveryFrames = 230,
                DropReversalsFromMs = 300,
                ReportDropped = true,
            };
            var result = loop.Run(DecPlayLoop.Corrector(play, GuideAlgorithmKind.Predictive), Hour);
            return (Result: result, Play: play.Values);
        }).ToList();
        var off = Enumerable.Range(1, 3)
            .Select(seed => new DecPlayLoop { Seed = seed, SeeingSigmaPx = 0.06, WanderPxPerFrame = 0, SlowWanderPx = 0.2, Stages = [(150, 1.0)], DitherEveryFrames = 230, DropReversalsFromMs = 300 }
                .Run(DecPlayLoop.Corrector(null, GuideAlgorithmKind.Predictive), Hour).TrueRmsPx)
            .Average();

        runs.Should().OnlyContain(r => r.Play.FullReversalMs < 300, "the rejected reversals are not taken for play");
        runs.Average(r => r.Result.TrueRmsPx).Should().BeLessThan(1.15 * off);
    }

    [Test]
    [Explicit("prints the Dec settings compared on the Sky Simulator session of 2026-10-18")]
    public void PrintSkySimulatorSession()
    {
        // the Sky Simulator with the OnStep profile on pier West: a hard 3″ play (410 ms at 7.3″/s), Dec rate 2.45 px/s,
        // 2.13 s frames of 1 s, a random walk of 0.25″/√s (0.12 px a frame), seeing about 0.06 px, drift 0.1″/min,
        // no dithers; measured on the simulator's 0.2 px grid or not
        const int frames = 1690;
        foreach (double quantum in new[] { 0.2, 0.0 })
        {
            Func<int, DecPlayLoop> scenario = seed => new DecPlayLoop
            {
                Seed = seed,
                YRatePxPerMs = 0.00245,
                FrameSec = 2.13,
                SeeingSigmaPx = 0.06,
                WanderPxPerFrame = 0.122,
                DriftPxPerSec = 0.1 / 2.99 / 60,
                Stages = [(410, 1.0)],
                MeasurementQuantumPx = quantum,
            };
            (string Name, GuideAlgorithmKind Kind, double? Pace)[] algorithms =
            [
                ("Predictive 0.5", GuideAlgorithmKind.Predictive, 0.5),
                ("Predictive 0.7", GuideAlgorithmKind.Predictive, 0.7),
                ("Predictive 1.0", GuideAlgorithmKind.Predictive, 1.0),
                ("Hysteresis", GuideAlgorithmKind.Hysteresis, null),
                ("ResistSwitch", GuideAlgorithmKind.ResistSwitch, null),
            ];
            foreach (var (name, kind, pace) in algorithms)
            {
                foreach (var mode in new[] { "off", "fixed", "play" })
                {
                    var runs = Enumerable.Range(1, 5).Select(seed =>
                    {
                        IBacklashCompensation? backlash = mode switch
                        {
                            "fixed" => new BacklashCompensation(410, 0, 0, enabled: true),
                            "play" => new PlayModelBacklash(410),
                            _ => null,
                        };
                        var corrector = DecPlayLoop.Corrector(backlash, kind);
                        if (pace is { } p)
                        {
                            corrector.DecAlgorithm.TrySetParam("pace", p);
                        }

                        return scenario(seed).Run(corrector, frames);
                    }).ToList();
                    TestContext.Out.WriteLine(FormattableString.Invariant(
                        $"| q {quantum:F1} | {name} | {mode} | true {runs.Average(r => r.TrueRmsPx):F3} | measured {runs.Average(r => r.MeasuredRmsPx):F3} | {runs.Average(r => r.ReversalsPerHour):F0} rev/h | {runs.Average(r => r.PulseSecPerHour):F0} s/h | held {runs.Average(r => r.Held):F0} |"));
                }
            }
        }
    }

    [Test]
    [Explicit("prints how the compensations fare on a mount that drops long reversals")]
    public void PrintDroppedReversals()
    {
        (string Name, Func<int, DecPlayLoop> Loop)[] scenarios =
        [
            ("wander hard 150", seed => new DecPlayLoop { Seed = seed, SeeingSigmaPx = 0.06, WanderPxPerFrame = 0, SlowWanderPx = 0.2, Stages = [(150, 1.0)], DitherEveryFrames = 230 }),
            ("wander hard 150 drop≥300", seed => new DecPlayLoop { Seed = seed, SeeingSigmaPx = 0.06, WanderPxPerFrame = 0, SlowWanderPx = 0.2, Stages = [(150, 1.0)], DitherEveryFrames = 230, DropReversalsFromMs = 300 }),
            ("wander hard 150 drop≥300 reported", seed => new DecPlayLoop { Seed = seed, SeeingSigmaPx = 0.06, WanderPxPerFrame = 0, SlowWanderPx = 0.2, Stages = [(150, 1.0)], DitherEveryFrames = 230, DropReversalsFromMs = 300, ReportDropped = true }),
            ("OnStepX+dither drop≥300", seed => new DecPlayLoop { Seed = seed, DitherEveryFrames = 230, DropReversalsFromMs = 300 }),
            ("OnStepX+dither drop≥300 reported", seed => new DecPlayLoop { Seed = seed, DitherEveryFrames = 230, DropReversalsFromMs = 300, ReportDropped = true }),
        ];
        foreach (var (name, scenario) in scenarios)
        {
            foreach (var mode in new[] { "off", "fixed", "play" })
            {
                var runs = Enumerable.Range(1, 3).Select(seed =>
                {
                    IBacklashCompensation? backlash = mode switch
                    {
                        "fixed" => new BacklashCompensation(900, 0, 0, enabled: true),
                        "play" => new PlayModelBacklash(900),
                        _ => null,
                    };
                    var r = scenario(seed).Run(DecPlayLoop.Corrector(backlash, GuideAlgorithmKind.Predictive), Hour);
                    return (Result: r, Play: (backlash as PlayModelBacklash)?.Values);
                }).ToList();
                double Avg(Func<DecPlayLoop.Result, double> v) => runs.Select(x => v(x.Result)).Where(double.IsFinite).DefaultIfEmpty(double.NaN).Average();
                string learned = runs[0].Play is null
                    ? string.Empty
                    : $" | learned {string.Join("/", runs.Select(x => $"{x.Play!.FullReversalMs:F0}"))} ms, σ {string.Join("/", runs.Select(x => $"{x.Play!.NoiseSigmaPx:F3}"))} px";
                TestContext.Out.WriteLine(
                    $"| {name} | {mode} | {Avg(r => r.TrueRmsPx):F3} | {Avg(r => r.GuidingRmsPx):F3} | {Avg(r => r.SettleFramesMean):F1} | {Avg(r => r.ReversalsPerHour):F0} | {Avg(r => r.PulseSecPerHour):F0} | dropped {Avg(r => r.Dropped):F0}{learned}");
            }
        }
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
