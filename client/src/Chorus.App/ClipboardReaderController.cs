using Chorus.Core.Clipboard;
using Chorus.Core.ScreenText;

namespace Chorus.App;

/// <summary>
/// Clipboard Reader orchestration (Scott's 2026-08-13 redirect — the
/// ClipReader MVP): copy any text (a long Hermes reply from Windows
/// Terminal is the canonical case), trigger via the global hotkey
/// (default Win+Shift+C), the tray menu ("Read Clipboard") or the console
/// button, and the clipboard text is cleaned (ANSI stripped, terminal-wrap
/// line breaks collapsed to spaces, paragraph breaks kept), chunked
/// (TtsChunker) and read aloud locally via SAPI. Fully local — no gateway
/// involvement. Pressing the trigger again stops the reading; PTT/wake
/// takes priority (Program stops the reader).
/// </summary>
public sealed class ClipboardReaderController : IDisposable
{
    private readonly SapiSpeechSynthesizer _speech;
    private readonly VoiceConsoleForm _form;
    private readonly TrayDaemon _tray;
    private readonly object _gate = new();
    private bool _running;
    private bool _disposed;

    public ClipboardReaderController(VoiceConsoleForm form, TrayDaemon tray, string? voiceName = null)
    {
        _speech = new SapiSpeechSynthesizer(voiceName);
        _form = form;
        _tray = tray;
    }

    /// <summary>True while a clipboard read-aloud is in flight.</summary>
    public bool IsRunning
    {
        get { lock (_gate) return _running; }
    }

    /// <summary>
    /// Toggle the clipboard read (call on the UI thread — the STA clipboard
    /// read happens synchronously before the first await):
    ///  - reading  → stop reading
    ///  - idle     → read the current clipboard text aloud
    /// </summary>
    public void Toggle()
    {
        bool shouldStart = false;
        lock (_gate)
        {
            if (_disposed) return;
            if (_running)
            {
                _speech.Stop();
            }
            else
            {
                _running = true;
                shouldStart = true;
            }
        }

        if (shouldStart) _ = ReadClipboardAsync();
    }

    /// <summary>Stop any in-flight reading (e.g. when PTT/wake takes the mic).</summary>
    public void StopReading() => _speech.Stop();

    private async Task ReadClipboardAsync()
    {
        // Clipboard access is STA-only; this method is entered on the UI
        // thread, and the read happens synchronously before the first await.
        string? raw;
        try
        {
            raw = Clipboard.ContainsText() ? Clipboard.GetText() : null;
        }
        catch (Exception ex)
        {
            _form.AppendSystem($"Clipboard: unreadable — {ex.Message}");
            _tray.SetStatus("CHORUS — clipboard error");
            FinishRun();
            return;
        }

        var text = ClipboardTextCleaner.Clean(raw);
        if (text.Length == 0)
        {
            _form.AppendSystem("Clipboard: no text to read (empty or non-text clipboard).");
            _tray.SetStatus("CHORUS — clipboard empty");
            _tray.ShowBalloon("CHORUS — clipboard empty", "Copy some text first, then press the hotkey again.");
            FinishRun();
            return;
        }

        try
        {
            var preview = ClipboardTextCleaner.Preview(text);
            _form.AppendSystem($"📋 {preview}");
            _tray.SetStatus($"CHORUS — reading {text.Length} chars…");

            var chunks = TtsChunker.Chunk(text);
            await _speech.SpeakAsync(chunks);

            _form.AppendSystem("Clipboard: done.");
            _tray.SetStatus("CHORUS — idle");
        }
        catch (OperationCanceledException)
        {
            _form.AppendSystem("Clipboard: stopped.");
            _tray.SetStatus("CHORUS — idle");
        }
        catch (Exception ex)
        {
            _form.AppendSystem($"Clipboard: failed — {ex.Message}");
            _tray.SetStatus("CHORUS — clipboard error");
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

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _speech.Dispose();
    }
}
