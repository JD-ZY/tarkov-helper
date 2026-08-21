using System.Windows.Forms;
using OpenCvSharp;
using ScreenCapture.NET;

namespace TarkovHelper.App;

// Captures a small screen region via DXGI Desktop Duplication (through the
// ScreenCapture.NET library), replacing System.Drawing.Graphics.CopyFromScreen.
//
// Real bug this exists to fix, confirmed via direct user testing: CopyFromScreen
// is a GDI BitBlt-based API, and BitBlt does not reliably observe content from
// apps using DirectX's modern "flip model" presentation (the default for
// essentially all current games, including EFT in Borderless Windowed mode) -
// it can return STALE compositor content instead of the live frame. This was
// reproduced directly: a diagnostic capture returned a File Explorer window
// that the user confirmed was NOT on screen at capture time - the game was the
// only thing visible, but GDI returned old cached content from that screen
// region instead. DXGI Desktop Duplication reads from the same live
// GPU-composited surface the OS itself uses, which is Microsoft's documented
// fix for this exact class of bug (the same underlying mechanism
// Windows.Graphics.Capture uses, which OBS/Discord/Game Bar are built on).
public sealed class DxgiScreenCapture : IDisposable
{
    private readonly DX11ScreenCaptureService _service = new();
    private readonly Dictionary<string, (Display Display, IScreenCapture Capture)> _capturesByDeviceName = new();

    // Finds/creates a capture pipeline for whichever physical display
    // contains the given point (global virtual-desktop coordinates, same
    // space as Cursor.Position), and captures a square region of the
    // given size centered on that point, clamped to the display's own
    // bounds. Returns null if no display contains the point (shouldn't
    // normally happen - the cursor is always on some display) or if the
    // capture itself fails.
    public Mat? CaptureRegionAroundPoint(System.Drawing.Point globalPoint, int regionSize) =>
        CaptureRegionAroundPoint(globalPoint, regionSize, regionSize);

    // Real bug this overload fixes: EFT's item tooltips can be wider than
    // the original fixed 500x500 square capture - confirmed directly
    // against a real capture where a long tooltip ("Monstrum Tactical
    // Compact Prism Scope 2x32") was clipped at the capture's right edge,
    // so its border never closed into a detectable rectangle at all (the
    // right-side border line was simply outside what was captured).
    // Lets a caller request a WIDER region than tall, since tooltips grow
    // horizontally with longer item names, not vertically - cheaper than
    // just squaring up the whole capture area to the same width.
    public Mat? CaptureRegionAroundPoint(System.Drawing.Point globalPoint, int regionWidth, int regionHeight)
    {
        var screen = Screen.FromPoint(globalPoint);
        var (display, capture) = GetOrCreateCapture(screen);

        // Convert the global cursor position into this display's own
        // local coordinate space - ScreenCapture.NET's RegisterCaptureZone
        // (and the underlying DXGI output) addresses pixels relative to
        // the display's own top-left corner, not the virtual desktop's,
        // confirmed against the real DX11ScreenCaptureService source
        // (output.Description.DesktopCoordinates is display-local internally,
        // Display itself exposes no absolute offset - Screen.Bounds is the
        // join key, via matching DeviceName, e.g. "\\.\DISPLAY1").
        var localX = globalPoint.X - screen.Bounds.X;
        var localY = globalPoint.Y - screen.Bounds.Y;

        var left = Math.Clamp(localX - regionWidth / 2, 0, Math.Max(0, display.Width - regionWidth));
        var top = Math.Clamp(localY - regionHeight / 2, 0, Math.Max(0, display.Height - regionHeight));
        var width = Math.Min(regionWidth, display.Width);
        var height = Math.Min(regionHeight, display.Height);

        var zone = capture.RegisterCaptureZone(left, top, width, height);

        try
        {
            if (!capture.CaptureScreen())
            {
                return null;
            }

            using var zoneLock = zone.Lock();
            var image = zone.Image;

            // Build a BGR Mat directly from the captured pixels - matches
            // the BGR ordering the rest of the pipeline (ItemGridDetector,
            // ItemIconMatcher) already assumes, avoiding a separate
            // System.Drawing.Bitmap round-trip.
            var mat = new Mat(image.Height, image.Width, MatType.CV_8UC3);
            for (var y = 0; y < image.Height; y++)
            {
                var row = image.Rows[y];
                for (var x = 0; x < image.Width; x++)
                {
                    var pixel = row[x];
                    mat.Set(y, x, new Vec3b(pixel.B, pixel.G, pixel.R));
                }
            }

            return mat;
        }
        finally
        {
            capture.UnregisterCaptureZone(zone);
        }
    }

    private (Display, IScreenCapture) GetOrCreateCapture(Screen screen)
    {
        if (_capturesByDeviceName.TryGetValue(screen.DeviceName, out var existing))
        {
            return existing;
        }

        // GetGraphicsCards()/GetDisplays() must be re-enumerated to find the
        // Display whose DeviceName matches this WinForms Screen - there's no
        // direct lookup by name in ScreenCapture.NET's API.
        foreach (var graphicsCard in _service.GetGraphicsCards())
        {
            foreach (var display in _service.GetDisplays(graphicsCard))
            {
                if (display.DeviceName != screen.DeviceName)
                {
                    continue;
                }

                var capture = _service.GetScreenCapture(display);
                var result = (display, (IScreenCapture)capture);
                _capturesByDeviceName[screen.DeviceName] = result;
                return result;
            }
        }

        throw new InvalidOperationException($"No DXGI display found matching '{screen.DeviceName}'.");
    }

    public void Dispose() => _service.Dispose();
}
