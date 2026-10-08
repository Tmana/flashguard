using FlashGuard.Capture;

namespace FlashGuard.UI;

/// <summary>Settings and live status. Every change applies immediately.</summary>
sealed class SettingsForm : Form
{
    readonly Func<GuardSettings> _get;
    readonly Action<GuardSettings> _apply;
    readonly Func<Keys, bool> _setHotkey;
    readonly Func<IReadOnlyList<MonitorStatus>> _status;
    readonly Action _quit;

    readonly TableLayoutPanel _grid;
    readonly Label _statusLabel;
    readonly System.Windows.Forms.Timer _statusTimer = new() { Interval = 300 };
    readonly List<Action> _refreshers = [];
    TextBox _hotkeyBox = null!;
    Label _hotkeyNote = null!;
    bool _loading;

    public SettingsForm(Func<GuardSettings> get, Action<GuardSettings> apply, Func<Keys, bool> setHotkey,
        Func<IReadOnlyList<MonitorStatus>> status, Action quit)
    {
        _get = get;
        _apply = apply;
        _setHotkey = setHotkey;
        _status = status;
        _quit = quit;

        Text = "Flash Guard";
        Icon = Icons.Idle;
        Font = SystemFonts.MessageBoxFont;
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        TopMost = true;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(14, 10, 14, 12);

        _grid = new TableLayoutPanel
        {
            ColumnCount = 3,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
        };
        _grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230));
        _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        Controls.Add(_grid);

        Section("Protection");
        Check("Protection enabled", s => s.Enabled, (s, v) => s with { Enabled = v });
        Radio("Smooth — let brightness change only slowly (strongest)", MitigationMode.Smooth);
        Radio("Dim — darken the screen while flashing (lightest)", MitigationMode.Dim);
        Check("Always on (don't wait to detect flashing)", s => s.AlwaysOn, (s, v) => s with { AlwaysOn = v });

        Section("Detection");
        Slider("Trigger at", 1.0, 3.0, 0.5, s => s.TriggerFlashesPerSecond, (s, v) => s with { TriggerFlashesPerSecond = v },
            v => $"{v:0.#} flashes/s");
        Slider("Brightness change", 0.03, 0.30, 0.01, s => s.LuminanceThreshold, (s, v) => s with { LuminanceThreshold = v },
            v => $"{v:P0}");
        Slider("Flashing area", 0.005, 0.25, 0.005, s => s.MinArea, (s, v) => s with { MinArea = v },
            v => $"{v * 100:0.#}% of screen");
        Check("Detect saturated red flashes", s => s.DetectRedFlashes, (s, v) => s with { DetectRedFlashes = v });
        Slider("Stay on for", 0.5, 15, 0.5, s => s.HoldSeconds, (s, v) => s with { HoldSeconds = v },
            v => $"{v:0.#} s after last");
        Hint("Lower values = more sensitive. Defaults follow the WCAG / Harding flash thresholds.");

        Section("Suppression");
        Slider("Smooth: max change", 0.1, 4.0, 0.1, s => s.MaxChangePerSecond, (s, v) => s with { MaxChangePerSecond = v },
            v => $"{v:0.0} × range/s");
        Hint("At 0.6 or below, nothing flashing 3+ times per second can exceed a 10% swing.");
        Slider("Smooth: extra dim", 0, 0.9, 0.05, s => s.SmoothDim, (s, v) => s with { SmoothDim = v }, v => $"{v:P0}");
        Slider("Dim: darkness", 0.1, 0.95, 0.05, s => s.DimOpacity, (s, v) => s with { DimOpacity = v }, v => $"{v:P0}");

        Section("Indicator & app");
        Check("Show corner indicator while active", s => s.ShowIndicator, (s, v) => s with { ShowIndicator = v });
        CornerPicker();
        HotkeyPicker();
        Check("Start with Windows", s => s.StartWithWindows, (s, v) => s with { StartWithWindows = v });

        Section("Status");
        _statusLabel = new Label { AutoSize = true, Margin = new Padding(3, 2, 3, 6), Font = new Font(FontFamily.GenericMonospace, Font.Size) };
        Span(_statusLabel);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 0) };
        var close = new Button { Text = "Close", AutoSize = true };
        close.Click += (_, _) => Hide();
        var quitButton = new Button { Text = "Quit Flash Guard", AutoSize = true };
        quitButton.Click += (_, _) => _quit();
        buttons.Controls.Add(close);
        buttons.Controls.Add(quitButton);
        Span(buttons);
        CancelButton = close;

        _statusTimer.Tick += (_, _) => RefreshStatus();
        RefreshFromSettings();
    }

    /// <summary>Shows the form centred on the monitor under the cursor, or hides it if it has focus.</summary>
    public void Toggle()
    {
        if (Visible && ContainsFocus)
        {
            Hide();
            return;
        }
        if (!Visible)
        {
            RefreshFromSettings();
            var area = Screen.FromPoint(Cursor.Position).WorkingArea;
            Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);
            Show();
        }
        Activate();
        BringToFront();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        _statusTimer.Enabled = Visible;
        if (Visible) RefreshStatus();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnFormClosing(e);
    }

    public void RefreshFromSettings()
    {
        _loading = true;
        foreach (var r in _refreshers) r();
        _loading = false;
    }

    void Change(Func<GuardSettings, GuardSettings> change)
    {
        if (_loading) return;
        _apply(change(_get()).Sanitized());
    }

    void RefreshStatus()
    {
        var lines = _status().Select(m =>
        {
            string state = m.State switch
            {
                GuardState.Suppressing => "SUPPRESSING",
                GuardState.Releasing => "releasing",
                GuardState.Watching => "watching",
                GuardState.Starting => "starting",
                _ => "error: " + m.Error,
            };
            string extra = m.SmoothAvailable ? "" : " (rotated: dim only)";
            return $"{m.Name,-9} {m.Width}×{m.Height,-5} {state,-11} {m.TransitionsInWindow / 2.0,3:0.#} flashes/s{extra}";
        }).ToList();
        _statusLabel.Text = lines.Count > 0 ? string.Join(Environment.NewLine, lines)
            : _get().Enabled ? "No displays found." : "Protection is off.";
    }

    // ---- layout helpers -------------------------------------------------------------------

    void Span(Control c)
    {
        _grid.Controls.Add(c);
        _grid.SetColumnSpan(c, 3);
    }

    void Section(string title)
    {
        Span(new Label
        {
            Text = title,
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Margin = new Padding(0, _grid.Controls.Count == 0 ? 0 : 12, 0, 2),
        });
    }

    void Hint(string text)
    {
        Span(new Label { Text = text, AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 0, 3, 4), MaximumSize = new Size(560, 0) });
    }

    void Check(string text, Func<GuardSettings, bool> get, Func<GuardSettings, bool, GuardSettings> set)
    {
        var cb = new CheckBox { Text = text, AutoSize = true, Margin = new Padding(3, 3, 3, 1) };
        cb.CheckedChanged += (_, _) => Change(s => set(s, cb.Checked));
        _refreshers.Add(() => cb.Checked = get(_get()));
        Span(cb);
    }

    void Radio(string text, MitigationMode mode)
    {
        var rb = new RadioButton { Text = text, AutoSize = true, Margin = new Padding(3, 3, 3, 1) };
        rb.CheckedChanged += (_, _) => { if (rb.Checked) Change(s => s with { Mode = mode }); };
        _refreshers.Add(() => rb.Checked = _get().Mode == mode);
        Span(rb);
    }

    void Slider(string label, double min, double max, double step, Func<GuardSettings, double> get,
        Func<GuardSettings, double, GuardSettings> set, Func<double, string> format)
    {
        int ticks = (int)Math.Round((max - min) / step);
        var bar = new TrackBar
        {
            Minimum = 0, Maximum = ticks, TickStyle = TickStyle.None, AutoSize = false,
            Height = 28, Dock = DockStyle.Fill, Margin = new Padding(3, 0, 3, 0),
            SmallChange = 1, LargeChange = Math.Max(1, ticks / 10),
        };
        var value = new Label { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 0) };
        double ToValue(int t) => Math.Round(min + t * step, 4);
        bar.ValueChanged += (_, _) =>
        {
            value.Text = format(ToValue(bar.Value));
            Change(s => set(s, ToValue(bar.Value)));
        };
        _refreshers.Add(() =>
        {
            var v = get(_get());
            bar.Value = Math.Clamp((int)Math.Round((v - min) / step), 0, ticks);
            value.Text = format(v);
        });
        _grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 8, 0) });
        _grid.Controls.Add(bar);
        _grid.Controls.Add(value);
    }

    void CornerPicker()
    {
        var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140, Margin = new Padding(3, 3, 3, 3) };
        combo.Items.AddRange(["Top left", "Top right", "Bottom left", "Bottom right"]);
        combo.SelectedIndexChanged += (_, _) => Change(s => s with { IndicatorCorner = (ScreenCorner)combo.SelectedIndex });
        _refreshers.Add(() => combo.SelectedIndex = (int)_get().IndicatorCorner);
        _grid.Controls.Add(new Label { Text = "Indicator corner", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 8, 0) });
        _grid.Controls.Add(combo);
        _grid.Controls.Add(new Label());
    }

    void HotkeyPicker()
    {
        _hotkeyBox = new TextBox { ReadOnly = true, Width = 180, Margin = new Padding(3, 3, 3, 3), Cursor = Cursors.Hand };
        _hotkeyNote = new Label { AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 6, 3, 0), Text = "click, then press keys" };
        _hotkeyBox.KeyDown += (_, e) =>
        {
            e.SuppressKeyPress = true;
            e.Handled = true;
            var keys = e.KeyData;
            if (!GlobalHotkey.IsValid(keys))
            {
                _hotkeyBox.Text = GlobalHotkey.Describe(keys) + " …";
                return;
            }
            if (_setHotkey(keys))
            {
                _hotkeyNote.Text = "saved";
                Change(s => s with { Hotkey = keys });
            }
            else
                _hotkeyNote.Text = "in use by another app";
            _hotkeyBox.Text = GlobalHotkey.Describe(_get().Hotkey);
        };
        _hotkeyBox.Leave += (_, _) => _hotkeyBox.Text = GlobalHotkey.Describe(_get().Hotkey);
        _refreshers.Add(() => _hotkeyBox.Text = GlobalHotkey.Describe(_get().Hotkey));
        _grid.Controls.Add(new Label { Text = "Menu shortcut", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 8, 0) });
        _grid.Controls.Add(_hotkeyBox);
        _grid.Controls.Add(_hotkeyNote);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _statusTimer.Dispose();
        base.Dispose(disposing);
    }
}
