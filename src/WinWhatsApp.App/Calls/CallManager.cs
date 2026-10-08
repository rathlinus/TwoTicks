using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using WinWhatsApp.App.Models;
using WinWhatsApp.Core;

namespace WinWhatsApp.App.Calls;

public enum CallPhase
{
    /// <summary>No call.</summary>
    Idle,

    /// <summary>Someone calls and it rings here.</summary>
    Incoming,

    /// <summary>Calling someone, until they answer.</summary>
    Outgoing,

    /// <summary>Answered; the sound is being set up.</summary>
    Connecting,
    Active,

    /// <summary>Over; the window still says so for a moment.</summary>
    Ended,
}

/// <summary>
/// The one call there can be at a time: answering, placing, muting and ending
/// it, and what the call window shows. Call stanzas go between the helper and
/// the calling engine through here. Everything runs on the UI thread.
/// </summary>
public sealed class CallManager : Observable
{
    // The engine's call states, from WhatsApp Web.
    private const int StateNone = 0, StateCalling = 1, StatePreacceptReceived = 2, StateReceivedCall = 3, StateAcceptSent = 4,
        StateAcceptReceived = 5, StateActive = 6, StateActiveElsewhere = 7, StateConnectedLonely = 11, StatePreCalling = 12, StateEnding = 13;

    private static readonly string s_sounds = Path.Combine(AppContext.BaseDirectory, "Assets", "WhatsAppSounds");

    private readonly DispatcherQueue _ui;
    private readonly WhatsAppClient _client;
    private readonly Notifier _notifier;
    private readonly AppSettings _settings;
    private readonly Func<string, ChatItem?> _chatOf;
    private readonly VoipEngine _engine;
    private readonly List<JsonObject> _waiting = [];
    private readonly DispatcherQueueTimer _clock;
    private readonly DispatcherQueueTimer _idle;
    private readonly DispatcherQueueTimer _closeTimer;
    private Task? _starting;
    private int _engineRun;
    private bool _ready;
    private MediaPlayer? _sound;
    private CallWindow? _window;

    private CallPhase _phase;
    private string? _callId;
    private string? _chat;
    private string _name = "";
    private ImageSource? _avatar;
    private string _status = "";
    private bool _muted;
    private DateTime _activeSince;
    private bool _wasActive;

    internal CallManager(DispatcherQueue ui, WhatsAppClient client, Notifier notifier, AppSettings settings, Func<string, ChatItem?> chatOf)
    {
        _ui = ui;
        _client = client;
        _notifier = notifier;
        _settings = settings;
        _chatOf = chatOf;
        _engine = new VoipEngine(ui);
        _engine.MessageReceived += OnEngineMessage;

        _clock = ui.CreateTimer();
        _clock.Interval = TimeSpan.FromSeconds(1);
        _clock.Tick += (_, _) => UpdateDuration();
        // The engine takes a few hundred megabytes; it stops a while after the last call.
        _idle = ui.CreateTimer();
        _idle.Interval = TimeSpan.FromMinutes(2);
        _idle.IsRepeating = false;
        _idle.Tick += (_, _) => StopEngine();
        _closeTimer = ui.CreateTimer();
        _closeTimer.Interval = TimeSpan.FromSeconds(2);
        _closeTimer.IsRepeating = false;
        _closeTimer.Tick += (_, _) => Reset();

        client.CallSignalReceived += signal => ui.TryEnqueue(() => OnSignal(signal));
    }

    public CallPhase Phase { get => _phase; private set { if (Set(ref _phase, value)) { OnPropertyChanged(nameof(IsRinging)); } } }

    /// <summary>The chat of the person on the call.</summary>
    public string? Chat => _chat;
    public string Name { get => _name; private set => Set(ref _name, value); }
    public ImageSource? Avatar { get => _avatar; private set => Set(ref _avatar, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public bool IsMuted { get => _muted; private set => Set(ref _muted, value); }
    public bool IsRinging => _phase == CallPhase.Incoming;

    /// <summary>Whether the calling engine is up and takes what a call has for it.</summary>
    public bool IsEngineReady => _ready;

    // ---- From WhatsApp ----

    private void OnSignal(CallSignalData signal)
    {
        Log.Info($"Call {signal.Kind} {signal.CallId} from {signal.Peer}");
        if (signal.Kind == "offer" && _phase == CallPhase.Idle)
        {
            _callId = signal.CallId;
            Show(signal.Chat ?? "", signal.Name ?? signal.Chat?.Split('@')[0] ?? "");
            Phase = CallPhase.Incoming;
            Status = Loc.T("calls.incomingVoiceCall");
            PlaySound("whatsapp_windows_ringtone_02.m4a", loop: true);
            _notifier.ShowIncomingCall(Name, _chatOf(_chat ?? "")?.AvatarPath);
            ShowWindow(activate: false);
        }
        else if (signal.Kind != "offer" && !_engine.IsRunning)
        {
            // About a call the engine never saw.
            return;
        }

        Send(new JsonObject
        {
            ["type"] = signal.Kind,
            ["node"] = signal.Node,
            ["peer"] = signal.Peer,
            ["platform"] = signal.Platform ?? "",
            ["version"] = signal.Version ?? "",
            ["t"] = signal.T,
            ["e"] = signal.E,
            ["offline"] = signal.Offline,
            ["notContact"] = signal.NotContact,
            ["tcToken"] = signal.TcToken ?? "",
        });
    }

    // ---- What the user does ----

    /// <summary>Calls the person of a chat, or shows the call there is.</summary>
    public async Task StartAsync(string chat, string name)
    {
        if (_phase != CallPhase.Idle)
        {
            ShowWindow(activate: true);
            return;
        }
        _callId = NewCallId();
        Log.Info($"Calling {chat}, call {_callId}");
        Show(chat, name);
        Phase = CallPhase.Outgoing;
        Status = Loc.T("calls.calling");
        ShowWindow(activate: true);
        string callId = _callId;
        try
        {
            EnsureEngine();
            CallTargetData target = await _client.PrepareCallAsync(chat);
            Log.Info($"Call target {target.Peer} with {target.Devices.Count} devices");
            if (_callId != callId || _phase != CallPhase.Outgoing)
            {
                return;
            }
            Send(new JsonObject
            {
                ["type"] = "call",
                ["peer"] = target.Peer,
                ["peerPn"] = target.PeerPn,
                ["devices"] = new JsonArray(target.Devices.Select(d => (JsonNode)d).ToArray()),
                ["callId"] = callId,
                ["tcToken"] = target.TcToken ?? "",
            });
        }
        catch (BridgeException e)
        {
            Log.Error("Failed to place a call", e);
            End(e.Message);
        }
    }

    public void Answer()
    {
        if (_phase != CallPhase.Incoming)
        {
            return;
        }
        StopSound();
        _notifier.ClearIncomingCall();
        Phase = CallPhase.Connecting;
        Status = Loc.T("calls.connecting");
        Send(new JsonObject { ["type"] = "accept" });
    }

    public void Decline()
    {
        if (_phase != CallPhase.Incoming)
        {
            return;
        }
        Send(new JsonObject { ["type"] = "reject" });
        End(Loc.T("calls.declined"));
    }

    /// <summary>Ends the call, or declines it while it rings.</summary>
    public void HangUp()
    {
        switch (_phase)
        {
            case CallPhase.Incoming:
                Decline();
                break;
            case CallPhase.Outgoing or CallPhase.Connecting or CallPhase.Active:
                if (_ready)
                {
                    Send(new JsonObject { ["type"] = "end" });
                }
                else
                {
                    _waiting.Clear();
                }
                End(Loc.T("calls.ended"));
                break;
        }
    }

    public void ToggleMute()
    {
        if (_phase is CallPhase.Idle or CallPhase.Ended)
        {
            return;
        }
        IsMuted = !IsMuted;
        Send(new JsonObject { ["type"] = "mute", ["muted"] = IsMuted });
    }

    /// <summary>Ends a call when the app quits.</summary>
    public void Shutdown()
    {
        if (_ready && _phase is CallPhase.Incoming or CallPhase.Outgoing or CallPhase.Connecting or CallPhase.Active)
        {
            Send(new JsonObject { ["type"] = _phase == CallPhase.Incoming ? "reject" : "end" });
        }
        StopSound();
        _window?.CloseForGood();
        _window = null;
        _engine.Dispose();
    }

    // ---- The engine ----

    /// <summary>Gets the engine ready to download, once connected to WhatsApp.</summary>
    public static void Prefetch() => VoipEngine.Prefetch();

    private void EnsureEngine()
    {
        _idle.Stop();
        if (_starting is null)
        {
            _starting = StartEngineAsync();
            // Goes to the engine once it is ready, before what is waiting for it.
            _waiting.Insert(0, DevicesMessage());
        }
    }

    /// <summary>Uses the microphone and speaker picked in the settings, also in a call that runs.</summary>
    public void ApplyDevices()
    {
        if (_ready)
        {
            _engine.Post(DevicesMessage());
        }
    }

    private JsonObject DevicesMessage() => new()
    {
        ["type"] = "devices",
        ["microphone"] = _settings.Microphone,
        ["speaker"] = _settings.Speaker,
    };

    private async Task StartEngineAsync()
    {
        int run = _engineRun;
        Log.Info("Starting the calling engine");
        try
        {
            CallIdentityData me = await _client.GetCallIdentityAsync();
            await _engine.StartAsync(me, me.CountryCode);
            await Task.Delay(TimeSpan.FromSeconds(60));
            if (run == _engineRun && !_ready)
            {
                Log.Error("The calling engine did not start in time");
                EngineFailed(Loc.T("calls.setupFailed"));
            }
        }
        catch (Exception e) when (run == _engineRun)
        {
            Log.Error("Failed to start the calling engine", e);
            EngineFailed(e switch
            {
                HttpRequestException => Loc.T("calls.downloadFailed"),
                InvalidDataException => Loc.T("calls.engineOutdated"),
                BridgeException or VoipSetupException => e.Message,
                _ => Loc.T("calls.setupFailed"),
            });
        }
    }

    private void Send(JsonObject message)
    {
        if (_ready)
        {
            _engine.Post(message);
            return;
        }
        _waiting.Add(message);
        EnsureEngine();
    }

    private void OnEngineMessage(JsonElement message)
    {
        string type = message.GetProperty("type").GetString() ?? "";
        switch (type)
        {
            case "ready":
                Log.Info("The calling engine is ready");
                _ready = true;
                foreach (JsonObject waiting in _waiting)
                {
                    _engine.Post(waiting);
                }
                _waiting.Clear();
                break;
            case "signal":
                _ = SendSignalAsync(message.GetProperty("peer").GetString() ?? "", message.GetProperty("payload").GetString() ?? "");
                break;
            case "state":
                OnState(message.GetProperty("state").GetInt32(), message.GetProperty("callId").GetString() ?? "");
                break;
            case "micFailed":
                Log.Error("No microphone for the call: " + message.GetProperty("message").GetString());
                Status = Loc.T("calls.micFailed");
                break;
            case "failed":
                string text = message.TryGetProperty("message", out JsonElement m) ? m.GetString() ?? "" : "";
                Log.Error("The calling engine failed: " + text);
                EngineFailed(Loc.T("calls.failed"));
                break;
            case "error":
                Log.Error($"The calling engine could not {message.GetProperty("request").GetString()}: {message.GetProperty("message").GetString()}");
                break;
        }
    }

    private async Task SendSignalAsync(string peer, string payload)
    {
        try
        {
            CallAckData ack = await _client.SendCallAsync(peer, payload);
            Log.Info($"Call stanza to {peer} acked, error {ack.Error}");
            if (_engine.IsRunning)
            {
                _engine.Post(new JsonObject
                {
                    ["type"] = "ack",
                    ["node"] = ack.Node,
                    ["error"] = ack.Error,
                    ["ackType"] = ack.Type ?? "",
                    ["peer"] = peer,
                    ["tcToken"] = ack.TcToken ?? "",
                });
            }
        }
        catch (BridgeException e)
        {
            Log.Error("Failed to send a call stanza", e);
        }
    }

    private void OnState(int state, string callId)
    {
        Log.Info($"Call {callId} is in state {state}");
        if (_callId is not null && callId.Length > 0 && !string.Equals(callId, _callId, StringComparison.OrdinalIgnoreCase))
        {
            // Another call, such as one that came in during this one.
            return;
        }
        switch (state)
        {
            case StateCalling or StatePreCalling:
                Status = Loc.T("calls.calling");
                break;
            case StatePreacceptReceived:
                Status = Loc.T("calls.ringing");
                break;
            case StateReceivedCall:
                break;
            case StateAcceptSent or StateAcceptReceived:
                if (_phase is CallPhase.Outgoing or CallPhase.Incoming)
                {
                    StopSound();
                    Phase = CallPhase.Connecting;
                    Status = Loc.T("calls.connecting");
                }
                break;
            case StateActive or StateConnectedLonely:
                if (_phase != CallPhase.Active)
                {
                    StopSound();
                    _notifier.ClearIncomingCall();
                    Phase = CallPhase.Active;
                    _wasActive = true;
                    _activeSince = DateTime.UtcNow;
                    UpdateDuration();
                    _clock.Start();
                }
                break;
            case StateActiveElsewhere:
                End(Loc.T("calls.answeredElsewhere"));
                break;
            case StateNone or StateEnding:
                if (_phase is not (CallPhase.Idle or CallPhase.Ended))
                {
                    End(_wasActive ? Loc.T("calls.ended") : _phase == CallPhase.Incoming ? Loc.T("calls.missed") : Loc.T("calls.notAnswered"));
                }
                break;
        }
    }

    private void EngineFailed(string message)
    {
        StopEngine();
        if (_phase is not (CallPhase.Idle or CallPhase.Ended))
        {
            End(message);
        }
    }

    private void StopEngine()
    {
        _engineRun++;
        _idle.Stop();
        _engine.Stop();
        _starting = null;
        _ready = false;
        _waiting.Clear();
    }

    // ---- The window and the sounds ----

    private void Show(string chat, string name)
    {
        _chat = chat;
        OnPropertyChanged(nameof(Chat));
        ChatItem? item = _chatOf(chat);
        Name = item?.Name ?? (name.Length > 0 ? name : chat.Split('@')[0]);
        Avatar = item?.Avatar;
        IsMuted = false;
        _wasActive = false;
        _closeTimer.Stop();
    }

    /// <summary>Brings the window of the call there is to the front.</summary>
    public void ShowWindow()
    {
        if (_phase != CallPhase.Idle)
        {
            ShowWindow(activate: true);
        }
    }

    private void ShowWindow(bool activate)
    {
        _window ??= new CallWindow(this);
        _window.ShowCall(activate);
    }

    private void End(string status)
    {
        Log.Info("Call ended: " + status);
        bool sound = _phase is CallPhase.Active or CallPhase.Connecting or CallPhase.Outgoing;
        StopSound();
        _notifier.ClearIncomingCall();
        _clock.Stop();
        Phase = CallPhase.Ended;
        Status = status;
        if (sound)
        {
            PlaySound("whatsapp_windows_hangup_01_705kbps.wav", loop: false);
        }
        _closeTimer.Start();
        _idle.Stop();
        _idle.Start();
    }

    private void Reset()
    {
        _window?.Hide();
        Phase = CallPhase.Idle;
        _callId = null;
        _chat = null;
        Status = "";
    }

    private void UpdateDuration()
    {
        if (_phase == CallPhase.Active)
        {
            Status = Formatting.Duration(DateTime.UtcNow - _activeSince);
        }
    }

    private void PlaySound(string file, bool loop)
    {
        StopSound();
        string path = Path.Combine(s_sounds, file);
        if (!File.Exists(path))
        {
            return;
        }
        _sound = new MediaPlayer { Source = MediaSource.CreateFromUri(new Uri(path)), IsLoopingEnabled = loop };
        _sound.Play();
    }

    private void StopSound()
    {
        _sound?.Pause();
        _sound?.Dispose();
        _sound = null;
    }

    /// <summary>A call ID as WhatsApp Web makes them: 32 hexadecimal digits, the first two zero.</summary>
    private static string NewCallId() => "00" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16))[2..];
}
