#if TIPAURA_AGENT_SELF_TEST
using System.Runtime.InteropServices;
using Vortice.Direct3D11;

// Discord capture mode: the main window's HWND swap chain must carry the timeline frames and be the only
// swap chain that presents (checked by patching the DXGI Present vtable slots, as a hook would see it),
// while the screen (read back from the desktop, with the main window made topmost and placed beside the
// timeline) keeps showing the main UI. PrintWindow cannot tell the two apart. Discord itself is not
// involved; whether its hook picks up these frames is checked by hand.
internal static class CaptureSmoke
{
    internal static void Run()
    {
        ShaderTool.Verify();
        Native.SetProcessDpiAwarenessContext(-4);
        string folder = Path.Combine(Path.GetTempPath(), "tipaura-capture-smoke");
        using var log = AppLog.Start(folder);
        var settings = new AppSettings
        {
            Volume = 0, EncounterPath = SelfTest.ProjectPath("data/example.yaml"),
            Timeline = OverlayPlacement.DefaultTimeline with { Anchor = 0, X = 840, Y = 0 }
        };
        using var window = new MainWindow(new AppWindowOptions("TipAura capture smoke", new Vector2i(820, 760)), settings, null,
            Path.Combine(folder, "tipaura.settings.json"));
        var app = window.WindowForSmoke;
        app.Location = new Vector2i(0, 0);
        Native.SetWindowPos(app.Hwnd, -1, 0, 0, 0, 0, Native.SwpNoMove | Native.SwpNoSize | Native.SwpNoActivate);
        try { Check(window, app); }
        finally { Native.SetWindowPos(app.Hwnd, -2, 0, 0, 0, 0, Native.SwpNoMove | Native.SwpNoSize | Native.SwpNoActivate); }
    }

    private static void Check(MainWindow window, AppWindow app)
    {
        window.StartForSmoke(1);
        window.StartForSmoke(5);
        window.RenderFramesForSmoke(10);
        var before = Snapshot(app);

        var frames = new List<(int Width, int Height, int Bright)>();
        app.CaptureProbeForSmoke = texture => frames.Add(Inspect(texture));
        Toggle(window, true);
        window.RenderFramesForSmoke(10);
        var timeline = window.SettingsForSmoke.Timeline;
        SelfTest.Check(app.Composited && window.SettingsForSmoke.DiscordCapture, $"Capture mode did not start: {window.NoticeForSmoke}");
        SelfTest.Check(frames.Count >= 9, $"Expected a capture frame per rendered frame, got {frames.Count}.");
        var last = frames[^1];
        SelfTest.Check(last.Width == timeline.Width && last.Height == timeline.Height,
            $"Capture frame is {last.Width}x{last.Height}, timeline is {timeline.Width}x{timeline.Height}.");
        SelfTest.Check(last.Bright > 100, $"Capture frame has only {last.Bright} bright pixels; the timeline bars are missing.");
        var during = Snapshot(app);
        SelfTest.Check(Similar(before, during) > 0.95, $"The visible main window changed in capture mode ({Similar(before, during):P1} equal pixels).");

        // A hidden timeline sends black frames at the saved timeline size.
        window.SettingsForSmoke = window.SettingsForSmoke with { Timeline = timeline with { Visible = false } };
        frames.Clear();
        window.RenderFramesForSmoke(3);
        SelfTest.Check(frames.Count > 0 && frames[^1] is { Bright: 0 } blank && blank.Width == timeline.Width,
            "A hidden timeline should send black frames.");
        window.SettingsForSmoke = window.SettingsForSmoke with { Timeline = timeline };

        // Minimized: the main UI is not drawn, the capture feed keeps presenting.
        app.WindowState = Silk.NET.Windowing.WindowState.Minimized;
        frames.Clear();
        window.RenderFramesForSmoke(5);
        SelfTest.Check(frames.Count >= 4 && app.Composited, "Capture should keep presenting while minimized.");
        app.WindowState = Silk.NET.Windowing.WindowState.Normal;
        window.RenderFramesForSmoke(5);

        for (int i = 0; i < 3; i++)
        {
            Toggle(window, false);
            window.RenderFramesForSmoke(3);
            SelfTest.Check(!app.Composited, "Capture mode did not stop.");
            Toggle(window, true);
            window.RenderFramesForSmoke(3);
            SelfTest.Check(app.Composited, $"Capture mode did not restart: {window.NoticeForSmoke}");
        }
        Toggle(window, false);
        window.RenderFramesForSmoke(5);
        var after = Snapshot(app);
        SelfTest.Check(Similar(before, after) > 0.95, $"The main window did not return to normal ({Similar(before, after):P1} equal pixels).");

        // Discord's hook keeps the first swap chain that presents, so in capture mode the timeline feed
        // must be the only Present in the process. About overlaps the main window, so it opens only now.
        var main = app.SwapChainForSmoke!;
        using (var monitor = new PresentMonitor(main.NativePointer))
        {
            window.OpenAboutForSmoke();
            window.RenderFramesForSmoke(5);
            SelfTest.Check(monitor.Callers.Contains(main.NativePointer) && monitor.Callers.Count >= 2,
                $"Without capture, the main window and About should both present ({monitor.Callers.Count} swap chains seen).");
            Toggle(window, true);
            window.RenderFramesForSmoke(2);
            monitor.Callers.Clear();
            window.RenderFramesForSmoke(10);
            SelfTest.Check(monitor.Callers.Count == 1 && monitor.Callers.Contains(main.NativePointer),
                $"In capture mode only the main window's HWND swap chain may present, {monitor.Callers.Count} did.");
            var description = main.Description1;
            SelfTest.Check(description.Width == timeline.Width && description.Height == timeline.Height && main.GetHwnd() == app.Hwnd,
                $"The presenting swap chain is {description.Width}x{description.Height} on {main.GetHwnd():X}, expected the timeline size on the main window.");
            Toggle(window, false);
            window.RenderFramesForSmoke(3);
            SelfTest.Check(monitor.Callers.Count >= 1 && main.Description1.Width == app.ClientSize.X,
                "After capture, the main window swap chain should present the UI at window size again.");
        }
        Console.WriteLine($"Capture smoke passed (timeline {last.Width}x{last.Height}, {last.Bright} bright pixels; " +
            $"main window {Similar(before, during):P1} unchanged in capture mode; only the capture swap chain presents).");
    }

    // Replaces IDXGISwapChain::Present (slot 8) and IDXGISwapChain1::Present1 (slot 22) in the DXGI swap
    // chain vtable, which every swap chain of the process shares, to record which swap chains present.
    private sealed unsafe class PresentMonitor : IDisposable
    {
        private const int PresentSlot = 8, Present1Slot = 22;
        private static PresentMonitor? s_current;
        private readonly nint* _vtable;
        private readonly nint _present, _present1;

        internal PresentMonitor(nint swapChain)
        {
            _vtable = *(nint**)swapChain;
            _present = _vtable[PresentSlot];
            _present1 = _vtable[Present1Slot];
            s_current = this;
            Write(PresentSlot, (nint)(delegate* unmanaged<nint, uint, uint, int>)&Present);
            Write(Present1Slot, (nint)(delegate* unmanaged<nint, uint, uint, nint, int>)&Present1);
        }

        internal HashSet<nint> Callers { get; } = [];

        [UnmanagedCallersOnly]
        private static int Present(nint self, uint sync, uint flags)
        {
            s_current!.Callers.Add(self);
            return ((delegate* unmanaged<nint, uint, uint, int>)s_current._present)(self, sync, flags);
        }

        [UnmanagedCallersOnly]
        private static int Present1(nint self, uint sync, uint flags, nint parameters)
        {
            s_current!.Callers.Add(self);
            return ((delegate* unmanaged<nint, uint, uint, nint, int>)s_current._present1)(self, sync, flags, parameters);
        }

        private void Write(int slot, nint value)
        {
            SelfTest.Check(VirtualProtect((nint)(_vtable + slot), (nuint)sizeof(nint), 0x04, out uint previous), "VirtualProtect failed.");
            _vtable[slot] = value;
            VirtualProtect((nint)(_vtable + slot), (nuint)sizeof(nint), previous, out _);
        }

        public void Dispose()
        {
            Write(PresentSlot, _present);
            Write(Present1Slot, _present1);
            s_current = null;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool VirtualProtect(nint address, nuint size, uint protect, out uint previous);
    }

    private static void Toggle(MainWindow window, bool on) =>
        window.SettingsForSmoke = window.SettingsForSmoke with { DiscordCapture = on };

    private static unsafe (int Width, int Height, int Bright) Inspect(ID3D11Texture2D texture)
    {
        var description = texture.Description;
        using var staging = Gpu.Device.CreateTexture2D(new Texture2DDescription(description.Format,
            description.Width, description.Height, arraySize: 1, mipLevels: 1, bindFlags: BindFlags.None,
            usage: ResourceUsage.Staging, cpuAccessFlags: CpuAccessFlags.Read));
        Gpu.Context.CopyResource(staging, texture);
        var mapped = Gpu.Context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        int bright = 0;
        try
        {
            for (int y = 0; y < description.Height; y++)
            {
                byte* row = (byte*)mapped.DataPointer + y * (long)mapped.RowPitch;
                for (int x = 0; x < description.Width; x++)
                    if (row[x * 4] > 40 || row[x * 4 + 1] > 40 || row[x * 4 + 2] > 40) bright++;
            }
        }
        finally { Gpu.Context.Unmap(staging, 0); }
        return ((int)description.Width, (int)description.Height, bright);
    }

    // The window's area of the desktop as composed by DWM.
    private static int[] Snapshot(AppWindow window)
    {
        var size = window.ClientSize;
        var location = window.Location;
        var info = new Native.BitmapInfo
        {
            Size = (uint)Marshal.SizeOf<Native.BitmapInfo>(), Width = size.X, Height = -size.Y, Planes = 1, BitCount = 32
        };
        nint screen = Native.GetDC(0);
        nint dc = Native.CreateCompatibleDC(screen);
        nint bitmap = Native.CreateDIBSection(screen, ref info, 0, out nint bits, 0, 0);
        nint previous = Native.SelectObject(dc, bitmap);
        try
        {
            SelfTest.Check(BitBlt(dc, 0, 0, size.X, size.Y, screen, location.X, location.Y, 0x40CC0020), // SRCCOPY | CAPTUREBLT
                "Could not read the screen.");
            var pixels = new int[size.X * size.Y];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            SelfTest.Check(pixels.Distinct().Take(3).Count() > 2, "The screen capture is blank.");
            return pixels;
        }
        finally
        {
            Native.SelectObject(dc, previous);
            Native.DeleteObject(bitmap);
            Native.DeleteDC(dc);
            Native.ReleaseDC(0, screen);
        }
    }

    // Share of pixels whose channels all differ by at most 8.
    private static double Similar(int[] a, int[] b)
    {
        if (a.Length != b.Length) return 0;
        int equal = 0;
        for (int i = 0; i < a.Length; i++)
        {
            int x = a[i], y = b[i];
            if (Math.Abs((x & 0xFF) - (y & 0xFF)) <= 8 && Math.Abs((x >> 8 & 0xFF) - (y >> 8 & 0xFF)) <= 8 &&
                Math.Abs((x >> 16 & 0xFF) - (y >> 16 & 0xFF)) <= 8) equal++;
        }
        return (double)equal / a.Length;
    }

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(nint destination, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint rop);
}
#endif
