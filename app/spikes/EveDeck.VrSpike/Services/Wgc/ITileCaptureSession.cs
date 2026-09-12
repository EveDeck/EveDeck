using System.Drawing;

namespace EveDeck.Services.Wgc;

// Minimal local copy of the app's capture-session contract. In the shipping app this lives in
// Services/ScreenshotCaptureSession.cs and is implemented by both the PrintWindow fallback and
// WgcTileCaptureSession; the spike only needs the shape so the ported WGC files compile.
internal interface ITileCaptureSession : IDisposable
{
    bool ConsumeFrameDirty();

    Bitmap? TryGetResizedFrame(int destWidth, int destHeight);
}
