namespace FlashGuard.UI;

/// <summary>
/// Full-monitor, click-through, topmost window that hosts the DirectComposition swap chain.
/// Excluded from screen capture so the detector keeps seeing the real desktop underneath it
/// (and so recordings/streams are unaffected).
/// </summary>
sealed class OverlayWindow : NativeWindow, IDisposable
{
    readonly Native.RECT _bounds;

    public OverlayWindow(Native.RECT bounds)
    {
        _bounds = bounds;
        CreateHandle(new CreateParams
        {
            Caption = "FlashGuard overlay",
            Style = Native.WS_POPUP,
            ExStyle = Native.WS_EX_TOPMOST | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE |
                      Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_NOREDIRECTIONBITMAP,
            X = bounds.Left,
            Y = bounds.Top,
            Width = bounds.Right - bounds.Left,
            Height = bounds.Bottom - bounds.Top,
        });
        // Layered + transparent is what makes the window click-through for other processes.
        Native.SetLayeredWindowAttributes(Handle, 0, 255, Native.LWA_ALPHA);
        ExcludedFromCapture = Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE);
        if (!ExcludedFromCapture)
            Log.Write("SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE) failed; overlay will be seen by capture");
    }

    public bool ExcludedFromCapture { get; }

    /// <summary>Safe to call from any thread; never blocks on the UI thread.</summary>
    public void SetVisible(bool visible)
    {
        if (visible)
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, _bounds.Left, _bounds.Top,
                _bounds.Right - _bounds.Left, _bounds.Bottom - _bounds.Top,
                Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW | Native.SWP_ASYNCWINDOWPOS);
        else
            Native.ShowWindowAsync(Handle, Native.SW_HIDE);
    }

    /// <summary>Re-asserts z-order without changing visibility.</summary>
    public void BringToTop() =>
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_ASYNCWINDOWPOS);

    public void Dispose() => DestroyHandle();
}
