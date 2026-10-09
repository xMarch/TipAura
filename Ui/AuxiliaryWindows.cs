using System.Diagnostics;
using System.Numerics;
using ImGuiNET;
using Vortice.Mathematics;

// Render secondary OS windows on the main GLFW thread, each with its own swap chain and ImGui context.
internal sealed class AuxiliaryWindows(AppWindow parent) : IDisposable
{
    private AuxiliaryWindow? _about;
    private bool _openAbout;
    private string _aboutMessage = "";
    private int _selectedNotice, _aboutTabRequest = -1;

    // Keep the Disclaimer sections in README.md and TECHNICAL.md in sync with these localization keys.
    private static readonly string[] Disclaimer =
    [
        "TipAura is an unofficial tool. It is not developed, endorsed or supported by any game developer or publisher. Game names and trademarks belong to their respective owners.",
        "TipAura only listens to the keyboard to start its own timers; it does not modify the game, read its memory or send input to it. Users are responsible for determining whether its use complies with the game's terms of service and bear any resulting risk.",
        "Timer alerts depend on the encounter files and the user's key presses and may be late, early or missing. Use them for reference only.",
        "Versions modified, redistributed or further developed by others are their own responsibility; the original author is not liable for any disputes or damages arising from them.",
        "TipAura is provided \"as is\", without warranty of any kind. The author shall not be liable for any damages arising from the use or inability to use this software.",
    ];
    private static string[]? _disclaimerText;
    private readonly LicenseTextView _licenseText = new();
    internal bool NeedsFrames => _about is not null || _openAbout;
    // Discord capture: show these windows through DirectComposition so they never present a swap chain.
    internal bool Composited { get; set; }

    internal void OpenAbout() { if (_about is not null) _about.Focus(); else _openAbout = true; }

#if TIPAURA_AGENT_SELF_TEST
    internal void SelectAboutForSmoke(int tab, int notice) { _aboutTabRequest = tab; _selectedNotice = notice; }
#endif

    internal void Render(float deltaTime, int theme, int size, string? fontPath)
    {
        if (!NeedsFrames) return;
        if (_openAbout && _about is null)
        {
            _about = new AuxiliaryWindow(parent, "About TipAura", new Vector2i(780, 540), theme, size, fontPath);
            _openAbout = false;
        }
        RenderWindow(ref _about, deltaTime, theme, size, fontPath, "About TipAura", DrawAbout);
    }

    private void DrawAbout()
    {
        if (!ImGui.BeginTabBar("AboutTabs")) return;
        if (BeginAboutTab("Overview", 0))
        {
            ImGui.TextUnformatted("TipAura");
            ImGui.SameLine();
            ImGui.TextDisabled(Localization.F("Version {0}", BuildInfo.Version));
            ImGui.Spacing();
            if (BuildInfo.RepositoryUrl.Length > 0)
            {
                ImGui.TextUnformatted("GitHub:");
                ImGui.SameLine();
                DrawLink(BuildInfo.RepositoryUrl);
            }
            ImGui.TextWrapped(Localization.T("TipAura is released under the MIT License. Bundled components keep their own licenses."));
            if (_aboutMessage.Length > 0) { ImGui.PushTextWrapPos(0); ImGui.TextUnformatted(_aboutMessage); ImGui.PopTextWrapPos(); }
            ImGui.SeparatorText(Localization.T("Disclaimer"));
            _disclaimerText ??= [.. Disclaimer.Select(p => KeepWithCjk(Localization.T(p)))];
            foreach (string paragraph in _disclaimerText)
            {
                ImGui.Bullet();
                ImGui.TextWrapped(paragraph);
            }
            ImGui.EndTabItem();
        }
        if (BeginAboutTab("License", 1))
        {
            _licenseText.Draw("TipAuraLicense", ThirdPartyNotice.TipAura.Text);
            ImGui.EndTabItem();
        }
        if (BeginAboutTab("Third-party licenses", 2))
        {
            // A full-width selector leaves the whole pane to the notice text, most of which is wrapped at 80 columns.
            var notices = ThirdPartyNotice.All;
            var notice = notices[_selectedNotice];
            ImGui.SetNextItemWidth(-1);
            if (ImGui.BeginCombo("##Notice", $"{notice.Name} {notice.Version}"))
            {
                for (int i = 0; i < notices.Length; i++)
                    if (ImGui.Selectable($"{notices[i].Name} {notices[i].Version}##Notice{i}", i == _selectedNotice))
                        _selectedNotice = i;
                ImGui.EndCombo();
            }
            ImGui.TextDisabled(notice.License);
            ImGui.SameLine();
            DrawLink(notice.Url);
            _licenseText.Draw("NoticeText", notice.Text);
            ImGui.EndTabItem();
        }
        ImGui.EndTabBar();
        _aboutTabRequest = -1;
    }

    // ImGui.NET only exposes tab flags together with p_open, which would add a close button.
    private unsafe bool BeginAboutTab(string label, int index)
    {
        byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(Localization.T(label) + "\0");
        fixed (byte* text = utf8)
            return ImGuiNative.igBeginTabItem(text, null,
                _aboutTabRequest == index ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None) != 0;
    }

    // ImGui wraps only at spaces, so "TipAura 為…" would break right after "TipAura" and leave the CJK run to
    // wrap by width on the next line. A no-break space next to CJK text keeps the visual gap without that break.
    internal static string KeepWithCjk(string text)
    {
        static bool IsCjk(char c) => c >= '⺀';
        var chars = text.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (chars[i] == ' ' && ((i > 0 && IsCjk(chars[i - 1])) || (i + 1 < chars.Length && IsCjk(chars[i + 1]))))
                chars[i] = ' ';
        return new string(chars);
    }

    private void DrawLink(string url)
    {
        if (ImGui.TextLink(url))
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); _aboutMessage = ""; }
            catch (Exception ex)
            {
                _aboutMessage = Localization.F("Could not open link: {0}", ex.Message);
                AppLog.Warn("UI", $"Could not open link {url}: {ex.Message}");
            }
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Localization.T("Click to open in the browser"));
    }

    private void RenderWindow(ref AuxiliaryWindow? auxiliary, float deltaTime, int theme, int size,
        string? fontPath, string name, Action drawContents)
    {
        if (auxiliary is null) return;
        if (auxiliary.IsExiting)
        {
            auxiliary.Dispose();
            auxiliary = null;
            return;
        }
        bool open = auxiliary.Draw(deltaTime, theme, size, fontPath, name, Composited, drawContents);
        if (!open)
        {
            auxiliary.Dispose();
            auxiliary = null;
        }
    }

    public void Dispose()
    {
        _about?.Dispose();
        _about = null;
    }

    private sealed class AuxiliaryWindow : IDisposable
    {
        private static readonly Color4 BackgroundColor = new(0.12f, 0.13f, 0.16f, 1f);
        private readonly AppWindow _window;
        private readonly ImGuiRenderer _renderer;
        private int _theme, _size;
        private string? _fontPath;
        private bool _dragging;
        private Vector2i _dragStartWindow, _dragStartMouse;

        internal bool IsExiting => _window.IsClosing;
        internal void Focus() => _window.Focus();

        internal AuxiliaryWindow(AppWindow parent, string title, Vector2i size, int theme, int fontSize, string? fontPath)
        {
            _window = new AppWindow(new AppWindowOptions(Localization.T(title), size) { Location = parent.Location + new Vector2i(80, 80) });
            _renderer = new ImGuiRenderer(fontSize, fontPath);
            _renderer.ApplyTheme(theme);
            _theme = theme; _size = fontSize; _fontPath = fontPath;
            _window.KeyChar += c => { _renderer.SetCurrentContext(); ImGui.GetIO().AddInputCharacterUTF16(c); };
            _window.KeyDown += key => { _renderer.SetCurrentContext(); MainWindow.SendKey(key, true); };
            _window.KeyUp += key => { _renderer.SetCurrentContext(); MainWindow.SendKey(key, false); };
        }

        internal bool Draw(float deltaTime, int theme, int size, string? fontPath, string title, bool composited, Action drawContents)
        {
            try { _window.Composited = composited; }
            catch (Exception ex) { AppLog.Error("Capture", $"{title} could not switch composition", ex); }
            if (_size != size || !string.Equals(_fontPath, fontPath, StringComparison.OrdinalIgnoreCase))
            {
                _renderer.UpdateFont(size, fontPath);
                _size = size; _fontPath = fontPath;
            }
            if (_theme != theme) { _renderer.ApplyTheme(theme); _theme = theme; }
            _window.BeginRender(BackgroundColor);
            _renderer.BeginFrame(_window, deltaTime);
            ImGui.SetNextWindowPos(Vector2.Zero);
            ImGui.SetNextWindowSize(ImGui.GetIO().DisplaySize);
            bool open = true;
            if (ImGui.Begin($"{Localization.T(title)}###Auxiliary", ref open,
                ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings))
            {
                MoveByTitleBar();
                drawContents();
            }
            ImGui.End();
            if (_dragging)
            {
                if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
                {
                    var mouse = ImGui.GetIO().MousePos;
                    _window.Location = _dragStartWindow + _window.Location
                        + new Vector2i((int)mouse.X, (int)mouse.Y) - _dragStartMouse;
                }
                else _dragging = false;
            }
            _renderer.EndFrame(_window.FramebufferSize.X, _window.FramebufferSize.Y);
            _window.Present();
            _window.NewInputFrame();
            return open;
        }

        private void MoveByTitleBar()
        {
            var mouse = ImGui.GetIO().MousePos;
            var origin = ImGui.GetWindowPos();
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left) && ImGui.IsWindowHovered()
                && mouse.Y >= origin.Y && mouse.Y < origin.Y + ImGui.GetFrameHeight()
                && mouse.X < origin.X + ImGui.GetWindowSize().X - ImGui.GetFrameHeight() * 2)
            {
                _dragging = true;
                _dragStartWindow = _window.Location;
                _dragStartMouse = _window.Location + new Vector2i((int)mouse.X, (int)mouse.Y);
            }
        }

        public void Dispose()
        {
            _renderer.Dispose();
            _window.Dispose();
        }
    }
}
