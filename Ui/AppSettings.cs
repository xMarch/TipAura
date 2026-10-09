using System.Text.Json;
using System.Text.Json.Serialization;

// Saved next to the exe as tipaura.settings.json. Load validates every value, so a hand-edited or
// older file can never put the UI into an invalid state. Missing properties keep their defaults.
// Properties here and in OverlayPlacement use set, not init: source-generated JSON treats init-only
// properties like constructor parameters and passes default(T) for any that the file lacks.
internal sealed record AppSettings
{
    public float Volume { get; set; } = 0.8f;
    public int MaxSounds { get; set; } = AudioEngine.DefaultMaxVoices;
    // Every clip (sound file, TTS, preview) stops after this many seconds.
    public float MaxSoundSeconds { get; set; } = AudioEngine.DefaultMaxPlaySeconds;
    public string? TtsVoice { get; set; }
    public float TtsRate { get; set; } = 1f;
    // Absolute path, or a file name inside ./data.
    public string? EncounterPath { get; set; }
    public int FontSizePx { get; set; } = 16;
    public int Theme { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Language { get; set; }
    public OverlayPlacement Timeline { get; set; } = OverlayPlacement.DefaultTimeline;
    public OverlayPlacement Alerts { get; set; } = OverlayPlacement.DefaultAlerts;
    public float AlertSeconds { get; set; } = 3f;
    public int MaxTimelineBars { get; set; } = 8;
    // Draw the timeline bars in the colors below (one set per theme) instead of each timer's YAML color.
    public bool TimelineMonochrome { get; set; }
    public TimelineColors MonochromeDark { get; set; } = TimelineColors.DefaultDark;
    public TimelineColors MonochromeLight { get; set; } = TimelineColors.DefaultLight;
    // Display order of the Timers tab list: 0 file order, 1 key, 2 time.
    public int TimerSort { get; set; }
    // Drop global hotkeys while a TipAura text field has keyboard focus, so typing digits does not start timers.
    public bool PauseHotkeysWhileTyping { get; set; } = true;
    // Drop global hotkeys unless the active window hook's window (or TipAura) is in the foreground.
    public bool HotkeysRequireHookFocus { get; set; }
    // Local key and modifier bindings; encounter files only refer to the slots 1-9.
    public HotkeyBindings Hotkeys { get; set; } = new();
    // Feed the timeline overlay to the main window's HWND swap chain for Discord's capture hook.
    public bool DiscordCapture { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? WindowX { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? WindowY { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? WindowWidth { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? WindowHeight { get; set; }

    internal TimelineColors MonochromeColors => Theme == 1 ? MonochromeLight : MonochromeDark;

    internal static AppSettings Validate(AppSettings? value)
    {
        value ??= new AppSettings();
        bool validPlacement = value.WindowX is not null && value.WindowY is not null &&
            value.WindowWidth is >= 640 and <= 10_000 && value.WindowHeight is >= 480 and <= 10_000;
        return value with
        {
            Volume = float.IsFinite(value.Volume) ? Math.Clamp(value.Volume, 0f, 1f) : 0.8f,
            MaxSounds = Math.Clamp(value.MaxSounds, 1, AudioEngine.MaxVoiceLimit),
            MaxSoundSeconds = float.IsFinite(value.MaxSoundSeconds)
                ? Math.Clamp(value.MaxSoundSeconds, 1f, AudioEngine.MaxPlaySecondsLimit) : AudioEngine.DefaultMaxPlaySeconds,
            TtsVoice = string.IsNullOrWhiteSpace(value.TtsVoice) ? null : value.TtsVoice,
            TtsRate = float.IsFinite(value.TtsRate) ? Math.Clamp(value.TtsRate, 0.5f, 3f) : 1f,
            EncounterPath = string.IsNullOrWhiteSpace(value.EncounterPath) ? null : value.EncounterPath,
            FontSizePx = Math.Clamp(value.FontSizePx, 10, 32),
            Theme = value.Theme is >= 0 and <= 1 ? value.Theme : 0,
            Language = string.Equals(value.Language, "en", StringComparison.OrdinalIgnoreCase) ? "en" : null,
            Timeline = OverlayPlacement.Validate(value.Timeline, OverlayPlacement.DefaultTimeline),
            Alerts = OverlayPlacement.Validate(value.Alerts, OverlayPlacement.DefaultAlerts),
            AlertSeconds = float.IsFinite(value.AlertSeconds) ? Math.Clamp(value.AlertSeconds, 1f, 10f) : 3f,
            MaxTimelineBars = Math.Clamp(value.MaxTimelineBars, 1, 30),
            MonochromeDark = TimelineColors.Validate(value.MonochromeDark, TimelineColors.DefaultDark),
            MonochromeLight = TimelineColors.Validate(value.MonochromeLight, TimelineColors.DefaultLight),
            TimerSort = value.TimerSort is >= 0 and <= 2 ? value.TimerSort : 0,
            Hotkeys = HotkeyBindings.Validate(value.Hotkeys),
            WindowX = validPlacement ? value.WindowX : null,
            WindowY = validPlacement ? value.WindowY : null,
            WindowWidth = validPlacement ? value.WindowWidth : null,
            WindowHeight = validPlacement ? value.WindowHeight : null
        };
    }
}

// Position and look of one floating window. X/Y are the offset from Anchor (row-major 3x3) of the hooked
// window's client area, or of the primary work area while no hook is active (see OverlayLayout); null
// means the default offset. Anchor -1 means the file predates anchors and X/Y are absolute screen
// coordinates, which OverlayLayout.MigrateLegacy converts on startup.
internal sealed record OverlayPlacement
{
    internal const int MinWidth = 160, MinHeight = 60;

    public bool Visible { get; set; } = true;
    public bool ClickThrough { get; set; } = true;
    public bool Topmost { get; set; } = true;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? X { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Y { get; set; }
    public int Anchor { get; set; } = -1;
    public int Width { get; set; } = 320;
    public int Height { get; set; } = 260;
    public int FontSize { get; set; } = 18;
    public float BackgroundOpacity { get; set; } = 0.35f;

    // Offset used while X/Y are null; only meaningful on the defaults below.
    internal Vector2i DefaultOffset { get; init; }

    internal static OverlayPlacement DefaultTimeline { get; } = new() { Anchor = 2, DefaultOffset = new(-16, 16) };
    internal static OverlayPlacement DefaultAlerts { get; } = new()
    {
        Width = 900, Height = 200, FontSize = 44, BackgroundOpacity = 0f, Anchor = 1, DefaultOffset = new(0, 80)
    };

    internal static OverlayPlacement Validate(OverlayPlacement? value, OverlayPlacement defaults)
    {
        value ??= defaults;
        bool legacy = value.Anchor < 0 && value.X is not null && value.Y is not null;
        return value with
        {
            Anchor = legacy ? -1 : value.Anchor is >= 0 and <= 8 ? value.Anchor : defaults.Anchor,
            X = value.X is int x ? Math.Clamp(x, -100_000, 100_000) : null,
            Y = value.Y is int y ? Math.Clamp(y, -100_000, 100_000) : null,
            Width = Math.Clamp(value.Width, MinWidth, 4000),
            Height = Math.Clamp(value.Height, MinHeight, 3000),
            FontSize = Math.Clamp(value.FontSize, 10, 96),
            BackgroundOpacity = float.IsFinite(value.BackgroundOpacity) ? Math.Clamp(value.BackgroundOpacity, 0f, 1f) : defaults.BackgroundOpacity
        };
    }
}

// Monochrome timeline colors (#RRGGBB or #RRGGBBAA). Flash replaces the warning color of a bar whose alert has fired.
internal sealed record TimelineColors
{
    public string Text { get; set; } = "#FFFFFF";
    public string Shadow { get; set; } = "#000000C0";
    public string Fill { get; set; } = "#458CD9E6";
    public string Track { get; set; } = "#0D0D0FBF";
    public string Flash { get; set; } = "#E66B2E";

    internal static TimelineColors DefaultDark { get; } = new();
    internal static TimelineColors DefaultLight { get; } = new()
    {
        Text = "#1A1A1A", Shadow = "#FFFFFFC0", Fill = "#7FB0E8E6", Track = "#E6E6EABF", Flash = "#E66B2E"
    };

    internal static TimelineColors Validate(TimelineColors? value, TimelineColors defaults)
    {
        if (value is null) return defaults;
        static string Pick(string? color, string fallback) => color is not null && EncounterYaml.IsColor(color) ? color : fallback;
        return new TimelineColors
        {
            Text = Pick(value.Text, defaults.Text),
            Shadow = Pick(value.Shadow, defaults.Shadow),
            Fill = Pick(value.Fill, defaults.Fill),
            Track = Pick(value.Track, defaults.Track),
            Flash = Pick(value.Flash, defaults.Flash)
        };
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJsonContext : JsonSerializerContext;

internal static class AppSettingsStore
{
    internal static string PathName => Path.Combine(AppContext.BaseDirectory, "tipaura.settings.json");
    internal static string? Warning { get; private set; }

    internal static AppSettings Load(string? path = null)
    {
        path ??= PathName;
        Warning = null;
        try
        {
            if (File.Exists(path))
            {
                string json = File.ReadAllText(path);
                return AppSettings.Validate(JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            Warning = $"Could not load settings: {ex.Message}";
            Console.Error.WriteLine(Warning);
            AppLog.Error("Settings", "Could not load settings", ex);
        }
        return new AppSettings();
    }

    internal static void Save(AppSettings value, string? path = null)
    {
        path ??= PathName;
        string temporary = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(AppSettings.Validate(value), AppSettingsJsonContext.Default.AppSettings));
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
