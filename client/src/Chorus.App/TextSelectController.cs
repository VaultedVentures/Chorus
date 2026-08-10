using System.Drawing.Imaging;
using Chorus.Core.ScreenText;

namespace Chorus.App;

/// <summary>
/// ScreenToTextToSpeech orchestration for the Text Select feature (card
/// Option 1): freeze the desktop, show the dim overlay, let the user drag a
/// rectangle, OCR the selected region with the Windows OCR engine, clean the
/// text and read it aloud locally via SAPI. Fully local — no gateway
/// involvement.
/// </summary>
public sealed class TextSelectController : IDisposable
{
    private readonly WindowsOcrReader _ocr;
    private readonly SapiSpeechSynthesizer _speech;
    private readonly VoiceConsoleForm _form;
    private readonly TrayDaemon _tray;
    private readonly object _gate = new();

    private bool _running;
    private TextSelectOverlayForm? _overlay;
    private bool _disposed;

    public TextSelectController(VoiceConsoleForm form, TrayDaemon tray)
    {
        _ocr = new WindowsOcrReader();
        _speech = new SapiSpeechSynthesizer();
        _form = form;
        _tray = tray;
    }

    /// <summary>True while the overlay is up or text is being read.</summary>
    public bool IsRunning
    {
        get { lock (_gate) return _running; }
    }

    /// <summary>
    /// Toggle the screen-text flow (call on the UI thread):
    ///  - overlay up     → cancel the overlay
    ///  - reading        → stop reading
    ///  - idle           → start a new selection
    /// </summary>
    public void Toggle()
    {
        TextSelectOverlayForm? overlayToCancel = null;
        bool shouldStart = false;

        lock (_gate)
        {
            if (_disposed) return;
            if (_overlay is not null)
            {
                overlayToCancel = _overlay;
            }
            else if (_running)
            {
                _speech.Stop();
            }
            else
            {
                _running = true;
                shouldStart = true;
            }
        }

        overlayToCancel?.Cancel();
        if (shouldStart) StartSelection();
    }

    /// <summary>Stop any in-flight reading (e.g. when PTT/wake takes the mic).</summary>
    public void StopReading() => _speech.Stop();

    private void StartSelection()
    {
        try
        {
            if (!_ocr.IsAvailable)
            {
                _form.AppendSystem("Screen text: Windows OCR has no language pack installed — nothing to read.");
                FinishRun();
                return;
            }

            // Freeze the desktop BEFORE the overlay appears so the snapshot is clean.
            using var desktop = CaptureDesktop();

            var overlay = new TextSelectOverlayForm(desktop);
            lock (_gate)
            {
                if (_disposed) { overlay.Dispose(); FinishRun(); return; }
                _overlay = overlay;
            }

            try
            {
                var result = overlay.ShowDialog(_form);
                var region = result == DialogResult.OK ? overlay.SelectedRegion : null;
                if (region is { } r)
                {
                    _ = ReadRegionAsync(desktop, r);
                }
                else
                {
                    FinishRun();
                }
            }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_overlay, overlay)) _overlay = null;
                }
                overlay.Dispose();
            }
        }
        catch (Exception ex)
        {
            _form.AppendSystem($"Screen text: failed — {ex.Message}");
            FinishRun();
        }
    }

    private async Task ReadRegionAsync(Bitmap desktop, ScreenRect region)
    {
        try
        {
            _form.AppendSystem($"Screen text: reading {region.Width}×{region.Height} region…");
            _tray.SetStatus("CHORUS — reading screen text…");

            using var crop = Crop(desktop, region);
            var raw = await _ocr.RecognizeAsync(crop);
            var text = OcrTextCleaner.Clean(raw);

            if (text.Length == 0)
            {
                _form.AppendSystem("Screen text: nothing recognizable in that selection.");
                _tray.SetStatus("CHORUS — no text in selection");
                FinishRun();
                return;
            }

            var preview = OcrTextCleaner.Preview(text);
            _form.AppendSystem($"📖 {preview}");
            _tray.SetStatus($"CHORUS — reading {text.Length} chars…");

            var chunks = TtsChunker.Chunk(text);
            await _speech.SpeakAsync(chunks);

            _form.AppendSystem("Screen text: done.");
            _tray.SetStatus("CHORUS — idle");
        }
        catch (OperationCanceledException)
        {
            _form.AppendSystem("Screen text: stopped.");
            _tray.SetStatus("CHORUS — idle");
        }
        catch (Exception ex)
        {
            _form.AppendSystem($"Screen text: failed — {ex.Message}");
            _tray.SetStatus("CHORUS — screen text error");
        }
        finally
        {
            FinishRun();
        }
    }

    private void FinishRun()
    {
        lock (_gate) _running = false;
    }

    /// <summary>Capture the virtual screen in physical pixels.</summary>
    private static Bitmap CaptureDesktop()
    {
        var bounds = SystemInformation.VirtualScreen;
        var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(bounds.X, bounds.Y, 0, 0, bounds.Size);
        }
        return bmp;
    }

    /// <summary>Crop a physical-pixel region out of the frozen desktop bitmap.</summary>
    private static Bitmap Crop(Bitmap desktop, ScreenRect region)
    {
        var bounds = SystemInformation.VirtualScreen;
        var x = region.X - bounds.X;
        var y = region.Y - bounds.Y;
        var w = Math.Clamp(region.Width, 1, desktop.Width - Math.Max(0, x));
        var h = Math.Clamp(region.Height, 1, desktop.Height - Math.Max(0, y));
        return desktop.Clone(new Rectangle(x, y, w, h), PixelFormat.Format32bppArgb);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _overlay?.Cancel();
        }
        _speech.Dispose();
        _ocr.Dispose();
    }
}
