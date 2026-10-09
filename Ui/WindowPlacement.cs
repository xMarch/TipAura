using System.Runtime.InteropServices;

internal readonly record struct RestoredWindow(Vector2i Location, Vector2i ClientSize);

internal static class WindowPlacement
{
    internal static RestoredWindow? Restore(AppSettings settings, IReadOnlyList<PixelRect> workAreas)
    {
        if (settings.WindowX is not int x || settings.WindowY is not int y ||
            settings.WindowWidth is not int width || settings.WindowHeight is not int height ||
            width is < 640 or > 10_000 || height is < 480 or > 10_000 || workAreas.Count == 0)
            return null;

        // Prefer the display with the greatest overlap; when it was disconnected, use the primary display.
        int best = 0;
        long overlap = 0;
        for (int i = 0; i < workAreas.Count; i++)
        {
            var area = workAreas[i];
            long left = Math.Max((long)x, area.Left);
            long top = Math.Max((long)y, area.Top);
            long covered = Math.Max(0, Math.Min((long)x + width, area.Right) - left)
                * Math.Max(0, Math.Min((long)y + height, area.Bottom) - top);
            if (covered > overlap) { overlap = covered; best = i; }
        }
        var work = workAreas[best];
        if (work.IsEmpty) return null;
        width = Math.Min(width, work.Width);
        height = Math.Min(height, work.Height);
        x = overlap == 0 ? work.Left + (work.Width - width) / 2
            : (int)Math.Clamp((long)x, work.Left, (long)work.Right - width);
        y = overlap == 0 ? work.Top + (work.Height - height) / 2
            : (int)Math.Clamp((long)y, work.Top, (long)work.Bottom - height);
        return new RestoredWindow(new Vector2i(x, y), new Vector2i(width, height));
    }

    internal static IReadOnlyList<PixelRect> CurrentWorkAreas()
    {
        var displays = new List<PixelRect>();
        Native.EnumDisplayMonitors(0, 0, (nint monitor, nint hdc, ref Native.Rect bounds, nint data) =>
        {
            var info = new Native.MonitorInfo { Size = (uint)Marshal.SizeOf<Native.MonitorInfo>() };
            if (Native.GetMonitorInfo(monitor, ref info) && !info.WorkArea.ToPixelRect().IsEmpty)
            {
                if ((info.Flags & 1) != 0) displays.Insert(0, info.WorkArea.ToPixelRect());
                else displays.Add(info.WorkArea.ToPixelRect());
            }
            return true;
        }, 0);
        return displays;
    }
}
