#if TIPAURA_AGENT_SELF_TEST
using System.Text.Json;

// Window hooks and anchored overlay placement: pure logic, part of --self-test.
internal static class HookSmoke
{
    private static void Check(bool condition, string message) => SelfTest.Check(condition, message);

    internal static void Run()
    {
        Layout();
        Settings();
        Matching();
        Console.WriteLine("Window hook and anchor checks passed.");
    }

    private static void Layout()
    {
        var game = new PixelRect(400, 100, 1200, 700);
        PixelRect[] workAreas = [new(0, 0, 1920, 1040), new(1920, 0, 3840, 1040)];
        var placement = OverlayPlacement.DefaultTimeline with { X = -30, Y = 20, Width = 300, Height = 200 };
        Check(OverlayLayout.Position(placement, OverlayPlacement.DefaultTimeline, game, workAreas) == new Vector2i(870, 120),
            "a top-right anchor places the overlay against the right edge of the client area");
        Check(OverlayLayout.Position(OverlayPlacement.DefaultAlerts, OverlayPlacement.DefaultAlerts, game, workAreas) == new Vector2i(350, 180),
            "a null offset uses the default offset from the top-center anchor");
        Check(OverlayLayout.Position(placement with { X = 5000 }, OverlayPlacement.DefaultTimeline, game, workAreas).X == 1920 - 300,
            "an off-screen position is clamped into the work area of the reference");

        var visible = OverlayLayout.Position(placement, OverlayPlacement.DefaultTimeline, game, workAreas);
        for (int anchor = 0; anchor < 9; anchor++)
        {
            var changed = OverlayLayout.ChangeAnchor(placement, game, anchor, visible);
            Check(changed.Anchor == anchor && OverlayLayout.Position(changed, OverlayPlacement.DefaultTimeline, game, workAreas) == visible,
                $"anchor {anchor} keeps the overlay in place");
            var grown = game with { Right = game.Right + 200, Bottom = game.Bottom + 100 };
            Check(OverlayLayout.Position(changed, OverlayPlacement.DefaultTimeline, grown, workAreas) - visible ==
                new Vector2i(anchor % 3 * 100, anchor / 3 * 50), $"anchor {anchor} follows client size changes");
            var odd = changed with { Width = 301, Height = 203 };
            var moved = new Vector2i(1111, 333);
            Check(OverlayLayout.Position(OverlayLayout.RememberPosition(odd, game, moved), OverlayPlacement.DefaultTimeline, game, workAreas) == moved,
                $"anchor {anchor} round-trips odd sizes after a drag or resize");
        }

        var work = workAreas[0] with { Left = 50, Top = 30 };
        var legacy = new OverlayPlacement { X = 700, Y = 400 };
        var migrated = OverlayLayout.MigrateLegacy(legacy, OverlayPlacement.DefaultTimeline, work);
        Check(migrated is { Anchor: 0, X: 650, Y: 370 } &&
            OverlayLayout.Position(migrated, OverlayPlacement.DefaultTimeline, work, workAreas) == new Vector2i(700, 400),
            "a pre-anchor position stays where it was relative to the primary work area");
        Check(OverlayLayout.MigrateLegacy(new OverlayPlacement(), OverlayPlacement.DefaultAlerts, work) is { Anchor: 1, X: null, Y: null },
            "a pre-anchor placement without a position takes the default anchor");
    }

    private static void Settings()
    {
        // Old files have no Anchor: a saved position stays absolute until MigrateLegacy, otherwise defaults apply.
        var old = AppSettings.Validate(JsonSerializer.Deserialize("""{"Timeline":{"X":10,"Y":20},"Alerts":{"Width":500}}""",
            AppSettingsJsonContext.Default.AppSettings));
        Check(old.Timeline is { Anchor: -1, X: 10, Y: 20 } && old.Alerts is { Anchor: 1, X: null } && !old.HotkeysRequireHookFocus,
            $"settings without anchors: {old.Timeline.Anchor} {old.Alerts.Anchor}");
        var fresh = AppSettings.Validate(new AppSettings());
        Check(fresh.Timeline.Anchor == 2 && fresh.Alerts.Anchor == 1, "default anchors are top-right and top-center");
        var clamped = AppSettings.Validate(new AppSettings { Timeline = new OverlayPlacement { Anchor = 42, X = 9_999_999 } });
        Check(clamped.Timeline is { Anchor: 2, X: 100_000 }, "anchors and offsets are clamped");
        string json = JsonSerializer.Serialize(fresh with { Alerts = fresh.Alerts with { Anchor = 7, X = -5, Y = 6 }, HotkeysRequireHookFocus = true },
            AppSettingsJsonContext.Default.AppSettings);
        Check(AppSettings.Validate(JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings)) is
            { Alerts: { Anchor: 7, X: -5, Y: 6 }, HotkeysRequireHookFocus: true }, "anchors and the hotkey gate survive a round trip");

        var hooks = WindowHookSettings.Validate(new WindowHookSettings
        {
            Hooks = [new() { Pattern = "  " }, new() { Name = " ", Pattern = " game.exe ", EncounterPath = " " },
                new() { Name = "Notes", Match = WindowMatchKind.Title, Pattern = "Notepad", EncounterPath = "example.yaml" }],
            Active = 2
        });
        Check(hooks is { Active: 1, Hooks: [{ Name: "game.exe", Pattern: "game.exe", EncounterPath: null }, { Name: "Notes", Match: WindowMatchKind.Title }] },
            "hook validation drops empty patterns, trims, names and keeps the active hook");
        Check(WindowHookSettings.Validate(new WindowHookSettings { Active = 3 }).Active == -1, "an out-of-range active hook becomes none");

        string folder = Path.Combine(Path.GetTempPath(), $"tipaura-hooks-{Guid.NewGuid():N}");
        try
        {
            string path = Path.Combine(folder, "tipaura.windows.json");
            Check(WindowHookStore.Load(path) is { Active: -1, Hooks.Count: 0 } && WindowHookStore.Warning is null, "a missing hook file means no hooks");
            WindowHookStore.Save(hooks, path);
            var loaded = WindowHookStore.Load(path);
            Check(loaded.Active == 1 && loaded.Hooks.SequenceEqual(hooks.Hooks), "hooks survive a round trip");
            Check(File.ReadAllText(path).Contains("\"Title\"", StringComparison.Ordinal), "match kinds are written as names");
            File.WriteAllText(path, "{ not json");
            Check(WindowHookStore.Load(path).Hooks.Count == 0 && WindowHookStore.Warning is not null, "a broken hook file is reported");
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    private static void Matching()
    {
        Check(WindowFinder.IsMatch(WindowMatchKind.ProcessName, "Game.EXE", "game", ""), "process names ignore case and .exe");
        Check(WindowFinder.IsMatch(WindowMatchKind.ProcessName, " game ", "game.exe", ""), "patterns are trimmed");
        Check(!WindowFinder.IsMatch(WindowMatchKind.ProcessName, "game", "gamelauncher", ""), "process names must match exactly");
        Check(WindowFinder.IsMatch(WindowMatchKind.Title, "artale", "", "MapleStory Worlds-Artale (繁體中文版)"), "titles match any part");
        Check(!WindowFinder.IsMatch(WindowMatchKind.Title, "", "", "anything"), "an empty pattern matches nothing");
        Check(WindowFinder.ProcessName((uint)Environment.ProcessId) is { } own &&
            string.Equals(own, Path.GetFileNameWithoutExtension(Environment.ProcessPath), StringComparison.OrdinalIgnoreCase),
            "the process name is read from the image path");

        Check(HotkeyGate.Allows(10, 10, false), "TipAura in the foreground allows hotkeys");
        Check(HotkeyGate.Allows(20, 10, true), "the hooked window in the foreground allows hotkeys");
        Check(!HotkeyGate.Allows(20, 10, false) && !HotkeyGate.Allows(0, 10, false), "other windows block hotkeys");
    }
}
#endif
