using System.Drawing;
using System.Drawing.Imaging;
using Chorus.Core.ScreenText;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace Chorus.App;

/// <summary>
/// OCR for the Text Select feature using the built-in Windows OCR engine
/// (WinRT <c>Windows.Media.Ocr</c>) — the card's preferred tech: no native
/// tesseract binaries to ship, uses the Windows 10/11 language packs already
/// on the machine.
/// </summary>
public sealed class WindowsOcrReader : IDisposable
{
    private OcrEngine? _engine;

    public WindowsOcrReader()
    {
        // Prefer the user's language; fall back to any installed recognizer.
        _engine = OcrEngine.TryCreateFromUserProfileLanguages()
            ?? OcrEngine.AvailableRecognizerLanguages
                .Select(OcrEngine.TryCreateFromLanguage)
                .FirstOrDefault(e => e is not null);
    }

    /// <summary>True when Windows has at least one OCR language pack installed.</summary>
    public bool IsAvailable => _engine is not null;

    /// <summary>
    /// Recognize text in the given bitmap region (physical pixels already
    /// cropped by the caller). Returns the raw OCR text; empty if nothing
    /// recognized or no OCR language is installed.
    /// </summary>
    public async Task<string> RecognizeAsync(Bitmap bitmap, CancellationToken ct = default)
    {
        if (_engine is null || bitmap is null) return string.Empty;

        // OcrEngine rejects images larger than MaxImageDimension.
        var (fitW, fitH) = ScreenRect.FitWithin(bitmap.Width, bitmap.Height, (int)OcrEngine.MaxImageDimension);
        using var scaled = (fitW == bitmap.Width && fitH == bitmap.Height)
            ? bitmap
            : new Bitmap(bitmap, fitW, fitH);

        using var ms = new MemoryStream();
        scaled.Save(ms, ImageFormat.Png);
        ms.Position = 0;

        using var stream = new InMemoryRandomAccessStream();
        var writer = new DataWriter(stream.GetOutputStreamAt(0));
        writer.WriteBytes(ms.ToArray());
        await writer.StoreAsync();
        stream.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(stream);
        var softwareBitmap = await decoder.GetSoftwareBitmapAsync();

        var result = await _engine.RecognizeAsync(softwareBitmap).AsTask(ct);
        return result?.Text ?? string.Empty;
    }

    public void Dispose() => _engine = null;
}
