// SPDX-License-Identifier: MPL-2.0

using NINA.GuideEngine.Algorithms;
using NINA.GuideEngine.Calibration;
using NINA.GuideEngine.Core;
using NINA.GuideEngine.MultiStar;

namespace NINA.GuideEngine.Guiding;

// The Dec backlash play model (docs/notes/PLAY-MODEL-BACKLASH.md): one instance for the guider's lifetime, so the gear's
// position and what was learned survive the corrector being rebuilt on a settings change. A calibration measures the
// shape of the play with either backlash model, so switching to the play model uses it.
public sealed partial class Guider
{
    private readonly PlayModelBacklash playBacklash = new();
    private int playBacklashLearnCount;
    private volatile PlayModelValues? playBacklashStatus;

    // the Dec play measured again before guiding starts after a slew, and where the play was last measured
    private DecPlayProbe? playProbe;
    private CalibrationAdjustment? playProbeAdjustment;
    private GuidePoint playProbeStart = GuidePoint.Invalid;
    private int playProbeLostFrames;
    private MountSnapshot? playMeasuredAt;

    /// <summary>The play model's values (also while the fixed compensation is in use). Loop thread only (tests and diagnostics).</summary>
    internal PlayModelValues BacklashPlayModel => playBacklash.Values;

    /// <summary>
    /// The Dec backlash play model while it compensates (as of the last guide frame), null while it does not. Safe to read
    /// from any thread.
    /// </summary>
    public PlayModelValues? BacklashPlayModelStatus => playBacklashStatus;

    /// <summary>
    /// A play model learned in an earlier session (stored by the host from <see cref="BacklashPlayModelLearnedEvent"/>).
    /// Null keeps what this session knows.
    /// </summary>
    public void RestoreBacklashPlayModel(PlayModelState? state) => Post(() => playBacklash.Restore(state));

    /// <summary>
    /// Whether the settings use the play model: it needs the Predictive Dec algorithm, which is told what the pulses really
    /// moved (<see cref="IGuideAlgorithm.CorrectionApplied"/>). PHD2's algorithms keep acting on corrections the play model
    /// held back or the play swallowed, and guided a hard play worse with it than without in the simulations
    /// (docs/notes/PLAY-MODEL-BACKLASH.md); they get the fixed compensation.
    /// </summary>
    internal static bool UsesPlayModel(GuiderSettings s) =>
        s.Backlash.Model == BacklashModel.PlayModel && s.DecAlgorithm.Kind == GuideAlgorithmKind.Predictive;

    // the compensation for a corrector built from the settings s
    private IBacklashCompensation CreateBacklash(GuiderSettings s, PulseLimiter limiter)
    {
        if (!UsesPlayModel(s))
        {
            // the fixed compensation, with the pulse it learned before (Guider.Backlash.cs)
            return CreateFixedBacklash(s.Backlash, limiter);
        }

        playBacklash.Limiter = limiter;
        playBacklash.SetPriorFullReversal(s.Backlash.PulseMs);
        playBacklash.Enable(s.Backlash.Enabled);
        return playBacklash;
    }

    // a calibration completed: its Dec return measured the shape of the play
    private void PlayModelCalibrated(CalibrationData result, MountSnapshot snapshot, DateTimeOffset now)
    {
        if (result.HasDecCalibration && playBacklash.SeedFromCalibration(result.DecTakeUp, result.YRate))
        {
            playMeasuredAt = snapshot;
            playBacklashLearnCount = -1;
            EmitPlayModelLearned(now);
        }
    }

    // guiding starts with a kept calibration: after a slew the gear rests on an unknown side of its play, and the play may
    // have changed with the load (docs/notes/PLAY-MODEL-BACKLASH.md, "the play changed with the target"); it is measured
    // again before guiding starts. False when no measurement is needed.
    private bool StartPlayProbe(GuidePoint starPosition, CalibrationAdjustment adj, MountSnapshot snapshot, DateTimeOffset now,
        out IReadOnlyList<PulseCommand> pulses)
    {
        pulses = [];
        if (!PlayProbeNeeded(adj.Calibration, snapshot))
        {
            return false;
        }

        UseCalibration(adj);
        playProbe = new DecPlayProbe(adj.Calibration.YRate, settings.MaxDecDurationMs, PlayModelBacklash.SizesMs[^1]);
        playProbeAdjustment = adj;
        playProbeStart = starPosition;
        playProbeLostFrames = 0;
        SetState(GuiderState.Calibrating);
        pulses = PlayProbeStep(starPosition, snapshot, now);
        return true;
    }

    private bool PlayProbeNeeded(CalibrationData cal, MountSnapshot snapshot)
    {
        var backlash = settings.Backlash;
        if (!backlash.Enabled || !UsesPlayModel(settings) || !(backlash.RemeasureAfterSlewDeg > 0) || settings.DecGuideMode != DecGuideMode.Auto
            || IsCoachMeasuring || !cal.HasDecCalibration || !(cal.YRate > 0) || !double.IsFinite(cal.YRate))
        {
            return false;
        }

        // nothing measured in this session yet: the pointing since the last measurement is unknown
        return playMeasuredAt is not { } measured || MountStateWatcher.MovedFar(measured, snapshot, backlash.RemeasureAfterSlewDeg * 60);
    }

    private MultiStarFrameResult PlayProbeFrame(GuideFrame frame, MountSnapshot snapshot, DateTimeOffset now, out IReadOnlyList<PulseCommand> pulses)
    {
        pulses = [];
        var r = tracker.ProcessFrame(frame, GuidePoint.Invalid, TrackerState.Looping, now);
        if (!r.StarFound)
        {
            Emit(new StarLostEvent(now, frame.FrameNumber, 0, r.Primary.Mass, r.Primary.Snr, r.Primary.Hfd, 0, (int)r.ErrorCode, r.Status));
            if (++playProbeLostFrames * Math.Max(settings.ExposureMs, 1) / 1000.0 > settings.Safety.ReacquireTimeoutSec)
            {
                // guiding takes over the search for the star; the gear's position stays unknown
                playProbe!.Abort("star lost");
                EndPlayProbe(snapshot, now);
            }

            return r;
        }

        playProbeLostFrames = 0;
        lastStarPosition = r.Primary.Position;
        pulses = PlayProbeStep(r.Primary.Position, snapshot, now);
        return r;
    }

    private IReadOnlyList<PulseCommand> PlayProbeStep(GuidePoint starPosition, MountSnapshot snapshot, DateTimeOffset now)
    {
        var probe = playProbe!;
        double decPx = transform!.CameraToMount(starPosition - playProbeStart).Y;
        var pulse = probe.Update(decPx);
        if (pulse is { } p)
        {
            Emit(new CalibratingEvent(now)
            {
                Mount = MountName,
                Direction = "Backlash",
                Distance = Math.Abs(decPx),
                Position = starPosition,
                Step = (probe.NorthMs + probe.SouthMs) / probe.StepMs,
                State = probe.Status,
                Progress = probe.Progress,
            });
            return [p];
        }

        if (probe.Phase is DecPlayProbePhase.Measured or DecPlayProbePhase.Failed)
        {
            EndPlayProbe(snapshot, now);
        }

        return [];
    }

    // the measurement ended: guiding starts on the star's original position, the play model seeded from the return
    private void EndPlayProbe(MountSnapshot snapshot, DateTimeOffset now)
    {
        var probe = playProbe!;
        var adj = playProbeAdjustment!;
        CancelPlayProbe();
        playMeasuredAt = snapshot;

        bool measured = probe.Phase == DecPlayProbePhase.Measured;
        bool seeded = measured && playBacklash.SeedFromCalibration(probe.TakeUp, adj.Calibration.YRate);
        EnterGuiding(playProbeStart, adj, now);
        if (measured)
        {
            // the return's last pulses moved the star South
            playBacklash.GearEngaged(GuideDirection.South);
        }

        string message = !measured ? $"Dec play not measured after the slew: {probe.FailureReason}"
            : seeded ? $"Dec play measured after the slew: North {probe.NorthMs} ms, South {probe.SouthMs} ms, gear engaged South"
            : $"Dec play after the slew: gear engaged South (North {probe.NorthMs} ms, South {probe.SouthMs} ms), the return too short to fit the play";
        Emit(new DecPlayProbeEvent(now, seeded, message));
        if (seeded)
        {
            playBacklashLearnCount = -1;
            EmitPlayModelLearned(now);
        }
    }

    private void CancelPlayProbe()
    {
        playProbe = null;
        playProbeAdjustment = null;
    }

    // the corrector counted the frame's Dec pulse as sent, but it was not: the gear's position is no longer known (the fixed
    // compensation keeps PHD2's behaviour)
    private void PlayModelPulsesNotSent()
    {
        playProbe?.Abort("a pulse was not sent");
        if (ReferenceEquals(corrector.Backlash, playBacklash))
        {
            playBacklash.ResetState();
        }
    }

    // guiding starts right after a calibration: its South return and nudges left the gear engaged South
    private void PlayModelAfterCalibration(CalibrationData result)
    {
        if (result.HasDecCalibration && result.DecTakeUp.Count > 0)
        {
            playBacklash.GearEngaged(GuideDirection.South);
        }
    }

    // after every guide frame: the status snapshot, and what was learned for the host to store
    private void EmitPlayModelLearned(DateTimeOffset now)
    {
        playBacklashStatus = settings.Backlash.Enabled && UsesPlayModel(settings) ? playBacklash.Values : null;
        if (playBacklash.LearnCount == playBacklashLearnCount)
        {
            return;
        }

        playBacklashLearnCount = playBacklash.LearnCount;
        Emit(new BacklashPlayModelLearnedEvent(now, playBacklash.State(now), playBacklash.Values, UsesPlayModel(settings)));
    }
}
