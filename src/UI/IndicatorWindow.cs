using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace FlashGuard.UI;

/// <summary>Small per-pixel-alpha label shown in a corner of a monitor while suppression is engaged.</summary>
sealed class IndicatorWindow : NativeWindow, IDisposable
{
    const string Text = "flash suppression on";

    readonly IntPtr _monitor;
    readonly Native.RECT _monitorBounds;
    ScreenCorner _corner = (ScreenCorner)(-1);

    public IndicatorWindow(IntPtr monitor, Native.RECT monitorBounds)
    {
        _monitor = monitor;
        _monitorBounds = monitorBounds;
        CreateHandle(new CreateParams
        {
            Caption = "FlashGuard indicator",
            Style = Native.WS_POPUP,
            ExStyle = Native.WS_EX_TOPMOST | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE |
                      Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT,
        });
        Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE);
    }

    /// <summary>UI thread only.</summary>
    public void Show(ScreenCorner corner)
    {
        if (corner != _corner)
        {
            Render(corner);
            _corner = corner;
        }
        // Re-assert topmost so the label sits above the overlay that was just shown.
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
    }

    public void Hide() => Native.ShowWindowAsync(Handle, Native.SW_HIDE);

    void Render(ScreenCorner corner)
    {
        float scale = Native.MonitorScale(_monitor);
        using var font = new Font("Segoe UI Semibold", 9f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        int padX = (int)(8 * scale), padY = (int)(3 * scale), margin = (int)(10 * scale);
        int dot = (int)(6 * scale), gap = (int)(6 * scale);

        Size textSize;
        using (var probe = new Bitmap(1, 1))
        using (var g = Graphics.FromImage(probe))
            textSize = Size.Ceiling(g.MeasureString(Text, font, PointF.Empty, StringFormat.GenericTypographic));

        int w = padX * 2 + dot + gap + textSize.Width;
        int h = padY * 2 + textSize.Height;

        using var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            using (var path = RoundedRect(new RectangleF(0, 0, w - 1, h - 1), h / 2f))
            using (var bg = new SolidBrush(Color.FromArgb(170, 16, 16, 20)))
                g.FillPath(bg, path);
            using (var amber = new SolidBrush(Color.FromArgb(255, 255, 183, 77)))
                g.FillEllipse(amber, padX, (h - dot) / 2f, dot, dot);
            using var fg = new SolidBrush(Color.FromArgb(225, 255, 255, 255));
            g.DrawString(Text, font, fg, padX + dot + gap, padY, StringFormat.GenericTypographic);
        }

        var work = Native.WorkArea(_monitor, _monitorBounds);
        int x = corner is ScreenCorner.TopLeft or ScreenCorner.BottomLeft ? work.Left + margin : work.Right - margin - w;
        int y = corner is ScreenCorner.TopLeft or ScreenCorner.TopRight ? work.Top + margin : work.Bottom - margin - h;

        IntPtr screen = Native.GetDC(IntPtr.Zero);
        IntPtr mem = Native.CreateCompatibleDC(screen);
        IntPtr hbmp = bmp.GetHbitmap(Color.FromArgb(0));
        IntPtr old = Native.SelectObject(mem, hbmp);
        try
        {
            var dst = new Native.POINT { X = x, Y = y };
            var src = new Native.POINT();
            var size = new Native.SIZE { Cx = w, Cy = h };
            var blend = new Native.BLENDFUNCTION { BlendOp = Native.AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = Native.AC_SRC_ALPHA };
            Native.UpdateLayeredWindow(Handle, screen, ref dst, ref size, mem, ref src, 0, ref blend, Native.ULW_ALPHA);
        }
        finally
        {
            Native.SelectObject(mem, old);
            Native.DeleteObject(hbmp);
            Native.DeleteDC(mem);
            Native.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        float d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 90, 180);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 180);
        p.CloseFigure();
        return p;
    }

    public void Dispose() => DestroyHandle();
}
