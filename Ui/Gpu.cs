using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

// One D3D11 device shared by every UI window; all rendering happens on the main thread.
internal static class Gpu
{
    private static readonly Lazy<(ID3D11Device Device, ID3D11DeviceContext Context)> s_device = new(Create);

    internal static ID3D11Device Device => s_device.Value.Device;
    internal static ID3D11DeviceContext Context => s_device.Value.Context;

    private static (ID3D11Device, ID3D11DeviceContext) Create()
    {
        FeatureLevel[] levels = [FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0];
        // The UI device is only used from the main thread, and its ImGui workload is tiny: skip the
        // runtime's locking and stop the driver from spawning a worker thread per CPU core.
        const DeviceCreationFlags flags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.Singlethreaded
            | DeviceCreationFlags.PreventInternalThreadingOptimizations;
        var result = D3D11.D3D11CreateDevice(null, DriverType.Hardware, flags,
            levels, out ID3D11Device? device, out ID3D11DeviceContext? context);
        if (result.Failure)
        {
            AppLog.Warn("UI", $"Hardware Direct3D 11 device unavailable ({result}); using WARP.");
            D3D11.D3D11CreateDevice(null, DriverType.Warp, flags,
                levels, out device, out context).CheckError();
        }
        AppLog.Info("UI", $"Direct3D 11 device created (feature level {device!.FeatureLevel}).");
        return (device, context!);
    }
}

// A sampled 2D texture whose ImGui texture ID is its shader-resource view pointer.
internal sealed class GpuTexture : IDisposable
{
    private static readonly Dictionary<nint, GpuTexture> s_byId = [];
    private ID3D11Texture2D? _texture;
    private ID3D11ShaderResourceView? _view;

    internal GpuTexture(Format format, bool pointSampling = false, bool alphaOnly = false)
    {
        Format = format;
        PointSampling = pointSampling;
        AlphaOnly = alphaOnly;
    }

    internal Format Format { get; }
    // Point sampling magnifies pixels exactly; minification remains linear like the GL textures did.
    internal bool PointSampling { get; set; }
    // Single-channel coverage for the font atlas: white color, alpha from the red channel.
    internal bool AlphaOnly { get; }
    internal int Width { get; private set; }
    internal int Height { get; private set; }
    internal nint Id => _view?.NativePointer ?? 0;
    internal ID3D11ShaderResourceView? View => _view;

    internal static GpuTexture? Find(nint id) => s_byId.GetValueOrDefault(id);

    internal void Upload(int width, int height, nint pixels, int rowPitch)
    {
        if (width <= 0 || height <= 0) return;
        if (_texture is null || width != Width || height != Height)
        {
            Release();
            _texture = Gpu.Device.CreateTexture2D(new Texture2DDescription(Format, (uint)width, (uint)height,
                arraySize: 1, mipLevels: 1, bindFlags: BindFlags.ShaderResource, usage: ResourceUsage.Default));
            _view = Gpu.Device.CreateShaderResourceView(_texture);
            s_byId[_view.NativePointer] = this;
            Width = width;
            Height = height;
        }
        Gpu.Context.UpdateSubresource(_texture, 0, null, pixels, (uint)rowPitch, 0);
    }

    internal unsafe void Upload(int width, int height, byte[] pixels, int bytesPerPixel = 4)
    {
        fixed (byte* data = pixels) Upload(width, height, (nint)data, width * bytesPerPixel);
    }

    // A single-channel square image with a box-filtered mip chain, so it stays smooth when drawn far smaller.
    internal unsafe void UploadMipmapped(int side, byte[] pixels)
    {
        Release();
        int levels = System.Numerics.BitOperations.Log2((uint)side) + 1;
        _texture = Gpu.Device.CreateTexture2D(new Texture2DDescription(Format, (uint)side, (uint)side,
            arraySize: 1, mipLevels: (uint)levels, bindFlags: BindFlags.ShaderResource, usage: ResourceUsage.Default));
        _view = Gpu.Device.CreateShaderResourceView(_texture);
        s_byId[_view.NativePointer] = this;
        Width = Height = side;
        for (int level = 0; level < levels; level++)
        {
            fixed (byte* data = pixels) Gpu.Context.UpdateSubresource(_texture, (uint)level, null, (nint)data, (uint)side, 0);
            if (side == 1) break;
            int half = side / 2;
            var next = new byte[half * half];
            for (int y = 0; y < half; y++)
                for (int x = 0; x < half; x++)
                {
                    int i = 2 * y * side + 2 * x;
                    next[y * half + x] = (byte)((pixels[i] + pixels[i + 1] + pixels[i + side] + pixels[i + side + 1] + 2) / 4);
                }
            pixels = next;
            side = half;
        }
    }

    private void Release()
    {
        if (_view is not null) s_byId.Remove(_view.NativePointer);
        _view?.Dispose();
        _texture?.Dispose();
        _view = null;
        _texture = null;
        Width = Height = 0;
    }

    public void Dispose() => Release();
}
