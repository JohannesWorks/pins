// SPDX-License-Identifier: MPL-2.0

using NINA.GuideEngine.Algorithms;
using NINA.GuideEngine.Calibration;
using NINA.GuideEngine.Core;

namespace NINA.GuideEngine.Guiding;

// The Dec backlash play model (docs/notes/PLAY-MODEL-BACKLASH.md): one instance for the guider's lifetime, so the gear's
// position and what was learned survive the corrector being rebuilt on a settings change. A calibration measures the
// shape of the play with either backlash model, so switching to the play model uses it.
public sealed partial class Guider
{
    private readonly PlayModelBacklash playBacklash = new();
    private int playBacklashLearnCount;
    private volatile PlayModelValues? playBacklashStatus;

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
            return new BacklashCompensation(s.Backlash.PulseMs, s.Backlash.FloorMs, s.Backlash.CeilingMs, s.Backlash.Enabled, limiter);
        }

        playBacklash.Limiter = limiter;
        playBacklash.SetPriorFullReversal(s.Backlash.PulseMs);
        playBacklash.Enable(s.Backlash.Enabled);
        return playBacklash;
    }

    // a calibration completed: its Dec return measured the shape of the play
    private void PlayModelCalibrated(CalibrationData result, DateTimeOffset now)
    {
        if (result.HasDecCalibration && playBacklash.SeedFromCalibration(result.DecTakeUp, result.YRate))
        {
            playBacklashLearnCount = -1;
            EmitPlayModelLearned(now);
        }
    }

    // the corrector counted the frame's Dec pulse as sent, but it was not: the gear's position is no longer known (the fixed
    // compensation keeps PHD2's behaviour)
    private void PlayModelPulsesNotSent()
    {
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
