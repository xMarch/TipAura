using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Input;
using Silk.NET.Input.Glfw;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using Silk.NET.Windowing.Glfw;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

internal sealed record AppWindowOptions(string Title, Vector2i ClientSize)
{
    public Vector2i? Location { get; init; }
    public bool Visible { get; init; } = true;
    public bool Borderless { get; init; } = true;
    // Per-pixel transparency through UpdateLayeredWindow instead of a swap chain.
    public bool Layered { get; init; }
}

// A Silk.NET (GLFW) OS window with Direct3D 11 presentation and per-frame polled mouse state.
internal sealed class AppWindow : IDisposable
{
    private const int ButtonCount = 3;
    private readonly IWindow _window;
    private readonly IInputContext _input;
    private readonly IMouse? _mouse;
    private readonly bool _layered;
    private readonly bool[] _previousButtons = new bool[ButtonCount];
    private Vector2 _scroll;
    private IDXGISwapChain1? _swapChain;
    private ID3D11Texture2D? _layeredTarget, _staging;
    private ID3D11RenderTargetView? _target;
    private CompositionPresenter? _composition;
    private ID3D11Texture2D? _composed;
    private ID3D11RenderTargetView? _composedView;
    private Vector2i _targetSize, _composedSize;
    private nint _layeredDc, _layeredBitmap, _layeredBits, _layeredPrevious;
    private bool _rendering;

    static AppWindow()
    {
        // Explicit registration avoids reflection-based platform discovery under Native AOT.
        GlfwWindowing.RegisterPlatform();
        GlfwInput.RegisterPlatform();
    }

    internal AppWindow(AppWindowOptions options)
    {
        var settings = WindowOptions.Default with
        {
            API = GraphicsAPI.None, Title = options.Title, Size = new Vector2D<int>(options.ClientSize.X, options.ClientSize.Y),
            WindowBorder = options.Borderless ? WindowBorder.Hidden : WindowBorder.Resizable,
            IsVisible = false, ShouldSwapAutomatically = false, VSync = false, IsEventDriven = false
        };
        if (options.Location is { } location) settings.Position = new Vector2D<int>(location.X, location.Y);
        _window = Window.Create(settings);
        _window.Initialize();
        // Without a saved position, start centered on the monitor rather than at Silk's (50, 50) default.
        if (options.Location is null) _window.Center();
        Hwnd = _window.Native?.Win32?.Hwnd ?? throw new InvalidOperationException("The window has no Win32 handle.");
        SetIcon(Hwnd);
        _layered = options.Layered;
        if (_layered)
            Native.SetWindowLongPtr(Hwnd, Native.GwlExStyle,
                (nint)(Native.GetWindowLongPtr(Hwnd, Native.GwlExStyle).ToInt64() | Native.WsExLayered));
        _input = _window.CreateInput();
        foreach (var keyboard in _input.Keyboards)
        {
            keyboard.KeyDown += (_, key, _) => KeyDown?.Invoke(key);
            keyboard.KeyUp += (_, key, _) => KeyUp?.Invoke(key);
            keyboard.KeyChar += (_, character) => KeyChar?.Invoke(character);
        }
        _mouse = _input.Mice.Count > 0 ? _input.Mice[0] : null;
        if (_mouse is not null) _mouse.Scroll += (_, wheel) => _scroll += new Vector2(wheel.X, wheel.Y);
        if (options.Visible) _window.IsVisible = true;
    }

    // GLFW registers its window class with the stock IDI_APPLICATION icon, so the title bar and taskbar get the
    // ApplicationIcon from the exe's resources instead. The SDK stores it as icon group 32512. The handles are
    // shared by every window and live as long as the process.
    private static readonly Lazy<(nint Big, nint Small)> Icons = new(() =>
    {
        const uint ImageIcon = 1, LrDefaultColor = 0;
        const int SmCxIcon = 11, SmCyIcon = 12, SmCxSmIcon = 49, SmCySmIcon = 50, IconGroupId = 32512;
        nint module = Native.GetModuleHandle(0);
        return (Native.LoadImage(module, IconGroupId, ImageIcon, Native.GetSystemMetrics(SmCxIcon), Native.GetSystemMetrics(SmCyIcon), LrDefaultColor),
            Native.LoadImage(module, IconGroupId, ImageIcon, Native.GetSystemMetrics(SmCxSmIcon), Native.GetSystemMetrics(SmCySmIcon), LrDefaultColor));
    });

    private static void SetIcon(nint hwnd)
    {
        const uint WmSetIcon = 0x0080;
        var (big, small) = Icons.Value;
        if (big != 0) Native.SendMessage(hwnd, WmSetIcon, 1, big); // ICON_BIG
        if (small != 0) Native.SendMessage(hwnd, WmSetIcon, 0, small); // ICON_SMALL
    }

    internal event Action<Key>? KeyDown, KeyUp;
    internal event Action<char>? KeyChar;

    internal nint Hwnd { get; }
    internal bool IsClosing => _window.IsClosing;
    internal bool IsFocused => Native.GetForegroundWindow() == Hwnd;
    internal bool IsMinimized => Native.IsIconic(Hwnd);

    internal bool IsVisible
    {
        get => Native.IsWindowVisible(Hwnd);
        set => _window.IsVisible = value;
    }

    internal Vector2i ClientSize
    {
        get { var size = _window.Size; return new Vector2i(size.X, size.Y); }
        set => _window.Size = new Vector2D<int>(value.X, value.Y);
    }

    internal Vector2i Location
    {
        get { var position = _window.Position; return new Vector2i(position.X, position.Y); }
        set => _window.Position = new Vector2D<int>(value.X, value.Y);
    }

    internal Vector2i FramebufferSize
    {
        get { var size = _window.FramebufferSize; return new Vector2i(size.X, size.Y); }
    }

    internal WindowState WindowState
    {
        get => IsMinimized ? WindowState.Minimized : _window.WindowState;
        set => _window.WindowState = value;
    }

    // Mouse position in client pixels.
    internal Vector2 MousePosition => _mouse?.Position ?? new Vector2(-float.MaxValue);
    internal Vector2 ScrollDelta => _scroll;
    internal bool IsButtonDown(MouseButton button) => _mouse?.IsButtonPressed(button) == true;
    // Down now but not at the previous NewInputFrame.
    internal bool IsButtonPressed(MouseButton button) => IsButtonDown(button) && !_previousButtons[(int)button];

    internal void NewInputFrame()
    {
        for (int i = 0; i < ButtonCount; i++) _previousButtons[i] = IsButtonDown((MouseButton)i);
        _scroll = Vector2.Zero;
    }

    // Polls the OS queue for every GLFW window on this thread.
    internal void DoEvents() => _window.DoEvents();
    internal void Close() => _window.Close();
    internal void Focus() => _window.Focus();

    internal void SetCursor(StandardCursor cursor)
    {
        if (_mouse is null) return;
        _mouse.Cursor.Type = CursorType.Standard;
        _mouse.Cursor.StandardCursor = cursor;
    }

    // The last frame of a layered window (premultiplied BGRA), valid until its next BeginRender.
    internal ID3D11Texture2D? LayeredFrame => _layered ? _layeredTarget : null;

    // Composited (non-layered windows): frames are drawn offscreen and shown through a DirectComposition
    // surface on top, so the window's UI never calls IDXGISwapChain::Present. Discord capture composites
    // every such window; the main window's HWND swap chain then only carries PresentCapture frames.
    internal bool Composited
    {
        get => _composition is not null;
        set
        {
            if (_layered || value == Composited) return;
            if (value) _composition = new CompositionPresenter(Hwnd);
            else
            {
                _composition!.Dispose();
                _composition = null;
                ReleaseComposed();
            }
        }
    }

    // Binds and clears this window's back buffer; returns false while it has no drawable area.
    internal bool BeginRender(Color4 clear)
    {
        var size = FramebufferSize;
        if (size.X <= 0 || size.Y <= 0) return _rendering = false;
        if (_layered) EnsureLayeredTarget(size);
        else if (_composition is not null) EnsureComposedTarget(size);
        else EnsureSwapChain(size);
        var target = _composition is not null ? _composedView! : _target!;
        var context = Gpu.Context;
        context.OMSetRenderTargets(target);
        context.RSSetViewport(0, 0, size.X, size.Y);
        context.ClearRenderTargetView(target, clear);
        return _rendering = true;
    }

    internal void Present()
    {
        if (!_rendering) return;
        _rendering = false;
        Gpu.Context.OMSetRenderTargets((ID3D11RenderTargetView)null!);
        if (_layered) PresentLayered();
        else if (_composition is not null) _composition.Present(_composed!, _composedSize);
        else _swapChain!.Present(0, PresentFlags.None).CheckError();
    }

    private void EnsureComposedTarget(Vector2i size)
    {
        if (_composedView is not null && _composedSize == size) return;
        ReleaseComposed();
        _composed = Gpu.Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm,
            (uint)size.X, (uint)size.Y, arraySize: 1, mipLevels: 1, bindFlags: BindFlags.RenderTarget));
        _composedView = Gpu.Device.CreateRenderTargetView(_composed);
        _composedSize = size;
    }

    private void ReleaseComposed()
    {
        _composedView?.Dispose();
        _composed?.Dispose();
        _composedView = null;
        _composed = null;
    }

    // Presents one frame on the HWND swap chain of a composited window: a copy of `frame` (BGRA) at its
    // size, or black at `blankSize` when there is none. Nothing of it is visible on screen.
    internal void PresentCapture(ID3D11Texture2D? frame, Vector2i blankSize)
    {
        if (_composition is null) return;
        var size = frame is null ? blankSize : new Vector2i((int)frame.Description.Width, (int)frame.Description.Height);
        if (size.X <= 0 || size.Y <= 0) return;
        EnsureSwapChain(size);
        var context = Gpu.Context;
        if (frame is not null)
        {
            using var buffer = _swapChain!.GetBuffer<ID3D11Texture2D>(0);
            context.CopyResource(buffer, frame);
        }
        else context.ClearRenderTargetView(_target!, new Color4(0f, 0f, 0f, 1f));
#if TIPAURA_AGENT_SELF_TEST
        if (CaptureProbeForSmoke is { } probe)
        {
            using var buffer = _swapChain!.GetBuffer<ID3D11Texture2D>(0);
            probe(buffer);
        }
#endif
        // Occluded (minimized) is a success code; the hook still sees the call.
        _swapChain!.Present(0, PresentFlags.None).CheckError();
    }

#if TIPAURA_AGENT_SELF_TEST
    internal Action<ID3D11Texture2D>? CaptureProbeForSmoke { get; set; }
    internal IDXGISwapChain1? SwapChainForSmoke => _swapChain;
#endif

    private void EnsureSwapChain(Vector2i size)
    {
        if (_swapChain is null)
        {
            using var device = Gpu.Device.QueryInterface<IDXGIDevice>();
            device.GetAdapter(out var adapter).CheckError();
            using (adapter)
            using (var factory = adapter.GetParent<IDXGIFactory2>())
            {
                var description = new SwapChainDescription1((uint)size.X, (uint)size.Y, Format.B8G8R8A8_UNorm,
                    bufferCount: 2, swapEffect: SwapEffect.FlipDiscard);
                _swapChain = factory.CreateSwapChainForHwnd(Gpu.Device, Hwnd, description);
                factory.MakeWindowAssociation(Hwnd, WindowAssociationFlags.IgnoreAltEnter);
            }
            _targetSize = size;
        }
        else if (_targetSize != size)
        {
            _target?.Dispose();
            _target = null;
            _swapChain.ResizeBuffers(2, (uint)size.X, (uint)size.Y, Format.Unknown, SwapChainFlags.None).CheckError();
            _targetSize = size;
        }
        if (_target is null)
        {
            using var buffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
            _target = Gpu.Device.CreateRenderTargetView(buffer);
        }
    }

    private void EnsureLayeredTarget(Vector2i size)
    {
        if (_target is not null && _targetSize == size) return;
        ReleaseLayered();
        _layeredTarget = Gpu.Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm,
            (uint)size.X, (uint)size.Y, arraySize: 1, mipLevels: 1, bindFlags: BindFlags.RenderTarget));
        _staging = Gpu.Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm,
            (uint)size.X, (uint)size.Y, arraySize: 1, mipLevels: 1, bindFlags: BindFlags.None,
            usage: ResourceUsage.Staging, cpuAccessFlags: CpuAccessFlags.Read));
        _target = Gpu.Device.CreateRenderTargetView(_layeredTarget);
        var info = new Native.BitmapInfo
        {
            Size = (uint)Marshal.SizeOf<Native.BitmapInfo>(), Width = size.X, Height = -size.Y,
            Planes = 1, BitCount = 32
        };
        nint screen = Native.GetDC(0);
        try
        {
            _layeredDc = Native.CreateCompatibleDC(screen);
            _layeredBitmap = Native.CreateDIBSection(screen, ref info, 0, out _layeredBits, 0, 0);
        }
        finally { Native.ReleaseDC(0, screen); }
        if (_layeredDc == 0 || _layeredBitmap == 0)
        {
            ReleaseLayered();
            throw new InvalidOperationException("Could not create the layered window surface.");
        }
        _layeredPrevious = Native.SelectObject(_layeredDc, _layeredBitmap);
        _targetSize = size;
    }

    // ImGui blending over a (0,0,0,0) clear leaves premultiplied BGRA, which UpdateLayeredWindow expects.
    private unsafe void PresentLayered()
    {
        var context = Gpu.Context;
        context.CopyResource(_staging!, _layeredTarget!);
        var mapped = context.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            int rowBytes = _targetSize.X * 4;
            for (int y = 0; y < _targetSize.Y; y++)
                Buffer.MemoryCopy((byte*)mapped.DataPointer + y * (long)mapped.RowPitch,
                    (byte*)_layeredBits + y * (long)rowBytes, rowBytes, rowBytes);
        }
        finally { context.Unmap(_staging!, 0); }
        var size = new Native.Size { Width = _targetSize.X, Height = _targetSize.Y };
        var source = new Native.Point();
        var blend = new Native.BlendFunction { SourceConstantAlpha = 255, AlphaFormat = 1 }; // AC_SRC_ALPHA
        Native.UpdateLayeredWindow(Hwnd, 0, 0, ref size, _layeredDc, ref source, 0, ref blend, 2); // ULW_ALPHA
    }

    private void ReleaseLayered()
    {
        if (_layeredDc != 0)
        {
            Native.SelectObject(_layeredDc, _layeredPrevious);
            Native.DeleteDC(_layeredDc);
        }
        if (_layeredBitmap != 0) Native.DeleteObject(_layeredBitmap);
        _layeredDc = _layeredBitmap = _layeredBits = _layeredPrevious = 0;
        _target?.Dispose();
        _staging?.Dispose();
        _layeredTarget?.Dispose();
        _target = null;
        _staging = _layeredTarget = null;
    }

    public void Dispose()
    {
        if (_layered) ReleaseLayered();
        _composition?.Dispose();
        _composition = null;
        ReleaseComposed();
        _target?.Dispose();
        _swapChain?.Dispose();
        _target = null;
        _swapChain = null;
        _input.Dispose();
        _window.Dispose();
    }
}

internal static partial class Native
{
    internal const long WsExLayered = 0x00080000, WsExTransparent = 0x00000020;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Size { public int Width, Height; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BlendFunction { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UpdateLayeredWindow(nint hwnd, nint destinationDc, nint destinationPoint,
        ref Size size, nint sourceDc, ref Point sourcePoint, uint colorKey, ref BlendFunction blend, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(nint hwnd, int command);
}
