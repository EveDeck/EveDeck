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

    public TextureReadback(ID3D11Device device, object contextLock)
    {
        _device = device;
        _context = device.ImmediateContext;
        _contextLock = contextLock;
    }

    /// Copies <paramref name="source"/> to CPU as BGRA32 (top-down, stride == width*4) into
    /// <paramref name="dest"/>, growing it if needed. <paramref name="stride"/> is width*4.
    public void CopyToBgra(ID3D11Texture2D source, ref byte[] dest, out int width, out int height, out int stride)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var desc = source.Description;
        width = (int)desc.Width;
        height = (int)desc.Height;
        stride = width * 4;
        var need = stride * height;
        if (dest.Length < need) dest = new byte[need];

        lock (_contextLock)
        {
            EnsureStaging(desc);
            _context.CopyResource(_staging!, source);

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

    private void EnsureStaging(Texture2DDescription srcDesc)
    {
        if (_staging is not null && _width == srcDesc.Width && _height == srcDesc.Height) return;

        _staging?.Dispose();
        _width = srcDesc.Width;
        _height = srcDesc.Height;
        _staging = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = srcDesc.Width,
            Height = srcDesc.Height,
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
    }
}
