#if TIPAURA_AGENT_SELF_TEST
if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
{
    SelfTest.Run();
    return;
}
if (args.Contains("--keybind-smoke", StringComparer.OrdinalIgnoreCase))
{
    KeybindSmoke.Run();
    return;
}
if (args.Contains("--tts-smoke", StringComparer.OrdinalIgnoreCase))
{
    SelfTest.TtsSmoke();
    return;
}
if (args.Contains("--decode-smoke", StringComparer.OrdinalIgnoreCase))
{
    SelfTest.DecodeSmoke();
    return;
}
if (args.Contains("--audio-smoke", StringComparer.OrdinalIgnoreCase))
{
    SelfTest.AudioSmoke();
    return;
}
if (args.Contains("--scheduler-smoke", StringComparer.OrdinalIgnoreCase))
{
    SelfTest.SchedulerSmoke();
    return;
}
if (args.Contains("--ui-smoke", StringComparer.OrdinalIgnoreCase))
{
    UiSmoke.Run();
    return;
}
if (args.Contains("--capture-smoke", StringComparer.OrdinalIgnoreCase))
{
    CaptureSmoke.Run();
    return;
}
if (args.Contains("--dump-shaders", StringComparer.OrdinalIgnoreCase))
{
    // The final argument is the output directory, normally assets/shaders.
    ShaderTool.Dump(args[^1]);
    return;
}
#endif

using var log = AppLog.Start();
AppLog.InstallCrashHandlers();
try
{
    Native.SetProcessDpiAwarenessContext(-4);
    var settings = AppSettingsStore.Load();
    Localization.Configure(settings.Language);
    var options = new AppWindowOptions("TipAura", new Vector2i(820, 760));
    if (WindowPlacement.Restore(settings, WindowPlacement.CurrentWorkAreas()) is { } placement)
        options = options with { Location = placement.Location, ClientSize = placement.ClientSize };
    using var window = new MainWindow(options, settings, AppSettingsStore.Warning);
    window.Run();
    AppLog.Info("App", "Exited normally.");
}
catch (Exception ex)
{
    AppLog.Fatal("App", "Unhandled GUI error", ex);
    throw;
}
