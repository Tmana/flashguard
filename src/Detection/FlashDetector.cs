namespace FlashGuard.Detection;

/// <summary>Per-frame tuning for <see cref="FlashDetector"/>.</summary>
/// <param name="LuminanceThreshold">Relative-luminance swing (0–1) that counts as a transition. WCAG/Harding use 0.10.</param>
/// <param name="MinArea">Fraction of the screen (0–1) that must transition together for it to count.</param>
/// <param name="DetectRed">Also count saturated-red transitions (WCAG "red flash").</param>
public readonly record struct DetectorParams(float LuminanceThreshold, float MinArea, bool DetectRed);

public readonly record struct FrameResult(int UpCells, int DownCells, bool Transition, int TransitionsInWindow);

/// <summary>
/// Detects general and red flashes on a low-resolution grid of the screen, following the
/// WCAG 2.3.1 / Harding definitions: a flash is a pair of opposing luminance transitions of at
/// least the threshold, where the darker state is below 0.80 relative luminance, over a large
/// enough area. Counts screen-level transitions in a sliding one-second window.
/// </summary>
public sealed class FlashDetector
{
    const float DarkerLimit = 0.80f;
    const float RedThreshold = 20f;   // Δ(R−G−B)×320 > 20
    const double WindowSeconds = 1.0;

    static readonly float[] SrgbToLinear = BuildLut();

    int _w, _h;
    float[] _lumAnchor = [], _redAnchor = [];
    sbyte[] _lumDir = [], _redDir = [];
    bool _primed;
    bool _upLatched, _downLatched;
    int _prevUp, _prevDown;
    readonly Queue<double> _events = new();

    public double LastTransitionTime { get; private set; } = double.NegativeInfinity;

    public int TransitionsInWindow(double now)
    {
        Prune(now);
        return _events.Count;
    }

    public void Reset()
    {
        _primed = false;
        _upLatched = _downLatched = false;
        _prevUp = _prevDown = 0;
        _events.Clear();
        LastTransitionTime = double.NegativeInfinity;
    }

    /// <param name="bgra">Grid pixels, 8-bit sRGB-encoded BGRA.</param>
    /// <param name="time">Frame time in seconds (monotonic).</param>
    public FrameResult Process(ReadOnlySpan<byte> bgra, int width, int height, int rowPitch, double time, in DetectorParams p)
    {
        if (width != _w || height != _h)
            Allocate(width, height);

        int up = 0, down = 0;
        var lut = SrgbToLinear;
        for (int y = 0; y < height; y++)
        {
            var row = bgra.Slice(y * rowPitch, width * 4);
            int k = y * width;
            for (int x = 0; x < width; x++, k++)
            {
                float b = lut[row[x * 4]], g = lut[row[x * 4 + 1]], r = lut[row[x * 4 + 2]];
                float lum = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                float sum = r + g + b;
                float red = sum > 0f && r / sum >= 0.8f ? MathF.Max(0f, r - g - b) * 320f : 0f;

                if (!_primed)
                {
                    _lumAnchor[k] = lum;
                    _redAnchor[k] = red;
                    continue;
                }

                int t = Step(ref _lumAnchor[k], ref _lumDir[k], lum, p.LuminanceThreshold, requireDark: true);
                int tr = Step(ref _redAnchor[k], ref _redDir[k], red, RedThreshold, requireDark: false);
                if (t == 0 && p.DetectRed) t = tr;
                if (t > 0) up++;
                else if (t < 0) down++;
            }
        }

        bool transition = false;
        if (_primed)
        {
            float minCells = MathF.Max(1f, p.MinArea * width * height);
            transition |= Edge(up, _prevUp, minCells, ref _upLatched);
            transition |= Edge(down, _prevDown, minCells, ref _downLatched);
            if (transition)
            {
                _events.Enqueue(time);
                LastTransitionTime = time;
            }
        }
        _primed = true;
        _prevUp = up;
        _prevDown = down;
        Prune(time);
        return new FrameResult(up, down, transition, _events.Count);
    }

    /// <summary>
    /// Rising-edge detector for "enough of the screen moved the same way". The area may be split
    /// over two consecutive frames (a fade that spans a capture boundary), so it sums this frame and
    /// the previous one, but only fires on a frame that itself contributed cells.
    /// </summary>
    static bool Edge(int now, int prev, float minCells, ref bool latched)
    {
        if (latched && now < minCells * 0.5f)
            latched = false;
        if (!latched && now > 0 && now + prev >= minCells)
        {
            latched = true;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Hysteresis tracker for one cell. The anchor follows the current extreme while the value keeps
    /// moving the same way; a swing of at least <paramref name="thr"/> back from it is a transition.
    /// Returns +1/−1 for a counted transition, 0 otherwise.
    /// </summary>
    static int Step(ref float anchor, ref sbyte dir, float v, float thr, bool requireDark)
    {
        float d = v - anchor;
        if ((dir > 0 && d > 0f) || (dir < 0 && d < 0f))
        {
            anchor = v;
            return 0;
        }
        if (MathF.Abs(d) < thr)
            return 0;

        sbyte nd = d > 0f ? (sbyte)1 : (sbyte)-1;
        bool counts = !requireDark || MathF.Min(anchor, v) < DarkerLimit;
        anchor = v;
        dir = nd;
        return counts ? nd : 0;
    }

    void Prune(double now)
    {
        while (_events.Count > 0 && _events.Peek() < now - WindowSeconds)
            _events.Dequeue();
    }

    void Allocate(int w, int h)
    {
        _w = w;
        _h = h;
        int n = w * h;
        _lumAnchor = new float[n];
        _redAnchor = new float[n];
        _lumDir = new sbyte[n];
        _redDir = new sbyte[n];
        Reset();
    }

    static float[] BuildLut()
    {
        var lut = new float[256];
        for (int i = 0; i < 256; i++)
        {
            double c = i / 255.0;
            lut[i] = (float)(c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4));
        }
        return lut;
    }
}
