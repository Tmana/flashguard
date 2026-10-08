using Microsoft.Win32;
using Vortice.DXGI;

namespace FlashGuard.Capture;

/// <summary>Owns one <see cref="MonitorGuard"/> per attached display and rebuilds them when the layout changes.</summary>
sealed class GuardEngine : IDisposable
{
    readonly Control _ui;
    readonly List<MonitorGuard> _guards = [];
    readonly System.Windows.Forms.Timer _displayDebounce = new() { Interval = 1500 };
    readonly System.Windows.Forms.Timer _zOrder = new() { Interval = 1000 };
    volatile GuardSettings _settings;

    public GuardEngine(Control ui, GuardSettings settings)
    {
        _ui = ui;
        _settings = settings;
        _displayDebounce.Tick += (_, _) =>
        {
            _displayDebounce.Stop();
            Log.Write("display configuration changed; restarting");
            Restart();
        };
        _zOrder.Tick += (_, _) =>
        {
            foreach (var g in _guards) g.RaiseToTop(_settings);
        };
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    /// <summary>Raised on the UI thread when any monitor starts or stops suppressing.</summary>
    public event Action? EngagedChanged;

    public GuardSettings Settings
    {
        get => _settings;
        set
        {
            bool wasEnabled = _settings.Enabled;
            bool indicatorChanged = value.ShowIndicator != _settings.ShowIndicator || value.IndicatorCorner != _settings.IndicatorCorner;
            _settings = value;
            if (indicatorChanged)
                foreach (var g in _guards.Where(g => g.Engaged))
                {
                    if (value.ShowIndicator) g.ShowIndicator(value);
                    else g.HideIndicator();
                }
            if (value.Enabled != wasEnabled)
            {
                if (value.Enabled) Start();
                else Stop();
            }
        }
    }

    public bool AnyEngaged => _guards.Any(g => g.Engaged);

    public IReadOnlyList<MonitorStatus> Statuses => _guards.Select(g => g.Status).ToList();

    internal IEnumerable<(MonitorStatus Status, IntPtr Overlay, Native.RECT Bounds)> Diagnostics() =>
        _guards.Select(g => (g.Status, g.OverlayHandle, g.Bounds)).ToList();

    public void Start()
    {
        if (_guards.Count > 0 || !_settings.Enabled) return;
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint a = 0; factory.EnumAdapters1(a, out IDXGIAdapter1 adapter).Success; a++)
            {
                using (adapter)
                {
                    for (uint o = 0; adapter.EnumOutputs(o, out IDXGIOutput output).Success; o++)
                    {
                        using (output)
                        {
                            var desc = output.Description;
                            if (!desc.AttachedToDesktop) continue;
                            var guard = new MonitorGuard(this, (int)a, (int)o, desc);
                            _guards.Add(guard);
                            guard.Start();
                        }
                    }
                }
            }
            Log.Write($"started on {_guards.Count} display(s): {string.Join(", ", _guards.Select(g => g.Name))}");
        }
        catch (Exception e)
        {
            Log.Write($"start failed: {e}");
        }
        EngagedChanged?.Invoke();
    }

    public void Stop()
    {
        foreach (var g in _guards) g.SignalStop();
        foreach (var g in _guards) g.Join();
        _guards.Clear();
        _zOrder.Enabled = false;
        EngagedChanged?.Invoke();
    }

    public void Restart()
    {
        Stop();
        Start();
    }

    /// <summary>Called from worker threads.</summary>
    internal void OnEngagedChanged(MonitorGuard guard, bool engaged)
    {
        Post(() =>
        {
            if (!_guards.Contains(guard)) return;
            if (engaged) guard.ShowIndicator(_settings);
            else guard.HideIndicator();
            _zOrder.Enabled = AnyEngaged;
            EngagedChanged?.Invoke();
        });
    }

    void Post(Action action)
    {
        try
        {
            if (_ui.IsHandleCreated && !_ui.IsDisposed)
                _ui.BeginInvoke(action);
        }
        catch (InvalidOperationException)
        {
            // UI is shutting down.
        }
    }

    void OnDisplaySettingsChanged(object? sender, EventArgs e) => Post(() =>
    {
        _displayDebounce.Stop();
        _displayDebounce.Start();
    });

    public void Dispose()
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _displayDebounce.Dispose();
        _zOrder.Dispose();
        Stop();
    }
}
