// SPDX-License-Identifier: MPL-2.0

namespace NINA.GuideEngine.Algorithms;

/// <summary>
/// Decides whether a Dec offset justifies a pulse that first has to take up the gear's play (a reversal, or a gear floating
/// inside its play): such a pulse costs the play it crosses, so it is made only when the offset on that side is clearly
/// more than the frame-to-frame noise.
/// </summary>
/// <remarks>
/// <para>Per frame the offset is standardised by the noise, z = e / σ, and a one-sided CUSUM per side accumulates the
/// evidence: S⁺ = max(0, S⁺ + z − k), S⁻ = max(0, S⁻ − z − k) (Page 1954, "Continuous inspection schemes", Biometrika
/// 41). A reversal towards South (positive offset) is allowed once S⁺ ≥ h, or at once when |z| ≥
/// <see cref="ImmediateSigmas"/>. With pure noise the sums drift down by k per frame and reach h about once in 7000 frames
/// per side (Siegmund's approximation of the average run length, k = 0.5, h = 7); a real offset of 1σ is detected in about
/// 14 frames, one of 2σ in about 5. The defaults were chosen in closed-loop simulations of a mount like the OnStepX of the
/// 2026-10-12 guide log (docs/notes/PLAY-MODEL-BACKLASH.md).</para>
/// <para>What the gate filters is the noise: a pulse it holds would only have moved the gear inside its play, so holding
/// it does not move the star; a real offset or drift builds up the evidence and gets through.</para>
/// <para>σ is learned from the frame-to-frame changes of the offset minus the corrections the model expects to have
/// acted: σ² ≈ E[(Δe)²] / 2 for independent frames, an exponentially weighted mean with each change clipped at
/// <see cref="ClipSigmas"/> σ so that a dither or a jump does not inflate it.</para>
/// </remarks>
public sealed class ReversalGate
{
    /// <summary>Default CUSUM allowance k, in noise σ per frame.</summary>
    public const double DefaultAllowance = 0.5;

    /// <summary>Default CUSUM decision threshold h, in noise σ.</summary>
    public const double DefaultThreshold = 7.0;

    /// <summary>CUSUM allowance k (σ per frame): offsets below it never add evidence; about half the smallest offset worth correcting.</summary>
    public double Allowance { get; init; } = DefaultAllowance;

    /// <summary>CUSUM decision threshold h (σ): higher means fewer false alarms and later detection.</summary>
    public double Threshold { get; init; } = DefaultThreshold;

    /// <summary>An offset this many σ away reverses at once.</summary>
    public const double ImmediateSigmas = 4.0;

    /// <summary>Weight of a new frame in the noise estimate (a memory of about 20 frames).</summary>
    public const double NoiseWeight = 0.05;

    /// <summary>Frame-to-frame changes are clipped at this many σ before they enter the noise estimate.</summary>
    public const double ClipSigmas = 4.0;

    /// <summary>Frames before the noise estimate is used; until then every reversal is allowed.</summary>
    public const int WarmupFrames = 5;

    /// <summary>Smallest noise σ used (px), so a perfectly steady star still needs a real offset.</summary>
    public const double MinNoisePx = 0.02;

    private double? lastOffset;
    private double noiseVariance;
    private int noiseFrames;
    private double sumSouth;
    private double sumNorth;
    private double lastZ;
    private double? lastChange;
    private double twoFrameVariance;
    private int twoFrameCount;

    /// <summary>Frame-to-frame noise σ of the Dec offset (px); 0 until the first frame pair.</summary>
    public double NoiseSigmaPx => noiseFrames > 0 ? Math.Max(MinNoisePx, Math.Sqrt(noiseVariance)) : 0;

    /// <summary>
    /// The noise split into a white part σ_w² (px², the seeing and centroid of each frame) and a wander q (px² per frame, a
    /// random walk), from the variances of the open-loop changes over one and two frames: V₁ = 2σ_w² + q, V₂ = 2σ_w² + 2q.
    /// On the 2026-10-12 log the Dec offset wandered with a lag-1 autocorrelation of 0.84, so the frames of a take-up
    /// window are far from independent; the play model's learning uses both parts.
    /// </summary>
    public (double WhitePx2, double WanderPx2PerFrame) NoiseComponents
    {
        get
        {
            if (noiseFrames == 0)
            {
                return (0, 0);
            }

            if (twoFrameCount == 0)
            {
                return (Math.Max(MinNoisePx * MinNoisePx, noiseVariance), 0);
            }

            double wander = Math.Max(0, 2 * (twoFrameVariance - noiseVariance));
            double white = Math.Max(MinNoisePx * MinNoisePx, 2 * noiseVariance - twoFrameVariance);
            return (white, wander);
        }
    }

    /// <summary>True once the noise estimate is used.</summary>
    public bool IsWarm => noiseFrames >= WarmupFrames;

    /// <summary>CUSUM evidence for a South (positive offset) correction, in σ.</summary>
    public double SouthEvidence => sumSouth;

    /// <summary>CUSUM evidence for a North (negative offset) correction, in σ.</summary>
    public double NorthEvidence => sumNorth;

    /// <summary>
    /// One frame's raw Dec offset (mount px; positive is corrected by a South pulse) and the correction (px, same sign
    /// convention) the pulses since the previous frame are expected to have applied.
    /// </summary>
    public void Observe(double offsetPx, double expectedCorrectionPx)
    {
        if (!double.IsFinite(offsetPx))
        {
            return;
        }

        if (lastOffset is { } previous && double.IsFinite(expectedCorrectionPx))
        {
            double change = offsetPx - previous + expectedCorrectionPx;
            double v = 0.5 * change * change;
            if (noiseFrames == 0)
            {
                noiseVariance = v;
            }
            else
            {
                double clip = ClipSigmas * ClipSigmas * Math.Max(noiseVariance, MinNoisePx * MinNoisePx);
                noiseVariance = (1 - NoiseWeight) * noiseVariance + NoiseWeight * Math.Min(v, clip);
            }

            noiseFrames++;

            if (lastChange is { } previousChange)
            {
                double two = change + previousChange;
                double v2 = 0.5 * two * two;
                double clip2 = ClipSigmas * ClipSigmas * Math.Max(twoFrameVariance, 2 * MinNoisePx * MinNoisePx);
                twoFrameVariance = twoFrameCount == 0 ? v2 : (1 - NoiseWeight) * twoFrameVariance + NoiseWeight * Math.Min(v2, clip2);
                twoFrameCount++;
            }

            lastChange = change;
        }

        lastOffset = offsetPx;
        if (!IsWarm)
        {
            return;
        }

        lastZ = offsetPx / NoiseSigmaPx;
        sumSouth = Math.Max(0, sumSouth + lastZ - Allowance);
        sumNorth = Math.Max(0, sumNorth - lastZ - Allowance);
    }

    /// <summary>
    /// Whether the last observed offset justifies a reversal towards <paramref name="sign"/> (+1 South, −1 North).
    /// </summary>
    public bool Allows(int sign)
    {
        if (!IsWarm || sign == 0)
        {
            return true;
        }

        if (sign * lastZ >= ImmediateSigmas)
        {
            return true;
        }

        return (sign > 0 ? sumSouth : sumNorth) >= Threshold;
    }

    /// <summary>The evidence starts over (the gear or the lock position changed); the noise estimate is kept.</summary>
    public void ResetEvidence()
    {
        sumSouth = 0;
        sumNorth = 0;
        lastZ = 0;
        lastOffset = null;
        lastChange = null;
    }
}
