using Chorus.Core;

namespace Chorus.App;

/// <summary>
/// Voice console window — the PRIMARY CHORUS surface (Scott-approved design:
/// a real movable/sizable/pinnable Form, not a tray-only app). Shows the
/// transcript, a big turn-state indicator, captions, mute, agent selector and
/// reconnect. Closing hides to the tray; the global hotkeys keep working.
/// </summary>
public sealed class VoiceConsoleForm : Form
{
    private readonly ChorusClient _client;
    private readonly SessionState _state;
    private readonly TrayDaemon _tray;

    private readonly Label _connectionLabel;
    private readonly ComboBox _agentCombo;
    private readonly CheckBox _pinCheck;
    private readonly CheckBox _muteCheck;
    private readonly Button _reconnectButton;
    private readonly Button _readScreenButton;
    private readonly Label _turnIndicator;
    private readonly RichTextBox _transcript;
    private readonly Label _captionLabel;
    private readonly Label _hintLabel;
    private readonly System.Windows.Forms.Timer _ringTimer;

    private DateTime _pendingEnd;
    private string _pendingVerdict = "";
    private bool _quitting;

    /// <summary>Raised when the user clicks "Read Screen" in the console.</summary>
    public event Action? ReadScreenRequested;

    public VoiceConsoleForm(ChorusClient client, SessionState state, TrayDaemon tray)
    {
        _client = client;
        _state = state;
        _tray = tray;

        Text = "CHORUS Voice Console";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(760, 540);
        MinimumSize = new Size(560, 400);
        Font = new Font("Segoe UI", 10f);

        // --- top bar: connection + agent + actions ---
        var top = new TableLayoutPanel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(8, 6, 8, 2) };
        top.ColumnCount = 6;
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _connectionLabel = new Label { Text = "CHORUS — connecting…", AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Color.FromArgb(90, 90, 90) };
        _agentCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130, Anchor = AnchorStyles.Right };
        _agentCombo.SelectedIndexChanged += async (_, _) => await OnAgentChangedAsync();
        _pinCheck = new CheckBox { Text = "Pin", AutoSize = true, Anchor = AnchorStyles.Right };
        _pinCheck.CheckedChanged += (_, _) => TopMost = _pinCheck.Checked;
        _muteCheck = new CheckBox { Text = "Mute", AutoSize = true, Anchor = AnchorStyles.Right };
        _muteCheck.CheckedChanged += (_, _) => _state.Muted = _muteCheck.Checked;
        _reconnectButton = new Button { Text = "Reconnect", AutoSize = true, Anchor = AnchorStyles.Right };
        _reconnectButton.Click += (_, _) => _state.ReconnectRequested = true;
        _readScreenButton = new Button { Text = "Read Screen", AutoSize = true, Anchor = AnchorStyles.Right };
        _readScreenButton.Click += (_, _) => ReadScreenRequested?.Invoke();

        top.Controls.Add(_connectionLabel, 0, 0);
        top.Controls.Add(_agentCombo, 1, 0);
        top.Controls.Add(_pinCheck, 2, 0);
        top.Controls.Add(_muteCheck, 3, 0);
        top.Controls.Add(_readScreenButton, 4, 0);
        top.Controls.Add(_reconnectButton, 5, 0);

        // --- turn indicator ---
        _turnIndicator = new Label
        {
            Dock = DockStyle.Top,
            Height = 52,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI Semibold", 13f),
            ForeColor = Color.White,
            BackColor = Color.FromArgb(120, 120, 120),
            Text = "IDLE — Win+Shift+T to talk",
        };

        // --- caption (last agent_text) ---
        _captionLabel = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 26,
            AutoEllipsis = true,
            ForeColor = Color.FromArgb(110, 110, 110),
            Font = new Font("Segoe UI", 9f),
            Text = "Captions appear here — never spoken aloud.",
        };

        // --- transcript ---
        _transcript = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = Color.FromArgb(250, 250, 250),
            Font = new Font("Segoe UI", 10.5f),
        };

        // --- hint bar ---
        _hintLabel = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 22,
            Text = "Hold Win+Shift+T to talk  ·  Win+Shift+W wake  ·  Win+Shift+R reads screen text  ·  close hides to tray",
            ForeColor = Color.FromArgb(130, 130, 130),
            Font = new Font("Segoe UI", 8.5f),
            TextAlign = ContentAlignment.MiddleLeft,
        };

        Controls.Add(_transcript);
        Controls.Add(_captionLabel);
        Controls.Add(_hintLabel);
        Controls.Add(_turnIndicator);
        Controls.Add(top);

        // --- pending ring ---
        _ringTimer = new System.Windows.Forms.Timer { Interval = 100 };
        _ringTimer.Tick += (_, _) => UpdateRing();

        FormClosing += OnFormClosing;
    }

    public void SetConnection(string text)
    {
        if (InvokeRequired) { BeginInvoke(() => SetConnection(text)); return; }
        _connectionLabel.Text = text;
        _tray.SetStatus($"CHORUS — {text}");
    }

    /// <summary>Handle a server event (called on the UI thread).</summary>
    public void HandleEvent(ServerEvent e)
    {
        switch (e)
        {
            case ServerEvent.HelloAck ack:
                PopulateAgents(ack.AgentRoster);
                AppendLine($"— session {ack.SessionId} · proto {ack.Proto} —", Color.FromArgb(150, 150, 150));
                break;
            case ServerEvent.Turn turn:
                SetTurnState(turn.State, turn.Complete, turn.TimeoutMs);
                break;
            case ServerEvent.Final final:
                AppendLine($"You: {final.Text}", Color.FromArgb(30, 30, 30));
                break;
            case ServerEvent.AgentText at:
                AppendLine($"{at.Agent}: {at.Text}", Color.FromArgb(0, 90, 140));
                _captionLabel.Text = $"{at.Agent}: {at.Text}";
                break;
            case ServerEvent.Error err:
                AppendLine($"error[{err.Code}]: {err.Detail}", Color.FromArgb(190, 40, 40));
                _tray.SetStatus($"CHORUS — error {err.Code}");
                break;
            case ServerEvent.Unknown unk:
                AppendLine($"unknown event: {unk.RawType}", Color.FromArgb(150, 150, 150));
                break;
            default:
                break; // audio markers, pong, bye_ack — no UI
        }
    }

    public void PopulateAgents(IReadOnlyList<AgentInfo> roster)
    {
        if (roster.Count == 0) return;
        _agentCombo.Items.Clear();
        foreach (var a in roster)
            _agentCombo.Items.Add(new AgentItem(a));
        var current = _agentCombo.Items.Cast<AgentItem>().FirstOrDefault(i => i.Info.Id == _state.Agent);
        _agentCombo.SelectedItem = current ?? _agentCombo.Items[0];
    }

    private async Task OnAgentChangedAsync()
    {
        if (_agentCombo.SelectedItem is not AgentItem item || item.Info.Id == _state.Agent) return;
        _state.Agent = item.Info.Id;
        AppendLine($"— switching agent to {item.Info.Id} —", Color.FromArgb(150, 150, 150));
        _state.ReconnectRequested = true;
        await Task.CompletedTask;
    }

    public void SetTurnState(string state, string? complete, int? timeoutMs)
    {
        _state.Turn = state;
        switch (state)
        {
            case "idle":
                SetIndicator("IDLE — Win+Shift+T to talk", Color.FromArgb(120, 120, 120));
                _ringTimer.Stop();
                break;
            case "listening":
                SetIndicator("LISTENING…", Color.FromArgb(46, 125, 50));
                _ringTimer.Stop();
                break;
            case "pending":
                _pendingVerdict = complete ?? "likely";
                _pendingEnd = DateTime.UtcNow.AddMilliseconds(timeoutMs ?? 1100);
                SetIndicator($"THINKING ({_pendingVerdict}) — {timeoutMs / 1000.0:F1}s", Color.FromArgb(249, 168, 37));
                _ringTimer.Start();
                break;
            case "processing":
                SetIndicator("PROCESSING…", Color.FromArgb(21, 101, 192));
                _ringTimer.Stop();
                break;
            case "speaking":
                SetIndicator("SPEAKING…", Color.FromArgb(0, 131, 143));
                _ringTimer.Stop();
                break;
            default:
                SetIndicator(state.ToUpperInvariant(), Color.FromArgb(120, 120, 120));
                break;
        }
    }

    private void UpdateRing()
    {
        var left = _pendingEnd - DateTime.UtcNow;
        if (left.TotalMilliseconds <= 0)
        {
            SetIndicator($"THINKING ({_pendingVerdict}) — …", Color.FromArgb(249, 168, 37));
            _ringTimer.Stop();
            return;
        }
        SetIndicator($"THINKING ({_pendingVerdict}) — {left.TotalSeconds:F1}s", Color.FromArgb(249, 168, 37));
    }

    private void SetIndicator(string text, Color color)
    {
        if (InvokeRequired) { BeginInvoke(() => SetIndicator(text, color)); return; }
        _turnIndicator.Text = text;
        _turnIndicator.BackColor = color;
    }

    /// <summary>Append a line to the transcript (thread-safe; called from anywhere).</summary>
    public void AppendLine(string text, Color color)
    {
        if (InvokeRequired) { BeginInvoke(() => AppendLine(text, color)); return; }
        _transcript.SelectionStart = _transcript.TextLength;
        _transcript.SelectionLength = 0;
        _transcript.SelectionColor = color;
        _transcript.AppendText(text + Environment.NewLine);
        _transcript.SelectionStart = _transcript.TextLength;
        _transcript.ScrollToCaret();
    }

    /// <summary>Append a local system/feature line (screen text reads, status) to the transcript.</summary>
    public void AppendSystem(string text)
    {
        AppendLine($"— {text} —", Color.FromArgb(130, 130, 130));
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_quitting && e.CloseReason == CloseReason.UserClosing)
        {
            // close = hide to tray (daemon owns the hotkeys and mic)
            e.Cancel = true;
            Hide();
            _tray.ShowBalloon("CHORUS", "Still running in the tray — Win+Shift+T to talk.");
        }
    }

    public void Quit()
    {
        _quitting = true;
        Close();
    }

    private sealed record AgentItem(AgentInfo Info)
    {
        public override string ToString() =>
            string.IsNullOrEmpty(Info.DisplayName) ? Info.Id : Info.DisplayName;
    }
}
