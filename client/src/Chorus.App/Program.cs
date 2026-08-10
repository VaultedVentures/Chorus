using Chorus.Core;

namespace Chorus.App;

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
        _ui = SynchronizationContext.Current;

        var settings = AppSettings.Load();
        _state = new SessionState();
        _client = new ChorusClient(settings.GatewayUrl);
        _audio = new AudioEngine();
        _tray = new TrayDaemon();
        _form = new VoiceConsoleForm(_client, _state, _tray);

        using var hotkeys = new GlobalHotkeys();
        hotkeys.Register(_form.Handle);
        hotkeys.PttPressed += OnPttPressed;
        hotkeys.PttReleased += OnPttReleased;
        hotkeys.WakePressed += OnWakePressed;

        _tray.ShowConsoleRequested += () => ShowConsole();
        _tray.ReconnectRequested += () => _state.ReconnectRequested = true;
        _tray.QuitRequested += () => _ui!.Post(_ => Shutdown(), null);

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

        Application.Run(_form);
        appCts.Cancel();
        _ = _client.DisconnectAsync();
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

    private static void ShowConsole()
    {
        _ui!.Post(_ =>
        {
            _form.Show();
            _form.WindowState = FormWindowState.Normal;
            _form.Activate();
        }, null);
    }

    private static void Shutdown()
    {
        _pttActive = false;
        _wakeActive = false;
        _audio.StopMic();
        _form.Quit();
    }

    private static async void FireAndForget(Task t)
    {
        try { await t; }
        catch { /* handled by reconnect loop */ }
    }
}
