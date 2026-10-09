#if TIPAURA_AGENT_SELF_TEST
// Builds the real main window with the example encounter, starts timers and fires alerts, and renders
// frames of every window. The settings file is redirected to a temporary folder.
internal static class UiSmoke
{
    internal static void Run()
    {
        ShaderTool.Verify();
        string folder = Path.Combine(Path.GetTempPath(), "tipaura-ui-smoke");
        using var log = AppLog.Start(folder);
        File.Delete(Path.Combine(folder, "tipaura.windows.json"));
        var settings = new AppSettings { Volume = 0, EncounterPath = SelfTest.ProjectPath("data/example.yaml") };
        using var window = new MainWindow(new AppWindowOptions("TipAura smoke", new Vector2i(820, 760)), settings, null,
            Path.Combine(folder, "tipaura.settings.json"));
        window.RenderFramesForSmoke(10);
        var state = window.StateForSmoke;
        SelfTest.Check(state.Timers == 7 && state.Errors == 0, $"Example encounter: {state.Timers} timers, {state.Errors} errors.");
        SelfTest.Check(window.IconFontComplete, "The Lucide icon font is missing glyphs.");
        window.StartForSmoke(1);
        window.StartForSmoke(5);
        window.RenderFramesForSmoke(10);
        SelfTest.Check(window.StateForSmoke.Running == 3, $"Expected 3 running timers, found {window.StateForSmoke.Running}.");
        // Monochrome timeline in both themes, with the color swatches on the Settings tab.
        window.SettingsForSmoke = window.SettingsForSmoke with { TimelineMonochrome = true };
        window.ShowAllTabsForSmoke();
        window.SettingsForSmoke = window.SettingsForSmoke with { Theme = 1 };
        window.ShowAllTabsForSmoke();
        window.SettingsForSmoke = window.SettingsForSmoke with { Theme = 0, TimelineMonochrome = false };
        window.ShowAllTabsForSmoke();
        window.ShowTestAlertForSmoke();
        window.RenderFramesForSmoke(10);
        SelfTest.Check(window.StateForSmoke.Alerts == 1 && window.StateForSmoke.OverlaysOpen == 2, "Alert or overlay windows missing.");
        window.HooksForSmoke();
        window.ResetAllForSmoke();
        window.RenderFramesForSmoke(5);
        SelfTest.Check(window.StateForSmoke.Running == 0, "Reset encounter did not clear the timers.");
        // Disabled timers (session only) do not start; custom bindings render on every tab.
        window.SetSlotEnabledForSmoke(1, false);
        window.StartForSmoke(1);
        window.SettingsForSmoke = window.SettingsForSmoke with
        {
            Hotkeys = new HotkeyBindings().WithKey(1, 0x7C) with { StartModifiers = new() { Ctrl = ModifierSide.Left }, ResetModifiers = new() { Alt = ModifierSide.Right } }
        };
        window.ShowAllTabsForSmoke();
        SelfTest.Check(window.StateForSmoke.Running == 0, "A disabled timer should not start.");
        window.SetSlotEnabledForSmoke(1, true);
        window.SettingsForSmoke = window.SettingsForSmoke with { Hotkeys = new HotkeyBindings() };
        window.StartForSmoke(1);
        window.RenderFramesForSmoke(5);
        SelfTest.Check(window.StateForSmoke.Running > 0, "An enabled timer should start again.");
        window.ResetAllForSmoke();
        window.LucideForSmoke();
        string copy = Path.Combine(folder, $"ui-smoke-{Guid.NewGuid():N}.yaml");
        try { window.EditorForSmoke(copy); }
        finally { File.Delete(copy); }
        string old = Path.Combine(folder, $"ui-smoke-v1-{Guid.NewGuid():N}.yaml");
        try
        {
            File.Copy(SelfTest.ProjectPath("SelfTests/fixtures/example-v1.yaml"), old);
            window.UpgradeForSmoke(old);
        }
        finally
        {
            File.Delete(old);
            File.Delete(old + ".v1.bak");
        }
        string export = Path.Combine(folder, $"ui-smoke-pack-{Guid.NewGuid():N}");
        try { window.PackForSmoke(SelfTest.ProjectPath("SelfTests/fixtures/demo.zip"), export); }
        finally { if (Directory.Exists(export)) Directory.Delete(export, true); }
        Console.WriteLine("UI smoke passed (main window tabs, timeline and alert overlays, window hooks and anchors, example encounter, Lucide picker and glyphs, editor history, save and upgrade, pack).");
    }
}
#endif
