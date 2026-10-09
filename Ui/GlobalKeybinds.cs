using System.Collections.Concurrent;
using System.Runtime.InteropServices;

internal enum HotkeyKind { Start, ResetSlot, ResetAll }

// Slot is the key index: 1-9 for Start and ResetSlot, 0 for ResetAll.
internal readonly record struct HotkeyEvent(HotkeyKind Kind, int Slot);

// Pure keyboard state machine over the configured HotkeyBindings; the global hook serializes access to it.
//   start modifier + key 1-9   start every timer bound to that slot
//   reset modifier + key 1-9   reset every timer bound to that slot
//   reset modifier + key 0     reset the encounter
internal sealed class KeybindProcessor
{
    private const int Extended = 0x100;
    private readonly HashSet<int> _down = [];

    internal HotkeyBindings Bindings { get; set; } = new();

    internal void SeedKey(int key) { _down.Add(key); }

    internal HotkeyEvent? Process(int key, bool extended, bool down)
    {
        int code = key | (extended ? Extended : 0);
        if (!down)
        {
            // Seeded keys carry no extended flag, so release both forms.
            _down.Remove(code);
            _down.Remove(key);
            return null;
        }
        // Auto-repeat while held fires once.
        if (!_down.Add(code)) return null;
        int ctrl = 0, alt = 0, shift = 0;
        bool win = false;
        foreach (int held in _down)
            switch (held & 0xFF)
            {
                case 0x11 or 0xA2: ctrl |= 1; break;
                case 0xA3: ctrl |= 2; break;
                case 0x12 or 0xA4: alt |= 1; break;
                case 0xA5: alt |= 2; break;
                case 0x10 or 0xA0: shift |= 1; break;
                case 0xA1: shift |= 2; break;
                case 0x5B or 0x5C: win = true; break;
            }
        var bindings = Bindings;
        int index = bindings.IndexOf(HotkeyBindings.Normalize(key, extended));
        if (win || index < 0) return null;
        var modifiers = new HeldModifiers(ctrl, alt, shift);
        if (index == HotkeyBindings.ResetAllIndex)
            return bindings.ResetModifiers.Matches(modifiers) ? new(HotkeyKind.ResetAll, 0) : null;
        if (bindings.StartModifiers.Matches(modifiers)) return new(HotkeyKind.Start, index);
        return bindings.ResetModifiers.Matches(modifiers) ? new(HotkeyKind.ResetSlot, index) : null;
    }
}

internal sealed class GlobalKeybinds : IDisposable
{
    private readonly object _gate = new();
    private readonly KeybindProcessor _processor = new();
    private readonly ConcurrentQueue<HotkeyEvent> _events = new();
    private readonly ConcurrentQueue<int> _captured = new();
    // While set, the next key press that is not a modifier is captured for binding instead of handled.
    private volatile bool _capturing;
    // The captured key's release is swallowed too; -1 when none is pending.
    private volatile int _swallowRelease = -1;
    private readonly Action? _notify;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private readonly Native.KeyboardHookCallback _callback;
    private uint _threadId;
    private nint _hook;
    internal string? Error { get; private set; }

    // notify runs on the hook thread after an event is queued; it must stay as cheap as the callback.
    internal GlobalKeybinds(Action? notify = null)
    {
        _notify = notify;
        _callback = HookCallback;
        _thread = new Thread(Listen) { IsBackground = true, Name = "Global keybinds" };
        _thread.Start();
        _ready.Wait();
    }

    internal bool TryDequeue(out HotkeyEvent value) => _events.TryDequeue(out value);

    internal void SetBindings(HotkeyBindings bindings)
    {
        lock (_gate) _processor.Bindings = bindings;
    }

    // Captures the next key press (normalized code; 0x1B for Esc) without passing it on to any window.
    internal void BeginCapture()
    {
        _captured.Clear();
        _capturing = true;
    }

    internal void CancelCapture() => _capturing = false;
    internal bool TryTakeCaptured(out int code) => _captured.TryDequeue(out code);

    private void Listen()
    {
        try
        {
            _threadId = Native.GetCurrentThreadId();
            // Create the message queue before another thread can request shutdown.
            Native.PeekMessage(out _, 0, 0, 0, 0);
            _hook = Native.SetWindowsHookEx(13, _callback, Native.GetModuleHandle(null), 0);
            if (_hook == 0)
            {
                Error = new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message;
                return;
            }
            // Seed held keys after hooking: callbacks run only once this thread pumps messages, so a key
            // released during seeding still delivers its key-up instead of staying down forever.
            lock (_gate)
                for (int key = 8; key <= 254; key++)
                    if (key is not (0x10 or 0x11 or 0x12) && Native.GetAsyncKeyState(key) < 0) _processor.SeedKey(key);
            _ready.Set();
            while (Native.GetMessage(out var message, 0, 0, 0) > 0)
            {
                Native.TranslateMessage(in message);
                Native.DispatchMessage(in message);
            }
        }
        catch (Exception ex) { Error = ex.Message; }
        finally
        {
            _ready.Set();
            if (_hook != 0) Native.UnhookWindowsHookEx(_hook);
        }
    }

    private unsafe nint HookCallback(int code, nuint message, nint data)
    {
        if (code >= 0 && message is 0x100 or 0x101 or 0x104 or 0x105)
        {
            var info = *(Native.KeyboardHookInfo*)data;
            int key = (int)info.Key;
            bool extended = (info.Flags & 1) != 0, down = message is 0x100 or 0x104;
            if (down && _capturing && !HotkeyBindings.IsModifier(key))
            {
                _capturing = false;
                _swallowRelease = key;
                _captured.Enqueue(HotkeyBindings.Normalize(key, extended));
                return 1;
            }
            if (!down && key == _swallowRelease)
            {
                _swallowRelease = -1;
                return 1;
            }
            // No UI, disk I/O or audio work in the low-level callback; Windows silently removes slow hooks.
            HotkeyEvent? value;
            lock (_gate) value = _processor.Process(key, extended, down);
            if (value is { } hotkey)
            {
                _events.Enqueue(hotkey);
                _notify?.Invoke();
            }
        }
        return Native.CallNextHookEx(0, code, message, data);
    }

    public void Dispose()
    {
        if (_thread.IsAlive)
        {
            Native.PostThreadMessage(_threadId, 0x12, 0, 0);
            _thread.Join();
        }
        _ready.Dispose();
        GC.KeepAlive(_callback);
    }
}

internal static partial class Native
{
    internal delegate nint KeyboardHookCallback(int code, nuint message, nint data);
    // KBDLLHOOKSTRUCT; Flags bit 0 is LLKHF_EXTENDED.
    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyboardHookInfo
    {
        public uint Key, Scan, Flags, Time;
        public nuint Extra;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyboardMessage
    {
        public nint Hwnd;
        public uint Message;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public Point Point;
        public uint Private;
    }
    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    internal static extern nint SetWindowsHookEx(int type, KeyboardHookCallback callback, nint module, uint thread);
    [DllImport("user32.dll")]
    internal static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")]
    internal static extern nint CallNextHookEx(nint hook, int code, nuint message, nint data);
    [DllImport("user32.dll", EntryPoint = "GetMessageW")]
    internal static extern int GetMessage(out KeyboardMessage message, nint hwnd, uint min, uint max);
    [DllImport("user32.dll", EntryPoint = "PeekMessageW")]
    internal static extern bool PeekMessage(out KeyboardMessage message, nint hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")]
    internal static extern bool TranslateMessage(in KeyboardMessage message);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
    internal static extern nint DispatchMessage(in KeyboardMessage message);
    [DllImport("user32.dll", EntryPoint = "PostThreadMessageW")]
    internal static extern bool PostThreadMessage(uint thread, uint message, nuint wparam, nint lparam);
    [DllImport("user32.dll")]
    internal static extern short GetAsyncKeyState(int key);
    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
    internal static extern nint GetModuleHandle(string? name);
}
