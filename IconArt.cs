using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace YaDiskFileSync;

/// <summary>Значок: круг цвета состояния и белая стрелка «в облако».</summary>
internal static class IconArt
{
    public static readonly Color Green = Color.FromArgb(46, 170, 80);
    public static readonly Color Blue = Color.FromArgb(40, 120, 220);
    public static readonly Color Orange = Color.FromArgb(240, 150, 20);
    public static readonly Color Red = Color.FromArgb(220, 60, 55);
    public static readonly Color Gray = Color.FromArgb(130, 130, 130);

    public static Color For(SyncState s) => s switch
    {
        SyncState.Synced => Green,
        SyncState.Pending or SyncState.Uploading => Blue,
        SyncState.Retrying => Orange,
        SyncState.Error or SyncState.NeedLogin => Red,
        _ => Gray,
    };

    /// <summary>Рисует с 4-кратным запасом и уменьшает — иначе стрелка в 16 px рассыпается.</summary>
    public static Bitmap Render(int size, Color color)
    {
        const int ss = 4;
        int big = size * ss;
        using var hi = new Bitmap(big, big);
        using (var g = Graphics.FromImage(hi))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float pad = big * 0.03f, d = big - 2 * pad;
            using (var fill = new SolidBrush(color))
                g.FillEllipse(fill, pad, pad, d, d);

            float u = big / 24f;
            PointF P(float x, float y) => new(x * u, y * u);
            using var pen = new Pen(Color.White, Math.Max(ss * 1.4f, big * (size <= 20 ? 0.12f : 0.09f)))
            {
                LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round,
            };
            g.DrawLine(pen, P(12, 18), P(12, 6.5f));                        // древко
            g.DrawLines(pen, new[] { P(7, 11), P(12, 6), P(17, 11) });        // наконечник
        }

        var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(hi, 0, 0, size, size);
        }
        return bmp;
    }

    public static Icon MakeIcon(int size, Color color)
    {
        using var bmp = Render(size, color);
        IntPtr h = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(h).Clone();
        DestroyIcon(h);
        return icon;
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
