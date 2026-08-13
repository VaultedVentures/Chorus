using Chorus.Core;
using Chorus.Core.WakeWord;

namespace Chorus.App;

/// <summary>
/// ApplicationContext that lets the app start WITHOUT a visible main window:
/// the form is created (its handle is forced so global hotkeys work) but not
/// shown until the tray's "Show Console" is used. The tray owns the loop.
/// </summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly VoiceConsoleForm _form;

    public TrayApplicationContext(VoiceConsoleForm form, bool startHidden)
    {
        _form = form;
        if (!startHidden) ShowConsole();
        else _ = form.Handle; // force handle creation → hotkeys register now
    }

    public void ShowConsole()
    {
        _form.Show();
        _form.WindowState = FormWindowState.Normal;
        _form.Activate();
    }
}

internal static class Program
{
    private const string MutexName = "ChorusVoiceClient_SingleInstance_v1";

    // Wake-mode VAD: RMS gate + 400 ms silence hangover.
    private const double VadThreshold = 600;
    private const int VadHangoverFrames = 20; // 20 frames x 20 ms = 400 ms

    private static ChorusClient _client = null!;
    private static AudioEngine _audio = null!;
    private static VoiceConsoleForm _form = null!;
    private static TrayDaemon _tray = null!;
    private static SessionState _state = null!;
    private static TextSelectController _textSelect = null!;
    private static ClipboardReaderController _clipboardReader = null!;
    private static TrayApplicationContext _appContext = null!;
    private static WakeWordEngine _wake = null!;
    private static SynchronizationContext? _ui;

    // Capture mode + wake VAD state (audio thread owns these).
    private static volatile bool _pttActive;
    private static volatile bool _wakeActive;
    private static volatile bool _vadInSpeech;
    private static int _vadSilentFrames;

    // Last moment a voiced frame was captured during a wake session — the
    // session auto-closes after WakeSessionIdleMs of silence so continuous
    // wake-word listening re-arms (a wake conversation must end eventually).
    // Ticks stored as a long for atomic cross-thread reads (audio -> UI).
    private static long _lastVoicedTicks = DateTime.UtcNow.Ticks;
    private static DateTime LastVoicedUtc => new(Interlocked.Read(ref _lastVoicedTicks), DateTimeKind.Utc);

    // PTT session accounting for the stream open/close log: frames actually
    // handed to the encoder during the current hold, and the hold start time.
    private static int _pttFrameCount;
    private static DateTime _pttHoldStart;
    private static string _pttDisplay = "Ctrl+Shift+Space";
    private static string _wakeHotkeyDisplay = "Win+Shift+W";

    // Last in-flight audio send. On PTT release we await it (bounded) so the
    // gateway receives every frame before the ptt up closes the stream — the
    // client-side "flush" half of the release contract.
    private static volatile Task _lastAudioSend = Task.CompletedTask;

    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, MutexName, out bool isNewInstance);
        if (!isNewInstance)
        {
            MessageBox.Show("CHORUS is already running (check the tray).", "CHORUS",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();

        var settings = ChorusConfig.Load();
        _state = new SessionState();
        _client = new ChorusClient(settings.GatewayUrl);
        _audio = new AudioEngine(settings);
        _tray = new TrayDaemon();
        _form = new VoiceConsoleForm(_client, _state, _tray,
            settings.PttHotkeyDisplay, settings.WakeHotkeyDisplay, settings.TextSelectHotkeyDisplay,
            settings.WakeEnabled, settings.WakePhrase, settings.ClipboardHotkeyDisplay);
        _appContext = new TrayApplicationContext(_form, settings.StartHidden);

        // The WinForms sync context is installed when the first control handle
        // is created (above). Capture it AFTER that, or _ui.Post would NPE.
        // Fallback: a fresh WindowsFormsSynchronizationContext posts to this
        // thread's message queue, which Application.Run pumps below.
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        using var hotkeys = new GlobalHotkeys(settings.PttBinding, settings.WakeBinding, settings.TextSelectBinding, settings.ClipboardBinding);
        _pttDisplay = settings.PttHotkeyDisplay;
        _wakeHotkeyDisplay = settings.WakeHotkeyDisplay;
        hotkeys.Register(_form.Handle);
        hotkeys.PttPressed += OnPttPressed;
        hotkeys.PttReleased += OnPttReleased;
        hotkeys.WakePressed += OnWakePressed;
        hotkeys.TextSelectPressed += OnTextSelectPressed;
        hotkeys.ClipboardPressed += OnClipboardReadPressed;
        hotkeys.RegistrationFailed += reason => _ui!.Post(_ =>
        {
            _tray.ShowBalloon("CHORUS — hotkey unavailable", reason);
            _form.AppendLine(reason, Color.FromArgb(190, 40, 40));
        }, null);

        _textSelect = new TextSelectController(_form, _tray, settings.VoiceName);
        _clipboardReader = new ClipboardReaderController(_form, _tray, settings.VoiceName);

        // Wake-word engine: continuous offline "hey chorus" spotting on the
        // mic frames. If the packaged model is missing/corrupt, degrade to
        // hotkey-only wake rather than crashing the tray app.
        try
        {
            _wake = WakeWordEngine.LoadDefault(settings.WakeSettings);
            _wake.WakeDetected += t => _ui!.Post(_ => OnWakeWordHeard(t), null);
            Log($"wake: listening for \"{settings.WakePhrase}\" ({_wake.TemplateCount} templates, sensitivity {settings.WakeSensitivity:F2}, cooldown {settings.WakeCooldownMs}ms)");
        }
        catch (Exception ex)
        {
            _wake = null!;
            Console.WriteLine($"[wake] engine unavailable: {ex.Message}");
            _form.AppendSystem($"wake word unavailable: {ex.Message}");
        }

        _tray.ShowConsoleRequested += () => _ui!.Post(_ => _appContext.ShowConsole(), null);
        _tray.ReconnectRequested += () => _state.ReconnectRequested = true;
        _tray.TextSelectRequested += () => _ui!.Post(_ => OnTextSelectPressed(), null);
        _tray.ClipboardReadRequested += () => _ui!.Post(_ => OnClipboardReadPressed(), null);
        _tray.QuitRequested += () => _ui!.Post(_ => Shutdown(), null);

        _form.ReadScreenRequested += OnTextSelectPressed;
        _form.ReadClipboardRequested += OnClipboardReadPressed;
        _form.WakeToggleRequested += enabled => _ui!.Post(_ => SetWakeEnabled(enabled), null);

        // Mic permission/device failures are surfaced clearly: tray balloon +
        // status + console line. Never silent.
        _audio.MicFailed += f => _ui!.Post(_ => OnMicFailure(f), null);

        _audio.MicFrameCaptured += OnMicFrame;
        _audio.StartPlayback();

        _client.EventReceived += e => _ui!.Post(_ => _form.HandleEvent(e), null);
        _client.TtsFrameDecoded += pcm => _ui!.Post(_ => _audio.EnqueuePlayback(pcm), null);

        using var appCts = new CancellationTokenSource();
        var connection = new ConnectionManager(_client, settings, _form, _tray, _state);
        _ = Task.Run(() => connection.RunAsync(appCts.Token), CancellationToken.None);

        using var keepalive = new System.Windows.Forms.Timer { Interval = (int)Protocol.KeepaliveInterval.TotalMilliseconds };
        keepalive.Tick += async (_, _) =>
        {
            if (_client.IsConnected)
            {
                try { await _client.SendPingAsync(); } catch { /* reconnect loop handles it */ }
            }
        };
        keepalive.Start();

        // keep reconnect requests flowing from the form/state into the manager,
        // and drive the wake session housekeeping (mute sync + idle close)
        var pollTimer = new System.Windows.Forms.Timer { Interval = 500 };
        pollTimer.Tick += (_, _) =>
        {
            if (_state.ReconnectRequested)
            {
                _state.ReconnectRequested = false;
                connection.RequestReconnect();
            }
            if (_wake is not null)
            {
                _wake.Muted = _state.Muted;
                if (_wakeActive
                    && !_state.IsSpeakingOrPending
                    && DateTime.UtcNow - LastVoicedUtc > TimeSpan.FromMilliseconds(settings.WakeSessionIdleMs))
                {
                    CloseWakeSession("idle timeout");
                }
            }
        };
        pollTimer.Start();

        // Startup mic probe: surfaces a missing/blocked microphone immediately,
        // before the user ever presses the hotkey. With wake listening enabled
        // the mic STAYS on afterwards — the whole point of a wake word.
        _ui.Post(_ => ProbeMic(), null);

        Application.Run(_appContext);
        appCts.Cancel();
        _ = _client.DisconnectAsync();
    }

    private static void ProbeMic()
    {
        if (_audio.StartMic())
        {
            if (_wake is not null && _wake.Enabled)
            {
                // mic stays running for continuous wake-word listening
                _tray.SetStatus($"CHORUS — wake listening · say \"{_wake.Phrase}\"");
                _form.AppendSystem($"wake listening active — say \"{_wake.Phrase}\" (mic stays on)");
            }
            else
            {
                _audio.StopMic();
            }
        }
    }

    private static void OnMicFailure(MicFailure f)
    {
        _tray.ShowBalloon("CHORUS — microphone unavailable", f.Message);
        _tray.SetStatus("CHORUS — mic unavailable");
        _form.AppendLine($"mic: {f.Message}", Color.FromArgb(190, 40, 40));
        _form.SetConnection("mic unavailable");
    }

    // -- hotkeys -----------------------------------------------------------
    private static void OnPttPressed()
    {
        if (_state.Muted) return;
        _textSelect.StopReading(); // user talking takes priority over screen reading
        _clipboardReader.StopReading(); // and over clipboard reading
        if (_wakeActive)
        {
            // PTT takes over from a wake window: close the wake speech first.
            _wakeActive = false;
            _vadInSpeech = false;
            FireAndForget(_client.SendVadAsync("speech_end"));
        }
        if (_state.IsSpeakingOrPending)
        {
            FireAndForget(_client.SendBargeInAsync());
            _ui!.Post(_ => _audio.ClearPlayback(), null);
        }
        _pttActive = true;
        _pttFrameCount = 0;
        _pttHoldStart = DateTime.UtcNow;
        _audio.StartMic();
        FireAndForget(_client.SendPttAsync(true));
        _tray.SetTransmitting(true);
        Log($"[ptt] stream OPEN via {_pttDisplay} at {DateTime.Now:HH:mm:ss.fff}");
    }

    private static async void OnPttReleased()
    {
        if (!_pttActive) return;
        _pttActive = false;

        // Stop the mic FIRST so no new frames are captured, then flush the
        // in-flight audio sends (bounded) so the gateway hears every frame
        // before ptt up closes the stream. No stuck-open: this runs whether
        // the key was released here or in another app — the hotkey layer
        // raises it on the physical key-up. With wake listening enabled the
        // mic stays on (continuous wake-word spotting needs it).
        if (!(_wake is not null && _wake.Enabled)) _audio.StopMic();
        try { await _lastAudioSend.WaitAsync(TimeSpan.FromMilliseconds(500)); }
        catch (Exception) { /* best-effort flush; ptt up still closes the stream */ }

        FireAndForget(_client.SendPttAsync(false));
        _tray.SetTransmitting(false);
        Log($"[ptt] stream CLOSED after {DateTime.UtcNow - _pttHoldStart:g} — {_pttFrameCount} frames sent");
    }

    private static void OnWakePressed()
    {
        if (_state.Muted || _pttActive) return;
        _textSelect.StopReading(); // mic takes priority over screen reading
        _clipboardReader.StopReading(); // and over clipboard reading
        _wakeActive = true;
        _vadInSpeech = false;
        _vadSilentFrames = 0;
        Interlocked.Exchange(ref _lastVoicedTicks, DateTime.UtcNow.Ticks);
        _audio.StartMic();
        FireAndForget(_client.SendWakeAsync());
        Log($"[wake] window opened via {_wakeHotkeyDisplay}");
    }

    /// <summary>
    /// The wake WORD was heard (offline engine). Same entry into the
    /// automatically-listening state as the manual wake hotkey, per the
    /// CHORUS Phase 1 design (wake -> server opens a speech window -> the
    /// client VAD-gates the stream until the turn ends).
    /// </summary>
    private static void OnWakeWordHeard(WakeWordTrigger trigger)
    {
        if (_state.Muted || _pttActive || _wakeActive) return;
        _textSelect.StopReading();
        _clipboardReader.StopReading();
        _wakeActive = true;
        _vadInSpeech = false;
        _vadSilentFrames = 0;
        Interlocked.Exchange(ref _lastVoicedTicks, DateTime.UtcNow.Ticks);
        _audio.StartMic();
        FireAndForget(_client.SendWakeAsync());
        _tray.SetStatus("CHORUS — listening…");
        _form.AppendSystem($"wake word \"{trigger.Phrase}\" heard (score {trigger.Score:F2}) — listening");
        Log($"[wake] \"{trigger.Phrase}\" heard (score {trigger.Score:F2}, hop {trigger.Hop}) → listening");
    }

    /// <summary>
    /// A wake session ends when the user stops talking: the continuous
    /// wake-word listening re-arms, so "hey chorus" works again for the next
    /// request (the server keeps its LISTENING turn, and a fresh wake event
    /// simply re-opens the speech window).
    /// </summary>
    private static void CloseWakeSession(string why)
    {
        if (!_wakeActive) return;
        if (_vadInSpeech)
        {
            _vadInSpeech = false;
            FireAndForget(_client.SendVadAsync("speech_end"));
        }
        _wakeActive = false;
        _wake?.Reset();
        _tray.SetStatus($"CHORUS — wake listening · say \"{_wake?.Phrase}\"");
        Log($"[wake] session closed ({why}) — listening for \"{_wake?.Phrase}\" again");
    }

    private static void SetWakeEnabled(bool enabled)
    {
        if (_wake is null) return;
        _wake.Enabled = enabled;
        if (!enabled)
        {
            // turning wake off: end any wake session and drop the mic unless
            // PTT is mid-hold
            CloseWakeSession("wake disabled");
            if (!_pttActive) _audio.StopMic();
            _tray.SetStatus("CHORUS — wake off");
            _form.AppendSystem("wake word off — use PTT or the wake hotkey");
        }
        else
        {
            _wake.Reset();
            _audio.StartMic();
            _tray.SetStatus($"CHORUS — wake listening · say \"{_wake.Phrase}\"");
            _form.AppendSystem($"wake word on — say \"{_wake.Phrase}\"");
        }
    }

    private static void OnTextSelectPressed()
    {
        // Toggle: second press cancels the overlay or stops the reading.
        _textSelect.Toggle();
    }

    private static void OnClipboardReadPressed()
    {
        // Toggle: second press stops the reading.
        _clipboardReader.Toggle();
    }

    // -- mic capture (NAudio thread) ---------------------------------------
    private static void OnMicFrame(short[] frame)
    {
        if (_state.Muted) return;

        // continuous wake-word spotting: feed every frame while idle and the
        // mic is ours. The engine gates on its own Enabled/Muted, and the
        // app gates on PTT/wake-session/agent-speaking so the wake word
        // can't fire mid-conversation or while the agent's voice is in the
        // room. All single-word volatile reads — audio-thread safe.
        if (_wake is not null
            && _wake.Enabled
            && !_wakeActive
            && !_pttActive
            && !_state.IsSpeakingOrPending)
        {
            _wake.FeedFrame(frame);
        }

        if (_pttActive)
        {
            Interlocked.Increment(ref _pttFrameCount);
            EncodeAndSend(frame);
            return;
        }

        if (_wakeActive)
        {
            double rms = Rms(frame);
            bool voiced = rms > VadThreshold;
            if (voiced) Interlocked.Exchange(ref _lastVoicedTicks, DateTime.UtcNow.Ticks);

            if (voiced && !_vadInSpeech)
            {
                _vadInSpeech = true;
                _vadSilentFrames = 0;
                if (_state.IsSpeakingOrPending)
                {
                    // talking over the agent = barge-in
                    FireAndForget(_client.SendBargeInAsync());
                    _ui!.Post(_ => _audio.ClearPlayback(), null);
                }
                else
                {
                    FireAndForget(_client.SendVadAsync("speech_start"));
                }
            }
            else if (_vadInSpeech)
            {
                if (voiced) _vadSilentFrames = 0;
                else
                {
                    _vadSilentFrames++;
                    if (_vadSilentFrames >= VadHangoverFrames)
                    {
                        _vadInSpeech = false;
                        FireAndForget(_client.SendVadAsync("speech_end"));
                    }
                }
            }

            if (_vadInSpeech) EncodeAndSend(frame);
        }
    }

    private static void EncodeAndSend(short[] frame)
    {
        try
        {
            var opus = _audioEncoder.EncodeFrame(frame);
            var send = _client.SendAudioFrameAsync(opus);
            _lastAudioSend = send;
            FireAndForget(send);
        }
        catch { /* dropped frame — non-fatal */ }
    }

    /// <summary>Stream open/close + lifecycle lines: console (dev) and transcript.</summary>
    private static void Log(string line)
    {
        Console.WriteLine(line);
        _ui?.Post(_ => _form.AppendLine(line, Color.FromArgb(0, 131, 143)), null);
    }

    // Encoder is owned by the audio thread only (Concentus is not thread-safe).
    [ThreadStatic] private static OpusEncoder16k? _tlsEncoder;
    private static OpusEncoder16k _audioEncoder => _tlsEncoder ??= new OpusEncoder16k();

    private static double Rms(short[] frame)
    {
        long sum = 0;
        foreach (var s in frame) sum += (long)s * s;
        return Math.Sqrt((double)sum / frame.Length);
    }

    private static void Shutdown()
    {
        _pttActive = false;
        _wakeActive = false;
        _textSelect.Dispose();
        _clipboardReader.Dispose();
        _audio.StopMic();
        _form.Quit();
        // Application.Run(TrayApplicationContext) does NOT exit when the form
        // closes — the context owns the loop, so exit it explicitly.
        _appContext.ExitThread();
    }

    private static async void FireAndForget(Task t)
    {
        try { await t; }
        catch { /* handled by reconnect loop */ }
    }
}
