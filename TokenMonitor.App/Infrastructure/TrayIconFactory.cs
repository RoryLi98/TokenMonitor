using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace TokenMonitor.App.Infrastructure;

internal static class TrayIconFactory
{
    public static Icon Create(double? codexRemaining, double? claudeRemaining)
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);

        using var background = new SolidBrush(Color.FromArgb(255, 28, 31, 38));
        using var border = new Pen(Color.FromArgb(255, 79, 87, 101), 1.5f);
        graphics.FillRoundedRectangle(background, 1, 1, 30, 30, 7);
        graphics.DrawRoundedRectangle(border, 1, 1, 30, 30, 7);

        DrawMeter(graphics, 5, codexRemaining, Color.FromArgb(255, 83, 143, 255));
        DrawMeter(graphics, 18, claudeRemaining, Color.FromArgb(255, 225, 125, 77));

        var handle = bitmap.GetHicon();
        try
        {
            using var icon = Icon.FromHandle(handle);
            return (Icon)icon.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static void DrawMeter(Graphics graphics, int x, double? remaining, Color color)
    {
        const int height = 22;
        using var track = new SolidBrush(Color.FromArgb(255, 57, 62, 73));
        using var fill = new SolidBrush(color);
        graphics.FillRectangle(track, x, 5, 9, height);

        if (remaining is null)
        {
            return;
        }

        var fillHeight = Math.Max(2, (int)Math.Round(height * Math.Clamp(remaining.Value, 0, 100) / 100d));
        graphics.FillRectangle(fill, x, 5 + height - fillHeight, 9, fillHeight);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    private static void FillRoundedRectangle(this Graphics graphics, Brush brush, float x, float y, float width, float height, float radius)
    {
        using var path = CreateRoundedPath(x, y, width, height, radius);
        graphics.FillPath(brush, path);
    }

    private static void DrawRoundedRectangle(this Graphics graphics, Pen pen, float x, float y, float width, float height, float radius)
    {
        using var path = CreateRoundedPath(x, y, width, height, radius);
        graphics.DrawPath(pen, path);
    }

    private static GraphicsPath CreateRoundedPath(float x, float y, float width, float height, float radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(x, y, diameter, diameter, 180, 90);
        path.AddArc(x + width - diameter, y, diameter, diameter, 270, 90);
        path.AddArc(x + width - diameter, y + height - diameter, diameter, diameter, 0, 90);
        path.AddArc(x, y + height - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
