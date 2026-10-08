using System.Diagnostics;
using FlashGuard.Detection;
using FlashGuard.UI;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;

namespace FlashGuard.Capture;

public enum GuardState { Starting, Watching, Suppressing, Releasing, Error }

public sealed record MonitorStatus(string Name, int Width, int Height, GuardState State, int TransitionsInWindow,
    bool SmoothAvailable, string? Error);

/// <summary>
/// Watches one monitor on its own thread: Desktop Duplication → GPU downsample → flash detector,
/// and drives that monitor's overlay while suppression is engaged.
/// </summary>
sealed class MonitorGuard
{
    const int MaxGridWidth = 128;
    const int ReadbackRing = 3;
    const double MaxAnalysisRate = 60;
    const double FadeOutSeconds = 0.6;

    static readonly FeatureLevel[] Levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0];
    static readonly Lazy<(byte[] Vs, byte[] Filter, byte[] Smooth, byte[] Dim)> Bytecode = new(() =>
    (
        Compiler.Compile(Shaders.Source, "VS", "flashguard.hlsl", "vs_4_0").ToArray(),
        Compiler.Compile(Shaders.Source, "PSFilter", "flashguard.hlsl", "ps_4_0").ToArray(),
        Compiler.Compile(Shaders.Source, "PSPresentSmooth", "flashguard.hlsl", "ps_4_0").ToArray(),
        Compiler.Compile(Shaders.Source, "PSPresentDim", "flashguard.hlsl", "ps_4_0").ToArray()
    ));

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Size = 16)]
    struct Params
    {
        public float MaxDelta, Dim, Opacity;
    }

    readonly GuardEngine _engine;
    readonly int _adapterIndex, _outputIndex;
    readonly OverlayWindow _overlay;
    readonly IndicatorWindow _indicator;
    readonly FlashDetector _detector = new();
    readonly Thread _thread;
    volatile bool _stop;

    public string Name { get; }
    public IntPtr OverlayHandle => _overlay.Handle;
    public Native.RECT Bounds { get; }
    int Width => Bounds.Right - Bounds.Left;
    int Height => Bounds.Bottom - Bounds.Top;

    // Status read by the UI thread.
    volatile GuardState _state = GuardState.Starting;
    volatile int _transitions;
    volatile string? _error;
    volatile bool _smoothAvailable = true;
    public bool Engaged => _state is GuardState.Suppressing or GuardState.Releasing;
    public MonitorStatus Status => new(Name, Width, Height, _state, _transitions, _smoothAvailable, _error);

    // D3D state, owned by the worker thread.
    IDXGIFactory2? _factory;
    ID3D11Device? _device;
    ID3D11DeviceContext? _ctx;
    IDXGIOutput? _output;
    IDXGIOutputDuplication? _dup;
    ID3D11Texture2D? _desktop;
    readonly ID3D11Texture2D?[] _staging = new ID3D11Texture2D?[ReadbackRing];
    readonly double[] _stagingTime = new double[ReadbackRing];
    int _ringHead, _readbacks;
    ID3D11ShaderResourceView? _desktopSrv;
    ID3D11Texture2D?[] _hist = new ID3D11Texture2D?[2];
    ID3D11ShaderResourceView?[] _histSrv = new ID3D11ShaderResourceView?[2];
    ID3D11RenderTargetView?[] _histRtv = new ID3D11RenderTargetView?[2];
    int _histCur;
    ID3D11VertexShader? _vs;
    ID3D11PixelShader? _psFilter, _psSmooth, _psDim;
    ID3D11Buffer? _cb;
    IDXGISwapChain1? _swap;
    ID3D11RenderTargetView? _backRtv;
    IDCompositionDevice? _dcomp;
    IDCompositionTarget? _dtarget;
    IDCompositionVisual? _dvisual;
    static readonly bool Stats = Environment.GetEnvironmentVariable("FLASHGUARD_STATS") == "1";
    double _statStart = Now();
    long _statFrames, _statAnalyzeTicks, _statLoops;
    int _srcW, _srcH, _mip, _gridW, _gridH;
    bool _haveFrame;
    volatile bool _visible;

    public MonitorGuard(GuardEngine engine, int adapterIndex, int outputIndex, OutputDescription desc)
    {
        _engine = engine;
        _adapterIndex = adapterIndex;
        _outputIndex = outputIndex;
        Name = desc.DeviceName.TrimStart('\\', '.');
        Bounds = new Native.RECT
        {
            Left = desc.DesktopCoordinates.Left, Top = desc.DesktopCoordinates.Top,
            Right = desc.DesktopCoordinates.Right, Bottom = desc.DesktopCoordinates.Bottom,
        };
        // Windows must be created on the UI thread (this constructor runs there).
        _overlay = new OverlayWindow(Bounds);
        _indicator = new IndicatorWindow(desc.Monitor, Bounds);
        _thread = new Thread(Run) { IsBackground = true, Name = $"FlashGuard {Name}", Priority = ThreadPriority.AboveNormal };
    }

    public void Start() => _thread.Start();

    public void SignalStop() => _stop = true;

    /// <summary>UI thread: waits for the worker and destroys the windows.</summary>
    public void Join()
    {
        _stop = true;
        if (_thread.IsAlive && !_thread.Join(3000))
            Log.Write($"{Name}: worker did not stop in time");
        _indicator.Dispose();
        _overlay.Dispose();
    }

    static double Now() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    void Run()
    {
        while (!_stop)
        {
            try
            {
                Init();
                Loop();
            }
            catch (OperationCanceledException) when (_stop)
            {
            }
            catch (Exception e) when (!_stop)
            {
                Log.Write($"{Name}: {e.GetType().Name}: {e.Message}");
                _error = e.Message;
                _state = GuardState.Error;
                Cleanup();
                SleepUnlessStopped(1000);
            }
        }
        Cleanup();
    }

    void SleepUnlessStopped(int ms)
    {
        for (int t = 0; t < ms && !_stop; t += 50)
            Thread.Sleep(50);
    }

    void Init()
    {
        _factory = DXGI.CreateDXGIFactory1<IDXGIFactory2>();
        _factory.EnumAdapters1((uint)_adapterIndex, out IDXGIAdapter1 adapter).CheckError();
        using (adapter)
        {
            adapter.EnumOutputs((uint)_outputIndex, out _output).CheckError();
            D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport, Levels,
                out _device, out _ctx).CheckError();
            _dup = Duplicate(_output!, _device!);
        }
        var device = _device!;

        var dd = _dup.Description;
        _srcW = (int)dd.ModeDescription.Width;
        _srcH = (int)dd.ModeDescription.Height;
        // The overlay mirrors pixels 1:1, which only lines up for an unrotated output.
        _smoothAvailable = dd.Rotation is ModeRotation.Identity or ModeRotation.Unspecified && _srcW == Width && _srcH == Height;

        _desktop = device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)_srcW, Height = (uint)_srcH, MipLevels = 0, ArraySize = 1,
            Format = Format.B8G8R8A8_Typeless, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
            MiscFlags = ResourceOptionFlags.GenerateMips,
        });
        _desktopSrv = device.CreateShaderResourceView(_desktop,
            new ShaderResourceViewDescription(_desktop, ShaderResourceViewDimension.Texture2D, Format.B8G8R8A8_UNorm_SRgb, 0, uint.MaxValue));

        _mip = 0;
        while ((_srcW >> _mip) > MaxGridWidth) _mip++;
        _gridW = Math.Max(1, _srcW >> _mip);
        _gridH = Math.Max(1, _srcH >> _mip);
        for (int i = 0; i < ReadbackRing; i++)
            _staging[i] = device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)_gridW, Height = (uint)_gridH, MipLevels = 1, ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging, CPUAccessFlags = CpuAccessFlags.Read,
            });
        _ringHead = _readbacks = 0;

        var bc = Bytecode.Value;
        _vs = device.CreateVertexShader(bc.Vs);
        _psFilter = device.CreatePixelShader(bc.Filter);
        _psSmooth = device.CreatePixelShader(bc.Smooth);
        _psDim = device.CreatePixelShader(bc.Dim);
        _cb = device.CreateBuffer(16, BindFlags.ConstantBuffer);

        _swap = _factory.CreateSwapChainForComposition(device, new SwapChainDescription1
        {
            Width = (uint)Width, Height = (uint)Height, Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0), BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2, Scaling = Scaling.Stretch, SwapEffect = SwapEffect.FlipSequential,
            AlphaMode = AlphaMode.Premultiplied,
        });
        using (var back = _swap.GetBuffer<ID3D11Texture2D>(0))
            _backRtv = device.CreateRenderTargetView(back,
                new RenderTargetViewDescription(back, RenderTargetViewDimension.Texture2D, Format.B8G8R8A8_UNorm_SRgb));

        using (var dxgiDevice = device.QueryInterface<IDXGIDevice>())
            _dcomp = DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgiDevice);
        _dcomp.CreateTargetForHwnd(_overlay.Handle, true, out _dtarget).CheckError();
        _dvisual = _dcomp.CreateVisual();
        _dvisual.SetContent(_swap).CheckError();
        _dtarget!.SetRoot(_dvisual).CheckError();
        _dcomp.Commit().CheckError();

        _haveFrame = false;
        _detector.Reset();
        _error = null;
        _state = GuardState.Watching;
    }

    IDXGIOutputDuplication Reconnect(ref int lostCount)
    {
        DisposeAndNull(ref _dup);
        _readbacks = 0;
        _haveFrame = false;
        _detector.Reset();
        while (!_stop)
        {
            SleepUnlessStopped(Math.Min(3000, 100 << Math.Min(lostCount, 5)));
            lostCount++;
            try
            {
                _dup = Duplicate(_output!, _device!);
                var md = _dup.Description.ModeDescription;
                if (md.Width != _srcW || md.Height != _srcH)
                    throw new InvalidOperationException("display mode changed");   // full rebuild
                _state = GuardState.Watching;
                return _dup;
            }
            catch (SharpGen.Runtime.SharpGenException e)
            {
                // E_ACCESSDENIED on the secure desktop, NOT_CURRENTLY_AVAILABLE, etc. Keep waiting.
                if (lostCount == 1 || lostCount % 20 == 0)
                    Log.Write($"{Name}: capture unavailable ({e.ResultCode.Code:X8}), retrying");
            }
        }
        throw new OperationCanceledException();
    }

    static IDXGIOutputDuplication Duplicate(IDXGIOutput output, ID3D11Device device)
    {
        // DuplicateOutput1 lets the system convert HDR/FP16 desktops to 8-bit BGRA for us.
        using var output5 = output.QueryInterfaceOrNull<IDXGIOutput5>();
        if (output5 != null)
        {
            try { return output5.DuplicateOutput1(device, [Format.B8G8R8A8_UNorm]); }
            catch (SharpGen.Runtime.SharpGenException e) { Log.Write($"DuplicateOutput1 failed ({e.Message}), falling back"); }
        }
        using var output1 = output.QueryInterface<IDXGIOutput1>();
        return output1.DuplicateOutput(device);
    }

    void Loop()
    {
        var ctx = _ctx!;
        var dup = _dup!;
        int lostCount = 0;
        bool engaged = false;
        _visible = false;
        double lastHazard = double.NegativeInfinity, opacity = 0, lastTick = Now(), lastRender = 0;
        Params lastParams = default;

        double nextAnalysis = 0;
        bool pendingFrame = false;

        while (!_stop)
        {
            var s = _engine.Settings;
            bool smooth = s.Mode == MitigationMode.Smooth && _smoothAvailable;
            bool active = engaged || _visible;
            // Idle: block in the driver until the desktop changes. Smooth: Present(1) paces the loop.
            double wait = !active ? 0.25 : smooth ? 0 : 0.016;
            if (pendingFrame) wait = Math.Min(wait, Math.Max(0, nextAnalysis - Now()));
            if (_readbacks > 0) wait = Math.Min(wait, 0.003);

            var r = dup.AcquireNextFrame((uint)Math.Ceiling(wait * 1000), out OutduplFrameInfo info, out IDXGIResource res);
            double now = Now();
            if (r.Success)
            {
                try
                {
                    if (info.LastPresentTime != 0)
                    {
                        using var tex = res.QueryInterface<ID3D11Texture2D>();
                        ctx.CopySubresourceRegion(_desktop!, 0, 0, 0, 0, tex, 0);
                        pendingFrame = _haveFrame = true;
                        lostCount = 0;
                    }
                }
                finally
                {
                    res.Dispose();
                    dup.ReleaseFrame();
                }
            }
            else if (r == Vortice.DXGI.ResultCode.AccessLost || r == Vortice.DXGI.ResultCode.InvalidCall)
            {
                // Exclusive fullscreen, UAC / lock screen, mode change: reconnect capture only,
                // backing off so a fullscreen game doesn't make us spin.
                HideOverlay();
                engaged = pendingFrame = false;
                opacity = 0;
                dup = Reconnect(ref lostCount);
                continue;
            }
            else if (r != Vortice.DXGI.ResultCode.WaitTimeout)
                r.CheckError();

            // Analyse at most MaxAnalysisRate times a second, always on the newest frame, so a
            // high-refresh display costs no more than a 60 Hz one and no final state is skipped.
            if (pendingFrame && now >= nextAnalysis)
            {
                SubmitAnalysis(now);
                pendingFrame = false;
                nextAnalysis = now + 1.0 / MaxAnalysisRate;
                _statFrames++;
            }
            long a0 = Stopwatch.GetTimestamp();
            DrainReadbacks(s, block: false);
            _statAnalyzeTicks += Stopwatch.GetTimestamp() - a0;

            if (Stats && now - _statStart > 10)
            {
                Log.Write($"stats {Name}: {_statFrames / (now - _statStart):0.0} analyses/s, " +
                          $"{(_statFrames == 0 ? 0 : _statAnalyzeTicks * 1000.0 / Stopwatch.Frequency / _statFrames):0.000} ms readback/analysis, " +
                          $"{_statLoops / (now - _statStart):0} loops/s");
                _statStart = now; _statFrames = 0; _statAnalyzeTicks = 0; _statLoops = 0;
            }
            _statLoops++;

            int transitions = _detector.TransitionsInWindow(now);
            _transitions = transitions;
            if (s.AlwaysOn || transitions >= s.TriggerTransitions)
            {
                if (!engaged)
                    Log.Write($"{Name}: engaged ({transitions} transitions in 1 s{(s.AlwaysOn ? ", always-on" : "")})");
                engaged = true;
                lastHazard = now;
            }
            else if (engaged && _detector.LastTransitionTime > lastHazard)
                lastHazard = _detector.LastTransitionTime;   // anything flash-like keeps it engaged
            if (engaged && now - lastHazard > s.HoldSeconds)
                engaged = false;

            double dt = Math.Clamp(now - lastTick, 0, 0.1);
            lastTick = now;
            opacity = engaged ? 1 : Math.Max(0, opacity - dt / FadeOutSeconds);
            _state = engaged ? GuardState.Suppressing : opacity > 0 ? GuardState.Releasing : GuardState.Watching;

            if (opacity > 0 && _haveFrame)
            {
                bool justShown = !_visible;
                if (smooth)
                {
                    float maxDelta = justShown ? 1e6f : (float)(s.MaxChangePerSecond * Math.Clamp(now - lastRender, 1 / 240.0, 0.1));
                    RenderSmooth(new Params { MaxDelta = maxDelta, Dim = (float)s.SmoothDim, Opacity = (float)opacity });
                }
                else
                {
                    var p = new Params { Dim = (float)s.DimOpacity, Opacity = (float)opacity };
                    if (justShown || !p.Equals(lastParams))
                        RenderDim(p);
                    lastParams = p;
                }
                lastRender = now;
                if (justShown)
                {
                    _visible = true;
                    _overlay.SetVisible(true);
                    _engine.OnEngagedChanged(this, true);
                }
            }
            else if (_visible)
            {
                lastParams = default;
                HideOverlay();
                Log.Write($"{Name}: released");
            }

            // Guard against a busy loop if Present stops blocking (monitor asleep, occluded, ...).
            if (active && Now() - now < 0.002)
                Thread.Sleep(4);
        }

        HideOverlay();
    }

    void HideOverlay()
    {
        if (!_visible) return;
        _visible = false;
        _overlay.SetVisible(false);
        _engine.OnEngagedChanged(this, false);
    }

    /// <summary>Downsamples the latest desktop copy on the GPU and queues a non-blocking readback.</summary>
    void SubmitAnalysis(double now)
    {
        var ctx = _ctx!;
        if (_readbacks == ReadbackRing)
            DrainReadbacks(_engine.Settings, block: true);
        ctx.GenerateMips(_desktopSrv!);
        ctx.CopySubresourceRegion(_staging[_ringHead]!, 0, 0, 0, 0, _desktop!, (uint)_mip);
        _stagingTime[_ringHead] = now;
        _ringHead = (_ringHead + 1) % ReadbackRing;
        _readbacks++;
        ctx.Flush();
    }

    /// <summary>Feeds every finished readback to the detector, oldest first, without stalling on the GPU.</summary>
    unsafe void DrainReadbacks(GuardSettings s, bool block)
    {
        var ctx = _ctx!;
        var p = new DetectorParams((float)s.LuminanceThreshold, (float)s.MinArea, s.DetectRedFlashes);
        while (_readbacks > 0)
        {
            int i = (_ringHead - _readbacks + ReadbackRing) % ReadbackRing;
            var flags = block ? Vortice.Direct3D11.MapFlags.None : Vortice.Direct3D11.MapFlags.DoNotWait;
            var r = ctx.Map(_staging[i]!, 0, MapMode.Read, flags, out MappedSubresource map);
            if (r == Vortice.DXGI.ResultCode.WasStillDrawing)
                return;
            r.CheckError();
            try
            {
                var data = new ReadOnlySpan<byte>((void*)map.DataPointer, (int)map.RowPitch * _gridH);
                _detector.Process(data, _gridW, _gridH, (int)map.RowPitch, _stagingTime[i], p);
            }
            finally
            {
                ctx.Unmap(_staging[i]!, 0);
            }
            _readbacks--;
            block = false;
        }
    }

    void EnsureHistory()
    {
        if (_hist[0] != null) return;
        for (int i = 0; i < 2; i++)
        {
            _hist[i] = _device!.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)_srcW, Height = (uint)_srcH, MipLevels = 1, ArraySize = 1,
                Format = Format.R16G16B16A16_Float, SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
            });
            _histSrv[i] = _device.CreateShaderResourceView(_hist[i]!);
            _histRtv[i] = _device.CreateRenderTargetView(_hist[i]!);
        }
    }

    void BeginPass(ID3D11RenderTargetView rtv, int w, int h, ID3D11PixelShader ps, in Params p)
    {
        var ctx = _ctx!;
        ctx.PSSetShaderResources(0, new ID3D11ShaderResourceView[2]);
        ctx.OMSetRenderTargets(rtv);
        ctx.RSSetViewport(0, 0, w, h);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.VSSetShader(_vs!);
        ctx.PSSetShader(ps);
        var copy = p;
        ctx.UpdateSubresource(in copy, _cb!);
        ctx.PSSetConstantBuffer(0, _cb!);
    }

    void RenderSmooth(in Params p)
    {
        EnsureHistory();
        var ctx = _ctx!;
        int next = 1 - _histCur;
        BeginPass(_histRtv[next]!, _srcW, _srcH, _psFilter!, p);
        ctx.PSSetShaderResources(0, [_desktopSrv!, _histSrv[_histCur]!]);
        ctx.Draw(3, 0);
        _histCur = next;

        BeginPass(_backRtv!, Width, Height, _psSmooth!, p);
        ctx.PSSetShaderResources(1, [_histSrv[_histCur]!]);
        ctx.Draw(3, 0);
        _swap!.Present(1, PresentFlags.None);
    }

    void RenderDim(in Params p)
    {
        BeginPass(_backRtv!, Width, Height, _psDim!, p);
        _ctx!.Draw(3, 0);
        _swap!.Present(1, PresentFlags.None);
    }

    void Cleanup()
    {
        HideOverlay();
        _ctx?.ClearState();
        _ctx?.Flush();
        for (int i = 0; i < 2; i++)
        {
            _histRtv[i]?.Dispose(); _histRtv[i] = null;
            _histSrv[i]?.Dispose(); _histSrv[i] = null;
            _hist[i]?.Dispose(); _hist[i] = null;
        }
        DisposeAndNull(ref _dvisual);
        DisposeAndNull(ref _dtarget);
        DisposeAndNull(ref _dcomp);
        DisposeAndNull(ref _backRtv);
        DisposeAndNull(ref _swap);
        DisposeAndNull(ref _cb);
        DisposeAndNull(ref _psDim);
        DisposeAndNull(ref _psSmooth);
        DisposeAndNull(ref _psFilter);
        DisposeAndNull(ref _vs);
        for (int i = 0; i < ReadbackRing; i++) DisposeAndNull(ref _staging[i]);
        _readbacks = 0;
        DisposeAndNull(ref _desktopSrv);
        DisposeAndNull(ref _desktop);
        DisposeAndNull(ref _dup);
        DisposeAndNull(ref _output);
        DisposeAndNull(ref _ctx);
        DisposeAndNull(ref _device);
        DisposeAndNull(ref _factory);
    }

    static void DisposeAndNull<T>(ref T? obj) where T : class, IDisposable
    {
        try { obj?.Dispose(); } catch { }
        obj = null;
    }

    public void ShowIndicator(GuardSettings s)
    {
        if (s.ShowIndicator) _indicator.Show(s.IndicatorCorner);
    }

    public void HideIndicator() => _indicator.Hide();

    /// <summary>UI thread: put the overlay (and its label) back on top of other topmost windows.</summary>
    public void RaiseToTop(GuardSettings s)
    {
        if (!_visible) return;
        _overlay.BringToTop();
        ShowIndicator(s);
    }
}
