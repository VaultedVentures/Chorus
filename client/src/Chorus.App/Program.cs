using Chorus.Core;

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
    private static TrayApplicationContext _appContext = null!;
    private static SynchronizationContext? _ui;

    // Capture mode + wake VAD state (audio thread owns these).
    private static volatile bool _pttActive;
    private static volatile bool _wakeActive;
    private static volatile bool _vadInSpeech;
    private static int _vadSilentFrames;

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
        _form = new VoiceConsoleForm(_client, _state, _tray);
        _appContext = new TrayApplicationContext(_form, settings.StartHidden);

        // The WinForms sync context is installed when the first control handle
        // is created (above). Capture it AFTER that, or _ui.Post would NPE.
        // Fallback: a fresh WindowsFormsSynchronizationContext posts to this
        // thread's message queue, which Application.Run pumps below.
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        using var hotkeys = new GlobalHotkeys();
        hotkeys.Register(_form.Handle);
        hotkeys.PttPressed += OnPttPressed;
        hotkeys.PttReleased += OnPttReleased;
        hotkeys.WakePressed += OnWakePressed;

        _tray.ShowConsoleRequested += () => _ui!.Post(_ => _appContext.ShowConsole(), null);
        _tray.ReconnectRequested += () => _state.ReconnectRequested = true;
        _tray.QuitRequested += () => _ui!.Post(_ => Shutdown(), null);

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

        // keep reconnect requests flowing from the form/state into the manager
        var pollTimer = new System.Windows.Forms.Timer { Interval = 500 };
        pollTimer.Tick += (_, _) =>
        {
            if (_state.ReconnectRequested)
            {
                _state.ReconnectRequested = false;
                connection.RequestReconnect();
            }
        };
        pollTimer.Start();

        // Startup mic probe: surfaces a missing/blocked microphone immediately,
        // before the user ever presses the hotkey.
        _ui.Post(_ => ProbeMic(), null);

        Application.Run(_appContext);
        appCts.Cancel();
        _ = _client.DisconnectAsync();
    }

    private static void ProbeMic()
    {
        if (_audio.StartMic()) _audio.StopMic();
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
        _audio.StartMic();
        FireAndForget(_client.SendPttAsync(true));
    }

    private static void OnPttReleased()
    {
        if (!_pttActive) return;
        _pttActive = false;
        _audio.StopMic();
        FireAndForget(_client.SendPttAsync(false));
    }

    private static void OnWakePressed()
    {
        if (_state.Muted || _pttActive) return;
        _wakeActive = true;
        _vadInSpeech = false;
        _vadSilentFrames = 0;
        _audio.StartMic();
        FireAndForget(_client.SendWakeAsync());
    }

    // -- mic capture (NAudio thread) ---------------------------------------
    private static void OnMicFrame(short[] frame)
    {
        if (_state.Muted) return;

        if (_pttActive)
        {
            EncodeAndSend(frame);
            return;
        }

        if (_wakeActive)
        {
            double rms = Rms(frame);
            bool voiced = rms > VadThreshold;

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
            FireAndForget(_client.SendAudioFrameAsync(opus));
        }
        catch { /* dropped frame — non-fatal */ }
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
