using FlashGuard.Capture;
using Microsoft.Win32;

namespace FlashGuard.UI;

/// <summary>Application root: tray icon, global hotkey, settings window and the capture engine.</summary>
sealed class TrayApp : ApplicationContext
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "FlashGuard";

    readonly Control _ui = new();
    readonly NotifyIcon _tray;
    readonly ToolStripMenuItem _enabledItem, _settingsItem;
    readonly GlobalHotkey _hotkey = new();
    readonly GuardEngine _engine;
    readonly SettingsForm _form;
    readonly System.Windows.Forms.Timer _saveDebounce = new() { Interval = 600 };
    GuardSettings _settings;

    public TrayApp(bool quietStart)
    {
        _ui.CreateControl();
        _settings = GuardSettings.Load();
        _engine = new GuardEngine(_ui, _settings);
        _engine.EngagedChanged += UpdateTray;

        _form = new SettingsForm(() => _settings, Apply, TryRegisterHotkey, () => _engine.Statuses, ExitThread);

        _enabledItem = new ToolStripMenuItem("Protection enabled", null, (_, _) => Apply(_settings with { Enabled = !_settings.Enabled }));
        _settingsItem = new ToolStripMenuItem("Settings…", null, (_, _) => _form.Toggle());
        var menu = new ContextMenuStrip();
        menu.Items.Add(_settingsItem);
        menu.Items.Add(_enabledItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Quit", null, (_, _) => ExitThread()));

        _tray = new NotifyIcon { ContextMenuStrip = menu, Visible = true };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) _form.Toggle(); };

        _hotkey.Pressed += () => _form.Toggle();
        if (!_hotkey.TryRegister(_settings.Hotkey))
            Log.Write($"hotkey {GlobalHotkey.Describe(_settings.Hotkey)} is unavailable");

        _saveDebounce.Tick += (_, _) => { _saveDebounce.Stop(); _settings.Save(); };

        SyncStartup(_settings.StartWithWindows);
        _engine.Start();
        UpdateTray();

        if (!quietStart)
            _tray.ShowBalloonTip(4000, "Flash Guard is running",
                $"Watching all displays for flashing. Press {GlobalHotkey.Describe(_hotkey.Current)} for settings.", ToolTipIcon.Info);
    }

    public void ShowSettings() => _ui.BeginInvoke(() => _form.Toggle());

    void Apply(GuardSettings next)
    {
        var prev = _settings;
        _settings = next;
        _engine.Settings = next;
        if (next.StartWithWindows != prev.StartWithWindows)
            SyncStartup(next.StartWithWindows);
        if (next.Enabled != prev.Enabled)
            _form.RefreshFromSettings();
        UpdateTray();
        _saveDebounce.Stop();
        _saveDebounce.Start();
    }

    bool TryRegisterHotkey(Keys keys) => _hotkey.TryRegister(keys);

    void UpdateTray()
    {
        bool engaged = _engine.AnyEngaged;
        _tray.Icon = !_settings.Enabled ? Icons.Off : engaged ? Icons.Active : Icons.Idle;
        _tray.Text = !_settings.Enabled ? "Flash Guard — off" : engaged ? "Flash Guard — suppressing flashes" : "Flash Guard — watching";
        _enabledItem.Checked = _settings.Enabled;
        _settingsItem.ShortcutKeyDisplayString = GlobalHotkey.Describe(_hotkey.Current);
    }

    static void SyncStartup(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
                key.SetValue(RunValue, $"\"{Environment.ProcessPath}\" --background");
            else if (key.GetValue(RunValue) != null)
                key.DeleteValue(RunValue);
        }
        catch (Exception e)
        {
            Log.Write($"start-with-Windows update failed: {e.Message}");
        }
    }

    protected override void ExitThreadCore()
    {
        _saveDebounce.Stop();
        _settings.Save();
        _tray.Visible = false;
        _engine.Dispose();
        _hotkey.Dispose();
        _form.Dispose();
        _tray.Dispose();
        _ui.Dispose();
        base.ExitThreadCore();
    }
}
