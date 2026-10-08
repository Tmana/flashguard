using System.Drawing.Drawing2D;

namespace FlashGuard.UI;

/// <summary>Tray icons drawn at runtime: a lightning bolt in a disc, tinted by state.</summary>
static class Icons
{
    public static readonly Icon Idle = Make(Color.FromArgb(38, 166, 154));
    public static readonly Icon Active = Make(Color.FromArgb(255, 167, 38));
    public static readonly Icon Off = Make(Color.FromArgb(120, 120, 120));

    static Icon Make(Color disc)
    {
        const int s = 32;
        using var bmp = new Bitmap(s, s);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using (var b = new SolidBrush(disc))
                g.FillEllipse(b, 1, 1, s - 2, s - 2);
            PointF[] bolt = [new(18, 4), new(9, 18), new(15, 18), new(13, 28), new(23, 13), new(17, 13), new(20, 4)];
            using (var w = new SolidBrush(Color.White))
                g.FillPolygon(w, bolt);
        }
        IntPtr h = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(h).Clone();
        Native.DestroyIcon(h);
        return icon;
    }
}
