using System.Numerics;
using ImGuiNET;
using Silk.NET.Input;
using Vortice.Direct3D11;
using Vortice.Mathematics;

// A borderless, per-pixel transparent window that never takes focus from the game. Click-through
// windows ignore the mouse entirely; otherwise the whole window drags and its edges resize it.
// The timeline and the center alerts each use one.
internal sealed class OverlayWindow : IDisposable
{
    private readonly AppWindow _window;
    private readonly ImGuiRenderer _renderer;
    private readonly string _id;
    private int _fontSize, _theme, _dragEdges; // left=1, right=2, top=4, bottom=8; 16=move
    private string? _fontPath;
    private bool _clickThrough, _topmost, _shown = true;
    private Vector2i _startMouse, _startPosition, _startSize;
    private Vector2i? _requestedSize;

    internal OverlayWindow(string title, OverlayPlacement placement, Vector2i location, int theme, string? fontPath,
        Func<nint>? glyphRanges = null)
    {
        _id = "##" + title;
        _window = new AppWindow(new AppWindowOptions(title, new Vector2i(placement.Width, placement.Height))
        {
            Location = location, Visible = false, Layered = true
        });
        _renderer = new ImGuiRenderer(placement.FontSize, fontPath, glyphRanges: glyphRanges);
        _renderer.ApplyTheme(theme);
        _fontSize = placement.FontSize; _fontPath = fontPath; _theme = theme;
        nint hwnd = _window.Hwnd;
        nint exStyle = Native.GetWindowLongPtr(hwnd, Native.GwlExStyle);
        Native.SetWindowLongPtr(hwnd, Native.GwlExStyle,
            (nint)((exStyle.ToInt64() & ~Native.WsExAppWindow) | Native.WsExNoActivate | Native.WsExToolWindow));
        Native.SetWindowPos(hwnd, 0, 0, 0, 0, 0,
            Native.SwpNoMove | Native.SwpNoSize | Native.SwpNoActivate | Native.SwpNoZOrder | Native.SwpFrameChanged);
        _clickThrough = !placement.ClickThrough;
        _topmost = !placement.Topmost;
        ApplyBehavior(placement);
        // Show without activation so the game keeps keyboard focus.
        Native.ShowWindow(hwnd, 4); // SW_SHOWNOACTIVATE
    }

    internal bool IsExiting => _window.IsClosing;
    internal Vector2i Location => _window.Location;
    // Set after a drag or resize ends to the window's new screen position; the caller stores it as an
    // offset from its anchor and clears it.
    internal Vector2i? Moved { get; set; }

    // Hides the window without disposing it (and its font atlas) while the hooked window is missing.
    internal bool Shown
    {
        get => _shown;
        set
        {
            if (_shown == value) return;
            _shown = value;
            _dragEdges = 0;
            Native.ShowWindow(_window.Hwnd, value ? 4 : 0); // SW_SHOWNOACTIVATE / SW_HIDE
        }
    }
    internal ImGuiRenderer Renderer => _renderer;
    // The last drawn frame, for the Discord capture feed.
    internal ID3D11Texture2D? FrameTexture => _window.LayeredFrame;

    // Rebuilds the font atlas when the encounter text changed (the alert font is built from it).
    internal void RefreshGlyphs() => _renderer.UpdateFont(_fontSize, _fontPath);

    // Draws one frame at location (screen pixels). The returned placement carries the size after a resize;
    // the position after a drag or resize is reported through Moved.
    internal OverlayPlacement Draw(float deltaTime, OverlayPlacement placement, Vector2i location, int theme,
        string? fontPath, Action<Vector2> drawContents)
    {
        if (_fontSize != placement.FontSize || _renderer.GlyphsStale ||
            !string.Equals(_fontPath, fontPath, StringComparison.OrdinalIgnoreCase))
        {
            _renderer.UpdateFont(placement.FontSize, fontPath);
            _fontSize = placement.FontSize; _fontPath = fontPath;
        }
        if (_theme != theme) { _renderer.ApplyTheme(theme); _theme = theme; }
        ApplyBehavior(placement);
        if (_dragEdges == 0)
        {
            if (_window.Location != location) _window.Location = location;
            var size = new Vector2i(placement.Width, placement.Height);
            if (_window.ClientSize != size) _window.ClientSize = size;
        }
        // Fully transparent layered pixels ignore the mouse; a 1/255 alpha floor keeps the whole
        // window draggable while click-through is off, without a visible tint.
        _window.BeginRender(new Color4(0f, 0f, 0f, placement.ClickThrough ? 0f : 1f / 255));
        _renderer.BeginFrame(_window, deltaTime);
        ImGui.SetNextWindowPos(Vector2.Zero);
        ImGui.SetNextWindowSize(ImGui.GetIO().DisplaySize);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8, 6));
        if (ImGui.Begin(_id, ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            var size = ImGui.GetIO().DisplaySize;
            var draw = ImGui.GetWindowDrawList();
            var background = _theme == 1 ? new Vector3(0.96f, 0.96f, 0.97f) : new Vector3(0.08f, 0.09f, 0.12f);
            if (placement.BackgroundOpacity > 0)
                draw.AddRectFilled(Vector2.Zero, size, ImGui.GetColorU32(new Vector4(background, placement.BackgroundOpacity)), 6f);
            // While unlocked, outline the window so an empty overlay can still be found and placed.
            if (!placement.ClickThrough)
                draw.AddRect(Vector2.One, size - Vector2.One, ImGui.GetColorU32(new Vector4(0.95f, 0.75f, 0.2f, 0.9f)), 6f, 0, 2f);
            drawContents(size);
        }
        ImGui.End();
        ImGui.PopStyleVar();
        if (!placement.ClickThrough) placement = HandleMouse(placement);
        _renderer.EndFrame(_window.FramebufferSize.X, _window.FramebufferSize.Y);
        _window.Present();
        if (_requestedSize is { } requested)
        {
            if (_window.ClientSize != requested) _window.ClientSize = requested;
            _requestedSize = null;
        }
        _window.NewInputFrame();
        return placement;
    }

    private void ApplyBehavior(OverlayPlacement placement)
    {
        nint hwnd = _window.Hwnd;
        if (_clickThrough != placement.ClickThrough)
        {
            long exStyle = Native.GetWindowLongPtr(hwnd, Native.GwlExStyle).ToInt64();
            Native.SetWindowLongPtr(hwnd, Native.GwlExStyle, (nint)(placement.ClickThrough
                ? exStyle | Native.WsExTransparent : exStyle & ~Native.WsExTransparent));
            _clickThrough = placement.ClickThrough;
            _dragEdges = 0;
        }
        if (_topmost != placement.Topmost)
        {
            Native.SetWindowPos(hwnd, placement.Topmost ? -1 : -2,
                0, 0, 0, 0, Native.SwpNoMove | Native.SwpNoSize | Native.SwpNoActivate);
            _topmost = placement.Topmost;
        }
    }

    private OverlayPlacement HandleMouse(OverlayPlacement placement)
    {
        var pos = _window.MousePosition;
        var client = _window.ClientSize;
        int edges = 0;
        const int margin = 9;
        if (pos.X >= 0 && pos.Y >= 0 && pos.X < client.X && pos.Y < client.Y)
        {
            if (pos.X < margin) edges |= 1;
            if (pos.X >= client.X - margin) edges |= 2;
            if (pos.Y < margin) edges |= 4;
            if (pos.Y >= client.Y - margin) edges |= 8;
        }
        if (_dragEdges == 0 && _window.IsButtonPressed(MouseButton.Left))
        {
            _dragEdges = edges == 0 ? 16 : edges;
            _startPosition = _window.Location;
            _startSize = client;
            Native.GetCursorPos(out var pointer);
            _startMouse = new Vector2i(pointer.X, pointer.Y);
        }
        if (_dragEdges != 0)
        {
            if (_window.IsButtonDown(MouseButton.Left))
            {
                Native.GetCursorPos(out var pointer);
                var delta = new Vector2i(pointer.X - _startMouse.X, pointer.Y - _startMouse.Y);
                if (_dragEdges == 16) _window.Location = _startPosition + delta;
                else
                {
                    int dx = (_dragEdges & 1) != 0 ? delta.X : (_dragEdges & 2) != 0 ? -delta.X : 0;
                    int dy = (_dragEdges & 4) != 0 ? delta.Y : (_dragEdges & 8) != 0 ? -delta.Y : 0;
                    var size = new Vector2i(Math.Max(OverlayPlacement.MinWidth, _startSize.X - dx),
                        Math.Max(OverlayPlacement.MinHeight, _startSize.Y - dy));
                    _window.Location = _startPosition + new Vector2i((_dragEdges & 1) != 0 ? _startSize.X - size.X : 0,
                        (_dragEdges & 4) != 0 ? _startSize.Y - size.Y : 0);
                    _requestedSize = size;
                }
            }
            else
            {
                _dragEdges = 0;
                Moved = _window.Location;
                placement = placement with { Width = _window.ClientSize.X, Height = _window.ClientSize.Y };
            }
        }
        _window.SetCursor((_dragEdges != 0 ? _dragEdges : edges) switch
        {
            16 => StandardCursor.ResizeAll,
            1 or 2 => StandardCursor.HResize, 4 or 8 => StandardCursor.VResize,
            5 or 10 => StandardCursor.NwseResize, 6 or 9 => StandardCursor.NeswResize,
            _ => StandardCursor.Default
        });
        return placement;
    }

    public void Dispose()
    {
        _renderer.Dispose();
        _window.Dispose();
    }
}
