using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlashGuard;

public enum MitigationMode { Smooth, Dim }

public enum ScreenCorner { TopLeft, TopRight, BottomLeft, BottomRight }

/// <summary>
/// User preferences. Immutable so capture threads can read a snapshot without locking;
/// the UI swaps in a new instance via <c>with</c>.
/// </summary>
public sealed record GuardSettings
{
    public bool Enabled { get; init; } = true;
    public MitigationMode Mode { get; init; } = MitigationMode.Smooth;

    /// <summary>Keep suppression engaged permanently instead of reacting to detected flashes.</summary>
    public bool AlwaysOn { get; init; }

    /// <summary>Flashes (pairs of opposing transitions) within one second that engage suppression.</summary>
    public double TriggerFlashesPerSecond { get; init; } = 2.0;

    /// <summary>Relative-luminance change (0–1) that counts as a transition.</summary>
    public double LuminanceThreshold { get; init; } = 0.10;

    /// <summary>Fraction of the screen (0–1) that must change together.</summary>
    public double MinArea { get; init; } = 0.03;

    public bool DetectRedFlashes { get; init; } = true;

    /// <summary>Smooth mode: maximum linear brightness change per second, per channel (1 = black→white in 1 s).</summary>
    public double MaxChangePerSecond { get; init; } = 0.5;

    /// <summary>Smooth mode: extra darkening while engaged (0–0.9).</summary>
    public double SmoothDim { get; init; } = 0.0;

    /// <summary>Dim mode: overlay opacity while engaged (0.1–0.95).</summary>
    public double DimOpacity { get; init; } = 0.65;

    /// <summary>Seconds without a transition before suppression releases.</summary>
    public double HoldSeconds { get; init; } = 3.0;

    public bool ShowIndicator { get; init; } = true;
    public ScreenCorner IndicatorCorner { get; init; } = ScreenCorner.TopRight;

    public Keys Hotkey { get; init; } = Keys.Control | Keys.Alt | Keys.Shift | Keys.F;

    public bool StartWithWindows { get; init; }

    [JsonIgnore]
    public int TriggerTransitions => Math.Max(2, (int)Math.Ceiling(TriggerFlashesPerSecond * 2 - 1e-9));

    public static string Directory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlashGuard");

    static string FilePath => Path.Combine(Directory, "settings.json");

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static GuardSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return (JsonSerializer.Deserialize<GuardSettings>(File.ReadAllText(FilePath), Json) ?? new()).Sanitized();
        }
        catch (Exception e)
        {
            Log.Write($"settings load failed, using defaults: {e.Message}");
        }
        return new GuardSettings();
    }

    public void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception e)
        {
            Log.Write($"settings save failed: {e.Message}");
        }
    }

    public GuardSettings Sanitized() => this with
    {
        TriggerFlashesPerSecond = Math.Clamp(TriggerFlashesPerSecond, 1.0, 3.0),
        LuminanceThreshold = Math.Clamp(LuminanceThreshold, 0.03, 0.30),
        MinArea = Math.Clamp(MinArea, 0.005, 0.25),
        MaxChangePerSecond = Math.Clamp(MaxChangePerSecond, 0.1, 4.0),
        SmoothDim = Math.Clamp(SmoothDim, 0.0, 0.9),
        DimOpacity = Math.Clamp(DimOpacity, 0.1, 0.95),
        HoldSeconds = Math.Clamp(HoldSeconds, 0.5, 15.0),
    };
}
