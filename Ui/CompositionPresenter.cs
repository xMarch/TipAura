using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Vortice.Mathematics;

// Shows a window's frames through a DirectComposition surface on a topmost visual instead of a swap
// chain. Nothing here calls IDXGISwapChain::Present, which is what a Present hook such as Discord's
// stream capture locks onto, and the visual covers whatever the window's own HWND swap chain presents.
internal sealed class CompositionPresenter : IDisposable
{
    private readonly IDCompositionDevice _device;
    private readonly IDCompositionTarget _target;
    private readonly IDCompositionVisual _visual;
    private IDCompositionSurface? _surface;
    private Vector2i _size;

    internal CompositionPresenter(nint hwnd)
    {
        using var dxgiDevice = Gpu.Device.QueryInterface<IDXGIDevice>();
        _device = DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgiDevice);
        _device.CreateTargetForHwnd(hwnd, true, out _target).CheckError();
        _device.CreateVisual(out _visual).CheckError();
        _target.SetRoot(_visual).CheckError();
    }

    // Copies `frame` (BGRA, window size) into the surface and commits it.
    internal void Present(ID3D11Texture2D frame, Vector2i size)
    {
        if (_surface is null || _size != size)
        {
            _surface?.Dispose();
            _device.CreateSurface((uint)size.X, (uint)size.Y, Format.B8G8R8A8_UNorm, AlphaMode.Ignore, out _surface).CheckError();
            _visual.SetContent(_surface).CheckError();
            _size = size;
        }
        var destination = _surface!.BeginDraw<ID3D11Texture2D>(null, out Int2 offset);
        try
        {
            using (destination)
                Gpu.Context.CopySubresourceRegion(destination, 0, (uint)offset.X, (uint)offset.Y, 0, frame, 0);
        }
        finally { _surface.EndDraw().CheckError(); }
        _device.Commit().CheckError();
    }

    public void Dispose()
    {
        _target.SetRoot(null);
        _device.Commit();
        _visual.Dispose();
        _target.Dispose();
        _surface?.Dispose();
        _device.Dispose();
    }
}
