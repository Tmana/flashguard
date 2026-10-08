using System.Runtime.InteropServices;
using FlashGuard.Capture;

namespace FlashGuard;

/// <summary>
/// <c>FlashGuard.exe --selftest</c>: engages a light (15%) steady dim on every display for a couple of
/// seconds — no flashing — and checks capture, click-through and capture exclusion. Results go to the
/// log; the exit code is 0 on success.
/// </summary>
static partial class SelfTest
{
    [LibraryImport("user32.dll")]
    private static partial IntPtr WindowFromPoint(Native.POINT p);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetAncestor(IntPtr hwnd, uint flags);

    public static int Run()
    {
        var ui = new Control();
        ui.CreateControl();
        var settings = new GuardSettings
        {
            AlwaysOn = true, Mode = MitigationMode.Dim, DimOpacity = 0.15, ShowIndicator = true,
        };
        var engine = new GuardEngine(ui, settings);
        var failures = new List<string>();
        int result = 1;

        var timer = new System.Windows.Forms.Timer { Interval = 2500 };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var statuses = engine.Statuses;
            if (statuses.Count == 0) failures.Add("no displays were found");
            foreach (var (s, hwnd, b) in engine.Diagnostics())
            {
                if (s.State != GuardState.Suppressing)
                    failures.Add($"{s.Name}: expected Suppressing, got {s.State} {s.Error}");
                if (s.TransitionsInWindow > 0)
                    failures.Add($"{s.Name}: overlay fed back into detection ({s.TransitionsInWindow} transitions)");
                var centre = new Native.POINT { X = (b.Left + b.Right) / 2, Y = (b.Top + b.Bottom) / 2 };
                var hit = GetAncestor(WindowFromPoint(centre), 2 /* GA_ROOT */);
                if (hit == hwnd)
                    failures.Add($"{s.Name}: overlay is catching the mouse (not click-through)");
                Log.Write($"selftest {s.Name} {s.Width}x{s.Height}: state={s.State} transitions={s.TransitionsInWindow} " +
                          $"smooth={(s.SmoothAvailable ? "yes" : "no")} hit=0x{hit:X} overlay=0x{hwnd:X}");
            }
            result = failures.Count == 0 ? 0 : 1;
            Log.Write(result == 0 ? "selftest PASSED" : "selftest FAILED: " + string.Join("; ", failures));
            engine.Dispose();
            Application.ExitThread();
        };

        engine.Start();
        timer.Start();
        Application.Run();
        ui.Dispose();
        return result;
    }
}
