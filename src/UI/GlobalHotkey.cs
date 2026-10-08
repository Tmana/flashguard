namespace FlashGuard.UI;

/// <summary>System-wide hotkey via RegisterHotKey on a message-only window.</summary>
sealed class GlobalHotkey : NativeWindow, IDisposable
{
    const int Id = 0x4647; // "FG"
    static readonly IntPtr HWND_MESSAGE = new(-3);
    bool _registered;

    public GlobalHotkey() => CreateHandle(new CreateParams { Parent = HWND_MESSAGE });

    public event Action? Pressed;

    public Keys Current { get; private set; } = Keys.None;

    /// <summary>Registers <paramref name="keys"/>, keeping the previous binding if it is taken.</summary>
    public bool TryRegister(Keys keys)
    {
        if (keys == Current && _registered) return true;
        if (!IsValid(keys)) return false;

        uint mods = Native.MOD_NOREPEAT;
        if (keys.HasFlag(Keys.Control)) mods |= Native.MOD_CONTROL;
        if (keys.HasFlag(Keys.Alt)) mods |= Native.MOD_ALT;
        if (keys.HasFlag(Keys.Shift)) mods |= Native.MOD_SHIFT;
        uint vk = (uint)(keys & Keys.KeyCode);

        if (_registered) Native.UnregisterHotKey(Handle, Id);
        if (Native.RegisterHotKey(Handle, Id, mods, vk))
        {
            _registered = true;
            Current = keys;
            return true;
        }
        // Restore the old binding.
        _registered = Current != Keys.None && Native.RegisterHotKey(Handle, Id, ModsOf(Current), (uint)(Current & Keys.KeyCode));
        return false;
    }

    static uint ModsOf(Keys k) => Native.MOD_NOREPEAT |
        (k.HasFlag(Keys.Control) ? Native.MOD_CONTROL : 0) |
        (k.HasFlag(Keys.Alt) ? Native.MOD_ALT : 0) |
        (k.HasFlag(Keys.Shift) ? Native.MOD_SHIFT : 0);

    public static bool IsValid(Keys keys)
    {
        var code = keys & Keys.KeyCode;
        bool hasMod = (keys & (Keys.Control | Keys.Alt)) != 0;
        return hasMod && code is not (Keys.None or Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin);
    }

    public static string Describe(Keys keys)
    {
        var parts = new List<string>();
        if (keys.HasFlag(Keys.Control)) parts.Add("Ctrl");
        if (keys.HasFlag(Keys.Alt)) parts.Add("Alt");
        if (keys.HasFlag(Keys.Shift)) parts.Add("Shift");
        var code = keys & Keys.KeyCode;
        if (code != Keys.None)
            parts.Add(code switch
            {
                >= Keys.D0 and <= Keys.D9 => ((char)('0' + (code - Keys.D0))).ToString(),
                Keys.Oemtilde => "`",
                _ => code.ToString(),
            });
        return string.Join(" + ", parts);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_HOTKEY && (int)m.WParam == Id)
            Pressed?.Invoke();
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (_registered) Native.UnregisterHotKey(Handle, Id);
        DestroyHandle();
    }
}
