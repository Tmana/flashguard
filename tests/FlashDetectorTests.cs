using FlashGuard.Detection;

namespace FlashGuard.Tests;

public class FlashDetectorTests
{
    const int W = 64, H = 36, Fps = 60;
    const int TriggerTransitions = 4; // default: 2 flashes per second
    static readonly DetectorParams Defaults = new(0.10f, 0.03f, DetectRed: true);

    /// <summary>Feeds <paramref name="frames"/> frames and returns the peak transitions-in-window and the first frame it reached the trigger.</summary>
    static (int Peak, int FirstTriggerFrame) Run(Func<int, byte[]> frame, int frames, DetectorParams? p = null)
    {
        var d = new FlashDetector();
        int peak = 0, first = -1;
        for (int f = 0; f < frames; f++)
        {
            var r = d.Process(frame(f), W, H, W * 4, f / (double)Fps, p ?? Defaults);
            peak = Math.Max(peak, r.TransitionsInWindow);
            if (first < 0 && r.TransitionsInWindow >= TriggerTransitions) first = f;
        }
        return (peak, first);
    }

    static byte[] Fill(byte r, byte g, byte b, double area = 1.0, (byte R, byte G, byte B)? rest = null)
    {
        var px = new byte[W * H * 4];
        int lit = (int)Math.Round(area * W * H);
        var other = rest ?? (0, 0, 0);
        for (int i = 0; i < W * H; i++)
        {
            var (cr, cg, cb) = i < lit ? (r, g, b) : other;
            px[i * 4] = cb; px[i * 4 + 1] = cg; px[i * 4 + 2] = cr; px[i * 4 + 3] = 255;
        }
        return px;
    }

    static readonly byte[] Black = Fill(0, 0, 0), White = Fill(255, 255, 255), Gray = Fill(128, 128, 128);

    [Fact]
    public void StaticScreen_NoTransitions()
    {
        Assert.Equal(0, Run(_ => Gray, 120).Peak);
    }

    [Fact]
    public void FullScreenFlicker10Hz_TriggersQuickly()
    {
        var (peak, first) = Run(f => (f / 3) % 2 == 0 ? Black : White, 120);
        Assert.True(peak >= 18, $"peak {peak}");
        Assert.InRange(first, 0, 15); // within a quarter second
    }

    [Fact]
    public void FrameRateFlicker_Triggers()
    {
        var (peak, first) = Run(f => f % 2 == 0 ? Black : White, 60);
        Assert.True(peak >= 50, $"peak {peak}");
        Assert.InRange(first, 0, 6);
    }

    [Fact]
    public void TinyAreaFlicker_Ignored()
    {
        var small = Fill(255, 255, 255, area: 0.01);
        Assert.Equal(0, Run(f => (f / 3) % 2 == 0 ? Black : small, 120).Peak);
    }

    [Fact]
    public void LargeEnoughAreaFlicker_Triggers()
    {
        var patch = Fill(255, 255, 255, area: 0.05);
        Assert.True(Run(f => (f / 3) % 2 == 0 ? Black : patch, 120).FirstTriggerFrame >= 0);
    }

    [Fact]
    public void LowContrastFlicker_Ignored()
    {
        // sRGB 128 ↔ 140 is ~0.22 → 0.26 linear: below the 10% threshold.
        var a = Fill(128, 128, 128);
        var b = Fill(140, 140, 140);
        Assert.Equal(0, Run(f => (f / 3) % 2 == 0 ? a : b, 120).Peak);
    }

    [Fact]
    public void FlickerBetweenTwoBrightStates_IgnoredPerWcag()
    {
        // Both states above 0.80 relative luminance: WCAG doesn't count it as a flash.
        var a = Fill(235, 235, 235);
        Assert.Equal(0, Run(f => (f / 3) % 2 == 0 ? a : White, 120).Peak);
    }

    [Fact]
    public void SaturatedRedFlicker_TriggersOnlyWithRedDetection()
    {
        // Dark red vs grey of nearly the same luminance: invisible to the luminance test.
        var red = Fill(149, 0, 0);   // linear R ≈ 0.30 → Y ≈ 0.064
        var grey = Fill(71, 71, 71); // Y ≈ 0.063
        Assert.True(Run(f => (f / 3) % 2 == 0 ? grey : red, 120).FirstTriggerFrame >= 0);
        Assert.Equal(0, Run(f => (f / 3) % 2 == 0 ? grey : red, 120, Defaults with { DetectRed = false }).Peak);
    }

    [Fact]
    public void SceneCutEverySecond_DoesNotTrigger()
    {
        var (peak, _) = Run(f => (f / Fps) % 2 == 0 ? Black : White, 300);
        Assert.InRange(peak, 0, 2);
    }

    [Fact]
    public void SlowFullRangeFade_DoesNotTrigger()
    {
        // 0.5 Hz sine between black and white.
        var (peak, first) = Run(f =>
        {
            byte v = (byte)Math.Round(127.5 + 127.5 * Math.Sin(f / (double)Fps * Math.PI));
            return Fill(v, v, v);
        }, 600);
        Assert.Equal(-1, first);
        Assert.InRange(peak, 0, 2);
    }

    [Fact]
    public void TransitionSplitAcrossTwoFrames_CountsOnce()
    {
        // Each change lands half on one captured frame and half on the next.
        var half = Fill(255, 255, 255, area: 0.5);
        var d = new FlashDetector();
        var seq = new[] { Black, Black, half, White, White, White };
        int events = 0;
        for (int f = 0; f < seq.Length; f++)
            events += d.Process(seq[f], W, H, W * 4, f / 60.0, Defaults).Transition ? 1 : 0;
        Assert.Equal(1, events);
    }
}
