using Vortice.Direct3D11;
using Vortice.DXGI;

namespace EveDeck.Services.Wgc;

// Pulls a captured GPU BGRA texture down to CPU as tightly-packed BGRA32. One instance per capture
// session; call only from the capture callback (not thread-safe). The staging texture is reused
// across frames and only rebuilt on a size change -- keeping per-frame GPU allocation out of the
// hot path was part of the fix that made screenshot capture safe again in v1.21.2.
internal sealed class TextureReadback : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly object _contextLock;
    private ID3D11Texture2D? _staging;
    private uint _width;
    private uint _height;
    private bool _disposed;

    // GPU-side downscale before readback. A preview tile is a few hundred pixels wide, but a capture
    // frame is the full client -- 2560x1409 is 14.4 MB. Reading that back every frame, per tile, and
    // then rescaling 3.6 megapixels on the CPU was the dominant cost of the WGC path (it scales
    // linearly with the frame-rate cap, so raising the cap made previews *worse*). Copying the frame
    // into a mip chain, letting the GPU filter it, and reading back only the mip that is closest to
    // the tile size cuts the transfer by one to two orders of magnitude and hands GDI an image that
    // is already near its final size.
    private readonly Action<string>? _log;
    private bool _loggedScale;
    private ID3D11Texture2D? _mipped;
    private ID3D11ShaderResourceView? _mipView;
    private uint _mipSourceWidth;
    private uint _mipSourceHeight;

    public TextureReadback(ID3D11Device device, object contextLock, Action<string>? log = null)
    {
        _device = device;
        _context = device.ImmediateContext;
        _contextLock = contextLock;
        _log = log;
    }

    /// Copies <paramref name="source"/> to CPU as BGRA32 (top-down, stride == width*4) into
    /// <paramref name="dest"/>, growing it if needed. <paramref name="stride"/> is width*4.
    /// <param name="targetWidth">
    /// Roughly how wide the result will be drawn. The readback is reduced to the smallest mip level
    /// that is still at least this wide, so a small tile costs a small transfer. Pass 0 for a
    /// full-resolution readback.
    /// </param>
    public void CopyToBgra(ID3D11Texture2D source, ref byte[] dest, out int width, out int height, out int stride, int targetWidth = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var desc = source.Description;

        lock (_contextLock)
        {
            var mipLevel = 0;
            var readSource = source;

            if (targetWidth > 0 && desc.Width > 0)
            {
                mipLevel = ChooseMipLevel(desc.Width, (uint)targetWidth);
                if (mipLevel > 0 && TryBuildMips(desc, source))
                {
                    readSource = _mipped!;
                }
                else
                {
                    mipLevel = 0;
                }
            }

            width = (int)Math.Max(1, desc.Width >> mipLevel);
            height = (int)Math.Max(1, desc.Height >> mipLevel);
            stride = width * 4;
            var need = stride * height;
            if (dest.Length < need) dest = new byte[need];

            if (!_loggedScale)
            {
                _loggedScale = true;
                var fullMiB = desc.Width * desc.Height * 4 / 1048576.0;
                var readMiB = width * height * 4 / 1048576.0;
                _log?.Invoke($"readback: {desc.Width}x{desc.Height} -> {width}x{height} (mip {mipLevel}), {fullMiB:F1} MiB -> {readMiB:F2} MiB per frame");
            }

            EnsureStaging(desc, (uint)width, (uint)height);
            if (mipLevel > 0)
                _context.CopySubresourceRegion(_staging!, 0, 0, 0, 0, readSource, (uint)mipLevel);
            else
                _context.CopyResource(_staging!, readSource);

            var box = _context.Map(_staging!, 0u, MapMode.Read);
            try
            {
                var srcPitch = (int)box.RowPitch;
                unsafe
                {
                    var s = (byte*)box.DataPointer;
                    fixed (byte* d = dest)
                    {
                        for (var y = 0; y < height; y++)
                            Buffer.MemoryCopy(s + (long)y * srcPitch, d + (long)y * stride, stride, stride);
                    }
                }
            }
            finally
            {
                _context.Unmap(_staging!, 0u);
            }
        }
    }

    // Smallest mip that is still at least as wide as the tile, so downscaling never invents detail
    // and GDI is left with a short final step.
    private static int ChooseMipLevel(uint sourceWidth, uint targetWidth)
    {
        var level = 0;
        var w = sourceWidth;
        while (w / 2 >= targetWidth && w / 2 >= 1 && level < 12)
        {
            w /= 2;
            level++;
        }
        return level;
    }

    private bool TryBuildMips(Texture2DDescription srcDesc, ID3D11Texture2D source)
    {
        try
        {
            if (_mipped is null || _mipSourceWidth != srcDesc.Width || _mipSourceHeight != srcDesc.Height)
            {
                _mipView?.Dispose();
                _mipped?.Dispose();
                _mipView = null;

                _mipped = _device.CreateTexture2D(new Texture2DDescription
                {
                    Width = srcDesc.Width,
                    Height = srcDesc.Height,
                    MipLevels = 0, // full chain
                    ArraySize = 1,
                    Format = srcDesc.Format == Format.Unknown ? Format.B8G8R8A8_UNorm : srcDesc.Format,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
                    CPUAccessFlags = CpuAccessFlags.None,
                    MiscFlags = ResourceOptionFlags.GenerateMips,
                });
                _mipView = _device.CreateShaderResourceView(_mipped);
                _mipSourceWidth = srcDesc.Width;
                _mipSourceHeight = srcDesc.Height;
            }

            // Seed level 0, then let the GPU filter the chain.
            _context.CopySubresourceRegion(_mipped!, 0, 0, 0, 0, source, 0);
            _context.GenerateMips(_mipView!);
            return true;
        }
        catch
        {
            _mipView?.Dispose();
            _mipped?.Dispose();
            _mipView = null;
            _mipped = null;
            return false; // fall back to a full-resolution readback
        }
    }

    private void EnsureStaging(Texture2DDescription srcDesc, uint width, uint height)
    {
        if (_staging is not null && _width == width && _height == height) return;

        _staging?.Dispose();
        _width = width;
        _height = height;
        _staging = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = srcDesc.Format == Format.Unknown ? Format.B8G8R8A8_UNorm : srcDesc.Format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _staging?.Dispose();
        _staging = null;
        _mipView?.Dispose();
        _mipView = null;
        _mipped?.Dispose();
        _mipped = null;
    }
}
