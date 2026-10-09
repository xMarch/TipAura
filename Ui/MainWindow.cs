using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using ImGuiNET;
using Silk.NET.Input;
using Silk.NET.Windowing;
using Vortice.Mathematics;

internal sealed class MainWindow : IDisposable
{
    private static readonly Color4 BackgroundColor = new(0.12f, 0.13f, 0.16f, 1f);
    private static readonly Vector4 ErrorColor = new(0.95f, 0.35f, 0.35f, 1f);
    private static readonly Vector4 WarningColor = new(0.95f, 0.80f, 0.40f, 1f);
    private static readonly Vector4 OkColor = new(0.35f, 0.75f, 0.40f, 1f);
    private static readonly string[] LimitLabels = ["Replace oldest", "Ignore", "Reset"];
    private static readonly string[] SortLabels = ["File order", "By key", "By time"];
    private static readonly string[] SoundKindLabels = ["Windows TTS", "Sound file"];
    private static readonly string[] AnchorNames = ["Top left", "Top center", "Top right", "Middle left", "Center",
        "Middle right", "Bottom left", "Bottom center", "Bottom right"];

    private readonly AppWindow _window;
    private readonly AudioEngine _audio = new();
    private readonly SoundCache _sounds = new();
    private readonly IconCache _icons = new();
    private readonly TimerScheduler _scheduler;
    private readonly ImGuiRenderer _renderer;
    private readonly AuxiliaryWindows _auxiliary;
    private readonly TitleBarButtons _titleButtons = new();
    private readonly List<ActiveAlert> _alerts = [];
    private AppSettings _settings, _savedSettings;
    private long _lastSave;
    private OverlayWindow? _timelineWindow, _alertWindow;
    private IReadOnlyList<PixelRect> _workAreas;
    private long _lastWorkAreaCheck;
    private nint _alertGlyphs;

    // Encounter state: the file on disk and the working copy shown in the editor.
    private IReadOnlyList<EncounterSource> _dataFiles = [];
    private string? _encounterPath;
    private Encounter? _encounter;
    private IReadOnlyList<EncounterIssue> _issues = [];
    // Set while the file on disk is in an older or untagged format; saving over it keeps a backup first.
    private (int Version, bool Tagged)? _outdated;
    private bool _pendingApply;
    // Every edit gets a new revision; the working copy is dirty while it differs from the saved one.
    private int _revision, _savedRevision, _nextRevision;
    private bool IsDirty => _revision != _savedRevision;
    // Undo/redo of the working copy, cleared when another file is loaded. Edits made while the same widget
    // stays active (typing in a field, dragging a slider) are one step; _historyItem is that widget's ID.
    private readonly EditHistory<EditState> _history = new(50);
    private uint _historyItem;
    private string _notice = "", _saveError = "";
    private (string Text, long Shown)? _toast;
    private Task<string?>? _openPicker, _savePicker, _packPicker;
    // Sound = -1 picks the timer's icon, otherwise the file of Sounds[Sound].
    private (Task<string?> Task, int Timer, int Sound)? _assetPicker;
    // The pack files modal: the folder shown (EncounterPack.SoundFolder or IconFolder) and the field a pick
    // goes to, as in _assetPicker; Timer = -1 only shows the files. _packFilePicker adds a file to the pack.
    private (string Folder, int Timer, int Sound)? _packFiles;
    private Task<string?>? _packFilePicker;
    // Extracting a pack into data/: the plan, the files that would replace different ones, the labels the
    // user chose to overwrite and the conflict being asked about.
    private EncounterYaml.PackExtraction? _extraction;
    private List<EncounterYaml.ExtractedFile> _extractConflicts = [];
    private readonly HashSet<string> _extractOverwrite = new(StringComparer.OrdinalIgnoreCase);
    private int _extractNext;
    // Lucide icon picker filters; index 0 of _lucideCategory is every category.
    private string _lucideFilter = "";
    private int _lucideCategory;
    // Previewed sounds, each played once its clip is ready and its time (Stopwatch ticks) has come.
    private readonly List<(Task<AudioClip> Task, long At)> _previews = [];
    // Values of sliders that apply only on release (font sizes), keyed per slider while it is dragged.
    private readonly Dictionary<string, int> _sliderDrafts = new(StringComparer.Ordinal);
    private IReadOnlyList<TtsVoice> _voices = [];
    private int _selectedTimer = -1;
    // Timer ids disabled for this session from the Timers tab list; never saved, cleared when a file loads.
    private readonly HashSet<string> _disabledTimers = new(StringComparer.Ordinal);
    // Key row of Settings > Hotkeys waiting for a key press (0 reset encounter, 1-9 slots), -1 for none.
    private int _capturingKey = -1;
    private bool _hotkeyRowsShown;
    private string _hotkeyError = "", _modifierError = "";

    private bool _closeRequested, _minimizeRequested, _draggingWindow;
    private Vector2i _dragStartWindow, _dragStartCursor;
    private int _fontSizeApplied, _tabRequest = -1;
    private readonly string? _settingsPath;

    // Window hooks (tipaura.windows.json): the active hook's window is the overlays' anchor reference.
    private WindowHookSettings _hooks;
    private readonly string? _hooksPath;
    private bool _hooksDirty;
    private nint _hookWindow;
    private long _lastHookSearch;
    // Reference rectangle of the anchors this frame; null while the active hook's window is missing.
    private PixelRect? _overlayBounds;
    private List<RunningWindow> _runningWindows = [];

    // settingsPath overrides the file next to the exe (used by the UI smoke test).
    internal MainWindow(AppWindowOptions options, AppSettings settings, string? settingsWarning, string? settingsPath = null)
    {
        _settings = _savedSettings = settings;
        _settingsPath = settingsPath;
        _hooksPath = settingsPath is null ? null : Path.Combine(Path.GetDirectoryName(settingsPath)!, "tipaura.windows.json");
        _hooks = WindowHookStore.Load(_hooksPath);
        _window = new AppWindow(options);
        _renderer = new ImGuiRenderer(settings.FontSizePx);
        _fontSizeApplied = settings.FontSizePx;
        _renderer.ApplyTheme(settings.Theme);
        _auxiliary = new AuxiliaryWindows(_window);
        _window.KeyChar += c => { _renderer.SetCurrentContext(); ImGui.GetIO().AddInputCharacterUTF16(c); };
        _window.KeyDown += key => { _renderer.SetCurrentContext(); SendKey(key, true); };
        _window.KeyUp += key => { _renderer.SetCurrentContext(); SendKey(key, false); };
        _workAreas = WindowPlacement.CurrentWorkAreas();
        var primary = PrimaryWorkArea();
        _settings = _settings with
        {
            Timeline = OverlayLayout.MigrateLegacy(_settings.Timeline, OverlayPlacement.DefaultTimeline, primary),
            Alerts = OverlayLayout.MigrateLegacy(_settings.Alerts, OverlayPlacement.DefaultAlerts, primary)
        };
        _audio.Volume = settings.Volume;
        _audio.MaxVoices = settings.MaxSounds;
        _audio.MaxPlaySeconds = settings.MaxSoundSeconds;
        _sounds.ConfigureTts(settings.TtsVoice, settings.TtsRate);
        _scheduler = new TimerScheduler(_audio, _sounds);
        _scheduler.SetHotkeyBindings(_settings.Hotkeys);
        if (_scheduler.HotkeyError is { } hookError)
        {
            _notice = Localization.F("Global hotkeys are unavailable: {0}", hookError);
            AppLog.Error("Hotkeys", $"Global hook failed: {hookError}");
        }
        if (settingsWarning is not null) _notice = AddLine(_notice, Localization.Status(settingsWarning));
        if (WindowHookStore.Warning is { } hooksWarning) _notice = AddLine(_notice, Localization.Status(hooksWarning));
        try { _voices = TtsSynthesizer.Voices(); }
        catch (Exception ex)
        {
            _notice = AddLine(_notice, Localization.F("Windows TTS is unavailable: {0}", ex.Message));
            AppLog.Error("Audio", "Could not list TTS voices", ex);
        }
        RefreshDataFiles();
        // The active hook's remembered file wins over the last loaded one.
        string? hooked = _hooks.ActiveHook?.EncounterPath is { } remembered ? ResolveEncounterPath(remembered) : null;
        string? initial = hooked is not null && File.Exists(hooked) ? hooked
            : settings.EncounterPath is { } saved ? ResolveEncounterPath(saved) : _dataFiles.FirstOrDefault()?.Path;
        if (initial is not null) LoadEncounter(initial);
    }

    public void Run()
    {
        using var waiter = new FrameWaiter();
        long previous = Stopwatch.GetTimestamp();
        long deadline = previous;
        try
        {
            while (!_window.IsClosing && !_closeRequested)
            {
                waiter.WaitUntil(deadline);
                _window.NewInputFrame();
                _window.DoEvents();
                if (_window.IsClosing) break;
                long now = Stopwatch.GetTimestamp();
                float elapsed = (float)((now - previous) / (double)Stopwatch.Frequency);
                previous = now;
                Frame(elapsed, now);
                // Smooth bars while something moves; otherwise idle cheaply.
                bool animating = _scheduler.Snapshot.Length > 0 || _alerts.Count > 0 || _previews.Count > 0;
                int fps = animating || _window.IsFocused || _auxiliary.NeedsFrames ? 60 : 15;
                long period = Stopwatch.Frequency / fps;
                deadline += period;
                long finished = Stopwatch.GetTimestamp();
                if (deadline <= finished) deadline += ((finished - deadline) / period + 1) * period;
            }
        }
        finally
        {
            _draggingWindow = false;
            SaveSettings(force: true);
        }
    }

    private void Frame(float deltaTime, long now)
    {
        while (_scheduler.TryTakeAlert(out var alert))
            _alerts.Add(new ActiveAlert(alert.Timer, alert.Encounter, alert.At));
        long lifetime = (long)(_settings.AlertSeconds * Stopwatch.Frequency);
        _alerts.RemoveAll(alert => now - alert.Started > lifetime);
        while (_alerts.Count > 6) _alerts.RemoveAt(0);
        PollTasks();
        if (_pendingApply && !ImGui.IsAnyItemActive()) ApplyDraft();

        _renderer.SetCurrentContext();
        _renderer.UpdateInput(_window);
        ApplyDiscordCapture();
        _hotkeyRowsShown = false;
        if (!_window.IsMinimized) DrawMain(deltaTime, now);
        PollKeyCapture();
        // ImGui can keep a field active after the game takes focus, so the window focus is checked too.
        _scheduler.HotkeysPaused = _settings.PauseHotkeysWhileTyping && _window.IsFocused && ImGui.GetIO().WantTextInput;
        UpdateHotkeyGate();
        _auxiliary.Render(deltaTime, _settings.Theme, _settings.FontSizePx, null);
        RenderOverlays(deltaTime, now);
        PresentDiscordCapture();
        _renderer.SetCurrentContext();
        if (_minimizeRequested) { _window.WindowState = WindowState.Minimized; _minimizeRequested = false; }
        SaveSettings();
    }

    // ---- Encounter files ----

    private void RefreshDataFiles() => _dataFiles = EncounterYaml.ListDataFiles();

    private static string ResolveEncounterPath(string path) =>
        Path.IsPathFullyQualified(path) ? path : Path.Combine(EncounterYaml.DataDirectory, path);

    // Files inside ./data are remembered by name, so the folder can move together with the exe.
    private static string RememberedPath(string path)
    {
        string data = Path.GetFullPath(EncounterYaml.DataDirectory) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(path);
        return full.StartsWith(data, StringComparison.OrdinalIgnoreCase) ? full[data.Length..] : full;
    }

    // The last loaded file is remembered globally and by the active window hook.
    private void RememberEncounter(string path)
    {
        string remembered = RememberedPath(path);
        _settings = _settings with { EncounterPath = remembered };
        if (_hooks.ActiveHook is { } hook && hook.EncounterPath != remembered)
            UpdateHook(_hooks.Active, hook with { EncounterPath = remembered });
    }

    private void LoadEncounter(string path)
    {
        var result = EncounterYaml.Load(path);
        _issues = result.Issues;
        _encounterPath = path;
        RememberEncounter(path);
        _sounds.Clear();
        _icons.Clear();
        _encounter = result.Encounter;
        _outdated = result.NeedsUpgrade ? (result.Version, result.Tagged) : null;
        _savedRevision = _revision = ++_nextRevision;
        _history.Clear();
        _selectedTimer = -1;
        ClearDisabledTimers();
        ApplyDraft();
        AppLog.Info("Encounter", $"Loaded {Path.GetFileName(path)}: {_encounter?.Timers.Count ?? 0} timer(s), " +
            $"{_issues.Count(i => i.IsError)} error(s), {_issues.Count(i => !i.IsError)} warning(s).");
    }

    // Pushes the working copy to the scheduler (which clears running timers) and prepares its sounds.
    private void ApplyDraft()
    {
        _pendingApply = false;
        _scheduler.Load(_encounter);
        if (_encounter is null) return;
        _sounds.Warm(_encounter);
        var text = _encounter.Timers.SelectMany(t => new[] { t.DisplayName, t.AlertText }).Append(_encounter.Name).ToArray();
        // Providers and notes only appear in the main window, so the large alert font does not carry them.
        CjkGlyphs.SetExtraText(text.Concat(_encounter.Timers.SelectMany(t => new[] { t.Provider ?? "", t.Note ?? "" })));
        _alertGlyphs = CjkGlyphs.RangesFor(text.Concat(OverlayPlaceholders()));
    }

    private static IEnumerable<string> OverlayPlaceholders() =>
        [Localization.T("Timeline (drag to move)"), Localization.T("Alerts (drag to move)"), "0123456789.:"];

    private void Edit(Encounter encounter)
    {
        uint item = igGetActiveID();
        if (item == 0 || item != _historyItem) _history.Push(new EditState(_encounter, _selectedTimer, _revision));
        _historyItem = item;
        _encounter = encounter;
        _revision = ++_nextRevision;
        _pendingApply = true;
    }

    private readonly record struct EditState(Encounter? Encounter, int SelectedTimer, int Revision);

    private void Undo(bool redo)
    {
        var current = new EditState(_encounter, _selectedTimer, _revision);
        if (!(redo ? _history.TryRedo(current, out var state) : _history.TryUndo(current, out state))) return;
        _historyItem = 0;
        _encounter = state.Encounter;
        _revision = state.Revision;
        _selectedTimer = _encounter is null ? -1 : Math.Min(state.SelectedTimer, _encounter.Timers.Count - 1);
        // A restored sound or icon path must be read again rather than served from the failed-load cache.
        _sounds.Clear();
        _icons.Clear();
        _pendingApply = true;
    }

    // Replaces the working copy with the file on disk as one undoable step.
    private void DiscardChanges()
    {
        if (_encounterPath is null) return;
        var result = EncounterYaml.Load(_encounterPath);
        if (result.Encounter is null)
        {
            _notice = string.Join("\n", result.Issues.Select(issue => issue.ToString()));
            return;
        }
        _historyItem = 0;
        Edit(result.Encounter);
        _historyItem = 0;
        _savedRevision = _revision;
        _issues = result.Issues;
        _outdated = result.NeedsUpgrade ? (result.Version, result.Tagged) : null;
        _selectedTimer = Math.Min(_selectedTimer, _encounter!.Timers.Count - 1);
        _sounds.Clear();
        _icons.Clear();
        AppLog.Info("Encounter", $"Discarded changes to {Path.GetFileName(_encounterPath)}.");
    }

    // ImGui's internal active-widget ID (0 when none); cimgui exports it, ImGui.NET does not wrap it.
    [System.Runtime.InteropServices.DllImport("cimgui", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
    private static extern uint igGetActiveID();

    private void EditTimer(int index, TimerDefinition timer)
    {
        if (_encounter is null) return;
        var timers = _encounter.Timers.ToList();
        timers[index] = timer;
        Edit(_encounter with { Timers = timers });
    }

    private void Save(string path)
    {
        if (_encounter is null) return;
        // A pack is saved by rewriting its zip.
        if (_encounter.Pack is not null)
        {
            WritePack(path);
            return;
        }
        try
        {
            // Relative sfx/icon paths stay relative to the file's own folder.
            string folder = Path.GetDirectoryName(Path.GetFullPath(path)) ?? "";
            string? backup = null;
            if (!string.Equals(Path.GetFullPath(folder), Path.GetFullPath(_encounter.BaseDirectory), StringComparison.OrdinalIgnoreCase))
                _encounter = Rebase(_encounter, folder);
            // Overwriting a file in an older format keeps the original (with its comments) as a backup.
            if (_outdated is { } outdated && _encounterPath is not null &&
                string.Equals(Path.GetFullPath(path), Path.GetFullPath(_encounterPath), StringComparison.OrdinalIgnoreCase))
                backup = EncounterYaml.Upgrade(_encounter, path, outdated.Version);
            else EncounterYaml.Save(_encounter, path);
            _outdated = null;
            _encounterPath = path;
            RememberEncounter(path);
            _savedRevision = _revision;
            // Further typing in the same field after Ctrl+S starts a new undo step.
            _historyItem = 0;
            if (_saveError.Length > 0 && _notice == _saveError) _notice = "";
            _saveError = "";
            _toast = (backup is null ? Localization.F("Saved {0}.", Path.GetFileName(path))
                : Localization.F("Upgraded {0}; the original is kept as {1}.", Path.GetFileName(path), Path.GetFileName(backup)), Stopwatch.GetTimestamp());
            AppLog.Info("Encounter", backup is null ? $"Saved {Path.GetFileName(path)}."
                : $"Upgraded {Path.GetFileName(path)} to format {EncounterYaml.FormatVersion}; backup {Path.GetFileName(backup)}.");
            RefreshDataFiles();
            _issues = EncounterYaml.Parse(EncounterYaml.Serialize(_encounter), folder).Issues;
            _pendingApply = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _notice = _saveError = Localization.F("Could not save {0}: {1}", Path.GetFileName(path), ex.Message);
            AppLog.Error("Encounter", "Could not save encounter", ex);
        }
    }

    private static Encounter Rebase(Encounter encounter, string folder)
    {
        string? Move(string? relative) => relative is null || Lucide.IsIcon(relative) ? relative
            : Path.GetRelativePath(folder, Path.GetFullPath(Path.Combine(encounter.BaseDirectory, relative)));
        return encounter with
        {
            BaseDirectory = folder,
            Timers = encounter.Timers.Select(t => t with
            {
                Icon = Move(t.Icon),
                Sounds = new(t.Sounds.Select(sound => sound.Sfx is null ? sound : sound with { Sfx = Move(sound.Sfx) }))
            }).ToList()
        };
    }

    // Sets the icon (sound = -1) or the file of Sounds[sound] of a timer in encounter, as one edit.
    private void SetAsset(Encounter encounter, int timerIndex, int sound, string value)
    {
        if (timerIndex < 0 || timerIndex >= encounter.Timers.Count) return;
        var timer = encounter.Timers[timerIndex];
        if (sound < 0) timer = timer with { Icon = value };
        else if (sound < timer.Sounds.Count)
            timer = timer with { Sounds = timer.Sounds.SetItem(sound, new TimerSound { Sfx = value, Offset = timer.Sounds[sound].Offset }) };
        var timers = encounter.Timers.ToList();
        timers[timerIndex] = timer;
        Edit(encounter with { Timers = timers });
        // A new sound or icon path must be read again rather than served from the failed-load cache.
        _sounds.Clear();
        _icons.Clear();
    }

    private void PollTasks()
    {
        if (_openPicker?.IsCompleted == true)
        {
            if (Result(_openPicker) is { } path) LoadEncounter(path);
            _openPicker = null;
        }
        if (_savePicker?.IsCompleted == true)
        {
            if (Result(_savePicker) is { } path) Save(path);
            _savePicker = null;
        }
        if (_packPicker?.IsCompleted == true)
        {
            if (Result(_packPicker) is { } path) WritePack(path);
            _packPicker = null;
        }
        if (_assetPicker is { Task.IsCompleted: true } asset)
        {
            if (Result(asset.Task) is { } path && _encounter is not null)
                SetAsset(_encounter, asset.Timer, asset.Sound, Path.GetRelativePath(_encounter.BaseDirectory, path));
            _assetPicker = null;
        }
        if (_packFilePicker?.IsCompleted == true)
        {
            if (Result(_packFilePicker) is { } path && _encounter is { Pack: not null } && _packFiles is { Timer: >= 0 } target)
            {
                try
                {
                    var (added, entry) = EncounterYaml.AddPackFile(_encounter, target.Folder, path);
                    SetAsset(added, target.Timer, target.Sound, entry);
                    _packFiles = null;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Security.SecurityException)
                {
                    _notice = Localization.F("Could not add {0} to the pack: {1}", Path.GetFileName(path), ex.Message);
                    AppLog.Error("Encounter", "Could not add a file to the pack", ex);
                }
            }
            _packFilePicker = null;
        }
        long now = Stopwatch.GetTimestamp();
        for (int i = 0; i < _previews.Count; i++)
        {
            var (task, at) = _previews[i];
            if (!task.IsCompleted || (task.IsCompletedSuccessfully && now < at)) continue;
            if (task.IsCompletedSuccessfully) _audio.Play(task.Result);
            else _notice = Localization.F("Could not play sound: {0}", task.Exception?.GetBaseException().Message);
            _previews.RemoveAt(i--);
        }

        string? Result(Task<string?> task)
        {
            try { return task.GetAwaiter().GetResult(); }
            catch (Exception ex)
            {
                _notice = ex.Message;
                AppLog.Error("UI", "File dialog failed", ex);
                return null;
            }
        }
    }

    // ---- Main window ----

    private void DrawMain(float deltaTime, long now)
    {
        if (_fontSizeApplied != _settings.FontSizePx || _renderer.GlyphsStale)
        {
            _renderer.UpdateFont(_settings.FontSizePx);
            _fontSizeApplied = _settings.FontSizePx;
        }
        if (!_window.BeginRender(BackgroundColor)) return;
        _renderer.BeginFrame(_window, deltaTime, updateInput: false);
        if (!ImGui.IsAnyItemActive()) _historyItem = 0;
        ImGui.SetNextWindowPos(Vector2.Zero);
        ImGui.SetNextWindowSize(ImGui.GetIO().DisplaySize);
        // NoBringToFrontOnFocus keeps the save toast above this full-window background.
        if (ImGui.Begin($"TipAura {BuildInfo.Version}###Main", ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBringToFrontOnFocus))
        {
            MoveByTitleBar();
            if (_notice.Length > 0)
            {
                WrappedText(_notice);
                ImGui.SameLine();
                if (ImGui.SmallButton(Localization.T("Dismiss"))) _notice = "";
            }
            if (AppLog.Warning is { } logWarning) ColoredText(WarningColor, Localization.Status(logWarning));
            if (ImGui.BeginTabBar("MainTabs"))
            {
                if (BeginTab(Localization.T("Encounter") + "###EncounterTab", 0))
                {
                    DrawEncounterTab(now);
                    ImGui.EndTabItem();
                }
                if (BeginTab(Localization.T("Timers") + (IsDirty ? " *" : "") + "###TimersTab", 1))
                {
                    DrawEditorTab();
                    ImGui.EndTabItem();
                }
                if (BeginTab(Localization.T("Settings") + "###SettingsTab", 2))
                {
                    DrawSettingsTab();
                    ImGui.EndTabItem();
                }
                ImGui.EndTabBar();
                _tabRequest = -1;
            }
            DrawPackFiles();
            DrawExtractPrompt();
        }
        ImGui.End();
        HandleShortcuts();
        DrawToast(now);
        DragWindow();
        _renderer.EndFrame(_window.FramebufferSize.X, _window.FramebufferSize.Y);
        _window.Present();
    }

    // ImGui.NET only exposes tab flags together with p_open, which would add a close button.
    private unsafe bool BeginTab(string label, int index)
    {
        byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(label + "\0");
        fixed (byte* text = utf8)
            return ImGuiNative.igBeginTabItem(text, null,
                _tabRequest == index ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None) != 0;
    }

    // Ctrl+S saves on every tab, even while typing (each keystroke is already in the working copy).
    // Ctrl+Z/Ctrl+Y belong to the text field while one is active, so the editor history only gets them otherwise.
    private void HandleShortcuts()
    {
        var io = ImGui.GetIO();
        if (!io.KeyCtrl || io.KeyAlt || ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId)) return;
        if (ImGui.IsKeyPressed(ImGuiKey.S, false)) SaveCurrent();
        if (io.WantTextInput) return;
        if (ImGui.IsKeyPressed(ImGuiKey.Z)) Undo(redo: io.KeyShift);
        else if (ImGui.IsKeyPressed(ImGuiKey.Y)) Undo(redo: true);
    }

    private void SaveCurrent()
    {
        if (_encounter is null) return;
        if (_encounterPath is not null) Save(_encounterPath);
        else SaveAs();
    }

    private void SaveAs()
    {
        if (_encounter is null || _savePicker is not null) return;
        Directory.CreateDirectory(EncounterYaml.DataDirectory);
        bool loaded = _encounterPath is not null;
        _savePicker = FilePicker.SaveAsync(Localization.T("Encounter files"), "*.yaml;*.yml",
            loaded ? Path.GetDirectoryName(_encounterPath) : EncounterYaml.DataDirectory,
            loaded ? Path.GetFileName(_encounterPath) : (_encounter.Id ?? _encounter.Name) + EncounterYaml.Extension);
    }

    // Extracts the loaded pack (with unsaved edits) into data/ as <pack>.yaml and prefixed sound and icon
    // files. Files that already exist with other contents are asked about one by one (DrawExtractPrompt).
    // dataDirectory overrides data/ (used by the UI smoke test).
    private void StartExtraction(string? dataDirectory = null)
    {
        if (_encounter?.Pack is not { } pack || _extraction is not null) return;
        try { _extraction = EncounterYaml.PlanExtraction(_encounter, dataDirectory); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Security.SecurityException)
        {
            _notice = Localization.F("Could not extract {0}: {1}", Path.GetFileName(pack.FullPath), ex.Message);
            AppLog.Error("Encounter", "Could not extract pack", ex);
            return;
        }
        _extractConflicts = [.. _extraction.Files.Where(file => file.Conflict)];
        _extractOverwrite.Clear();
        _extractNext = 0;
        if (_extractConflicts.Count == 0) FinishExtraction();
    }

    // Writes the planned files; once the YAML in data/ is the extracted one, it becomes the loaded file.
    private void FinishExtraction()
    {
        if (_extraction is not { } plan) return;
        _extraction = null;
        string name = Path.GetFileName(plan.YamlPath);
        try
        {
            var (written, yamlReady) = EncounterYaml.ApplyExtraction(plan, _extractOverwrite);
            int skipped = _extractConflicts.Count - _extractOverwrite.Count;
            AppLog.Info("Encounter", $"Extracted pack to {name}: {written} file(s) written, {skipped} kept.");
            RefreshDataFiles();
            if (yamlReady) LoadEncounter(plan.YamlPath);
            _toast = (yamlReady ? Localization.F("Extracted {0}: {1} file(s) written, {2} kept.", name, written, skipped)
                : Localization.F("Extracted {1} file(s); {0} was kept, so the pack stays open.", name, written), Stopwatch.GetTimestamp());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _notice = Localization.F("Could not extract {0}: {1}", name, ex.Message);
            AppLog.Error("Encounter", "Could not extract pack", ex);
        }
    }

    private void DrawExtractPrompt()
    {
        const string Popup = "###ExtractConflict";
        if (_extraction is null && !ImGui.IsPopupOpen(Popup)) return;
        if (_extraction is not null && !ImGui.IsPopupOpen(Popup)) ImGui.OpenPopup(Popup);
        if (!ImGui.BeginPopupModal(Localization.T("File already exists") + Popup, ImGuiWindowFlags.AlwaysAutoResize)) return;
        if (_extraction is null || _extractNext >= _extractConflicts.Count)
        {
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }
        var file = _extractConflicts[_extractNext];
        ImGui.TextUnformatted(Localization.F("data/{0} already exists with different contents.", file.Label));
        ImGui.TextDisabled(Localization.F("Conflict {0} of {1}", _extractNext + 1, _extractConflicts.Count));
        if (ImGui.Button(Localization.T("Overwrite")))
        {
            _extractOverwrite.Add(file.Label);
            _extractNext++;
        }
        ImGui.SameLine();
        if (ImGui.Button(Localization.T("Keep existing"))) _extractNext++;
        ImGui.SameLine();
        if (ImGui.Button(Localization.T("Overwrite all")))
        {
            foreach (var other in _extractConflicts.Skip(_extractNext)) _extractOverwrite.Add(other.Label);
            _extractNext = _extractConflicts.Count;
        }
        ImGui.SameLine();
        if (ImGui.Button(Localization.T("Keep all"))) _extractNext = _extractConflicts.Count;
        ImGui.SameLine();
        if (RedButton(Localization.T("Cancel extraction")))
        {
            _extraction = null;
            AppLog.Info("Encounter", "Pack extraction cancelled.");
        }
        if (_extraction is not null && _extractNext >= _extractConflicts.Count) FinishExtraction();
        if (_extraction is null) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private void ExportPack()
    {
        if (_encounter?.Id is not { } id || _packPicker is not null) return;
        string folder = Path.Combine(EncounterYaml.DataDirectory, EncounterPack.DirectoryName);
        Directory.CreateDirectory(folder);
        _packPicker = FilePicker.SaveAsync(Localization.T("Encounter packs"), "*.zip", folder, id + EncounterPack.Extension);
    }

    // The working copy stays as it is, except that writing over the loaded pack reloads it from the new zip.
    private void WritePack(string path)
    {
        if (_encounter is null) return;
        try
        {
            var (assets, trimmed) = EncounterYaml.WritePack(_encounter, path);
            AppLog.Info("Encounter", $"Exported pack {Path.GetFileName(path)} with {assets} asset file(s), {trimmed} unused file(s) left out.");
            bool saved = _encounter.Pack is { } pack && string.Equals(pack.FullPath, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
            if (saved) LoadEncounter(path);
            RefreshDataFiles();
            string message = saved ? Localization.F("Saved pack {0} with {1} asset file(s).", Path.GetFileName(path), assets)
                : Localization.F("Exported pack {0} with {1} asset file(s).", Path.GetFileName(path), assets);
            if (trimmed > 0) message += " " + Localization.F("{0} unused file(s) removed.", trimmed);
            _toast = (message, Stopwatch.GetTimestamp());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Security.SecurityException)
        {
            _notice = Localization.F("Could not export {0}: {1}", Path.GetFileName(path), ex.Message);
            AppLog.Error("Encounter", "Could not export pack", ex);
        }
    }

    // The "Saved" message: bottom-right inside the main window for ToastSeconds, fading over the last half second.
    private const double ToastSeconds = 5;

    private void DrawToast(long now)
    {
        if (_toast is not { } toast) return;
        double age = (now - toast.Shown) / (double)Stopwatch.Frequency;
        if (age >= ToastSeconds) { _toast = null; return; }
        var style = ImGui.GetStyle();
        ImGui.SetNextWindowPos(ImGui.GetIO().DisplaySize - style.WindowPadding * 2, ImGuiCond.Always, Vector2.One);
        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, (float)Math.Clamp((ToastSeconds - age) / 0.5, 0, 1));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, ImGui.GetColorU32(ImGuiCol.PopupBg));
        if (ImGui.Begin("##SavedToast", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoInputs |
            ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoMove))
        {
            // Not wrapped: wrapping at the window edge would keep an auto-resizing window narrow.
            ImGui.PushStyleColor(ImGuiCol.Text, OkColor);
            ImGui.TextUnformatted(toast.Text);
            ImGui.PopStyleColor();
        }
        ImGui.End();
        ImGui.PopStyleColor();
        ImGui.PopStyleVar();
    }

    private void DrawEncounterTab(long now)
    {
        ImGui.SeparatorText(Localization.T("Window hook"));
        ImGui.SetNextItemWidth(Math.Max(160, ImGui.GetContentRegionAvail().X / 2));
        if (ImGui.BeginCombo("##Hook", _hooks.ActiveHook is { } active ? HookLabel(active) : Localization.T("(none)")))
        {
            if (ImGui.Selectable(Localization.T("(none)"), _hooks.Active < 0)) SelectHook(-1);
            for (int i = 0; i < _hooks.Hooks.Count; i++)
            {
                int index = i;
                if (ImGui.Selectable($"{HookLabel(_hooks.Hooks[i])}##Hook{i}", i == _hooks.Active) && i != _hooks.Active)
                    ConfirmDiscard(() => SelectHook(index));
            }
            if (_hooks.Hooks.Count == 0) ImGui.TextDisabled(Localization.T("Add window hooks on the Settings tab."));
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        DrawHookStatus();

        ImGui.SeparatorText(Localization.T("Encounter file"));
        string current = _encounterPath is null ? Localization.T("(none)")
            : _dataFiles.FirstOrDefault(f => string.Equals(f.Path, _encounterPath, StringComparison.OrdinalIgnoreCase))?.Label ?? Path.GetFileName(_encounterPath);
        ImGui.SetNextItemWidth(Math.Max(160, ImGui.GetContentRegionAvail().X - ButtonsWidth(3)));
        if (ImGui.BeginCombo("##EncounterFile", current))
        {
            foreach (var file in _dataFiles)
                if (ImGui.Selectable(file.Label, string.Equals(file.Path, _encounterPath, StringComparison.OrdinalIgnoreCase)))
                    ConfirmDiscard(() => LoadEncounter(file.Path));
            if (_dataFiles.Count == 0) ImGui.TextDisabled(Localization.F("No .yaml files in {0}", EncounterYaml.DataDirectory));
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered()) Tooltip(EncounterYaml.DataDirectory);
        ImGui.SameLine();
        if (ImGui.Button(Localization.T("Refresh"))) RefreshDataFiles();
        ImGui.SameLine();
        if (ImGui.Button(Localization.T("Reload")) && _encounterPath is not null) ConfirmDiscard(() => LoadEncounter(_encounterPath));
        ImGui.SameLine();
        ImGui.BeginDisabled(_openPicker is not null);
        if (ImGui.Button(Localization.T("Open...")))
            ConfirmDiscard(() => _openPicker = FilePicker.OpenAsync(Localization.T("Encounter files"), "*.yaml;*.yml;*.zip",
                Directory.Exists(EncounterYaml.DataDirectory) ? EncounterYaml.DataDirectory : null));
        ImGui.EndDisabled();
        DrawDiscardPrompt();
        if (_encounter is { } encounter)
        {
            ImGui.TextUnformatted(Localization.F("{0} - {1} timer(s)", encounter.Name, encounter.Timers.Count));
            var (ready, pending, failed) = _sounds.Status(encounter);
            ImGui.SameLine();
            if (failed > 0) ColoredText(ErrorColor, Localization.F("Sounds: {0} ready, {1} preparing, {2} failed", ready, pending, failed));
            else if (pending > 0) ColoredText(WarningColor, Localization.F("Sounds: {0} ready, {1} preparing", ready, pending));
            else ColoredText(OkColor, Localization.F("Sounds: {0} ready", ready));
        }
        DrawUpgradeNotice();
        DrawIssues();

        ImGui.SeparatorText(Localization.T("Running timers"));
        if (ImGui.Button($"{Localization.T("Reset encounter")} ({ResetAllHotkey})###ResetEncounter")) _scheduler.ResetAll();
        ImGui.SameLine();
        ImGui.TextDisabled(Localization.T("Test:"));
        for (int slot = 1; slot <= 9; slot++)
        {
            ImGui.SameLine();
            bool used = _encounter?.Timers.Any(t => t.Keys.Contains(slot) && !_disabledTimers.Contains(t.Id)) == true;
            ImGui.BeginDisabled(!used);
            if (ImGui.SmallButton($"{slot}##Start{slot}")) _scheduler.Start(slot);
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) Tooltip(Localization.F("Start the {0} timers", KeyLabel(slot)));
        }
        var running = _scheduler.Snapshot;
        if (running.Length == 0) ImGui.TextDisabled(Localization.T("No running timers. Press a key's hotkey to start."));
        else if (ImGui.BeginTable("Running", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn(Localization.T("Key"), ImGuiTableColumnFlags.WidthFixed);
            ImGui.TableSetupColumn(Localization.T("Name"), ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn(Localization.T("Remaining"), ImGuiTableColumnFlags.WidthFixed);
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed);
            ImGui.TableHeadersRow();
            foreach (var timer in running)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(string.Join(", ", timer.Timer.Keys.Slots.Select(KeyLabel)));
                ImGui.TableNextColumn();
                double remaining = timer.Remaining(now, Stopwatch.Frequency);
                ImGui.ProgressBar((float)(remaining / timer.Timer.Duration), new Vector2(-1, 0), timer.Timer.DisplayName);
                ImGui.TableNextColumn();
                if (timer.Alerted) ColoredText(WarningColor, OverlayViews.FormatRemaining(remaining));
                else ImGui.TextUnformatted(OverlayViews.FormatRemaining(remaining));
                ImGui.TableNextColumn();
                if (ImGui.SmallButton(Localization.T("Reset") + $"##Instance{timer.InstanceId}")) _scheduler.ResetInstance(timer.InstanceId);
            }
            ImGui.EndTable();
        }
    }

    // ---- Window hooks ----

    private static string HookLabel(WindowHook hook) =>
        hook.Name.Trim().Length > 0 ? hook.Name : hook.Pattern.Trim().Length > 0 ? hook.Pattern : Localization.T("(unnamed)");

    private void DrawHookStatus()
    {
        if (_hooks.ActiveHook is null) ImGui.TextDisabled(Localization.T("Floating windows follow the primary screen."));
        else if (_overlayBounds is null) ColoredText(WarningColor, Localization.T("Window not found; floating windows are hidden."));
        else ColoredText(OkColor, Localization.T("Window found; floating windows follow it."));
    }

    // Activates a hook (-1 for none) and loads the encounter file it remembers; a hook without one
    // remembers the current file.
    private void SelectHook(int index)
    {
        _hooks = _hooks with { Active = index };
        _hooksDirty = true;
        _hookWindow = 0;
        _lastHookSearch = 0;
        if (_hooks.ActiveHook is not { } hook) return;
        AppLog.Info("Hooks", $"Selected window hook '{HookLabel(hook)}'.");
        if (hook.EncounterPath is null)
        {
            if (_encounterPath is not null) RememberEncounter(_encounterPath);
            return;
        }
        string path = ResolveEncounterPath(hook.EncounterPath);
        if (!File.Exists(path)) _notice = Localization.F("The hooked encounter file was not found: {0}", hook.EncounterPath);
        else if (_encounterPath is null || !string.Equals(Path.GetFullPath(path), Path.GetFullPath(_encounterPath), StringComparison.OrdinalIgnoreCase))
            LoadEncounter(path);
    }

    private void UpdateHook(int index, WindowHook hook)
    {
        var hooks = _hooks.Hooks.ToList();
        hooks[index] = hook;
        _hooks = _hooks with { Hooks = hooks };
        _hooksDirty = true;
        if (index == _hooks.Active) _lastHookSearch = 0;
    }

    private void AddHook(WindowHook hook)
    {
        if (_hooks.Hooks.Count >= WindowHookSettings.MaxHooks) return;
        _hooks = _hooks with { Hooks = [.. _hooks.Hooks, hook] };
        _hooksDirty = true;
    }

    private void RemoveHook(int index)
    {
        var hooks = _hooks.Hooks.ToList();
        hooks.RemoveAt(index);
        int active = _hooks.Active == index ? -1 : _hooks.Active > index ? _hooks.Active - 1 : _hooks.Active;
        if (_hooks.Active == index) { _hookWindow = 0; _lastHookSearch = 0; }
        _hooks = new WindowHookSettings { Hooks = hooks, Active = active };
        _hooksDirty = true;
    }

    private void UpdateHotkeyGate()
    {
        var hook = _hooks.ActiveHook;
        var gate = _scheduler.Gate;
        if (!_settings.HotkeysRequireHookFocus) { if (gate is not null) _scheduler.Gate = null; }
        else if (gate is null || !ReferenceEquals(gate.Hook, hook)) _scheduler.Gate = new HotkeyGate(hook);
    }

    // The anchors' reference: the hooked window's client area, the primary work area without a hook, or
    // null while the hooked window is closed or minimized. The window is searched again once a second.
    private void UpdateOverlayBounds()
    {
        if (_hooks.ActiveHook is not { } hook)
        {
            _hookWindow = 0;
            _overlayBounds = PrimaryWorkArea();
            return;
        }
        if (_lastHookSearch == 0 || Stopwatch.GetElapsedTime(_lastHookSearch).TotalSeconds >= 1)
        {
            _lastHookSearch = Stopwatch.GetTimestamp();
            if (_hookWindow == 0 || !WindowFinder.Matches(hook, _hookWindow) || Native.IsIconic(_hookWindow))
            {
                nint found = WindowFinder.Find(hook);
                if (found != _hookWindow)
                    AppLog.Info("Hooks", found == 0 ? $"Window for '{HookLabel(hook)}' not found." : $"Window for '{HookLabel(hook)}' found.");
                _hookWindow = found;
            }
        }
        _overlayBounds = WindowFinder.ClientBounds(_hookWindow);
    }

    private PixelRect PrimaryWorkArea() => _workAreas.Count > 0 ? _workAreas[0] : new PixelRect(0, 0, 1920, 1080);

    private void DrawHookSettings()
    {
        ImGui.SeparatorText(Localization.T("Window hooks"));
        if (_hooks.Hooks.Count == 0)
            ImGui.TextDisabled(Localization.T("No window hooks. A hook anchors the floating windows to a game window and remembers its encounter file."));
        else if (ImGui.BeginTable("Hooks", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn(Localization.T("Name"), ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableSetupColumn(Localization.T("Match by"), ImGuiTableColumnFlags.WidthFixed, ImGui.GetFontSize() * 7);
            ImGui.TableSetupColumn(Localization.T("Process name / title"), ImGuiTableColumnFlags.WidthStretch, 1.2f);
            ImGui.TableSetupColumn(Localization.T("Encounter file"), ImGuiTableColumnFlags.WidthStretch, 1.2f);
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed);
            ImGui.TableHeadersRow();
            string[] kinds = [Localization.T("Process name"), Localization.T("Window title")];
            for (int i = 0; i < _hooks.Hooks.Count; i++)
            {
                var hook = _hooks.Hooks[i];
                ImGui.PushID(i);
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(-1);
                string name = hook.Name;
                if (ImGui.InputText("##Name", ref name, 128)) UpdateHook(i, hook with { Name = name });
                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(-1);
                int kind = (int)hook.Match;
                if (ImGui.Combo("##Match", ref kind, kinds, kinds.Length)) UpdateHook(i, hook with { Match = (WindowMatchKind)kind });
                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(-1);
                string pattern = hook.Pattern;
                if (ImGui.InputText("##Pattern", ref pattern, 256)) UpdateHook(i, hook with { Pattern = pattern });
                if (ImGui.IsItemHovered())
                    Tooltip(Localization.T("Process name: the executable name, with or without .exe. Window title: any part of the title. Case is ignored."));
                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(-1);
                if (ImGui.BeginCombo("##File", hook.EncounterPath ?? Localization.T("(none)")))
                {
                    if (ImGui.Selectable(Localization.T("(none)"), hook.EncounterPath is null)) UpdateHook(i, hook with { EncounterPath = null });
                    foreach (var file in _dataFiles)
                    {
                        string remembered = RememberedPath(file.Path);
                        if (ImGui.Selectable(file.Label, string.Equals(remembered, hook.EncounterPath, StringComparison.OrdinalIgnoreCase)))
                            UpdateHook(i, hook with { EncounterPath = remembered });
                    }
                    ImGui.EndCombo();
                }
                if (ImGui.IsItemHovered())
                    Tooltip(Localization.T("Loaded when this hook is selected on the Encounter tab. Loading another file while the hook is selected replaces it."));
                ImGui.TableNextColumn();
                bool removed = ImGui.SmallButton(Localization.T("Remove"));
                ImGui.PopID();
                if (removed) { RemoveHook(i); break; }
            }
            ImGui.EndTable();
        }
        ImGui.BeginDisabled(_hooks.Hooks.Count >= WindowHookSettings.MaxHooks);
        if (ImGui.Button(Localization.T("Add hook"))) AddHook(new WindowHook());
        ImGui.SameLine();
        ImGui.SetNextItemWidth(Math.Max(200, ImGui.GetContentRegionAvail().X));
        if (ImGui.BeginCombo("##Running", Localization.T("Add from running window..."), ImGuiComboFlags.HeightLarge))
        {
            if (ImGui.IsWindowAppearing()) _runningWindows = WindowFinder.List();
            foreach (var window in _runningWindows)
                if (ImGui.Selectable($"{window.ProcessName}.exe - {window.Title}##{window.Hwnd}"))
                    AddHook(new WindowHook { Name = window.ProcessName, Pattern = window.ProcessName + ".exe" });
            if (_runningWindows.Count == 0) ImGui.TextDisabled(Localization.T("No windows found."));
            ImGui.EndCombo();
        }
        ImGui.EndDisabled();
        ImGui.TextDisabled(Localization.T("Select the active hook on the Encounter tab."));
        ImGui.SameLine();
        DrawHookStatus();
    }

    private void DrawUpgradeNotice()
    {
        // A pack has no upgrade backup: saving it rewrites the zip in the current format.
        if (_outdated is not { } outdated || _encounterPath is null || _encounter is null || _encounter.Pack is not null) return;
        ColoredText(WarningColor, outdated.Tagged || outdated.Version < EncounterYaml.FormatVersion
            ? Localization.F("This file uses the older format version {0}.", outdated.Version)
            : Localization.T("This file does not declare its format version."));
        if (ImGui.Button(Localization.T("Upgrade"))) Save(_encounterPath);
        if (ImGui.IsItemHovered())
            Tooltip(Localization.F("Copies the file to {0}.v{1}.bak first (an unused name), then saves it in format version {2}. Comments are kept only in the copy.",
                Path.GetFileName(_encounterPath), outdated.Version, EncounterYaml.FormatVersion));
    }

    private void DrawIssues()
    {
        if (_issues.Count == 0) return;
        int errors = _issues.Count(i => i.IsError);
        string header = Localization.F("{0} error(s), {1} warning(s)", errors, _issues.Count - errors);
        ImGui.PushStyleColor(ImGuiCol.Text, errors > 0 ? ErrorColor : WarningColor);
        bool open = ImGui.TreeNodeEx(header + "###Issues", errors > 0 ? ImGuiTreeNodeFlags.DefaultOpen : 0);
        ImGui.PopStyleColor();
        if (!open) return;
        foreach (var issue in _issues)
            ColoredText(issue.IsError ? ErrorColor : WarningColor, issue.ToString());
        ImGui.TreePop();
    }

    private Action? _pendingDiscard;

    // Callers may sit inside a combo popup, so the modal is opened later from DrawDiscardPrompt's ID scope.
    private void ConfirmDiscard(Action action)
    {
        if (!IsDirty) action();
        else _pendingDiscard = action;
    }

    private void DrawDiscardPrompt()
    {
        if (_pendingDiscard is not null && !ImGui.IsPopupOpen("DiscardChanges")) ImGui.OpenPopup("DiscardChanges");
        if (!ImGui.BeginPopupModal("DiscardChanges", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoTitleBar)) return;
        ImGui.TextUnformatted(Localization.T("Discard unsaved timer changes?"));
        if (ImGui.Button(Localization.T("Discard")))
        {
            _savedRevision = _revision;
            _pendingDiscard?.Invoke();
            _pendingDiscard = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button(Localization.T("Cancel")))
        {
            _pendingDiscard = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    private void DrawEditorTab()
    {
        if (_encounter is not { } encounter)
        {
            ImGui.TextDisabled(Localization.T("Load an encounter file first."));
            if (ImGui.Button(Localization.T("New encounter")))
            {
                _encounterPath = null;
                _outdated = null;
                _issues = [];
                ClearDisabledTimers();
                Edit(new Encounter { Name = Localization.T("New encounter"), BaseDirectory = EncounterYaml.DataDirectory });
                _history.Clear();
            }
            return;
        }
        bool pack = encounter.Pack is not null;
        ImGui.BeginDisabled(_encounterPath is null || !IsDirty);
        if (ImGui.Button(Localization.T(pack ? "Save pack" : "Save YAML")) && _encounterPath is not null) Save(_encounterPath);
        ImGui.EndDisabled();
        if (pack && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            Tooltip(Localization.T("Rewrite the zip with index.yaml and the files the timers use; unused files are removed."));
        ImGui.SameLine();
        if (pack)
        {
            ImGui.BeginDisabled(_extraction is not null);
            if (ImGui.Button(Localization.T("Extract to data"))) StartExtraction();
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                Tooltip(Localization.F("Write {0}.yaml into data/, and the sounds and icons it uses into data/sfx and data/icons with the prefix {0}_. Existing files with other contents are asked about one by one.",
                    encounter.Pack!.FileId));
        }
        else
        {
            ImGui.BeginDisabled(_savePicker is not null);
            if (ImGui.Button(Localization.T("Save as..."))) SaveAs();
            ImGui.EndDisabled();
        }
        ImGui.SameLine();
        ImGui.BeginDisabled(_packPicker is not null || encounter.Id is null);
        if (ImGui.Button(Localization.T("Export pack..."))) ExportPack();
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            Tooltip(encounter.Id is null ? Localization.T("Set an id first; the pack is named <id>.zip.")
                : Localization.T("Write <id>.zip with index.yaml and the sounds and icons it uses, by default into data/pack."));
        if (pack)
        {
            ImGui.SameLine();
            if (ImGui.Button(Localization.T("Pack files..."))) _packFiles = (EncounterPack.SoundFolder, -1, -1);
            if (ImGui.IsItemHovered()) Tooltip(Localization.T("Show the sounds and icons in the pack."));
        }
        ImGui.SameLine();
        ImGui.TextDisabled(Localization.T("(?)"));
        float afterHelp = ImGui.GetItemRectMax().X - ImGui.GetWindowPos().X + ImGui.GetStyle().ItemSpacing.X;
        if (ImGui.IsItemHovered())
            Tooltip(Localization.T("Edits apply immediately and reset running timers. Saving rewrites the file without its comments.") + "\n" +
                Localization.T("Ctrl+S: save. Ctrl+Z / Ctrl+Y: undo / redo (up to 50 steps, cleared when another file is loaded)."));
        // Right-aligned on the same row, unless the window is too narrow.
        string discard = Localization.T("Discard changes");
        float discardWidth = ImGui.CalcTextSize(discard).X + ImGui.GetStyle().FramePadding.X * 2;
        ImGui.SameLine(Math.Max(afterHelp, ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - discardWidth));
        ImGui.BeginDisabled(_encounterPath is null || !IsDirty);
        if (RedButton(discard)) DiscardChanges();
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            Tooltip(Localization.T("Reload the file and drop unsaved edits. Ctrl+Z brings them back."));

        string name = encounter.Name;
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X * 0.65f);
        if (ImGui.InputTextWithHint("##EncounterName", Localization.T("Encounter name"), ref name, 128)) Edit(encounter with { Name = name });
        ImGui.SameLine();
        string id = encounter.Id ?? "";
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("##EncounterId", Localization.T("Id (optional)"), ref id, 128))
            Edit(encounter with { Id = id.Trim().Length == 0 ? null : id.Trim() });
        if (ImGui.IsItemHovered()) Tooltip(Localization.T("A pack's index.yaml must declare the zip's file name as its id."));

        float listWidth = Math.Clamp(ImGui.GetContentRegionAvail().X * 0.32f, 150, 260);
        if (ImGui.BeginChild("TimerList", new Vector2(listWidth, 0), ImGuiChildFlags.Borders))
        {
            int sort = _settings.TimerSort;
            string[] sorts = [.. SortLabels.Select(Localization.T)];
            float arrows = ImGui.GetFrameHeight() * 2 + ImGui.GetStyle().ItemSpacing.X * 2;
            ImGui.SetNextItemWidth(Math.Max(60, ImGui.GetContentRegionAvail().X - arrows));
            if (ImGui.Combo("##TimerSort", ref sort, sorts, sorts.Length)) _settings = _settings with { TimerSort = sort };
            if (ImGui.IsItemHovered()) Tooltip(Localization.T("Sort the list. The order in the file does not change."));
            // Reordering edits the file order, so it is only offered while the list shows that order.
            bool selected = _settings.TimerSort == 0 && _selectedTimer >= 0 && _selectedTimer < encounter.Timers.Count;
            ImGui.SameLine();
            ImGui.BeginDisabled(!selected || _selectedTimer == 0);
            if (ImGui.ArrowButton("##MoveUp", ImGuiDir.Up)) MoveTimer(_selectedTimer, -1);
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) Tooltip(Localization.T("Move up (file order only)"));
            ImGui.SameLine();
            ImGui.BeginDisabled(!selected || _selectedTimer == encounter.Timers.Count - 1);
            if (ImGui.ArrowButton("##MoveDown", ImGuiDir.Down)) MoveTimer(_selectedTimer, 1);
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) Tooltip(Localization.T("Move down (file order only)"));
            foreach (int i in SortedTimers(_encounter!.Timers, _settings.TimerSort))
            {
                var timer = _encounter.Timers[i];
                bool enabled = !_disabledTimers.Contains(timer.Id);
                if (ImGui.Checkbox($"##Enabled{i}", ref enabled)) SetTimerEnabled(timer.Id, enabled);
                if (ImGui.IsItemHovered())
                    Tooltip(Localization.T("Unchecked timers are disabled for this session: their keys do not start them. The file does not change, and reloading enables them again."));
                ImGui.SameLine();
                if (!enabled) ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
                if (ImGui.Selectable(TimerListLabel(timer, KeyLabel) + $"##Timer{i}", i == _selectedTimer)) _selectedTimer = i;
                if (!enabled) ImGui.PopStyleColor();
                if (ImGui.IsItemHovered() && !string.IsNullOrEmpty(timer.Note)) Tooltip(timer.Note);
            }
            encounter = _encounter;
            ImGui.Spacing();
            if (ImGui.Button(Localization.T("Add timer"), new Vector2(-1, 0)))
            {
                var ids = encounter.Timers.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
                int n = encounter.Timers.Count + 1;
                while (ids.Contains($"timer{n}")) n++;
                Edit(encounter with { Timers = [.. encounter.Timers, new TimerDefinition { Id = $"timer{n}", Name = $"Timer {n}" }] });
                _selectedTimer = encounter.Timers.Count;
            }
        }
        ImGui.EndChild();
        ImGui.SameLine();
        if (ImGui.BeginChild("TimerEditor", Vector2.Zero, ImGuiChildFlags.Borders))
        {
            if (_selectedTimer >= 0 && _selectedTimer < _encounter!.Timers.Count) DrawTimerEditor(_selectedTimer);
            else ImGui.TextDisabled(Localization.T("Select a timer to edit it."));
        }
        ImGui.EndChild();
    }

    private void DrawTimerEditor(int index)
    {
        var encounter = _encounter!;
        var timer = encounter.Timers[index];
        var edited = timer;
        float column = ImGui.CalcTextSize(Localization.T("Simultaneous limit")).X + 24;
        Row("Id");
        string id = timer.Id;
        if (ImGui.InputText("##Id", ref id, 64))
        {
            id = id.Trim();
            if (id.Length > 0 && !encounter.Timers.Where((_, i) => i != index).Any(t => t.Id == id)) edited = edited with { Id = id };
        }
        Row("Name");
        string name = timer.Name;
        if (ImGui.InputText("##Name", ref name, 128)) edited = edited with { Name = name };
        Row("Keys");
        for (int slot = TimerDefinition.MinKey; slot <= TimerDefinition.MaxKey; slot++)
        {
            if (slot > TimerDefinition.MinKey) ImGui.SameLine(0, 2);
            bool on = edited.Keys.Contains(slot);
            if (on) ImGui.PushStyleColor(ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.ButtonActive));
            // The last bound key cannot be cleared: a timer always has at least one key.
            if (ImGui.Button($"{slot}##Key{slot}", new Vector2(ImGui.GetFrameHeight(), 0)) && !(on && edited.Keys.Count == 1))
                edited = edited with { Keys = edited.Keys.With(slot, !on) };
            if (on) ImGui.PopStyleColor();
            if (ImGui.IsItemHovered()) Tooltip(KeyLabel(slot));
        }
        ImGui.SameLine();
        ImGui.TextDisabled(Localization.T("(?)"));
        if (ImGui.IsItemHovered()) Tooltip(Localization.T("Any selected key starts this timer; the reset modifier with that key resets it."));
        Row("Provider");
        string provider = timer.Provider ?? "";
        if (ImGui.InputTextWithHint("##Provider", Localization.T("Optional"), ref provider, 256))
            edited = edited with { Provider = provider.Trim().Length == 0 ? null : provider };
        Row("Duration (s)");
        double duration = timer.Duration;
        if (ImGui.InputDouble("##Duration", ref duration, 1, 10, "%.1f") && duration > 0 && duration <= TimerDefinition.MaxDuration)
            edited = edited with { Duration = duration };
        Row("Warn before (s)");
        double warn = timer.WarnBefore;
        if (ImGui.InputDouble("##Warn", ref warn, 1, 5, "%.1f") && warn >= 0) edited = edited with { WarnBefore = warn };
        if (ImGui.IsItemHovered()) Tooltip(Localization.T("Seconds before the end to show the message. Sound offsets count from this point. 0 = at the end."));
        Row("Repeat");
        bool repeat = timer.Repeat;
        if (ImGui.Checkbox(Localization.T("Restart automatically until reset") + "##Repeat", ref repeat)) edited = edited with { Repeat = repeat };
        Row("Simultaneous limit");
        int max = timer.MaxInstances;
        if (ImGui.SliderInt("##Max", ref max, 1, TimerDefinition.MaxInstanceLimit)) edited = edited with { MaxInstances = max };
        Row("At the limit");
        int limit = (int)timer.OnLimit;
        string[] limitLabels = [.. LimitLabels.Select(Localization.T)];
        if (ImGui.Combo("##OnLimit", ref limit, limitLabels, limitLabels.Length)) edited = edited with { OnLimit = (LimitAction)limit };
        if (ImGui.IsItemHovered())
            Tooltip(Localization.T("Replace oldest: drop the oldest copy. Ignore: keep the running copies. Reset: clear them and start one."));

        Row("Sounds");
        ImGui.TextDisabled(Localization.T("Offsets are seconds from the warn-before point; sounds at the same offset play together."));
        int remove = -1;
        for (int s = 0; s < timer.Sounds.Count; s++)
        {
            var sound = timer.Sounds[s];
            var changed = sound;
            ImGui.PushID($"Sound{s}");
            // Each sound is a two-line block marked by a bar on its left:
            // type, offset, preview and remove on the first line; the text or file on the second.
            float blockX = column + ImGui.GetStyle().WindowPadding.X, contentX = blockX + 10;
            if (s > 0) ImGui.Spacing();
            ImGui.SetCursorPosX(blockX);
            var blockTop = ImGui.GetCursorScreenPos();
            ImGui.SetCursorPosX(contentX);
            int kind = sound.Sfx is not null ? 1 : 0;
            string[] kinds = [.. SoundKindLabels.Select(Localization.T)];
            float kindWidth = kinds.Max(k => ImGui.CalcTextSize(k).X) + ImGui.GetFrameHeight() + ImGui.GetStyle().FramePadding.X * 2;
            ImGui.SetNextItemWidth(kindWidth);
            if (ImGui.Combo("##Kind", ref kind, kinds, kinds.Length))
                changed = kind == 1 ? new TimerSound { Sfx = "", Offset = sound.Offset } : new TimerSound { Tts = "{name}", Offset = sound.Offset };
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextDisabled(Localization.T("Offset"));
            ImGui.SameLine();
            ImGui.SetNextItemWidth(ImGui.CalcTextSize("-0000.0 s").X + ImGui.GetFrameHeight() * 2);
            double offset = sound.Offset;
            if (ImGui.InputDouble("##Offset", ref offset, 0.5, 1, "%.1f s") && Math.Abs(offset) <= TimerSound.MaxOffset)
                changed = changed with { Offset = offset };
            if (ImGui.IsItemHovered())
                Tooltip(Localization.F("Plays {0} s after the start (negative offsets play before the warn-before point).",
                    TimerDefinition.FormatSeconds(Math.Round(timer.SoundTime(sound), 1))));
            ImGui.SameLine();
            if (ImGui.Button(Localization.T("Preview")) && _sounds.Request(encounter, timer, sound) is { } task) _previews.Add((task, 0));
            ImGui.SameLine();
            if (RedButton(Localization.T("Remove"))) remove = s;

            ImGui.SetCursorPosX(contentX);
            if (sound.Sfx is { } sfx)
            {
                ImGui.SetNextItemWidth(Math.Max(80, ImGui.GetContentRegionAvail().X - ButtonsWidth(1)));
                if (ImGui.InputText("##Sfx", ref sfx, 260)) changed = changed with { Sfx = sfx };
                ImGui.SameLine();
                ImGui.BeginDisabled(_assetPicker is not null);
                if (ImGui.Button(Localization.T("Browse...")))
                {
                    if (encounter.Pack is not null) _packFiles = (EncounterPack.SoundFolder, index, s);
                    else _assetPicker = (FilePicker.OpenAsync(Localization.T("Sound files"),
                        string.Join(';', EncounterYaml.SoundExtensions.Select(e => "*" + e)), SubFolder("sfx")), index, s);
                }
                ImGui.EndDisabled();
                PackBrowseTooltip();
            }
            else
            {
                string tts = sound.Tts ?? "";
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputText("##Tts", ref tts, 1024)) changed = changed with { Tts = tts };
                if (ImGui.IsItemHovered()) Tooltip(Localization.T("{name} = timer name, {sec} = seconds left when this sound plays"));
            }
            if (_sounds.ErrorFor(encounter, timer, sound) is { } soundError)
            {
                ImGui.SetCursorPosX(contentX);
                ColoredText(ErrorColor, soundError);
            }
            float blockBottom = ImGui.GetItemRectMax().Y;
            ImGui.GetWindowDrawList().AddLine(new Vector2(blockTop.X + 2, blockTop.Y), new Vector2(blockTop.X + 2, blockBottom),
                ImGui.GetColorU32(ImGuiCol.ButtonActive), 3f);
            if (changed != sound) edited = edited with { Sounds = edited.Sounds.SetItem(s, changed) };
            ImGui.PopID();
        }
        if (remove >= 0) edited = edited with { Sounds = edited.Sounds.RemoveAt(remove) };
        ImGui.SetCursorPosX(column + ImGui.GetStyle().WindowPadding.X);
        ImGui.BeginDisabled(timer.Sounds.Count >= TimerDefinition.MaxSounds);
        if (ImGui.Button(Localization.T("Add sound"))) edited = edited with { Sounds = edited.Sounds.Add(new TimerSound { Tts = "{name}" }) };
        ImGui.EndDisabled();
        if (timer.Sounds.Count > 0)
        {
            ImGui.SameLine();
            if (ImGui.Button(Localization.T("Preview all"))) PreviewTimeline(encounter, timer);
            if (ImGui.IsItemHovered()) Tooltip(Localization.T("Plays every sound with its offset."));
        }

        Row("Message");
        string message = timer.Message ?? "";
        if (ImGui.InputTextWithHint("##Message", timer.DisplayName, ref message, 256))
            edited = edited with { Message = message.Length == 0 ? null : message };
        Row("Icon");
        string icon = timer.Icon ?? "";
        ImGui.SetNextItemWidth(Math.Max(80, ImGui.GetContentRegionAvail().X - ButtonsWidth(2)));
        if (ImGui.InputText("##Icon", ref icon, 260)) edited = edited with { Icon = icon.Length == 0 ? null : icon };
        ImGui.SameLine();
        bool openPicker = ImGui.Button(Localization.T("Lucide...") + "##Icon");
        if (ImGui.IsItemHovered()) Tooltip(Localization.T("Pick a built-in Lucide icon. It is drawn in the timer's color."));
#if TIPAURA_AGENT_SELF_TEST
        if (_lucidePickerForSmoke == true)
        {
            openPicker = true;
            _lucidePickerForSmoke = null;
        }
#endif
        if (openPicker) ImGui.OpenPopup("LucidePicker");
        if (LucidePicker(timer.Icon) is { } picked) edited = edited with { Icon = picked };
        ImGui.SameLine();
        ImGui.BeginDisabled(_assetPicker is not null);
        if (ImGui.Button(Localization.T("Browse...") + "##Icon"))
        {
            if (encounter.Pack is not null) _packFiles = (EncounterPack.IconFolder, index, -1);
            else _assetPicker = (FilePicker.OpenAsync(Localization.T("Images"),
                string.Join(';', EncounterYaml.ImageExtensions.Select(e => "*" + e)), SubFolder("icons")), index, -1);
        }
        ImGui.EndDisabled();
        PackBrowseTooltip();
        if (_icons.Get(encounter.Resolve(timer.Icon)) is { } texture)
        {
            ImGui.SetCursorPosX(column + ImGui.GetStyle().WindowPadding.X);
            // A Lucide glyph takes the timer's color, as on the overlays.
            ImGui.Image(texture.Id, new Vector2(ImGui.GetFrameHeight() * 2), Vector2.Zero, Vector2.One,
                texture.AlphaOnly ? OverlayViews.IconTint(timer.Color) : Vector4.One);
        }
        Row("Color");
        var color = OverlayViews.ParseColor(timer.Color, OverlayViews.DefaultBarColor);
        bool custom = timer.Color is not null;
        if (ImGui.Checkbox("##CustomColor", ref custom))
            edited = edited with { Color = custom ? ToHex(color) : null };
        ImGui.SameLine();
        ImGui.BeginDisabled(!custom);
        if (ImGui.ColorEdit4("##Color", ref color, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaBar))
            edited = edited with { Color = ToHex(color) };
        ImGui.EndDisabled();
        Row("Note");
        string note = timer.Note ?? "";
        // The box grows with the text and always keeps one empty line at the bottom, where the counter sits;
        // past NoteMaxLines it scrolls.
        int noteLines = note.Count(c => c == '\n') + 1;
        int shownLines = Math.Clamp(noteLines + 1, NoteMinLines, NoteMaxLines);
        // The buffer is in UTF-8 bytes, so it is sized for the character limit and trimmed to it afterwards.
        if (ImGui.InputTextMultiline("##Note", ref note, TimerDefinition.MaxNoteLength * 4,
            new Vector2(-1, ImGui.GetTextLineHeight() * shownLines + ImGui.GetStyle().FramePadding.Y * 2)))
        {
            note = TimerDefinition.LimitNote(note, out _);
            edited = edited with { Note = note.Trim().Length == 0 ? null : note };
        }
        NoteCounter($"{TimerDefinition.NoteLength(note)}/{TimerDefinition.MaxNoteLength}", noteLines + 1 > NoteMaxLines);

        ImGui.Spacing();
        ImGui.Separator();
        if (ImGui.Button(Localization.T("Duplicate")))
        {
            var ids = encounter.Timers.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
            int n = 2;
            while (ids.Contains($"{timer.Id}_{n}")) n++;
            var copy = timer with { Id = $"{timer.Id}_{n}" };
            Edit(encounter with { Timers = [.. encounter.Timers.Take(index + 1), copy, .. encounter.Timers.Skip(index + 1)] });
            _selectedTimer = index + 1;
            return;
        }
        ImGui.SameLine();
        if (RedButton(Localization.T("Delete timer")))
        {
            Edit(encounter with { Timers = [.. encounter.Timers.Where((_, i) => i != index)] });
            _selectedTimer = Math.Min(index, encounter.Timers.Count - 2);
            return;
        }
        if (edited != timer)
        {
            // A new sound or icon path must be read again rather than served from the failed-load cache.
            if (edited.Icon != timer.Icon) _icons.Clear();
            // A disabled timer stays disabled under its new id.
            if (edited.Id != timer.Id && _disabledTimers.Remove(timer.Id))
            {
                _disabledTimers.Add(edited.Id);
                PushDisabledTimers();
            }
            EditTimer(index, edited);
        }

        void Row(string label)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(Localization.T(label));
            ImGui.SameLine(column);
            ImGui.SetNextItemWidth(-1);
        }

        void PackBrowseTooltip()
        {
            if (encounter.Pack is not null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                Tooltip(Localization.T("A pack only uses files inside its zip: pick one, or add a file to the pack."));
        }

        string? SubFolder(string name)
        {
            string folder = Path.Combine(encounter.BaseDirectory, name);
            return Directory.Exists(folder) ? folder : encounter.BaseDirectory;
        }
    }

    // Display order for the Timers tab: indices into timers. Ties keep the file order.
    internal static IEnumerable<int> SortedTimers(IReadOnlyList<TimerDefinition> timers, int sort)
    {
        var order = Enumerable.Range(0, timers.Count);
        return sort switch
        {
            1 => order.OrderBy(i => timers[i].Keys.Lowest).ThenBy(i => timers[i].Duration),
            2 => order.OrderBy(i => timers[i].Duration).ThenBy(i => timers[i].Keys.Lowest),
            _ => order
        };
    }

    // First line: first key, name and provider; each further key on its own line, at most three lines.
    internal static string TimerListLabel(TimerDefinition timer, Func<int, string> keyLabel)
    {
        int[] slots = [.. timer.Keys.Slots];
        string provider = string.IsNullOrEmpty(timer.Provider) ? "" : $"  [{timer.Provider}]";
        var lines = new List<string> { $"{keyLabel(slots[0])}  {timer.DisplayName}{provider}" };
        lines.AddRange(slots.Skip(1).Take(2).Select(keyLabel));
        if (slots.Length > 3) lines[^1] += $"  +{slots.Length - 3}";
        return string.Join('\n', lines);
    }

    // Swaps a timer with its neighbour in the file order and keeps it selected.
    private void MoveTimer(int index, int direction)
    {
        if (_encounter is null || index + direction < 0 || index + direction >= _encounter.Timers.Count) return;
        var timers = _encounter.Timers.ToList();
        (timers[index], timers[index + direction]) = (timers[index + direction], timers[index]);
        Edit(_encounter with { Timers = timers });
        _selectedTimer = index + direction;
    }

    private static bool RedButton(string label)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.55f, 0.18f, 0.18f, 1f));
        bool pressed = ImGui.Button(label);
        ImGui.PopStyleColor();
        return pressed;
    }

    private const int NoteMinLines = 5, NoteMaxLines = 12;

    // Draws the note's character count inset in the note box's bottom-right corner, on the box's own
    // background color at half its alpha. The box is a child window that renders above the parent's draw
    // list, so the counter is a child window of its own, submitted after the box and ignoring the mouse.
    private static void NoteCounter(string text, bool scrollbar)
    {
        var style = ImGui.GetStyle();
        var box = ImGui.GetItemRectMax();
        var after = ImGui.GetCursorScreenPos();
        var padding = new Vector2(MathF.Round(style.FramePadding.X * 0.5f), 1);
        var size = ImGui.CalcTextSize(text) + padding * 2;
        var min = new Vector2(box.X - style.FramePadding.X - (scrollbar ? style.ScrollbarSize : 0) - size.X,
            box.Y - style.FramePadding.Y - size.Y);
        ImGui.SetCursorScreenPos(min);
        if (ImGui.BeginChild("##NoteCount", size, ImGuiChildFlags.None, ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoSavedSettings))
        {
            var draw = ImGui.GetWindowDrawList();
            draw.AddRectFilled(min, min + size, ImGui.GetColorU32(ImGuiCol.FrameBg, 0.5f), style.FrameRounding);
            draw.AddText(min + padding, ImGui.GetColorU32(ImGuiCol.TextDisabled), text);
        }
        ImGui.EndChild();
        ImGui.SetCursorScreenPos(after);
    }

    // Plays a timer's sounds with their relative spacing, starting with the earliest one now.
    private void PreviewTimeline(Encounter encounter, TimerDefinition timer)
    {
        double first = timer.Sounds.Min(timer.SoundTime);
        long now = Stopwatch.GetTimestamp();
        foreach (var sound in timer.Sounds)
            if (_sounds.Request(encounter, timer, sound) is { } task)
                _previews.Add((task, now + (long)((timer.SoundTime(sound) - first) * Stopwatch.Frequency)));
    }

    private static string ToHex(Vector4 color)
    {
        static int Byte(float v) => (int)MathF.Round(Math.Clamp(v, 0f, 1f) * 255);
        return color.W >= 0.999f
            ? $"#{Byte(color.X):X2}{Byte(color.Y):X2}{Byte(color.Z):X2}"
            : $"#{Byte(color.X):X2}{Byte(color.Y):X2}{Byte(color.Z):X2}{Byte(color.W):X2}";
    }

    private void DrawSettingsTab()
    {
        // Rows under Floating windows are indented, so their labels need the indent as well.
        float column = Math.Max(
            new[] { "Simultaneous sounds", "Max sound length" }.Max(label => ImGui.CalcTextSize(Localization.T(label)).X),
            new[] { "Colors (dark theme)", "Colors (light theme)" }.Max(label => ImGui.CalcTextSize(Localization.T(label)).X)
                + ImGui.GetStyle().IndentSpacing) + 24;
        ImGui.SeparatorText(Localization.T("Audio"));
        Row("Volume");
        float volume = _settings.Volume * 100;
        if (ImGui.SliderFloat("##Volume", ref volume, 0, 100, "%.0f%%"))
        {
            _settings = _settings with { Volume = volume / 100 };
            _audio.Volume = _settings.Volume;
        }
        Row("Simultaneous sounds");
        int max = _settings.MaxSounds;
        if (ImGui.SliderInt("##MaxSounds", ref max, 1, AudioEngine.MaxVoiceLimit))
        {
            _settings = _settings with { MaxSounds = max };
            _audio.MaxVoices = max;
        }
        if (ImGui.IsItemHovered()) Tooltip(Localization.T("When more sounds play at once, the oldest one stops."));
        Row("Max sound length");
        float maxSeconds = _settings.MaxSoundSeconds;
        if (ImGui.SliderFloat("##MaxSoundSeconds", ref maxSeconds, 1, AudioEngine.MaxPlaySecondsLimit, "%.0f s"))
        {
            _settings = _settings with { MaxSoundSeconds = MathF.Round(maxSeconds) };
            _audio.MaxPlaySeconds = _settings.MaxSoundSeconds;
        }
        if (ImGui.IsItemHovered()) Tooltip(Localization.T("Each sound file or TTS phrase stops after this time, previews included."));
        Row("TTS voice");
        string voiceName = _voices.FirstOrDefault(v => v.Id == _settings.TtsVoice) is { } voice
            ? $"{voice.Name} ({voice.Language})" : Localization.T("System default");
        if (ImGui.BeginCombo("##Voice", voiceName))
        {
            if (ImGui.Selectable(Localization.T("System default"), _settings.TtsVoice is null)) SetVoice(null, _settings.TtsRate);
            foreach (var v in _voices)
                if (ImGui.Selectable($"{v.Name} ({v.Language})##{v.Id}", v.Id == _settings.TtsVoice)) SetVoice(v.Id, _settings.TtsRate);
            ImGui.EndCombo();
        }
        Row("TTS speed");
        float rate = _settings.TtsRate;
        ImGui.SliderFloat("##Rate", ref rate, 0.5f, 3f, "%.1fx");
        if (rate != _settings.TtsRate) _settings = _settings with { TtsRate = rate };
        if (ImGui.IsItemDeactivatedAfterEdit()) SetVoice(_settings.TtsVoice, rate);
        if (_voices.Count > 0 && !_voices.Any(v => v.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase)))
            ColoredText(WarningColor, Localization.T("No Chinese voice installed: Settings > Time & Language > Speech > Add voices."));
        if (_audio.Error is { } audioError) ColoredText(ErrorColor, Localization.F("Audio error: {0}", audioError));

        DrawHookSettings();

        ImGui.SeparatorText(Localization.T("Floating windows"));
        ImGui.Indent();
        ImGui.SeparatorText(Localization.T("Timeline bars"));
        _settings = _settings with { Timeline = DrawPlacement("Timeline", _settings.Timeline, OverlayPlacement.DefaultTimeline, _timelineWindow) };
        Row("Bars shown");
        int bars = _settings.MaxTimelineBars;
        if (ImGui.SliderInt("##Bars", ref bars, 1, 30)) _settings = _settings with { MaxTimelineBars = bars };
        bool capture = _settings.DiscordCapture;
        if (ImGui.Checkbox(Localization.T("Discord capture (timeline)"), ref capture))
            _settings = _settings with { DiscordCapture = capture };
        if (ImGui.IsItemHovered())
            Tooltip(Localization.T("For Discord streaming: choose the TipAura main window in Discord, and the stream shows the timeline bars on black instead of the main window. Nothing changes on your screen. Requires the timeline window to be shown."));
        DrawMonochromeSettings();
        ImGui.SeparatorText(Localization.T("Center alerts"));
        _settings = _settings with { Alerts = DrawPlacement("Alerts", _settings.Alerts, OverlayPlacement.DefaultAlerts, _alertWindow) };
        Row("Alert duration");
        float seconds = _settings.AlertSeconds;
        if (ImGui.SliderFloat("##AlertSeconds", ref seconds, 1, 10, "%.1f s")) _settings = _settings with { AlertSeconds = seconds };
        if (ImGui.Button(Localization.T("Test alert")) && _encounter?.Timers.FirstOrDefault() is { } first)
            _alerts.Add(new ActiveAlert(first, _encounter, Stopwatch.GetTimestamp()));
        ImGui.Unindent();

        ImGui.SeparatorText(Localization.T("Hotkeys"));
        bool pause = _settings.PauseHotkeysWhileTyping;
        if (ImGui.Checkbox(Localization.T("Pause hotkeys while typing in TipAura"), ref pause))
            _settings = _settings with { PauseHotkeysWhileTyping = pause };
        if (ImGui.IsItemHovered())
            Tooltip(Localization.T("While a TipAura text field has keyboard focus, hotkeys are ignored, so typing does not start timers. They work again once another window, such as the game, is focused."));
        bool requireFocus = _settings.HotkeysRequireHookFocus;
        if (ImGui.Checkbox(Localization.T("Disable hotkeys unless the hooked window is focused"), ref requireFocus))
            _settings = _settings with { HotkeysRequireHookFocus = requireFocus };
        if (ImGui.IsItemHovered())
            Tooltip(Localization.T("Hotkeys only work while the window of the selected window hook, or TipAura itself, is in the foreground. With no hook selected, they only work in TipAura."));
        if (_settings.HotkeysRequireHookFocus)
        {
            if (_hooks.ActiveHook is { } hook) ImGui.TextDisabled(Localization.F("Hotkeys only work while {0} or TipAura is focused.", HookLabel(hook)));
            else ColoredText(WarningColor, Localization.T("No window hook is selected: hotkeys only work while TipAura is focused."));
        }
        if (_scheduler.HotkeyError is { } hotkeyError) ColoredText(ErrorColor, Localization.F("Global hotkeys are unavailable: {0}", hotkeyError));
        DrawHotkeyBindings(column);

        ImGui.SeparatorText(Localization.T("Interface"));
        Row("Font size");
        _settings = _settings with { FontSizePx = ReleasedSlider("Font", "##FontSize", _settings.FontSizePx, 10, 32) };
        Row("Theme");
        int theme = _settings.Theme;
        string[] themes = [Localization.T("Dark"), Localization.T("Light")];
        if (ImGui.Combo("##Theme", ref theme, themes, themes.Length))
        {
            _settings = _settings with { Theme = theme };
            _renderer.ApplyTheme(theme);
        }
        Row("Language");
        int language = _settings.Language == "en" ? 1 : 0;
        string[] languages = ["繁體中文", "English"];
        if (ImGui.Combo("##Language", ref language, languages, languages.Length))
        {
            _settings = _settings with { Language = language == 1 ? "en" : null };
            _notice = Localization.T("Restart TipAura to change the language.");
        }
        if (ImGui.Button(Localization.T("Open data folder"))) OpenFolder(EncounterYaml.DataDirectory);
        ImGui.SameLine();
        if (ImGui.Button(Localization.T("Open log folder"))) OpenFolder(AppLog.DirectoryPath);
        ImGui.SameLine();
        if (ImGui.Button(Localization.T("About"))) _auxiliary.OpenAbout();

        void Row(string label)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(Localization.T(label));
            ImGui.SameLine(column);
            ImGui.SetNextItemWidth(-1);
        }

        // Monochrome timeline colors; the swatches edit the set of the current theme.
        void DrawMonochromeSettings()
        {
            bool monochrome = _settings.TimelineMonochrome;
            if (ImGui.Checkbox(Localization.T("Monochrome colors"), ref monochrome)) _settings = _settings with { TimelineMonochrome = monochrome };
            if (ImGui.IsItemHovered())
                Tooltip(Localization.T("Draw every bar in the colors below instead of the timers' colors from the encounter file. The dark and light themes each have their own colors."));
            if (!monochrome) return;
            bool light = _settings.Theme == 1;
            var colors = _settings.MonochromeColors;
            var defaults = light ? TimelineColors.DefaultLight : TimelineColors.DefaultDark;
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(Localization.T(light ? "Colors (light theme)" : "Colors (dark theme)"));
            ImGui.SameLine(column);
            ImGui.BeginGroup();
            float right = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
            colors = colors with { Text = Swatch("Text", colors.Text, defaults.Text, true) };
            colors = colors with { Shadow = Swatch("Text shadow", colors.Shadow, defaults.Shadow, false) };
            colors = colors with { Fill = Swatch("Bar", colors.Fill, defaults.Fill, false) };
            colors = colors with { Track = Swatch("Track", colors.Track, defaults.Track, false) };
            colors = colors with { Flash = Swatch("Flash", colors.Flash, defaults.Flash, false) };
            ImGui.EndGroup();
            ImGui.SameLine();
            ImGui.BeginDisabled(colors == defaults);
            if (ImGui.Button(Localization.T("Restore default colors"))) colors = defaults;
            ImGui.EndDisabled();
            _settings = light ? _settings with { MonochromeLight = colors } : _settings with { MonochromeDark = colors };

            // A label and a color button, wrapped to the next line when it does not fit.
            string Swatch(string label, string value, string fallback, bool first)
            {
                var style = ImGui.GetStyle();
                float width = style.ItemSpacing.X * 2 + ImGui.CalcTextSize(Localization.T(label)).X + ImGui.GetFrameHeight();
                if (!first && ImGui.GetItemRectMax().X + width <= right) ImGui.SameLine();
                var color = OverlayViews.ParseColor(value, OverlayViews.ParseColor(fallback, Vector4.One));
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(Localization.T(label));
                ImGui.SameLine();
                return ImGui.ColorEdit4($"##{label}", ref color, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaBar)
                    ? ToHex(color) : value;
            }
        }

        OverlayPlacement DrawPlacement(string id, OverlayPlacement value, OverlayPlacement defaults, OverlayWindow? window)
        {
            ImGui.PushID(id);
            bool visible = value.Visible, locked = value.ClickThrough, topmost = value.Topmost;
            if (ImGui.Checkbox(Localization.T("Enabled"), ref visible)) value = value with { Visible = visible };
            ImGui.SameLine();
            if (ImGui.Checkbox(Localization.T("Locked (no preview or editing)"), ref locked)) value = value with { ClickThrough = locked };
            if (ImGui.IsItemHovered()) Tooltip(Localization.T("Locked windows are click-through and show no placeholder. Unlock to preview the window, drag it, or resize it from its edges."));
            ImGui.SameLine();
            if (ImGui.Checkbox(Localization.T("Topmost"), ref topmost)) value = value with { Topmost = topmost };
            ImGui.SameLine();
            if (ImGui.SmallButton(Localization.T("Reset position")))
                value = value with { X = null, Y = null, Anchor = defaults.Anchor, Width = defaults.Width, Height = defaults.Height };
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(Localization.T("Anchor"));
            if (ImGui.IsItemHovered())
                Tooltip(Localization.T("The point of the hooked window (or of the primary screen without a hook) that the floating window keeps its distance from."));
            ImGui.SameLine(column);
            ImGui.BeginGroup();
            ImGui.BeginDisabled(_overlayBounds is null);
            float buttonWidth = AnchorNames.Max(name => ImGui.CalcTextSize(Localization.T(name)).X) + ImGui.GetStyle().FramePadding.X * 4;
            for (int anchor = 0; anchor < 9; anchor++)
            {
                if (anchor % 3 != 0) ImGui.SameLine();
                bool current = value.Anchor == anchor;
                if (current) ImGui.PushStyleColor(ImGuiCol.Button, ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonActive]);
                if (ImGui.Button($"{Localization.T(AnchorNames[anchor])}##Anchor{anchor}", new Vector2(buttonWidth, 0)) &&
                    !current && _overlayBounds is { } bounds)
                {
                    var position = window is { Shown: true } ? window.Location : OverlayLayout.Position(value, defaults, bounds, _workAreas);
                    value = OverlayLayout.ChangeAnchor(value, bounds, anchor, position);
                }
                if (current) ImGui.PopStyleColor();
                if (ImGui.IsItemHovered()) Tooltip(Localization.T("Changing the anchor keeps the floating window in place."));
            }
            ImGui.EndDisabled();
            ImGui.EndGroup();
            Row("Text size");
            value = value with { FontSize = ReleasedSlider(id + ".Font", "##FontSize", value.FontSize, 10, 96) };
            Row("Background");
            float opacity = value.BackgroundOpacity * 100;
            if (ImGui.SliderFloat("##Opacity", ref opacity, 0, 100, "%.0f%%")) value = value with { BackgroundOpacity = opacity / 100 };
            ImGui.PopID();
            return value;
        }
    }

    // A pixel-size slider whose value is committed only when it is released, so the font atlas is rebuilt
    // once instead of every frame of a drag. The dragged value lives in _sliderDrafts meanwhile; reading it
    // back from the setting each frame would lose it.
    private int ReleasedSlider(string draftKey, string label, int value, int min, int max)
    {
        int shown = _sliderDrafts.TryGetValue(draftKey, out int draft) ? draft : value;
        if (ImGui.SliderInt(label, ref shown, min, max, "%d px")) _sliderDrafts[draftKey] = shown;
        if (ImGui.IsItemActive()) return value;
        return _sliderDrafts.Remove(draftKey, out int released) ? released : value;
    }

    private void SetVoice(string? voice, float rate)
    {
        _settings = _settings with { TtsVoice = voice, TtsRate = rate };
        _sounds.ConfigureTts(voice, rate);
        if (_encounter is not null) _sounds.Warm(_encounter);
    }

    private void OpenFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _notice = Localization.F("Could not open folder: {0}", ex.Message);
            AppLog.Warn("UI", $"Could not open {folder}: {ex.Message}");
        }
    }

    // ImGui.NET hands these strings to ImGui as printf formats; issues, paths and error messages may contain '%'.
    private static void WrappedText(string text)
    {
        ImGui.PushTextWrapPos(0);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
    }

    private static void ColoredText(Vector4 color, string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        WrappedText(text);
        ImGui.PopStyleColor();
    }

    // ---- Hotkeys ----

    private static string Hotkey(ModifierCombo modifiers, int code) =>
        modifiers.IsEmpty ? HotkeyBindings.KeyName(code) : $"{modifiers.Label}+{HotkeyBindings.KeyName(code)}";

    // "Key 1 (Num1)": the slot encounter files use, with the hotkey that starts it on this computer.
    private string KeyLabel(int slot) => Localization.F("Key {0} ({1})", slot, Hotkey(_settings.Hotkeys.StartModifiers, _settings.Hotkeys.Keys[slot]));

    private string ResetAllHotkey => Hotkey(_settings.Hotkeys.ResetModifiers, _settings.Hotkeys.Keys[HotkeyBindings.ResetAllIndex]);

    private static string KeyRowName(int index) =>
        index == HotkeyBindings.ResetAllIndex ? Localization.T("Reset encounter key") : Localization.F("Key {0}", index);

    private void SetHotkeys(HotkeyBindings bindings)
    {
        _settings = _settings with { Hotkeys = bindings };
        _scheduler.SetHotkeyBindings(bindings);
        _hotkeyError = _modifierError = "";
    }

    private void DrawHotkeyBindings(float column)
    {
        _hotkeyRowsShown = true;
        var bindings = _settings.Hotkeys;
        var start = ModifierRows("Start modifier ({0})", "StartModifiers", bindings.StartModifiers);
        var reset = ModifierRows("Reset modifier ({0})", "ResetModifiers", bindings.ResetModifiers);
        if (start != bindings.StartModifiers || reset != bindings.ResetModifiers)
        {
            if (start.Overlaps(reset)) _modifierError = Localization.T("The modifiers must differ.");
            else SetHotkeys(bindings = bindings with { StartModifiers = start, ResetModifiers = reset });
        }
        if (_modifierError.Length > 0) ColoredText(ErrorColor, _modifierError);

        float width = Math.Min(ImGui.GetContentRegionAvail().X - column, ImGui.GetFontSize() * 14);
        foreach (int index in new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, HotkeyBindings.ResetAllIndex })
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(KeyRowName(index));
            ImGui.SameLine(column);
            bool capturing = _capturingKey == index;
            string text = capturing ? Localization.T("Press a key... (Esc to cancel)") : HotkeyBindings.KeyName(bindings.Keys[index]);
            ImGui.BeginDisabled(_scheduler.HotkeyError is not null);
            if (capturing) ImGui.PushStyleColor(ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.ButtonActive));
            if (ImGui.Button($"{text}###Bind{index}", new Vector2(width, 0)))
            {
                if (capturing) CancelKeyCapture();
                else
                {
                    _capturingKey = index;
                    _hotkeyError = "";
                    _scheduler.BeginKeyCapture();
                }
            }
            if (capturing) ImGui.PopStyleColor();
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered())
                Tooltip(Localization.T("Click, then press the key to bind. Key bindings are stored on this computer only; encounter files keep using keys 1-9."));
        }
        if (ImGui.Button(Localization.T("Restore default hotkeys")))
        {
            CancelKeyCapture();
            SetHotkeys(new HotkeyBindings());
        }
        if (_hotkeyError.Length > 0) ColoredText(ErrorColor, _hotkeyError);

        // A heading with the current combination, then one row of Left/Right/Either radios per modifier.
        // Clicking the selected radio again clears it (None).
        ModifierCombo ModifierRows(string label, string id, ModifierCombo value)
        {
            ImGui.TextUnformatted(Localization.F(label, value.IsEmpty ? Localization.T("None") : value.Label));
            if (ImGui.IsItemHovered())
                Tooltip(Localization.T("Hold these together with a key. Click a selected option again to clear it; a modifier with nothing selected must not be held."));
            ImGui.PushID(id);
            ImGui.Indent();
            ModifierSide[] sides = [value.Ctrl, value.Alt, value.Shift];
            string[] names = ["Ctrl", "Alt", "Shift"];
            (ModifierSide Side, string Label)[] options =
                [(ModifierSide.Left, "Left"), (ModifierSide.Right, "Right"), (ModifierSide.Either, "Either")];
            float radios = ImGui.GetCursorPosX() + ImGui.CalcTextSize("Shift").X + ImGui.GetStyle().ItemSpacing.X * 2;
            for (int k = 0; k < 3; k++)
            {
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(names[k]);
                ImGui.SameLine(radios);
                foreach (var (side, text) in options)
                {
                    if (side != ModifierSide.Left) ImGui.SameLine();
                    if (ImGui.RadioButton($"{Localization.T(text)}##{names[k]}{side}", sides[k] == side))
                        sides[k] = sides[k] == side ? ModifierSide.None : side;
                }
            }
            ImGui.Unindent();
            ImGui.PopID();
            return new ModifierCombo { Ctrl = sides[0], Alt = sides[1], Shift = sides[2] };
        }
    }

    // The global hook swallows the captured key, so it reaches neither ImGui nor the game.
    private void PollKeyCapture()
    {
        if (_capturingKey < 0) return;
        if (_scheduler.TryTakeCapturedKey(out int code))
        {
            int index = _capturingKey;
            _capturingKey = -1;
            if (code == 0x1B) return;
            var bindings = _settings.Hotkeys;
            int other = bindings.IndexOf(code);
            if (!HotkeyBindings.IsBindable(code)) _hotkeyError = Localization.F("{0} cannot be bound.", HotkeyBindings.KeyName(code));
            else if (other >= 0 && other != index)
                _hotkeyError = Localization.F("{0} is already bound to {1}.", HotkeyBindings.KeyName(code), KeyRowName(other));
            else SetHotkeys(bindings.WithKey(index, code));
        }
        else if (!_window.IsFocused || !_hotkeyRowsShown) CancelKeyCapture();
    }

    private void CancelKeyCapture()
    {
        if (_capturingKey < 0) return;
        _capturingKey = -1;
        _scheduler.CancelKeyCapture();
    }

    // ---- Disabled timers (session only) ----

    private void SetTimerEnabled(string id, bool enabled)
    {
        if (enabled ? _disabledTimers.Remove(id) : _disabledTimers.Add(id)) PushDisabledTimers();
    }

    private void ClearDisabledTimers()
    {
        if (_disabledTimers.Count == 0) return;
        _disabledTimers.Clear();
        PushDisabledTimers();
    }

    private void PushDisabledTimers() => _scheduler.SetDisabled(new HashSet<string>(_disabledTimers, StringComparer.Ordinal));

    private static void Tooltip(string text)
    {
        if (!ImGui.BeginTooltip()) return;
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 32);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

    // The popup of the Icon row's Lucide... button: a name search, a category filter and a grid of the
    // catalog's glyphs. Returns the picked `lucide:<name>`, or null while nothing is picked.
    private unsafe string? LucidePicker(string? current)
    {
        if (!ImGui.BeginPopup("LucidePicker")) return null;
        string? picked = null;
#if TIPAURA_AGENT_SELF_TEST
        if (_lucidePickerForSmoke == false)
        {
            ImGui.CloseCurrentPopup();
            _lucidePickerForSmoke = null;
        }
#endif
        var style = ImGui.GetStyle();
        float frame = ImGui.GetFrameHeight();
        if (ImGui.IsWindowAppearing()) ImGui.SetKeyboardFocusHere();
        ImGui.SetNextItemWidth(frame * 8);
        ImGui.InputTextWithHint("##LucideFilter", Localization.T("Search icons"), ref _lucideFilter, 64);
        ImGui.SameLine();
        string[] categories = [Localization.T("All categories"), .. Lucide.Categories.Select(c => Localization.T(c.Title))];
        ImGui.SetNextItemWidth(frame * 8);
        ImGui.Combo("##LucideCategory", ref _lucideCategory, categories, categories.Length);
        string filter = _lucideFilter.Trim().ToLowerInvariant().Replace(' ', '-');
        var category = _lucideCategory > 0 && _lucideCategory <= Lucide.Categories.Length ? Lucide.Categories[_lucideCategory - 1].Category : 0;
        var shown = Lucide.Icons.Where(i => (category == 0 || (i.Categories & category) != 0) && i.Name.Contains(filter, StringComparison.Ordinal)).ToList();
        var selected = Lucide.Find(current);

        const int Columns = 10, Rows = 7;
        var image = new Vector2(MathF.Round(ImGui.GetFontSize() * 1.6f));
        var cell = image + style.FramePadding * 2 + style.ItemSpacing;
        var tint = *ImGui.GetStyleColorVec4(ImGuiCol.Text);
        var highlight = *ImGui.GetStyleColorVec4(ImGuiCol.Header);
        if (ImGui.BeginChild("##LucideGrid", new Vector2(cell.X * Columns + style.ScrollbarSize + style.WindowPadding.X, cell.Y * Rows)))
        {
            if (shown.Count == 0) ImGui.TextDisabled(Localization.T("No matching icons."));
            // Only visible rows are drawn, so only their glyphs are rasterized.
            var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
            clipper.Begin((shown.Count + Columns - 1) / Columns, cell.Y);
            while (clipper.Step())
                for (int row = clipper.DisplayStart; row < clipper.DisplayEnd; row++)
                    for (int column = 0; column < Columns && row * Columns + column < shown.Count; column++)
                    {
                        var entry = shown[row * Columns + column];
                        if (column > 0) ImGui.SameLine();
                        if (_icons.Get(entry.Value) is not { } glyph) continue;
                        if (ImGui.ImageButton(entry.Name, glyph.Id, image, Vector2.Zero, Vector2.One,
                            entry == selected ? highlight : Vector4.Zero, tint))
                        {
                            picked = entry.Value;
                            ImGui.CloseCurrentPopup();
                        }
                        if (ImGui.IsItemHovered()) Tooltip(entry.Name);
                    }
            clipper.End();
            clipper.Destroy();
        }
        ImGui.EndChild();
        ImGui.TextDisabled(Localization.F("{0} icons", shown.Count));
        ImGui.EndPopup();
        return picked;
    }

    // Sound previews in the pack files modal; an sfx key does not depend on the timer.
    private static readonly TimerDefinition PreviewTimer = new();

    // The pack's sounds or icons with a preview. Opened from Browse... it picks a file for that field or adds
    // one to the pack (written when the pack is saved); opened from Pack files... it only shows them, both
    // folders in tabs. Files that no timer uses are marked, since saving the pack removes them.
    private void DrawPackFiles()
    {
        const string Popup = "###PackFiles";
        if (_packFiles is null && !ImGui.IsPopupOpen(Popup)) return;
        if (_packFiles is not null && !ImGui.IsPopupOpen(Popup)) ImGui.OpenPopup(Popup);
        float font = ImGui.GetFontSize();
        ImGui.SetNextWindowSize(new Vector2(font * 34, font * 26), ImGuiCond.Appearing);
        bool open = true;
        if (!ImGui.BeginPopupModal(Localization.T("Pack files") + Popup, ref open)) return;
        if (!open || _packFiles is not { } state || _encounter is not { Pack: not null } encounter)
        {
            _packFiles = null;
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }
        bool picking = state.Timer >= 0 && state.Timer < encounter.Timers.Count;
        string folder = state.Folder;
        string? current = null;
        if (picking)
        {
            var timer = encounter.Timers[state.Timer];
            current = state.Sound < 0 ? timer.Icon : state.Sound < timer.Sounds.Count ? timer.Sounds[state.Sound].Sfx : null;
            current = EncounterPack.EntryName(current ?? "");
            ImGui.TextDisabled(Localization.T(folder == EncounterPack.SoundFolder ? "Pick a sound for the timer." : "Pick an icon for the timer."));
        }
        else if (ImGui.BeginTabBar("##PackFolders"))
        {
            foreach (string tab in new[] { EncounterPack.SoundFolder, EncounterPack.IconFolder })
                if (ImGui.BeginTabItem(Localization.T(tab == EncounterPack.SoundFolder ? "Sounds" : "Icons") + $" ({tab}/)"))
                {
                    folder = tab;
                    ImGui.EndTabItem();
                }
            ImGui.EndTabBar();
            _packFiles = state with { Folder = folder };
        }

        var used = encounter.Timers.SelectMany(t => t.Sounds.Select(s => s.Sfx).Append(t.Icon))
            .Select(path => path is null || Lucide.IsIcon(path) ? null : EncounterPack.EntryName(path))
            .OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var files = encounter.PackAssets(folder);
        string? picked = null;
        if (ImGui.BeginChild("##PackFileList", new Vector2(0, -ImGui.GetFrameHeightWithSpacing()), ImGuiChildFlags.Borders))
        {
            if (files.Count == 0) ImGui.TextDisabled(Localization.T("No files in this folder."));
            float thumbnail = ImGui.GetFrameHeight();
            foreach (string file in files)
            {
                ImGui.PushID(file);
                if (folder == EncounterPack.SoundFolder)
                {
                    if (ImGui.Button(Localization.T("Preview")) &&
                        _sounds.Request(encounter, PreviewTimer, new TimerSound { Sfx = file }) is { } task)
                        _previews.Add((task, 0));
                }
                else if (_icons.Get(encounter.Resolve(file)) is { } texture)
                    ImGui.Image(texture.Id, new Vector2(thumbnail));
                else ImGui.Dummy(new Vector2(thumbnail));
                ImGui.SameLine();
                string label = file;
                if (encounter.PackFiles.ContainsKey(file)) label += " " + Localization.T("(added, not saved yet)");
                bool unused = !used.Contains(file);
                if (unused) ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
                if (picking)
                {
                    ImGui.AlignTextToFramePadding();
                    if (ImGui.Selectable(label, string.Equals(file, current, StringComparison.OrdinalIgnoreCase))) picked = file;
                }
                else
                {
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextUnformatted(label);
                }
                if (unused) ImGui.PopStyleColor();
                if (unused && ImGui.IsItemHovered()) Tooltip(Localization.T("No timer uses this file; saving the pack removes it."));
                ImGui.PopID();
            }
        }
        ImGui.EndChild();
        if (picking)
        {
            ImGui.BeginDisabled(_packFilePicker is not null);
            if (ImGui.Button(Localization.T("Add file...")))
            {
                string[] extensions = folder == EncounterPack.SoundFolder ? EncounterYaml.SoundExtensions : EncounterYaml.ImageExtensions;
                string sourceFolder = Path.Combine(EncounterYaml.DataDirectory, folder);
                _packFilePicker = FilePicker.OpenAsync(Localization.T(folder == EncounterPack.SoundFolder ? "Sound files" : "Images"),
                    string.Join(';', extensions.Select(e => "*" + e)), Directory.Exists(sourceFolder) ? sourceFolder : null);
            }
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                Tooltip(Localization.F("Copy a file into the pack as {0}/<file name> and use it. It is written into the zip when the pack is saved.", folder));
            ImGui.SameLine();
        }
        if (ImGui.Button(Localization.T("Close"))) _packFiles = null;
        if (picked is not null)
        {
            SetAsset(encounter, state.Timer, state.Sound, picked);
            _packFiles = null;
        }
        if (_packFiles is null) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private static float ButtonsWidth(int count) =>
        count * (ImGui.CalcTextSize("Browse...").X + ImGui.GetStyle().FramePadding.X * 2 + ImGui.GetStyle().ItemSpacing.X + 24);

    private static string AddLine(string text, string line) => text.Length == 0 ? line : text + "\n" + line;

    // ---- Floating windows ----

    private void RenderOverlays(float deltaTime, long now)
    {
        if (_lastWorkAreaCheck == 0 || Stopwatch.GetElapsedTime(_lastWorkAreaCheck).TotalSeconds >= 2)
        {
            _workAreas = WindowPlacement.CurrentWorkAreas();
            _lastWorkAreaCheck = Stopwatch.GetTimestamp();
        }
        UpdateOverlayBounds();
        var timers = _scheduler.Snapshot;
        _settings = _settings with
        {
            Timeline = Render(ref _timelineWindow, "TipAura timeline", _settings.Timeline, OverlayPlacement.DefaultTimeline,
                null, size => OverlayViews.DrawTimeline(size, timers, now, Stopwatch.Frequency, _settings.MaxTimelineBars,
                    _icons, _encounter, !_settings.Timeline.ClickThrough,
                    _settings.TimelineMonochrome ? _settings.MonochromeColors : null)),
            Alerts = Render(ref _alertWindow, "TipAura alerts", _settings.Alerts, OverlayPlacement.DefaultAlerts,
                () => _alertGlyphs != 0 ? _alertGlyphs : _alertGlyphs = CjkGlyphs.RangesFor(OverlayPlaceholders()),
                size => OverlayViews.DrawAlerts(size, _alerts, now, Stopwatch.Frequency, _settings.AlertSeconds, _icons,
                    !_settings.Alerts.ClickThrough))
        };

        // Overlays are hidden, not closed, while the hooked window is missing; a drag ends as an offset from the anchor.
        OverlayPlacement Render(ref OverlayWindow? window, string title, OverlayPlacement placement, OverlayPlacement defaults,
            Func<nint>? glyphs, Action<Vector2> draw)
        {
            if (window?.IsExiting == true) { window.Dispose(); window = null; }
            if (!placement.Visible)
            {
                window?.Dispose();
                window = null;
                return placement;
            }
            try
            {
                var bounds = _overlayBounds;
                var location = OverlayLayout.Position(placement, defaults, bounds ?? PrimaryWorkArea(), _workAreas);
                window ??= new OverlayWindow(title, placement, location, _settings.Theme, null, glyphs);
                window.Shown = bounds is not null;
                if (bounds is not { } reference) return placement;
                placement = window.Draw(deltaTime, placement, location, _settings.Theme, null, draw);
                if (window.Moved is { } moved)
                {
                    placement = OverlayLayout.RememberPosition(placement, reference, moved);
                    window.Moved = null;
                }
                return placement;
            }
            catch (Exception ex)
            {
                _notice = Localization.F("Floating window failed: {0}", ex.Message);
                AppLog.Error("Overlay", $"{title} failed", ex);
                window?.Dispose();
                window = null;
                return placement with { Visible = false };
            }
        }
    }

    // ---- Discord capture ----

    // Discord's hook captures the first swap chain that presents and keeps it until it is released, so
    // in capture mode the timeline feed must be the only Present in the process: the main window and
    // About show their UI through DirectComposition surfaces, and the main window's HWND swap chain
    // presents the timeline overlay's frames, which only the hook sees.
    private void ApplyDiscordCapture()
    {
        if (_window.Composited == _settings.DiscordCapture) return;
        try
        {
            _window.Composited = _settings.DiscordCapture;
            _auxiliary.Composited = _settings.DiscordCapture;
            AppLog.Info("Capture", _settings.DiscordCapture ? "Discord capture on." : "Discord capture off.");
        }
        catch (Exception ex) { DiscordCaptureFailed(ex); }
    }

    private void PresentDiscordCapture()
    {
        if (!_window.Composited) return;
        try
        {
            _window.PresentCapture(_timelineWindow is { Shown: true } timeline ? timeline.FrameTexture : null,
                new Vector2i(_settings.Timeline.Width, _settings.Timeline.Height));
        }
        catch (Exception ex) { DiscordCaptureFailed(ex); }
    }

    private void DiscordCaptureFailed(Exception ex)
    {
        _notice = Localization.F("Discord capture failed: {0}", ex.Message);
        AppLog.Error("Capture", "Discord capture failed", ex);
        _settings = _settings with { DiscordCapture = false };
        _auxiliary.Composited = false;
        try { _window.Composited = false; }
        catch (Exception cleanup) { AppLog.Warn("Capture", $"Could not leave capture mode: {cleanup.Message}"); }
    }

    // ---- Window chrome ----

    private void MoveByTitleBar()
    {
        var mouse = ImGui.GetIO().MousePos;
        var origin = ImGui.GetWindowPos();
        float height = ImGui.GetFrameHeight();
        var action = _titleButtons.Draw(origin, ImGui.GetWindowSize().X, height);
        if (action == TitleBarAction.Minimize) _minimizeRequested = true;
        if (action == TitleBarAction.Close) _closeRequested = true;
        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left) && ImGui.IsWindowHovered()
            && mouse.Y >= origin.Y && mouse.Y < origin.Y + height
            && mouse.X < origin.X + ImGui.GetWindowSize().X - height * 2)
        {
            _draggingWindow = true;
            _dragStartWindow = _window.Location;
            _dragStartCursor = _window.Location + new Vector2i((int)mouse.X, (int)mouse.Y);
        }
    }

    private void DragWindow()
    {
        if (!_draggingWindow) return;
        if (!ImGui.IsMouseDown(ImGuiMouseButton.Left)) { _draggingWindow = false; return; }
        var mouse = ImGui.GetIO().MousePos;
        var cursor = _window.Location + new Vector2i((int)mouse.X, (int)mouse.Y);
        _window.Location = _dragStartWindow + cursor - _dragStartCursor;
    }

    private void SaveSettings(bool force = false)
    {
        var location = _window.Location;
        var size = _window.ClientSize;
        if (!_window.IsMinimized)
            _settings = _settings with { WindowX = location.X, WindowY = location.Y, WindowWidth = size.X, WindowHeight = size.Y };
        if (_settings == _savedSettings && !_hooksDirty) return;
        // Debounced, and never mid-drag, so sliders and window moves do not write the file every frame.
        if (!force && (_draggingWindow || ImGui.IsAnyItemActive() || Stopwatch.GetElapsedTime(_lastSave).TotalSeconds < 1)) return;
        try
        {
            if (_settings != _savedSettings)
            {
                AppSettingsStore.Save(_settings, _settingsPath);
                _savedSettings = _settings;
            }
            if (_hooksDirty)
            {
                WindowHookStore.Save(_hooks, _hooksPath);
                _hooksDirty = false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.WarnThrottled("Settings", "save", $"Could not save settings: {ex.Message}");
        }
        _lastSave = Stopwatch.GetTimestamp();
    }

    internal static void SendKey(Key key, bool down)
    {
        var io = ImGui.GetIO();
        var mapped = key switch
        {
            >= Key.A and <= Key.Z => ImGuiKey.A + (key - Key.A),
            >= Key.Number0 and <= Key.Number9 => ImGuiKey._0 + (key - Key.Number0),
            >= Key.Keypad0 and <= Key.Keypad9 => ImGuiKey.Keypad0 + (key - Key.Keypad0),
            Key.Tab => ImGuiKey.Tab,
            Key.Left => ImGuiKey.LeftArrow,
            Key.Right => ImGuiKey.RightArrow,
            Key.Up => ImGuiKey.UpArrow,
            Key.Down => ImGuiKey.DownArrow,
            Key.PageUp => ImGuiKey.PageUp,
            Key.PageDown => ImGuiKey.PageDown,
            Key.Home => ImGuiKey.Home,
            Key.End => ImGuiKey.End,
            Key.Insert => ImGuiKey.Insert,
            Key.Delete => ImGuiKey.Delete,
            Key.Backspace => ImGuiKey.Backspace,
            Key.Space => ImGuiKey.Space,
            Key.Enter => ImGuiKey.Enter,
            Key.KeypadEnter => ImGuiKey.KeypadEnter,
            Key.Escape => ImGuiKey.Escape,
            Key.ControlLeft => ImGuiKey.LeftCtrl,
            Key.ControlRight => ImGuiKey.RightCtrl,
            Key.ShiftLeft => ImGuiKey.LeftShift,
            Key.ShiftRight => ImGuiKey.RightShift,
            Key.AltLeft => ImGuiKey.LeftAlt,
            Key.AltRight => ImGuiKey.RightAlt,
            Key.Apostrophe => ImGuiKey.Apostrophe,
            Key.Comma => ImGuiKey.Comma,
            Key.Minus => ImGuiKey.Minus,
            Key.Period => ImGuiKey.Period,
            Key.Slash => ImGuiKey.Slash,
            Key.Semicolon => ImGuiKey.Semicolon,
            Key.Equal => ImGuiKey.Equal,
            Key.LeftBracket => ImGuiKey.LeftBracket,
            Key.BackSlash => ImGuiKey.Backslash,
            Key.RightBracket => ImGuiKey.RightBracket,
            Key.GraveAccent => ImGuiKey.GraveAccent,
            _ => ImGuiKey.None
        };
        if (mapped != ImGuiKey.None) io.AddKeyEvent(mapped, down);
        // ImGui tracks modifier state separately for shortcuts such as Ctrl+A in text fields.
        if (key is Key.ControlLeft or Key.ControlRight) io.AddKeyEvent(ImGuiKey.ModCtrl, down);
        if (key is Key.ShiftLeft or Key.ShiftRight) io.AddKeyEvent(ImGuiKey.ModShift, down);
        if (key is Key.AltLeft or Key.AltRight) io.AddKeyEvent(ImGuiKey.ModAlt, down);
    }

#if TIPAURA_AGENT_SELF_TEST
    internal (int Timers, int Errors, int Running, int Alerts, int OverlaysOpen) StateForSmoke =>
        (_encounter?.Timers.Count ?? 0, _issues.Count(i => i.IsError), _scheduler.Snapshot.Length, _alerts.Count,
            (_timelineWindow is null ? 0 : 1) + (_alertWindow is null ? 0 : 1));
    internal bool IconFontComplete => _renderer.HasAllIcons;
    internal AppWindow WindowForSmoke => _window;
    internal AppSettings SettingsForSmoke { get => _settings; set => _settings = value; }
    internal string NoticeForSmoke => _notice;
    internal void OpenAboutForSmoke() => _auxiliary.OpenAbout();
    internal void StartForSmoke(int slot) => _scheduler.Start(slot);
    internal void ResetAllForSmoke() => _scheduler.ResetAll();
    internal void SetSlotEnabledForSmoke(int slot, bool enabled)
    {
        foreach (var timer in _encounter!.Timers.Where(t => t.Keys.Contains(slot))) SetTimerEnabled(timer.Id, enabled);
    }
    internal void ShowTestAlertForSmoke() =>
        _alerts.Add(new ActiveAlert(_encounter!.Timers[0], _encounter, Stopwatch.GetTimestamp()));

    internal void RenderFramesForSmoke(int frames)
    {
        for (int i = 0; i < frames; i++)
        {
            _window.NewInputFrame();
            _window.DoEvents();
            Frame(1 / 60f, Stopwatch.GetTimestamp());
            Thread.Sleep(16);
        }
    }

    // Selects each tab for a few frames, with a timer open in the editor.
    // True opens the Lucide picker on the next frame, false closes it.
    private bool? _lucidePickerForSmoke;

    // Opens the Lucide picker on the last example timer (lucide:tornado), then filters it, and draws that
    // timer's glyph on the timeline and in an alert.
    internal void LucideForSmoke()
    {
        _selectedTimer = _encounter!.Timers.Count - 1;
        _tabRequest = 1;
        _lucideFilter = "";
        _lucideCategory = 0;
        _lucidePickerForSmoke = true;
        RenderFramesForSmoke(3);
        SelfTest.Check(_icons.GlyphCountForSmoke >= 50, $"The Lucide picker should draw a grid of glyphs; {_icons.GlyphCountForSmoke} were rasterized.");
        _lucideFilter = "torn";
        _lucideCategory = Array.FindIndex(Lucide.Categories, c => c.Category == Lucide.Category.Weather) + 1;
        RenderFramesForSmoke(3);
        var timer = _encounter.Timers[^1];
        SelfTest.Check(_icons.Get(_encounter.Resolve(timer.Icon)) is { AlphaOnly: true, Width: 128 }, "lucide:tornado should load as an alpha-only glyph.");
        _scheduler.Start(timer.Keys.Slots.First());
        _alerts.Add(new ActiveAlert(timer, _encounter, Stopwatch.GetTimestamp()));
        _lucidePickerForSmoke = false;
        RenderFramesForSmoke(5);
        _scheduler.ResetAll();
        _lucideFilter = "";
        _lucideCategory = 0;
    }

    internal void ShowAllTabsForSmoke()
    {
        _selectedTimer = 0;
        for (int tab = 0; tab < 3; tab++)
        {
            _tabRequest = tab;
            RenderFramesForSmoke(3);
        }
        // The last example timer has several keys and sounds, a provider and a note; sort the list too.
        _selectedTimer = _encounter!.Timers.Count - 1;
        _settings = _settings with { TimerSort = 2 };
        _tabRequest = 1;
        RenderFramesForSmoke(3);
    }

    // Edits, undo/redo, discard and save on the Timers tab, with a note long enough to scroll.
    internal void EditorForSmoke(string savePath)
    {
        string name = _encounter!.Name;
        _tabRequest = 1;
        _selectedTimer = 0;
        Edit(_encounter with { Name = "smoke" });
        EditTimer(0, _encounter.Timers[0] with { Note = string.Join('\n', Enumerable.Range(1, 15)) });
        RenderFramesForSmoke(3);
        SelfTest.Check(IsDirty && _history.UndoCount == 2, $"Two edits should be two undo steps, found {_history.UndoCount}.");
        Undo(redo: false);
        Undo(redo: false);
        SelfTest.Check(!IsDirty && _encounter.Name == name, "Undoing every edit should return to the saved state.");
        Undo(redo: true);
        SelfTest.Check(IsDirty && _encounter.Name == "smoke", "Redo should reapply the edit.");
        DiscardChanges();
        SelfTest.Check(!IsDirty && _encounter.Name == name, "Discard should reload the file.");
        Undo(redo: false);
        SelfTest.Check(IsDirty && _encounter.Name == "smoke", "Discard should be undoable.");
        Save(savePath);
        RenderFramesForSmoke(3);
        SelfTest.Check(!IsDirty && _toast is not null && _history.CanUndo, "Saving should show the toast and keep the history.");
        LoadEncounter(savePath);
        SelfTest.Check(!_history.CanUndo && !_history.CanRedo, "Loading a file should clear the history.");
    }

    // Window hooks: a hook whose window does not exist hides both overlays and remembers the encounter file;
    // the Program Manager desktop window (when listed) is a real window to anchor to; the hotkey gate follows
    // the active hook; the hook file is written next to the settings.
    internal void HooksForSmoke()
    {
        string example = _encounterPath!;
        AddHook(new WindowHook { Name = "missing", Pattern = $"tipaura-missing-{Guid.NewGuid():N}.exe" });
        SelectHook(_hooks.Hooks.Count - 1);
        _settings = _settings with { HotkeysRequireHookFocus = true };
        _tabRequest = 2;
        RenderFramesForSmoke(5);
        SelfTest.Check(_overlayBounds is null && _timelineWindow is { Shown: false } && _alertWindow is { Shown: false },
            "Overlays should hide while the hooked window is missing.");
        SelfTest.Check(_hooks.ActiveHook!.EncounterPath == RememberedPath(example), "A hook without a file should remember the current one.");
        SelfTest.Check(_scheduler.Gate is { } gate && ReferenceEquals(gate.Hook, _hooks.ActiveHook), "The hotkey gate should follow the active hook.");
        _runningWindows = WindowFinder.List();
        if (_runningWindows.Any(w => w.Title == "Program Manager" && string.Equals(w.ProcessName, "explorer", StringComparison.OrdinalIgnoreCase)))
        {
            AddHook(new WindowHook { Name = "desktop", Match = WindowMatchKind.Title, Pattern = "Program Manager", EncounterPath = RememberedPath(example) });
            SelectHook(_hooks.Hooks.Count - 1);
            RenderFramesForSmoke(5);
            SelfTest.Check(_overlayBounds is { } bounds && _timelineWindow is { Shown: true } timeline &&
                timeline.Location == OverlayLayout.Position(_settings.Timeline, OverlayPlacement.DefaultTimeline, bounds, _workAreas),
                "The timeline should be placed at its anchor of the hooked window.");
        }
        else Console.WriteLine("Program Manager is not listed; skipping the found-window check.");
        SelectHook(-1);
        _settings = _settings with { HotkeysRequireHookFocus = false };
        RenderFramesForSmoke(3);
        SelfTest.Check(_scheduler.Gate is null && _timelineWindow is { Shown: true } && _overlayBounds == PrimaryWorkArea(),
            "Without a hook the overlays should follow the primary work area.");
        SaveSettings(force: true);
        SelfTest.Check(WindowHookStore.Load(_hooksPath) is { Active: -1 } saved && saved.Hooks.Count == _hooks.Hooks.Count,
            "The hook file should be saved.");
        _tabRequest = 0;
        RenderFramesForSmoke(3);
    }

    // Loads a format 1 file, shows the upgrade notice and upgrades it the way the button does.
    internal void UpgradeForSmoke(string path)
    {
        LoadEncounter(path);
        _tabRequest = 0;
        RenderFramesForSmoke(3);
        SelfTest.Check(_outdated is { Version: 1, Tagged: false } && _encounter is { Timers.Count: 6 }, "A format 1 file should offer an upgrade.");
        Save(_encounterPath!);
        RenderFramesForSmoke(3);
        SelfTest.Check(_outdated is null && File.Exists(path + ".v1.bak") && _toast?.Text.Contains(".v1.bak", StringComparison.Ordinal) == true,
            $"Upgrading should keep a backup and say so: {_toast?.Text} {_notice}");
        LoadEncounter(path);
        SelfTest.Check(_outdated is null, "An upgraded file should not offer another upgrade.");
    }

    // Loads a copy of a pack and draws its icon from the zip on every window; shows the pack files modal and
    // its pick mode; adds a file to the pack and saves the pack in place; extracts it into a data folder,
    // first without conflicts, then overwriting a changed file; and exports it to another pack.
    internal void PackForSmoke(string source, string folder)
    {
        string pack = Path.Combine(folder, EncounterPack.DirectoryName, Path.GetFileName(source));
        Directory.CreateDirectory(Path.GetDirectoryName(pack)!);
        File.Copy(source, pack, true);
        LoadEncounter(pack);
        SelfTest.Check(_encounter is { Pack: not null, Timers.Count: 2 } && !_issues.Any(), "The pack should load: " + string.Join("; ", _issues));
        ShowAllTabsForSmoke();
        _scheduler.Start(1);
        ShowTestAlertForSmoke();
        RenderFramesForSmoke(5);
        SelfTest.Check(_icons.Get(_encounter!.Resolve("icons/fire.png")) is not null, "The pack icon should load from the zip.");

        _tabRequest = 1;
        _selectedTimer = 0;
        _packFiles = (EncounterPack.IconFolder, -1, -1);
        RenderFramesForSmoke(3);
        _packFiles = (EncounterPack.SoundFolder, 0, -1);
        RenderFramesForSmoke(3);
        _packFiles = null;
        RenderFramesForSmoke(2);
        SelfTest.Check(!ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId), "The pack files modal should close.");

        string frost = SelfTest.ProjectPath("data/icons/frost.png");
        var (added, entry) = EncounterYaml.AddPackFile(_encounter, EncounterPack.IconFolder, frost);
        SetAsset(added, 0, -1, entry);
        SelfTest.Check(entry == "icons/frost.png" && _encounter.PackFiles.Count == 1 && IsDirty &&
            _icons.Get(_encounter.Resolve(entry)) is not null, $"Adding a file to the pack should use it before saving: {entry}");
        Save(_encounterPath!);
        RenderFramesForSmoke(3);
        SelfTest.Check(_encounter is { Pack: { } saved, PackFiles.Count: 0 } && saved.Contains("icons/frost.png") && !IsDirty && _encounterPath == pack &&
            _icons.Get(_encounter.Resolve("icons/frost.png")) is not null, $"Saving the pack should write the added file into the zip: {_notice}");

        string data = Path.Combine(folder, "data");
        StartExtraction(data);
        RenderFramesForSmoke(3);
        string yaml = Path.Combine(data, "demo.yaml"), icon = Path.Combine(data, "icons", "demo_frost.png");
        SelfTest.Check(_extraction is null && _encounter is { Pack: null } && _encounterPath == yaml && File.Exists(icon) && !_issues.Any() &&
            _encounter.Timers[0].Icon == "icons/demo_frost.png", $"Extracting should write prefixed files into data and open the YAML: {_notice} " +
            string.Join("; ", _issues));

        LoadEncounter(pack);
        File.WriteAllText(icon, "changed");
        StartExtraction(data);
        RenderFramesForSmoke(3);
        SelfTest.Check(_extraction is not null && _extractConflicts is [{ Label: "icons/demo_frost.png" }] && ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId),
            "A changed file should be asked about.");
        _extractOverwrite.Add(_extractConflicts[0].Label);
        _extractNext = 1;
        FinishExtraction();
        RenderFramesForSmoke(3);
        SelfTest.Check(File.ReadAllBytes(icon).AsSpan().SequenceEqual(File.ReadAllBytes(frost)) && _encounterPath == yaml &&
            !ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId), $"Overwriting should restore the file and close the prompt: {_notice}");

        string copy = Path.Combine(folder, "copy", "demo" + EncounterPack.Extension);
        WritePack(copy);
        SelfTest.Check(_encounterPath == yaml && EncounterYaml.Load(copy) is { HasErrors: false, Issues.Count: 0 },
            $"Export pack should write a loadable pack and keep the loose file open: {_notice}");
    }
#endif

    public void Dispose()
    {
        _scheduler.Dispose();
        _timelineWindow?.Dispose();
        _alertWindow?.Dispose();
        _auxiliary.Dispose();
        _icons.Dispose();
        _audio.Dispose();
        _renderer.Dispose();
        _window.Dispose();
    }
}
