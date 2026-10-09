using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

// A blocking frame deadline, rather than a Thread.Yield busy-wait tail.
internal sealed class FrameWaiter : IDisposable
{
    private readonly nint _timer;

    internal FrameWaiter()
    {
        _timer = CreateWaitableTimerExW(0, null, 2, 0x001F0003);
        if (_timer == 0) _timer = CreateWaitableTimerExW(0, null, 0, 0x001F0003);
        if (_timer == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    internal void WaitUntil(long deadline)
    {
        long remaining = deadline - Stopwatch.GetTimestamp();
        if (remaining <= 0) return;
        long due = -Math.Max(1, (long)(remaining * (10_000_000d / Stopwatch.Frequency)));
        if (!SetWaitableTimer(_timer, ref due, 0, 0, 0, false))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (WaitForSingleObject(_timer, uint.MaxValue) != 0)
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public void Dispose() => CloseHandle(_timer);

#if TIPAURA_AGENT_SELF_TEST
    internal static void Smoke()
    {
        using var waiter = new FrameWaiter();
        using var process = Process.GetCurrentProcess();
        foreach (int fps in new[] { 30, 60, 120 }) Measure(fps);

        void Measure(int fps)
        {
            long period = Stopwatch.Frequency / fps;
            long start = Stopwatch.GetTimestamp();
            TimeSpan cpuStart = process.TotalProcessorTime;
            int frames = fps * 2;
            for (int frame = 1; frame <= frames; frame++)
                waiter.WaitUntil(start + frame * period);
            double elapsed = Stopwatch.GetElapsedTime(start).TotalSeconds;
            double cpu = (process.TotalProcessorTime - cpuStart).TotalSeconds;
            Console.WriteLine($"Blocking timer: target={fps} FPS actual={frames / elapsed:F1} FPS CPU={cpu / elapsed * 100:F2}% of one core");
            if (Math.Abs(frames / elapsed - fps) > fps * 0.1)
                throw new InvalidOperationException("Blocking frame timer missed its target rate.");
        }
    }
#endif

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWaitableTimerExW(nint attributes, string? name, uint flags, uint access);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(nint timer, ref long due, int period,
        nint callback, nint argument, [MarshalAs(UnmanagedType.Bool)] bool resume);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(nint handle, uint milliseconds);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
