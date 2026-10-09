using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

internal enum WindowMatchKind { ProcessName, Title }

// A saved binding to a game window. EncounterPath is remembered like AppSettings.EncounterPath:
// a name inside ./data, or an absolute path.
internal sealed record WindowHook
{
    public string Name { get; set; } = "";
    public WindowMatchKind Match { get; set; }
    public string Pattern { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EncounterPath { get; set; }
}

// Saved next to the exe as tipaura.windows.json. Active is the index of the selected hook, -1 for none.
internal sealed record WindowHookSettings
{
    internal const int MaxHooks = 64;

    public List<WindowHook> Hooks { get; set; } = [];
    public int Active { get; set; } = -1;

    internal WindowHook? ActiveHook => Active >= 0 && Active < Hooks.Count ? Hooks[Active] : null;

    internal static WindowHookSettings Validate(WindowHookSettings? value)
    {
        value ??= new WindowHookSettings();
        var hooks = new List<WindowHook>();
        int active = -1;
        for (int i = 0; i < (value.Hooks?.Count ?? 0) && hooks.Count < MaxHooks; i++)
        {
            if (value.Hooks![i] is not { } hook) continue;
            string pattern = hook.Pattern?.Trim() ?? "";
            if (pattern.Length == 0) continue;
            if (i == value.Active) active = hooks.Count;
            hooks.Add(hook with
            {
                Name = string.IsNullOrWhiteSpace(hook.Name) ? pattern : hook.Name.Trim(),
                Match = Enum.IsDefined(hook.Match) ? hook.Match : WindowMatchKind.ProcessName,
                Pattern = pattern,
                EncounterPath = string.IsNullOrWhiteSpace(hook.EncounterPath) ? null : hook.EncounterPath
            });
        }
        return new WindowHookSettings { Hooks = hooks, Active = active };
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(WindowHookSettings))]
internal sealed partial class WindowHookJsonContext : JsonSerializerContext;

internal static class WindowHookStore
{
    internal static string PathName => Path.Combine(AppContext.BaseDirectory, "tipaura.windows.json");
    internal static string? Warning { get; private set; }

    internal static WindowHookSettings Load(string? path = null)
    {
        path ??= PathName;
        Warning = null;
        try
        {
            if (File.Exists(path))
                return WindowHookSettings.Validate(JsonSerializer.Deserialize(File.ReadAllText(path), WindowHookJsonContext.Default.WindowHookSettings));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            Warning = $"Could not load window hooks: {ex.Message}";
            AppLog.Error("Settings", "Could not load window hooks", ex);
        }
        return new WindowHookSettings();
    }

    internal static void Save(WindowHookSettings value, string? path = null)
    {
        path ??= PathName;
        string temporary = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(WindowHookSettings.Validate(value), WindowHookJsonContext.Default.WindowHookSettings));
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}

internal readonly record struct RunningWindow(nint Hwnd, string ProcessName, string Title);

// Finds hooked windows with plain Win32 calls. Everything here is safe to call from any thread:
// the scheduler checks the foreground window when a hotkey arrives.
internal static class WindowFinder
{
    internal static bool IsMatch(WindowMatchKind kind, string pattern, string processName, string title)
    {
        pattern = pattern.Trim();
        if (pattern.Length == 0) return false;
        if (kind == WindowMatchKind.Title) return title.Contains(pattern, StringComparison.OrdinalIgnoreCase);
        return string.Equals(WithoutExe(pattern), WithoutExe(processName), StringComparison.OrdinalIgnoreCase);

        static string WithoutExe(string name) =>
            name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    internal static bool Matches(WindowHook hook, nint hwnd)
    {
        if (hwnd == 0 || !Native.IsWindow(hwnd)) return false;
        if (hook.Match == WindowMatchKind.Title) return IsMatch(hook.Match, hook.Pattern, "", Title(hwnd));
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        return ProcessName(pid) is { } name && IsMatch(hook.Match, hook.Pattern, name, "");
    }

    // The foreground window when it matches, else the first visible, non-minimized match.
    internal static nint Find(WindowHook hook)
    {
        nint foreground = Native.GetForegroundWindow();
        if (Matches(hook, foreground) && !Native.IsIconic(foreground)) return foreground;
        foreach (var window in List())
            if (!Native.IsIconic(window.Hwnd) && IsMatch(hook.Match, hook.Pattern, window.ProcessName, window.Title))
                return window.Hwnd;
        return 0;
    }

    // Visible, titled top-level windows of other processes, in Z order.
    internal static List<RunningWindow> List()
    {
        var windows = new List<RunningWindow>();
        var names = new Dictionary<uint, string?>();
        uint own = (uint)Environment.ProcessId;
        Native.EnumWindows((hwnd, _) =>
        {
            if (!Native.IsWindowVisible(hwnd) || Native.GetWindowTextLength(hwnd) <= 0) return true;
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == own) return true;
            if (!names.TryGetValue(pid, out string? name)) names[pid] = name = ProcessName(pid);
            if (name is not null) windows.Add(new RunningWindow(hwnd, name, Title(hwnd)));
            return true;
        }, 0);
        return windows;
    }

    // The client area in screen pixels, or null while the window is closed or minimized.
    internal static PixelRect? ClientBounds(nint hwnd)
    {
        if (hwnd == 0 || !Native.IsWindow(hwnd) || Native.IsIconic(hwnd) || !Native.IsWindowVisible(hwnd)) return null;
        if (!Native.GetClientRect(hwnd, out var client)) return null;
        var origin = new Native.Point();
        if (!Native.ClientToScreen(hwnd, ref origin)) return null;
        var bounds = new PixelRect(origin.X, origin.Y, origin.X + client.Right, origin.Y + client.Bottom);
        return bounds.IsEmpty ? null : bounds;
    }

    internal static uint ProcessId(nint hwnd)
    {
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        return pid;
    }

    private static string Title(nint hwnd)
    {
        int length = Native.GetWindowTextLength(hwnd);
        if (length <= 0) return "";
        var buffer = new char[length + 1];
        int copied = Native.GetWindowText(hwnd, buffer, buffer.Length);
        return new string(buffer, 0, Math.Clamp(copied, 0, length));
    }

    // Executable name without extension; null when the process cannot be queried.
    internal static string? ProcessName(uint pid)
    {
        if (pid == 0) return null;
        nint process = Native.OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
        if (process == 0) return null;
        try
        {
            var buffer = new char[1024];
            uint size = (uint)buffer.Length;
            if (!Native.QueryFullProcessImageName(process, 0, buffer, ref size)) return null;
            return Path.GetFileNameWithoutExtension(new string(buffer, 0, (int)size));
        }
        finally { Native.CloseHandle(process); }
    }
}

internal static partial class Native
{
    internal delegate bool EnumWindowsCallback(nint hwnd, nint param);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(EnumWindowsCallback callback, nint param);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowText(nint hwnd, [Out] char[] text, int maxCount);
    [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW")]
    internal static extern int GetWindowTextLength(nint hwnd);
    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetClientRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ClientToScreen(nint hwnd, ref Point point);
    [DllImport("kernel32.dll")]
    internal static extern nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryFullProcessImageName(nint process, uint flags, [Out] char[] name, ref uint size);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(nint handle);
}
