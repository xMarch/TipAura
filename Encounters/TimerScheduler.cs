using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

internal readonly record struct AlertEvent(TimerDefinition Timer, Encounter Encounter, long At);

// Set while hotkeys require the hooked window in the foreground. Hook is the active window hook, or null
// when none is selected (then only TipAura's own windows let hotkeys through).
internal sealed record HotkeyGate(WindowHook? Hook)
{
    internal static bool Allows(uint foregroundPid, uint ownPid, bool hookMatches) =>
        foregroundPid != 0 && foregroundPid == ownPid || hookMatches;

    // Reads the foreground window when the hotkey is handled, so the check does not lag behind the UI frame rate.
    internal bool AllowsForeground()
    {
        nint foreground = Native.GetForegroundWindow();
        return Allows(WindowFinder.ProcessId(foreground), (uint)Environment.ProcessId,
            Hook is not null && WindowFinder.Matches(Hook, foreground));
    }
}

// Owns the TimerEngine on a dedicated thread. It sleeps on a high-resolution waitable timer until the
// next alert or expiry, or until a hotkey or UI command arrives, so alert timing does not depend on
// the UI frame rate or on the window being minimized.
internal sealed class TimerScheduler : IDisposable
{
    private readonly TimerEngine _engine = new(Stopwatch.Frequency);
    private readonly AudioEngine _audio;
    private readonly SoundCache _sounds;
    private readonly ConcurrentQueue<Action<long>> _commands = new();
    private readonly ConcurrentQueue<AlertEvent> _alerts = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly nint _timer;
    private readonly Thread _thread;
    private volatile bool _stopping;
    private volatile TimerView[] _snapshot = [];
    private GlobalKeybinds? _keybinds;

    internal TimerScheduler(AudioEngine audio, SoundCache sounds, bool installHook = true)
    {
        _audio = audio;
        _sounds = sounds;
        // CREATE_WAITABLE_TIMER_HIGH_RESOLUTION where available, as in FrameWaiter.
        _timer = CreateWaitableTimerExW(0, null, 2, 0x001F0003);
        if (_timer == 0) _timer = CreateWaitableTimerExW(0, null, 0, 0x001F0003);
        if (_timer == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        _thread = new Thread(Run) { IsBackground = true, Name = "Timer scheduler" };
        _thread.Start();
        if (installHook) _keybinds = new GlobalKeybinds(() => _wake.Set());
    }

    internal string? HotkeyError => _keybinds?.Error;
    // Set by the UI while the user types in one of its fields; hotkeys arriving meanwhile are dropped.
    internal volatile bool HotkeysPaused;
    // Set by the UI while hotkeys require the hooked window to be focused; null when that setting is off.
    internal volatile HotkeyGate? Gate;
    // Sorted by end time; replaced as a whole whenever timers start, stop or alert.
    internal TimerView[] Snapshot => _snapshot;
    internal bool TryTakeAlert(out AlertEvent alert) => _alerts.TryDequeue(out alert);

    internal void Load(Encounter? encounter) => Post(_ => _engine.Load(encounter));
    internal void Start(int slot) => Post(now => Fire(_engine.Start(slot, now), now));
    internal void ResetSlot(int slot) => Post(_ => _engine.ResetSlot(slot));
    internal void ResetAll() => Post(_ => _engine.ResetAll());
    internal void ResetInstance(long id) => Post(_ => _engine.ResetInstance(id));
    // Timer ids that hotkeys and Start skip for this session; running copies are removed.
    internal void SetDisabled(IReadOnlySet<string> ids) => Post(_ => _engine.SetDisabled(ids));

    internal void SetHotkeyBindings(HotkeyBindings bindings) => _keybinds?.SetBindings(bindings);
    internal void BeginKeyCapture() => _keybinds?.BeginCapture();
    internal void CancelKeyCapture() => _keybinds?.CancelCapture();
    internal bool TryTakeCapturedKey(out int code)
    {
        code = 0;
        return _keybinds?.TryTakeCaptured(out code) == true;
    }

    private void Post(Action<long> command)
    {
        _commands.Enqueue(command);
        _wake.Set();
    }

    private void Run()
    {
        var handles = new[] { _wake.SafeWaitHandle.DangerousGetHandle(), _timer };
        while (!_stopping)
        {
            try
            {
                long now = Stopwatch.GetTimestamp();
                while (_keybinds?.TryDequeue(out var hotkey) == true)
                {
                    if (HotkeysPaused) AppLog.Info("Scheduler", $"Hotkey {hotkey.Kind} {hotkey.Slot} ignored while typing in TipAura.");
                    else if (Gate is { } gate && !gate.AllowsForeground())
                        AppLog.Info("Scheduler", $"Hotkey {hotkey.Kind} {hotkey.Slot} ignored: the hooked window is not focused.");
                    else Handle(hotkey, now);
                }
                while (_commands.TryDequeue(out var command)) command(now);
                Fire(_engine.Advance(now), now);
                _snapshot = _engine.Snapshot();
            }
            catch (Exception ex) { AppLog.Error("Scheduler", "Timer update failed", ex); }

            uint timeout = uint.MaxValue;
            if (_engine.NextDeadline() is { } deadline)
            {
                long remaining = deadline - Stopwatch.GetTimestamp();
                if (remaining <= 0) continue;
                long due = -Math.Max(1, (long)(remaining * (10_000_000d / Stopwatch.Frequency)));
                if (!SetWaitableTimer(_timer, ref due, 0, 0, 0, false))
                {
                    // Without the timer, fall back to a millisecond wait on the wake event alone.
                    timeout = (uint)Math.Clamp(remaining * 1000 / Stopwatch.Frequency + 1, 1, int.MaxValue);
                    WaitForSingleObject(handles[0], timeout);
                    continue;
                }
            }
            else CancelWaitableTimer(_timer);
            WaitForMultipleObjects(2, handles, false, timeout);
        }
    }

    private void Handle(HotkeyEvent hotkey, long now)
    {
        switch (hotkey.Kind)
        {
            case HotkeyKind.Start: Fire(_engine.Start(hotkey.Slot, now), now); break;
            case HotkeyKind.ResetSlot: _engine.ResetSlot(hotkey.Slot); break;
            default: _engine.ResetAll(); break;
        }
        AppLog.Info("Scheduler", $"Hotkey {hotkey.Kind} {hotkey.Slot}.");
    }

    private void Fire(List<TimerCue> cues, long now)
    {
        if (cues.Count == 0 || _engine.Encounter is not { } encounter) return;
        foreach (var cue in cues)
        {
            if (cue.Sound < 0)
            {
                _alerts.Enqueue(new AlertEvent(cue.Timer, encounter, now));
                continue;
            }
            if (_sounds.TryGetReady(encounter, cue.Timer, cue.Timer.Sounds[cue.Sound]) is { } clip) _audio.Play(clip);
            else AppLog.WarnThrottled("Scheduler", "sound-not-ready", $"Sound {cue.Sound + 1} of '{cue.Timer.Id}' is not ready; it is skipped.");
        }
    }

    public void Dispose()
    {
        _keybinds?.Dispose();
        _stopping = true;
        _wake.Set();
        _thread.Join();
        _wake.Dispose();
        CloseHandle(_timer);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWaitableTimerExW(nint attributes, string? name, uint flags, uint access);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(nint timer, ref long due, int period,
        nint callback, nint argument, [MarshalAs(UnmanagedType.Bool)] bool resume);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CancelWaitableTimer(nint timer);
    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(nint handle, uint milliseconds);
    [DllImport("kernel32.dll")]
    private static extern uint WaitForMultipleObjects(uint count, nint[] handles,
        [MarshalAs(UnmanagedType.Bool)] bool waitAll, uint milliseconds);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
