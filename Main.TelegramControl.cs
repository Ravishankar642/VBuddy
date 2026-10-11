// Main.TelegramControl.cs  -  VBuddy Telegram control (full command set)
//
// Two-way control of vPilot from a Telegram bot: connect/disconnect, messaging, radio text,
// push-to-talk, ident, Mode C, flight plans, plus live info lookups (/where, /atc, /atis,
// /metar) and alerts (controller watch, broadcasts, radio listening).
//
// Also: SimBrief OFP (/simbrief), in-chat toggle buttons (/settings), optional SimConnect telemetry
// (/sim, top-of-descent and touchdown alerts) live in TelegramControl.Extras.cs and SimLink.cs.
//
// Hook-up: ONE line in Main.Initialize(), right after InitAtcAhead():   InitTelegramControl();
//
// What vPilot's plugin SDK can NOT do (so neither can this): tune COM frequencies, set the
// squawk code, or read your own aircraft's position from the sim. /where therefore uses the
// public VATSIM data feed (about 15 s behind).
//
// No extra package references needed.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using RossCarlson.Vatsim.Vpilot.Plugins;
using RossCarlson.Vatsim.Vpilot.Plugins.Events;
using RossCarlson.Vatsim.Vpilot.Plugins.Exceptions;

namespace VBuddy {

    public partial class Main {

        private TelegramControl _tgControl;

        private void InitTelegramControl() {
            try {
                var ini = new IniFile(Path.Combine(_pluginDir, "VBuddy.ini"));
                var s = TelegramControlSettings.Load(ini);
                if (!s.Enabled) return;

                if (!s.IsValid) {
                    SendDebug("[TelegramControl] Enabled, but BotToken or ChatId is missing/invalid. " +
                              "Set them under [Telegram] (or [TelegramControl]) in VBuddy.ini.");
                    return;
                }

                _tgControl = new TelegramControl(s, _vPilot, _uiContext, SendDebug, _pluginDir);
                _tgControl.Start();
                SendDebug("[TelegramControl] Started. Send /help to your bot.");
            } catch (Exception ex) {
                SendDebug("[TelegramControl] Failed to start: " + ex.Message);
            }
        }
    }

    // ===================================================================== settings

    internal sealed partial class TelegramControlSettings {
        public bool Enabled;
        public string BotToken;
        public long ChatId;
        public bool RelayBroadcast = true;
        public bool NotifyConnect = true;
        public bool NotifyDisconnect;          // off: [Disconnect] + your driver already does this
        public bool ConfirmDisconnect = true;  // /disconnect needs /confirm
        public bool ListenAllRadio;            // forward every text radio message on your frequencies
        public int PttMaxSeconds = 30;         // /ptt on auto-releases after this long
        public string Watch;                   // comma separated controller callsign prefixes
        public bool VBuddyRelaysOwnRadio;      // VBuddy's own [RelayRadio] already sends these to Telegram

        public bool IsValid {
            get { return !string.IsNullOrWhiteSpace(BotToken) && ChatId != 0; }
        }

        public static TelegramControlSettings Load(IniFile ini) {
            var s = new TelegramControlSettings();
            s.Enabled = ReadBool(ini, "Enabled", false);
            s.RelayBroadcast = ReadBool(ini, "RelayBroadcast", true);
            s.NotifyConnect = ReadBool(ini, "NotifyConnect", true);
            s.NotifyDisconnect = ReadBool(ini, "NotifyDisconnect", false);
            s.ConfirmDisconnect = ReadBool(ini, "ConfirmDisconnect", true);
            s.ListenAllRadio = ReadBool(ini, "ListenAllRadio", false);
            s.Watch = Clean(ini.Read("Watch", "TelegramControl", null));

            int ptt;
            string pv = Clean(ini.Read("PttMaxSeconds", "TelegramControl", null));
            if (pv != null && int.TryParse(pv, NumberStyles.Integer, CultureInfo.InvariantCulture, out ptt))
                s.PttMaxSeconds = Math.Max(5, Math.Min(120, ptt));

            // Reuse the existing [Telegram] credentials unless overridden in [TelegramControl].
            s.BotToken = Clean(ini.Read("BotToken", "TelegramControl", null)) ?? Clean(ini.Read("BotToken", "Telegram", null));
            string chat = Clean(ini.Read("ChatId", "TelegramControl", null)) ?? Clean(ini.Read("ChatId", "Telegram", null));
            long id;
            if (chat != null && long.TryParse(chat, NumberStyles.Integer, CultureInfo.InvariantCulture, out id)) s.ChatId = id;

            string driver = Clean(ini.Read("Driver", "General", null));
            string rr = Clean(ini.Read("Enabled", "RelayRadio", null));
            bool rrOn;
            s.VBuddyRelaysOwnRadio = string.Equals(driver, "telegram", StringComparison.OrdinalIgnoreCase)
                                     && rr != null && bool.TryParse(rr, out rrOn) && rrOn;
            s.LoadExtras(ini);
            return s;
        }

        private static string Clean(string v) {
            if (v == null) return null;
            v = v.Trim().Trim('"').Trim();
            return v.Length == 0 ? null : v;
        }

        private static bool ReadBool(IniFile ini, string key, bool def) {
            string v = Clean(ini.Read(key, "TelegramControl", null));
            bool b;
            return v != null && bool.TryParse(v, out b) ? b : def;
        }
    }

    // ===================================================================== the bot

    internal sealed partial class TelegramControl {

        // Long-polling needs a timeout longer than the 25 s poll window.
        private static readonly HttpClient Client = new HttpClient { Timeout = TimeSpan.FromSeconds(40) };

        private static readonly Dictionary<string, string> Presets = new Dictionary<string, string> {
            { "/wilco", "Wilco" }, { "/roger", "Roger" }, { "/standby", "Standby" }, { "/unable", "Unable" }
        };

        private const string Keyboard =
            "{\"keyboard\":[[\"/status\",\"/where\",\"/atc\"],[\"/fp\",\"/history\",\"/zulu\"]," +
            "[\"/ident\",\"/modec on\",\"/modec off\"],[\"/wilco\",\"/roger\",\"/standby\",\"/unable\"],[\"/sim\",\"/simbrief\",\"/settings\"]]," +
            "\"resize_keyboard\":true}";

        private static readonly string[][] BotMenu = {
            new[] { "status", "Connection status" },
            new[] { "where", "Position, speed, heading (yours or any pilot)" },
            new[] { "atc", "Online ATC and frequencies for an airport" },
            new[] { "atis", "ATIS / controller info text from the data feed" },
            new[] { "metar", "METAR for an airport" },
            new[] { "freq", "Frequency of an online controller" },
            new[] { "fp", "Show your flight plan" },
            new[] { "fileplan", "OFP values to enter in vPilot" },
            new[] { "simbrief", "Load your SimBrief OFP" },
            new[] { "sim", "Live aircraft data from the simulator" },
            new[] { "tune", "Tune COM1 or COM2 (frequency or controller)" },
            new[] { "squawk", "Set the transponder code" },
            new[] { "flightlog", "Current or last flight summary" },
            new[] { "track", "Send the flight track as a GPX file" },
            new[] { "settings", "Toggle alerts with buttons" },
            new[] { "connect", "Connect to VATSIM" },
            new[] { "disconnect", "Disconnect from VATSIM" },
            new[] { "confirm", "Confirm a pending action" },
            new[] { "msg", "Private message: /msg CALLSIGN text" },
            new[] { "reply", "Reply to the last private message" },
            new[] { "radio", "Send text on your radio frequency" },
            new[] { "wilco", "Radio: Wilco" },
            new[] { "roger", "Radio: Roger" },
            new[] { "standby", "Radio: Standby" },
            new[] { "unable", "Radio: Unable" },
            new[] { "ptt", "Push-to-talk on/off (auto-releases)" },
            new[] { "ident", "Squawk ident" },
            new[] { "modec", "Transponder Mode C on/off" },
            new[] { "info", "Request controller info text" },
            new[] { "listen", "Forward all radio text on/off" },
            new[] { "watch", "Alert when a controller comes online" },
            new[] { "unwatch", "Stop watching a controller" },
            new[] { "watchlist", "Show watched controllers" },
            new[] { "mute", "Pause automatic alerts" },
            new[] { "unmute", "Resume automatic alerts" },
            new[] { "history", "Recent messages" },
            new[] { "zulu", "Current Zulu time" },
            new[] { "ping", "Check the bot is alive" },
            new[] { "help", "List commands" }
        };

        private readonly TelegramControlSettings _s;
        private readonly IBroker _broker;
        private readonly SynchronizationContext _ui;
        private readonly Action<string> _log;
        private readonly string _identityPath;
        private readonly string _watchPath;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly object _lock = new object();

        private long _offset;
        private volatile bool _connected;
        private string _callsign = "";
        private string _connectedType = "";
        private DateTime _connectedAtUtc = DateTime.MinValue;
        private string _lastCallsign = "", _lastType = "", _lastSelcal = "";
        private string _modeC = "unknown";
        private volatile bool _listen;
        private DateTime _mutedUntilUtc = DateTime.MinValue;
        private string _lastPmFrom;
        private string _confirmAction;
        private DateTime _confirmExpiresUtc = DateTime.MinValue;
        private CancellationTokenSource _pttCts;
        private bool _conflictLogged;

        private readonly HashSet<string> _watch = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _history = new List<string>();
        private readonly Dictionary<string, DateTime> _infoRequested = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _lastAlert = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        public TelegramControl(TelegramControlSettings s, IBroker broker, SynchronizationContext ui,
                               Action<string> log, string pluginDir) {
            _s = s;
            _broker = broker;
            _ui = ui;
            _log = log;
            _identityPath = Path.Combine(pluginDir, "VBuddy.identity");
            _watchPath = Path.Combine(pluginDir, "VBuddy.watch");
            InitExtrasPaths(pluginDir);
        }

        private bool IsMuted { get { return DateTime.UtcNow < _mutedUntilUtc; } }

        public void Start() {
            LoadIdentity();
            LoadWatch();
            _listen = _s.ListenAllRadio;
            StartExtras();

            _broker.NetworkConnected += OnConnected;
            _broker.NetworkDisconnected += OnDisconnected;
            _broker.PrivateMessageReceived += (sender, e) => OnPrivate(e.From, e.Message);
            _broker.RadioMessageReceived += (sender, e) => OnRadio(e.Frequencies, e.From, e.Message);
            _broker.BroadcastMessageReceived += (sender, e) => OnBroadcast(e.From, e.Message);
            _broker.AtisReceived += (sender, e) => OnAtis(e.From, e.Lines);
            _broker.ControllerAdded += (sender, e) => OnControllerChange(e.Callsign, true);
            _broker.ControllerDeleted += (sender, e) => OnControllerChange(e.Callsign, false);
            _broker.ControllerFrequencyChanged += (sender, e) => OnControllerFrequency(e.Callsign);
            _broker.SessionEnded += (sender, e) => Stop();

            _log("[TelegramControl] UI context: " + (_ui != null ? "captured" : "none (using main window fallback)"));
            Task.Run(() => PollLoop(_cts.Token));
        }

        public void Stop() {
            try { _cts.Cancel(); } catch { }
            StopExtras();
            CancelPttTimer();
        }

        // ------------------------------------------------------ vPilot events

        private void OnConnected(object sender, NetworkConnectedEventArgs e) {
            _connected = true;
            _callsign = e.Callsign ?? "";
            _connectedType = e.TypeCode ?? "";
            _connectedAtUtc = DateTime.UtcNow;
            if (!string.IsNullOrEmpty(e.Callsign)) {
                _lastCallsign = e.Callsign;
                _lastType = e.TypeCode ?? "";
                _lastSelcal = e.SelcalCode ?? "";
                SaveIdentity();
            }
            Remember("Connected as " + _callsign);
            AutoFetchSimBrief();
            if (_s.NotifyConnect) {
                string type = string.IsNullOrEmpty(e.TypeCode) ? "" : " (" + e.TypeCode + ")";
                _ = SendAsync("\u2705 Connected to VATSIM as " + _callsign + type + (e.ObserverMode ? " [observer]" : ""));
            }
        }

        private void OnDisconnected(object sender, EventArgs e) {
            _connected = false;
            _callsign = "";
            _connectedType = "";
            lock (_lock) { _confirmAction = null; }
            CancelPttTimer();
            Remember("Disconnected");
            if (_s.NotifyDisconnect) _ = SendAsync("\u274C Disconnected from VATSIM.");
        }

        private void OnPrivate(string from, string message) {
            _lastPmFrom = from;
            Remember("PM from " + from + ": " + message);
            // VBuddy's own [RelayPrivate] already forwards these to your phone.
        }

        private void OnRadio(int[] freqs, string from, string message) {
            string f = TgFormat.Freqs(freqs);
            Remember("Radio " + f + " " + from + ": " + message);
            if (!_listen || IsMuted) return;

            bool mentionsMe = !string.IsNullOrEmpty(_callsign) && message != null && message.Contains(_callsign);
            if (_s.VBuddyRelaysOwnRadio && mentionsMe) return;   // avoid a duplicate of VBuddy's own message

            _ = SendAsync("\uD83D\uDCFB " + (f.Length > 0 ? f + "  " : "") + from + ":\n" + message);
        }

        private void OnBroadcast(string from, string message) {
            Remember("Broadcast from " + from + ": " + message);
            if (_s.RelayBroadcast && !IsMuted)
                _ = SendAsync("\uD83D\uDCE2 Broadcast from " + from + ":\n" + message);
        }

        private void OnAtis(string from, List<string> lines) {
            bool wanted;
            lock (_lock) {
                DateTime when;
                wanted = from != null && _infoRequested.TryGetValue(from, out when) && (DateTime.UtcNow - when).TotalSeconds < 120;
                if (wanted) _infoRequested.Remove(from);
            }
            if (!wanted) return;
            string body = lines == null ? "(empty)" : string.Join("\n", lines);
            _ = SendAsync("\u2139 Info from " + from + ":\n" + body);
        }

        private bool WatchMatch(string callsign) {
            if (string.IsNullOrEmpty(callsign)) return false;
            lock (_lock) {
                foreach (string w in _watch)
                    if (TgFormat.MatchesStation(callsign.ToUpperInvariant(), w)) return true;
            }
            return false;
        }

        private bool AlertAllowed(string key) {
            lock (_lock) {
                DateTime last;
                if (_lastAlert.TryGetValue(key, out last) && (DateTime.UtcNow - last).TotalMinutes < 5) return false;
                _lastAlert[key] = DateTime.UtcNow;
                return true;
            }
        }

        private void OnControllerChange(string callsign, bool online) {
            if (IsMuted || !WatchMatch(callsign)) return;
            if (!AlertAllowed(callsign + (online ? "+" : "-"))) return;
            _ = SendAsync(online ? "\uD83D\uDFE2 " + callsign + " is now online. (/atc for frequencies)"
                                 : "\uD83D\uDD34 " + callsign + " went offline or out of range.");
        }

        private void OnControllerFrequency(string callsign) {
            if (IsMuted || !WatchMatch(callsign)) return;
            if (!AlertAllowed(callsign + "f")) return;
            _ = SendAsync("\uD83D\uDD04 " + callsign + " changed frequency. Check /freq " + callsign);
        }

        // ------------------------------------------------------ Telegram plumbing

        private string Api(string method) {
            return "https://api.telegram.org/bot" + _s.BotToken + "/" + method;
        }

        private string Scrub(string text) {
            return string.IsNullOrEmpty(text) ? text : text.Replace(_s.BotToken, "***");
        }

        private async Task PollLoop(CancellationToken ct) {
            // Discard anything queued while vPilot was closed, so a stale "/disconnect"
            // can never fire at startup.
            try { await FlushBacklog(ct); } catch { }
            try { await RegisterCommands(); } catch { }

            _log("[TelegramControl] Listening for Telegram commands (chat id " + _s.ChatId + ").");
            await SendAsync("VBuddy Telegram control is online. Send /help for commands.", true);

            while (!ct.IsCancellationRequested) {
                try {
                    string url = Api("getUpdates") + "?timeout=25&offset=" + _offset +
                                 "&allowed_updates=%5B%22message%22%2C%22callback_query%22%5D";
                    using (var resp = await Client.GetAsync(url, ct)) {
                        string body = await resp.Content.ReadAsStringAsync();

                        if (!resp.IsSuccessStatusCode) {
                            if ((int)resp.StatusCode == 409 && !_conflictLogged) {
                                _conflictLogged = true;
                                _log("[TelegramControl] Telegram returned 409: another program is already " +
                                     "polling this bot (or a webhook is set). Use a dedicated bot for VBuddy.");
                            } else if ((int)resp.StatusCode != 409) {
                                _log("[TelegramControl] getUpdates failed: HTTP " + (int)resp.StatusCode);
                            }
                            await Task.Delay(10000, ct);
                            continue;
                        }
                        _conflictLogged = false;
                        await ProcessUpdates(body);
                    }
                } catch (OperationCanceledException) {
                    if (ct.IsCancellationRequested) return;      // shutting down
                    // otherwise just an HTTP timeout; loop again
                } catch (Exception ex) {
                    _log("[TelegramControl] Poll error: " + Scrub(ex.Message));
                    try { await Task.Delay(5000, ct); } catch { return; }
                }
            }
        }

        private async Task FlushBacklog(CancellationToken ct) {
            using (var resp = await Client.GetAsync(Api("getUpdates") + "?offset=-1&timeout=0", ct)) {
                var root = MiniJson.Parse(await resp.Content.ReadAsStringAsync()) as Dictionary<string, object>;
                var list = MiniJson.Get(root, "result") as List<object>;
                if (list == null || list.Count == 0) return;
                var last = list[list.Count - 1] as Dictionary<string, object>;
                long id = MiniJson.ToLong(MiniJson.Get(last, "update_id"));
                if (id >= _offset) _offset = id + 1;
            }
        }

        private async Task RegisterCommands() {
            var sb = new StringBuilder("[");
            for (int i = 0; i < BotMenu.Length; i++) {
                if (i > 0) sb.Append(',');
                sb.Append("{\"command\":\"").Append(BotMenu[i][0]).Append("\",\"description\":\"")
                  .Append(TgFormat.JsonEscape(BotMenu[i][1])).Append("\"}");
            }
            sb.Append(']');
            var form = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("commands", sb.ToString()) });
            using (var resp = await Client.PostAsync(Api("setMyCommands"), form)) { }
        }

        private async Task ProcessUpdates(string json) {
            var root = MiniJson.Parse(json) as Dictionary<string, object>;
            var list = MiniJson.Get(root, "result") as List<object>;
            if (list == null) return;

            foreach (object item in list) {
                var upd = item as Dictionary<string, object>;
                if (upd == null) continue;

                long id = MiniJson.ToLong(MiniJson.Get(upd, "update_id"));
                if (id >= _offset) _offset = id + 1;

                var cq = MiniJson.Get(upd, "callback_query") as Dictionary<string, object>;
                if (cq != null) {
                    try { await HandleCallback(cq); }
                    catch (Exception ex) { _log("[TelegramControl] Button press failed: " + Scrub(ex.Message)); }
                    continue;
                }

                var msg = MiniJson.Get(upd, "message") as Dictionary<string, object>;
                var chat = MiniJson.Get(msg, "chat") as Dictionary<string, object>;
                string text = MiniJson.Get(msg, "text") as string;
                if (chat == null || text == null) continue;

                // Only ever obey the configured chat. Strangers who find the bot get no reply at all.
                long fromChat = MiniJson.ToLong(MiniJson.Get(chat, "id"));
                if (fromChat != _s.ChatId) {
                    _log("[TelegramControl] Ignored a message from chat id " + fromChat +
                         " because your ChatId in VBuddy.ini is " + _s.ChatId + ".");
                    continue;
                }

                _log("[TelegramControl] Received: " + text);
                string reply;
                try { reply = await HandleCommand(text.Trim()); }
                catch (Exception ex) {
                    _log("[TelegramControl] Command crashed: " + ex);
                    reply = "Something went wrong running that command: " + ex.Message;
                }
                if (!string.IsNullOrEmpty(reply)) await SendAsync(reply);
            }
        }

        private async Task SendAsync(string text, bool keyboard = false) {
            if (string.IsNullOrEmpty(text)) return;
            foreach (string chunk in TgFormat.Chunk(text, 3800, 4)) {
                await SendOne("sendMessage", new[] {
                    new KeyValuePair<string, string>("chat_id", _s.ChatId.ToString(CultureInfo.InvariantCulture)),
                    new KeyValuePair<string, string>("text", chunk)
                }, keyboard);
                keyboard = false;
            }
        }

        private async Task SendLocationAsync(double lat, double lon) {
            await SendOne("sendLocation", new[] {
                new KeyValuePair<string, string>("chat_id", _s.ChatId.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("latitude", lat.ToString("0.000000", CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("longitude", lon.ToString("0.000000", CultureInfo.InvariantCulture))
            }, false);
        }

        private async Task SendOne(string method, KeyValuePair<string, string>[] fields, bool keyboard) {
            try {
                var all = new List<KeyValuePair<string, string>>(fields);
                if (keyboard) all.Add(new KeyValuePair<string, string>("reply_markup", Keyboard));
                using (var resp = await Client.PostAsync(Api(method), new FormUrlEncodedContent(all))) {
                    if (!resp.IsSuccessStatusCode) {
                        var err = MiniJson.Parse(await resp.Content.ReadAsStringAsync()) as Dictionary<string, object>;
                        string why = MiniJson.Get(err, "description") as string;
                        _log("[TelegramControl] " + method + " failed: HTTP " + (int)resp.StatusCode +
                             (why != null ? " - " + Scrub(why) : "") +
                             " (401 = wrong BotToken, 400 'chat not found' = wrong ChatId or you never pressed Start on the bot)");
                    }
                }
            } catch (Exception ex) {
                _log("[TelegramControl] Send error: " + Scrub(ex.Message));
            }
        }

        // Runs vPilot calls on the UI thread and turns SDK exceptions into readable replies.
        private Task<string> OnUi(Func<string> action) {
            var tcs = new TaskCompletionSource<string>();
            Action run = () => {
                try { tcs.SetResult(action()); }
                catch (AlreadyConnectedException) { tcs.SetResult("You're already connected to the network."); }
                catch (NotConnectedException) { tcs.SetResult("You're not connected to the network."); }
                catch (SimNotReadyException) { tcs.SetResult("vPilot isn't connected to the simulator yet."); }
                catch (NotTransmittingException) { tcs.SetResult("No COM radio is set to transmit, or avionics power is off."); }
                catch (Exception ex) {
                    _log("[TelegramControl] Command failed: " + ex);
                    tcs.SetResult("Request failed: " + ex.Message);
                }
            };
            if (_ui != null) {
                _ui.Post(_ => run(), null);
            } else {
                var forms = System.Windows.Forms.Application.OpenForms;
                var main = forms.Count > 0 ? forms[0] : null;
                if (main != null && main.IsHandleCreated) main.BeginInvoke(run);
                else run();
            }
            return tcs.Task;
        }

        // ------------------------------------------------------ commands

        internal async Task<string> HandleCommand(string text) {
            if (!text.StartsWith("/")) return null;

            string[] parts = text.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            string cmd = parts[0].ToLowerInvariant();
            int at = cmd.IndexOf('@');
            if (at > 0) cmd = cmd.Substring(0, at);              // "/status@MyBot" form used in groups

            switch (cmd) {
                case "/start":
                case "/help":
                    await SendAsync(HelpText(), true);
                    return null;

                case "/ping":
                    return "pong";

                case "/zulu":
                    return DateTime.UtcNow.ToString("HHmm'Z' dd MMM yyyy", CultureInfo.InvariantCulture);

                case "/status":
                    return StatusText();

                case "/where":
                    return await CmdWhere(parts);

                case "/atc":
                case "/freq":
                    return await CmdAtc(parts);

                case "/atis":
                    return await CmdAtis(parts);

                case "/metar":
                    return await CmdMetar(parts);

                case "/info": {
                    if (parts.Length < 2) return "Usage: /info CONTROLLER_CALLSIGN   (e.g. /info EGLL_TWR)";
                    string cs = parts[1].ToUpperInvariant();
                    lock (_lock) { _infoRequested[cs] = DateTime.UtcNow; }
                    return await OnUi(() => { _broker.RequestAtis(cs); return "Requested controller info from " + cs + "..."; });
                }

                case "/fp":
                    return CmdFp();

                case "/fileplan":
                    return await CmdFilePlanSheet();

                case "/simbrief":
                case "/ofp":
                    return await CmdSimBrief(parts);

                case "/sim":
                    return CmdSim();

                case "/tune":
                case "/tune2":
                    if (cmd == "/tune2" && parts.Length == 2) parts = new[] { parts[0], parts[1], "2" };
                    return await CmdTune(parts);

                case "/squawk":
                case "/xpdr":
                    return CmdSquawk(parts);

                case "/flightlog":
                case "/log":
                    return CmdFlightLog();

                case "/track":
                    return await CmdTrack();

                case "/settings":
                case "/toggles":
                    await SendSettingsAsync();
                    return null;

                case "/connect": {
                    if (_connected) return "You're already connected to the network.";
                    string cs = parts.Length > 1 ? parts[1].ToUpperInvariant() : _lastCallsign;
                    string tc = parts.Length > 2 ? parts[2].ToUpperInvariant() : _lastType;
                    string sc = parts.Length > 3 ? parts[3].ToUpperInvariant() : _lastSelcal;
                    if (string.IsNullOrEmpty(cs) || string.IsNullOrEmpty(tc))
                        return "Usage: /connect CALLSIGN TYPECODE [SELCAL]\n" +
                               "(or connect once from the vPilot window so it can be remembered)";
                    return await OnUi(() => {
                        _broker.RequestConnect(cs, tc, sc ?? "");
                        return "Connecting as " + cs + " (" + tc + ")...";
                    });
                }

                case "/disconnect": {
                    if (!_connected) return "You're already disconnected from the network.";
                    bool now = parts.Length > 1 && parts[1].ToLowerInvariant() == "now";
                    if (_s.ConfirmDisconnect && !now) {
                        lock (_lock) {
                            _confirmAction = "disconnect";
                            _confirmExpiresUtc = DateTime.UtcNow.AddSeconds(30);
                        }
                        return "Are you sure? Send /confirm within 30 seconds to disconnect from VATSIM.";
                    }
                    return await DoDisconnect();
                }

                case "/confirm": {
                    string action;
                    lock (_lock) {
                        action = DateTime.UtcNow < _confirmExpiresUtc ? _confirmAction : null;
                        _confirmAction = null;
                    }
                    if (action == "disconnect") {
                        if (!_connected) return "You're already disconnected from the network.";
                        return await DoDisconnect();
                    }
                    return "Nothing to confirm (it may have expired).";
                }

                case "/msg":
                case "/message": {
                    string body = Rest(text, 2);
                    if (parts.Length < 3 || body.Length == 0) return "Usage: /msg CALLSIGN message text";
                    return await SendPm(parts[1].ToUpperInvariant(), body);
                }

                case "/reply":
                case "/r": {
                    string body = Rest(text, 1);
                    if (body.Length == 0) return "Usage: /reply message text   (replies to the last private message)";
                    string to = _lastPmFrom;
                    if (string.IsNullOrEmpty(to)) return "No private message has been received yet.";
                    return await SendPm(to, body);
                }

                case "/radio": {
                    string body = Rest(text, 1);
                    if (body.Length == 0) return "Usage: /radio text to send on your current radio frequency";
                    return await SendRadio(body);
                }

                case "/ptt": {
                    string a = parts.Length > 1 ? parts[1].ToLowerInvariant() : "";
                    if (a == "on") {
                        bool ok = false;
                        string r = await OnUi(() => {
                            _broker.SetPtt(true);
                            ok = true;
                            return "PTT pressed. It releases automatically after " + _s.PttMaxSeconds + " s. Send /ptt off to release sooner.";
                        });
                        if (ok) StartPttTimer();
                        return r;
                    }
                    if (a == "off") {
                        CancelPttTimer();
                        return await OnUi(() => { _broker.SetPtt(false); return "PTT released."; });
                    }
                    return "Usage: /ptt on   or   /ptt off";
                }

                case "/ident":
                    return await OnUi(() => { _broker.SquawkIdent(); return "Squawk ident sent."; });

                case "/modec": {
                    string a = parts.Length > 1 ? parts[1].ToLowerInvariant() : "";
                    if (a != "on" && a != "off") return "Usage: /modec on   or   /modec off";
                    bool on = a == "on";
                    return await OnUi(() => { _broker.SetModeC(on); _modeC = on ? "on" : "off"; return "Mode C " + a + "."; });
                }

                case "/listen": {
                    string a = parts.Length > 1 ? parts[1].ToLowerInvariant() : "";
                    if (a == "on") _listen = true;
                    else if (a == "off") _listen = false;
                    else return "Radio listening is " + (_listen ? "ON" : "OFF") + ". Use /listen on or /listen off.";
                    return "Radio listening is now " + (_listen ? "ON: every text radio message on your frequencies is forwarded." : "OFF.");
                }

                case "/watch": {
                    if (parts.Length < 2) return WatchText();
                    var added = new List<string>();
                    for (int i = 1; i < parts.Length; i++) {
                        string w = parts[i].ToUpperInvariant();
                        if (!Regex.IsMatch(w, "^[A-Z0-9_]{2,12}$")) continue;
                        lock (_lock) { _watch.Add(w); }
                        added.Add(w);
                    }
                    SaveWatch();
                    return added.Count == 0 ? "Usage: /watch EGLL KLAX LON_ ..." : "Now watching: " + string.Join(", ", added) + "\n" + WatchText();
                }

                case "/unwatch": {
                    if (parts.Length < 2) return "Usage: /unwatch PREFIX   (or /unwatch all)";
                    lock (_lock) {
                        for (int i = 1; i < parts.Length; i++) {
                            if (parts[i].ToLowerInvariant() == "all") _watch.Clear();
                            else _watch.Remove(parts[i].ToUpperInvariant());
                        }
                    }
                    SaveWatch();
                    return WatchText();
                }

                case "/watchlist":
                    return WatchText();

                case "/mute": {
                    int minutes = 60;
                    if (parts.Length > 1) int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out minutes);
                    if (minutes < 1) minutes = 60;
                    if (minutes > 24 * 60) minutes = 24 * 60;
                    _mutedUntilUtc = DateTime.UtcNow.AddMinutes(minutes);
                    return "Automatic alerts (broadcasts, watched controllers, radio listening) muted for " + minutes + " min. /unmute to resume.";
                }

                case "/unmute":
                    _mutedUntilUtc = DateTime.MinValue;
                    return "Automatic alerts resumed.";

                case "/history": {
                    int n = 10;
                    if (parts.Length > 1) int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out n);
                    if (n < 1) n = 10;
                    if (n > 30) n = 30;
                    lock (_lock) {
                        if (_history.Count == 0) return "No messages yet this session.";
                        int skip = Math.Max(0, _history.Count - n);
                        return string.Join("\n", _history.GetRange(skip, _history.Count - skip));
                    }
                }

                default: {
                    string preset;
                    if (Presets.TryGetValue(cmd, out preset)) return await SendRadio(preset);
                    return "Unknown command. Try /help";
                }
            }
        }

        private Task<string> DoDisconnect() {
            return OnUi(() => { _broker.RequestDisconnect(); return "Disconnect requested."; });
        }

        private Task<string> SendPm(string to, string body) {
            return OnUi(() => {
                _broker.SendPrivateMessage(to, body);
                Remember("PM to " + to + ": " + body);
                return "Sent to " + to + ".";
            });
        }

        private Task<string> SendRadio(string message) {
            return OnUi(() => {
                _broker.SendRadioMessage(message);
                Remember("YOU (radio): " + message);
                return "Sent on radio: " + message;
            });
        }

        private void StartPttTimer() {
            var cts = new CancellationTokenSource();
            CancellationTokenSource old;
            lock (_lock) { old = _pttCts; _pttCts = cts; }
            if (old != null) old.Cancel();

            int secs = _s.PttMaxSeconds;
            Task.Run(async () => {
                try { await Task.Delay(secs * 1000, cts.Token); }
                catch (OperationCanceledException) { return; }
                string r = await OnUi(() => {
                    _broker.SetPtt(false);
                    return "PTT released automatically after " + secs + " s (safety limit).";
                });
                await SendAsync(r);
            });
        }

        private void CancelPttTimer() {
            CancellationTokenSource cts;
            lock (_lock) { cts = _pttCts; _pttCts = null; }
            if (cts != null) cts.Cancel();
        }

        private string HelpText() {
            return "VBuddy Telegram control\n\n" +
                   "INFO\n" +
                   "/where [CALLSIGN]  position, speed, heading\n" +
                   "/status  connection state\n" +
                   "/atc [ICAO...]  online ATC + frequencies\n" +
                   "/freq CALLSIGN  controller frequency\n" +
                   "/atis [ICAO]  ATIS text\n" +
                   "/metar [ICAO...]  weather\n" +
                   "/fp  your SimBrief plan (or /where for the filed one)\n" +
                   "/history [n]  recent messages\n" +
                   "/zulu  current Zulu time\n\n" +
                   "NETWORK\n" +
                   "/connect [CALLSIGN TYPE [SELCAL]]\n" +
                   "/disconnect  (asks for /confirm)\n" +
                   "\n" +
                   "COMMUNICATE\n" +
                   "/msg CALLSIGN text   /reply text\n" +
                   "/radio text  (on your transmit frequency)\n" +
                   "/wilco /roger /standby /unable\n" +
                   "/info CONTROLLER  controller info text\n\n" +
                   "AIRCRAFT RADIO\n" +
                   "/ptt on|off   /ident   /modec on|off\n\n" +
                   "ALERTS\n" +
                   "/watch PREFIX...  alert when ATC comes online\n" +
                   "/unwatch PREFIX|all   /watchlist\n" +
                   "/listen on|off  forward all radio text\n" +
                   "/mute [minutes]   /unmute\n" +
                   "/settings  toggle alerts with buttons\n\n" +
                   "SIMBRIEF AND SIM\n" +
                   "/simbrief [name or ID]  load your latest OFP\n" +
                   "/fileplan  OFP values to enter in vPilot\n" +
                   "/sim  live aircraft data (needs SimConnect)\n\n" +
                   "SIMULATOR CONTROL (needs SimConnect)\n" +
                   "/tune 118.300 | /tune EGLL_TWR [2]  set COM1 (or COM2)\n" +
                   "/squawk 1234  set the transponder code\n" +
                   "/flightlog  current or last flight   /track  GPX track file\n\n" +
                   "Not possible with vPilot: tuning COM frequencies or setting the squawk code.";
        }

        private string StatusText() {
            var sb = new StringBuilder();
            if (_connected) {
                sb.Append("Connected to VATSIM as ").Append(_callsign);
                if (_connectedType.Length > 0) sb.Append(" (").Append(_connectedType).Append(")");
                sb.Append("\nOnline for ").Append(TgFormat.Duration(DateTime.UtcNow - _connectedAtUtc));
            } else {
                sb.Append("Not connected.");
                if (_lastCallsign.Length > 0) sb.Append(" Last identity: ").Append(_lastCallsign).Append(" / ").Append(_lastType);
            }
            sb.Append("\nMode C (as set from Telegram): ").Append(_modeC);
            sb.Append("\nRadio listening: ").Append(_listen ? "on" : "off");
            sb.Append(ExtrasStatus());
            if (IsMuted) sb.Append("\nAlerts muted for ").Append(TgFormat.Duration(_mutedUntilUtc - DateTime.UtcNow)).Append(" more");
            lock (_lock) { sb.Append("\nWatching ").Append(_watch.Count).Append(" controller prefix(es)"); }
            sb.Append("\nVBuddy v").Append(Main.Version);
            return sb.ToString();
        }

        private string WatchText() {
            lock (_lock) {
                if (_watch.Count == 0) return "Not watching any controllers. Example: /watch EGLL KLAX";
                var list = new List<string>(_watch);
                list.Sort(StringComparer.Ordinal);
                return "Watching: " + string.Join(", ", list);
            }
        }

        // ------------------------------------------------------ data-feed commands

        private async Task<string> CmdWhere(string[] parts) {
            if (parts.Length == 1) {
                string simText; double slat, slon;
                if (TrySimWhere(out simText, out slat, out slon)) {
                    await SendAsync(simText);
                    await SendLocationAsync(slat, slon);
                    return null;
                }
            }
            string cs = parts.Length > 1 ? parts[1].ToUpperInvariant() : _callsign;
            if (string.IsNullOrEmpty(cs))
                return "I don't know your callsign because you aren't connected.\nUse /where CALLSIGN to look up any pilot.";

            Dictionary<string, object> feed;
            try { feed = await TgFeed.GetAsync(); }
            catch (Exception ex) { return "Couldn't reach the VATSIM data feed: " + Scrub(ex.Message); }

            var p = TgFeed.FindPilot(feed, cs);
            if (p == null)
                return cs + " is not in the VATSIM data feed (yet).\nThe feed updates about every 15 seconds, so a fresh connection can take up to a minute to appear.";

            double lat, lon;
            string info = TgFormat.Where(p, DateTime.UtcNow, out lat, out lon);
            await SendAsync(info);
            await SendLocationAsync(lat, lon);
            return null;
        }

        private async Task<string> CmdAtc(string[] parts) {
            var prefixes = new List<string>();
            for (int i = 1; i < parts.Length; i++) prefixes.Add(parts[i].ToUpperInvariant());

            Dictionary<string, object> feed;
            try { feed = await TgFeed.GetAsync(); }
            catch (Exception ex) { return "Couldn't reach the VATSIM data feed: " + Scrub(ex.Message); }

            if (prefixes.Count == 0) {
                prefixes.AddRange(OwnAirports(feed));
                if (prefixes.Count == 0)
                    return "Usage: /atc ICAO-or-prefix   (e.g. /atc EGLL   or   /atc KLAX KSFO)\n" +
                           "Tip: once you're connected with a flight plan, plain /atc shows your departure and arrival airports.";
            }

            List<string> lines = TgFormat.ControllerLines(feed, prefixes);
            if (lines.Count == 0) return "No ATC or ATIS online matching: " + string.Join(", ", prefixes);
            return "Online now (VATSIM data, about 15 s delayed):\n" + string.Join("\n", lines);
        }

        private async Task<string> CmdAtis(string[] parts) {
            var prefixes = new List<string>();
            for (int i = 1; i < parts.Length; i++) prefixes.Add(parts[i].ToUpperInvariant());

            Dictionary<string, object> feed;
            try { feed = await TgFeed.GetAsync(); }
            catch (Exception ex) { return "Couldn't reach the VATSIM data feed: " + Scrub(ex.Message); }

            if (prefixes.Count == 0) {
                prefixes.AddRange(OwnAirports(feed));
                if (prefixes.Count == 0) return "Usage: /atis ICAO   (e.g. /atis EGLL)";
            }

            string text = TgFormat.AtisText(feed, prefixes);
            return text.Length == 0 ? "No ATIS or controller info online for: " + string.Join(", ", prefixes) : text;
        }

        private async Task<string> CmdMetar(string[] parts) {
            var stations = new List<string>();
            for (int i = 1; i < parts.Length && stations.Count < 4; i++) stations.Add(parts[i].ToUpperInvariant());

            if (stations.Count == 0) {
                try {
                    var feed = await TgFeed.GetAsync();
                    stations.AddRange(OwnAirports(feed));
                } catch { }
                if (stations.Count == 0) return "Usage: /metar ICAO   (e.g. /metar EGLL)";
            }

            var sb = new StringBuilder();
            foreach (string st in stations) {
                if (!TgFormat.IsStation(st)) { sb.AppendLine(st + ": not a valid airport code"); continue; }
                try {
                    string metar = (await TgFeed.GetTextAsync("https://metar.vatsim.net/" + Uri.EscapeDataString(st))).Trim();
                    sb.AppendLine(metar.Length == 0 ? st + ": no METAR available" : metar);
                } catch (Exception ex) {
                    sb.AppendLine(st + ": couldn't fetch METAR (" + Scrub(ex.Message) + ")");
                }
            }
            return sb.ToString().TrimEnd();
        }

        private List<string> OwnAirports(Dictionary<string, object> feed) {
            var result = new List<string>();
            if (string.IsNullOrEmpty(_callsign)) return result;
            var p = TgFeed.FindPilot(feed, _callsign);
            var fp = MiniJson.Get(p, "flight_plan") as Dictionary<string, object>;
            if (fp == null) return result;
            string dep = MiniJson.Str(MiniJson.Get(fp, "departure")).ToUpperInvariant();
            string arr = MiniJson.Str(MiniJson.Get(fp, "arrival")).ToUpperInvariant();
            if (TgFormat.IsStation(dep)) result.Add(dep);
            if (TgFormat.IsStation(arr) && arr != dep) result.Add(arr);
            return result;
        }

        private async Task<string> CmdFilePlan(string text) {
            const string usage =
                "Usage:\n" +
                "/fileplan DEP ARR ALT CRZALT SPEED DEPTIME ETE FUEL SUFFIX [heavy] [vfr] | route | remarks\n\n" +
                "Example:\n" +
                "/fileplan KLAX RKSI RKSS FL350 480 1200 1230 1500 L heavy | DCT SYRAH J1 ... | PBN/A1 RMK/TCAS\n\n" +
                "ALT = alternate airport (use - for none). CRZALT is FL350 or 35000. SPEED in knots. " +
                "DEPTIME is zulu HHMM. ETE and FUEL are hours+minutes as HHMM (1230 = 12 h 30 min). " +
                "SUFFIX is your equipment letter (L, W, G ...). Route and remarks are optional.";

            string body = Rest(text, 1);
            if (body.Length == 0) return usage;

            string[] segs = body.Split('|');
            string[] t = segs[0].Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (t.Length < 9) return usage;

            string dep = t[0].ToUpperInvariant();
            string arr = t[1].ToUpperInvariant();
            string alt = t[2].ToUpperInvariant();
            if (alt == "-" || alt == "NONE") alt = "";
            int cruiseAlt = TgFormat.ParseAltitude(t[3]);
            int speed;
            int depTime, eteH, eteM, fuelH, fuelM;
            string suffix = t[8].ToUpperInvariant();

            if (!TgFormat.IsStation(dep) || !TgFormat.IsStation(arr) || (alt.Length > 0 && !TgFormat.IsStation(alt)))
                return "Airport codes must be 3-4 letters/digits.\n\n" + usage;
            if (cruiseAlt <= 0) return "Couldn't read the cruise altitude '" + t[3] + "'. Use FL350 or 35000.\n\n" + usage;
            if (!int.TryParse(t[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out speed) || speed < 50 || speed > 1000)
                return "Cruise speed must be knots between 50 and 1000.\n\n" + usage;
            int dh, dm;
            if (!TgFormat.ParseHHMM(t[5], out dh, out dm) || dh > 23) return "Departure time must be zulu HHMM, e.g. 1200.\n\n" + usage;
            depTime = dh * 100 + dm;
            if (!TgFormat.ParseHHMM(t[6], out eteH, out eteM)) return "ETE must be HHMM, e.g. 1230 for 12 h 30 min.\n\n" + usage;
            if (!TgFormat.ParseHHMM(t[7], out fuelH, out fuelM)) return "FUEL must be HHMM, e.g. 1500 for 15 h 00 min.\n\n" + usage;
            if (!Regex.IsMatch(suffix, "^[A-Z]$")) return "SUFFIX must be a single letter.\n\n" + usage;

            bool heavy = false, vfr = false;
            for (int i = 9; i < t.Length; i++) {
                string f = t[i].ToLowerInvariant();
                if (f == "heavy") heavy = true;
                else if (f == "vfr") vfr = true;
            }

            string route = segs.Length > 1 ? segs[1].Trim() : "";
            if (route.Length == 0) route = "DCT";
            string remarks = segs.Length > 2 ? segs[2].Trim() : "";

            await Task.CompletedTask;
            return "Filing a flight plan isn't available in this vPilot plugin SDK.";
        }

        // ------------------------------------------------------ helpers

        // Text after the first N whitespace-separated tokens.
        internal static string Rest(string text, int tokens) {
            int i = 0;
            for (int t = 0; t < tokens; t++) {
                while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
                while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
            }
            return text.Substring(i).Trim();
        }

        private void Remember(string line) {
            lock (_lock) {
                _history.Add(DateTime.UtcNow.ToString("HH:mm'Z' ", CultureInfo.InvariantCulture) + line);
                if (_history.Count > 50) _history.RemoveAt(0);
            }
        }

        private void SaveIdentity() {
            try { File.WriteAllLines(_identityPath, new[] { _lastCallsign, _lastType, _lastSelcal }); }
            catch { }
        }

        private void LoadIdentity() {
            try {
                if (!File.Exists(_identityPath)) return;
                string[] l = File.ReadAllLines(_identityPath);
                if (l.Length > 0) _lastCallsign = l[0];
                if (l.Length > 1) _lastType = l[1];
                if (l.Length > 2) _lastSelcal = l[2];
            } catch { }
        }

        private void LoadWatch() {
            try {
                var items = new List<string>();
                if (!string.IsNullOrEmpty(_s.Watch)) items.AddRange(_s.Watch.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries));
                if (File.Exists(_watchPath)) items.AddRange(File.ReadAllLines(_watchPath));
                lock (_lock) {
                    foreach (string raw in items) {
                        string w = raw.Trim().ToUpperInvariant();
                        if (Regex.IsMatch(w, "^[A-Z0-9_]{2,12}$")) _watch.Add(w);
                    }
                }
            } catch { }
        }

        private void SaveWatch() {
            try {
                string[] list;
                lock (_lock) { list = new string[_watch.Count]; _watch.CopyTo(list); }
                File.WriteAllLines(_watchPath, list);
            } catch { }
        }
    }

    // ===================================================================== VATSIM data feed

    internal static class TgFeed {

        private static readonly HttpClient Web = CreateClient();
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);
        private static Dictionary<string, object> _cache;
        private static DateTime _cacheUtc = DateTime.MinValue;

        private static HttpClient CreateClient() {
            var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate };
            var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("VBuddy-TelegramControl/2.0");
            return c;
        }

        // The feed only changes every ~15 s, so cache it for that long.
        public static async Task<Dictionary<string, object>> GetAsync() {
            await Gate.WaitAsync();
            try {
                if (_cache != null && (DateTime.UtcNow - _cacheUtc).TotalSeconds < 15) return _cache;
                string json = await Web.GetStringAsync("https://data.vatsim.net/v3/vatsim-data.json");
                var root = MiniJson.Parse(json) as Dictionary<string, object>;
                if (root == null) throw new InvalidOperationException("Unexpected data from VATSIM.");
                _cache = root;
                _cacheUtc = DateTime.UtcNow;
                return root;
            } finally {
                Gate.Release();
            }
        }

        public static Task<string> GetTextAsync(string url) {
            return Web.GetStringAsync(url);
        }

        public static Dictionary<string, object> FindPilot(Dictionary<string, object> feed, string callsign) {
            var pilots = MiniJson.Get(feed, "pilots") as List<object>;
            if (pilots == null || string.IsNullOrEmpty(callsign)) return null;
            foreach (object o in pilots) {
                var d = o as Dictionary<string, object>;
                if (d != null && string.Equals(MiniJson.Str(MiniJson.Get(d, "callsign")), callsign, StringComparison.OrdinalIgnoreCase))
                    return d;
            }
            return null;
        }
    }

    // ===================================================================== formatting helpers

    internal static class TgFormat {

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static readonly string[] Compass =
            { "N", "NNE", "NE", "ENE", "E", "ESE", "SE", "SSE", "S", "SSW", "SW", "WSW", "W", "WNW", "NW", "NNW" };

        // vPilot radio frequencies drop the leading 1 and the decimal point: 123.725 -> 23725.
        public static string Freq(int v) {
            string s = v.ToString(Inv).PadLeft(5, '0');
            if (s.Length == 5) s = "1" + s;
            return s.Length == 6 ? s.Substring(0, 3) + "." + s.Substring(3) : s;
        }

        public static string Freqs(int[] v) {
            if (v == null || v.Length == 0) return "";
            var parts = new string[v.Length];
            for (int i = 0; i < v.Length; i++) parts[i] = Freq(v[i]);
            return string.Join("/", parts);
        }

        public static string LatLon(double lat, double lon) {
            return Math.Abs(lat).ToString("0.0000", Inv) + "\u00B0" + (lat >= 0 ? "N" : "S") + "  " +
                   Math.Abs(lon).ToString("0.0000", Inv) + "\u00B0" + (lon >= 0 ? "E" : "W");
        }

        public static string Feet(double ft) {
            return ((long)Math.Round(ft)).ToString("N0", Inv) + " ft";
        }

        public static string Heading(double h) {
            int deg = ((int)Math.Round(h)) % 360;
            if (deg < 0) deg += 360;
            if (deg == 0) deg = 360;
            int idx = ((int)Math.Round((deg % 360) / 22.5)) % 16;
            return deg.ToString("000", Inv) + "\u00B0 (" + Compass[idx] + ")";
        }

        public static string Duration(TimeSpan ts) {
            if (ts < TimeSpan.Zero) ts = TimeSpan.Zero;
            if (ts.TotalHours >= 1) return (int)ts.TotalHours + " h " + ts.Minutes + " min";
            return Math.Max(0, ts.Minutes) + " min";
        }

        public static string JsonEscape(string s) {
            var sb = new StringBuilder();
            foreach (char c in s) {
                switch (c) {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", Inv));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        // Split long text on line breaks into Telegram-sized pieces.
        public static List<string> Chunk(string text, int max, int maxChunks) {
            var chunks = new List<string>();
            var cur = new StringBuilder();
            foreach (string line in text.Split('\n')) {
                string l = line.Length > max ? line.Substring(0, max) : line;
                if (cur.Length + l.Length + 1 > max && cur.Length > 0) {
                    chunks.Add(cur.ToString());
                    cur.Clear();
                }
                if (cur.Length > 0) cur.Append('\n');
                cur.Append(l);
            }
            if (cur.Length > 0) chunks.Add(cur.ToString());
            if (chunks.Count > maxChunks) {
                chunks = chunks.GetRange(0, maxChunks);
                chunks[maxChunks - 1] += "\n...(truncated)";
            }
            return chunks;
        }

        public static bool IsStation(string s) {
            return !string.IsNullOrEmpty(s) && Regex.IsMatch(s, "^[A-Z0-9]{3,4}$");
        }

        // True if a callsign belongs to the given prefix. A 4-letter US "KLAX" also matches "LAX_TWR".
        public static bool MatchesStation(string callsign, string prefix) {
            if (string.IsNullOrEmpty(callsign) || string.IsNullOrEmpty(prefix)) return false;
            if (callsign.StartsWith(prefix, StringComparison.Ordinal)) return true;
            if (prefix.Length == 4 && prefix[0] == 'K' && callsign.StartsWith(prefix.Substring(1) + "_", StringComparison.Ordinal)) return true;
            return false;
        }

        public static bool MatchesAny(string callsign, IList<string> prefixes) {
            foreach (string p in prefixes)
                if (MatchesStation(callsign, p)) return true;
            return false;
        }

        // FL350 / F350 / 35000 -> 35000. Returns -1 if unreadable.
        public static int ParseAltitude(string s) {
            if (string.IsNullOrEmpty(s)) return -1;
            s = s.Trim().ToUpperInvariant();
            int n;
            if (s.StartsWith("FL") || s.StartsWith("F")) {
                string digits = s.StartsWith("FL") ? s.Substring(2) : s.Substring(1);
                if (digits.Length >= 2 && digits.Length <= 3 && int.TryParse(digits, NumberStyles.Integer, Inv, out n)) return n * 100;
                return -1;
            }
            if (int.TryParse(s, NumberStyles.Integer, Inv, out n) && n >= 100 && n <= 99999) return n;
            return -1;
        }

        // "1230" -> 12 h 30 min. Minutes must be under 60.
        public static bool ParseHHMM(string s, out int h, out int m) {
            h = 0; m = 0;
            if (string.IsNullOrEmpty(s) || s.Length < 3 || s.Length > 4) return false;
            int v;
            if (!int.TryParse(s, NumberStyles.None, Inv, out v)) return false;
            h = v / 100;
            m = v % 100;
            return m < 60;
        }

        public static string FacilityName(int f) {
            switch (f) {
                case 1: return "Flight Service";
                case 2: return "Delivery";
                case 3: return "Ground";
                case 4: return "Tower";
                case 5: return "Approach/Departure";
                case 6: return "Center";
                default: return "ATC";
            }
        }

        public static string Where(Dictionary<string, object> p, DateTime nowUtc, out double lat, out double lon) {
            string cs = MiniJson.Str(MiniJson.Get(p, "callsign"));
            lat = MiniJson.ToDouble(MiniJson.Get(p, "latitude"));
            lon = MiniJson.ToDouble(MiniJson.Get(p, "longitude"));
            double alt = MiniJson.ToDouble(MiniJson.Get(p, "altitude"));
            double gs = MiniJson.ToDouble(MiniJson.Get(p, "groundspeed"));
            double hdg = MiniJson.ToDouble(MiniJson.Get(p, "heading"));
            string squawk = MiniJson.Str(MiniJson.Get(p, "transponder"));
            double qnhIn = MiniJson.ToDouble(MiniJson.Get(p, "qnh_i_hg"));
            double qnhMb = MiniJson.ToDouble(MiniJson.Get(p, "qnh_mb"));
            var fp = MiniJson.Get(p, "flight_plan") as Dictionary<string, object>;

            var sb = new StringBuilder();
            sb.Append("\u2708 ").Append(cs);
            if (fp != null) {
                string ac = MiniJson.Str(MiniJson.Get(fp, "aircraft_short"));
                if (ac.Length == 0) ac = MiniJson.Str(MiniJson.Get(fp, "aircraft_faa"));
                if (ac.Length > 0) sb.Append(" (").Append(ac).Append(")");
            }
            sb.Append("\n\uD83D\uDCCD ").Append(LatLon(lat, lon));
            sb.Append("\nAltitude: ").Append(Feet(alt));
            sb.Append("\nGround speed: ").Append(((int)Math.Round(gs)).ToString(Inv)).Append(" kt");
            sb.Append("\nHeading: ").Append(Heading(hdg));
            if (squawk.Length > 0) sb.Append("\nSquawk: ").Append(squawk);
            if (qnhMb > 0)
                sb.Append("\nQNH: ").Append(qnhIn.ToString("0.00", Inv)).Append(" inHg / ").Append(((int)Math.Round(qnhMb)).ToString(Inv)).Append(" hPa");

            if (fp != null) {
                string dep = MiniJson.Str(MiniJson.Get(fp, "departure"));
                string arr = MiniJson.Str(MiniJson.Get(fp, "arrival"));
                string altn = MiniJson.Str(MiniJson.Get(fp, "alternate"));
                string cruise = MiniJson.Str(MiniJson.Get(fp, "altitude"));
                string tas = MiniJson.Str(MiniJson.Get(fp, "cruise_tas"));
                if (dep.Length > 0 || arr.Length > 0) {
                    sb.Append("\nPlan: ").Append(dep).Append(" \u2192 ").Append(arr);
                    if (altn.Length > 0) sb.Append(" (alt ").Append(altn).Append(")");
                }
                if (cruise.Length > 0 || tas.Length > 0)
                    sb.Append("\nFiled: ").Append(cruise.Length > 0 ? cruise + " ft" : "?").Append(tas.Length > 0 ? ", " + tas + " kt TAS" : "");
            }

            DateTime logon, updated;
            if (TryParseTime(MiniJson.Str(MiniJson.Get(p, "logon_time")), out logon))
                sb.Append("\nOnline for ").Append(Duration(nowUtc - logon));
            if (TryParseTime(MiniJson.Str(MiniJson.Get(p, "last_updated")), out updated))
                sb.Append("\nPosition is about ").Append(Math.Max(0, (int)(nowUtc - updated).TotalSeconds)).Append(" s old");

            sb.Append("\nhttps://www.google.com/maps?q=")
              .Append(lat.ToString("0.00000", Inv)).Append(",").Append(lon.ToString("0.00000", Inv));
            return sb.ToString();
        }

        private static bool TryParseTime(string s, out DateTime utc) {
            return DateTime.TryParse(s, Inv, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out utc);
        }

        public static string FlightPlan(string cs, string dep, string arr, string alt, int cruiseAlt, int cruiseSpd,
                                        string route, string remarks, string equip, int depTime,
                                        int eteH, int eteM, int fuelH, int fuelM, bool vfr) {
            var sb = new StringBuilder();
            sb.Append("\uD83D\uDCCB Flight plan for ").Append(cs).Append(vfr ? " (VFR)" : " (IFR)");
            sb.Append("\n").Append(dep).Append(" \u2192 ").Append(arr);
            if (!string.IsNullOrEmpty(alt)) sb.Append("   alternate ").Append(alt);
            sb.Append("\nCruise: ").Append(cruiseAlt.ToString("N0", Inv)).Append(" ft at ").Append(cruiseSpd).Append(" kt");
            sb.Append("\nDeparture: ").Append(depTime.ToString("0000", Inv)).Append("Z");
            sb.Append("   ETE: ").Append(eteH).Append(" h ").Append(eteM).Append(" min");
            sb.Append("   Fuel: ").Append(fuelH).Append(" h ").Append(fuelM).Append(" min");
            if (!string.IsNullOrEmpty(equip)) sb.Append("\nEquipment: ").Append(equip);
            sb.Append("\nRoute: ").Append(string.IsNullOrEmpty(route) ? "(none)" : route);
            if (!string.IsNullOrEmpty(remarks)) sb.Append("\nRemarks: ").Append(remarks);
            return sb.ToString();
        }

        public static List<string> ControllerLines(Dictionary<string, object> feed, IList<string> prefixes) {
            var rows = new List<KeyValuePair<string, string>>();
            foreach (string listName in new[] { "controllers", "atis" }) {
                var list = MiniJson.Get(feed, listName) as List<object>;
                if (list == null) continue;
                foreach (object o in list) {
                    var d = o as Dictionary<string, object>;
                    if (d == null) continue;
                    string cs = MiniJson.Str(MiniJson.Get(d, "callsign")).ToUpperInvariant();
                    if (cs.Length == 0 || !MatchesAny(cs, prefixes)) continue;
                    string freq = MiniJson.Str(MiniJson.Get(d, "frequency"));
                    if (freq.StartsWith("199.998", StringComparison.Ordinal)) continue;   // observers

                    string label;
                    if (listName == "atis") {
                        string code = MiniJson.Str(MiniJson.Get(d, "atis_code"));
                        label = code.Length > 0 ? "ATIS info " + code : "ATIS";
                    } else {
                        label = FacilityName((int)MiniJson.ToLong(MiniJson.Get(d, "facility")));
                    }
                    string who = MiniJson.Str(MiniJson.Get(d, "name"));
                    rows.Add(new KeyValuePair<string, string>(cs,
                        cs + "   " + freq + "   " + label + (who.Length > 0 && listName != "atis" ? "  (" + who + ")" : "")));
                }
            }
            rows.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            var lines = new List<string>();
            foreach (var r in rows) {
                if (lines.Count >= 30) { lines.Add("...and more. Narrow it down, e.g. /atc EGLL"); break; }
                lines.Add(r.Value);
            }
            return lines;
        }

        public static string AtisText(Dictionary<string, object> feed, IList<string> prefixes) {
            var sb = new StringBuilder();
            AppendAtis(sb, MiniJson.Get(feed, "atis") as List<object>, prefixes, true);
            if (sb.Length == 0) AppendAtis(sb, MiniJson.Get(feed, "controllers") as List<object>, prefixes, false);
            return sb.ToString().TrimEnd();
        }

        private static void AppendAtis(StringBuilder sb, List<object> list, IList<string> prefixes, bool isAtis) {
            if (list == null) return;
            foreach (object o in list) {
                var d = o as Dictionary<string, object>;
                if (d == null) continue;
                string cs = MiniJson.Str(MiniJson.Get(d, "callsign")).ToUpperInvariant();
                if (cs.Length == 0 || !MatchesAny(cs, prefixes)) continue;
                var lines = MiniJson.Get(d, "text_atis") as List<object>;
                if (lines == null || lines.Count == 0) continue;

                var text = new StringBuilder();
                foreach (object l in lines) text.Append(MiniJson.Str(l)).Append(' ');
                string t = text.ToString().Trim();
                if (t.Length > 1500) t = t.Substring(0, 1500) + "...";

                string code = MiniJson.Str(MiniJson.Get(d, "atis_code"));
                sb.Append(isAtis ? "\uD83C\uDF24 " : "\u2139 ").Append(cs).Append("  ")
                  .Append(MiniJson.Str(MiniJson.Get(d, "frequency")))
                  .Append(code.Length > 0 ? "  info " + code : "").Append("\n").Append(t).Append("\n\n");
            }
        }
    }

    // ===================================================================== JSON reader

    // Minimal JSON reader: objects -> Dictionary, arrays -> List, numbers -> long/double,
    // strings, bool, null. Enough for Telegram and VATSIM responses; avoids extra references.
    internal static class MiniJson {

        public static object Parse(string s) {
            int i = 0;
            return ParseValue(s, ref i);
        }

        public static object Get(Dictionary<string, object> d, string key) {
            object v;
            return d != null && d.TryGetValue(key, out v) ? v : null;
        }

        public static long ToLong(object o) {
            if (o is long) return (long)o;
            if (o is double) return (long)(double)o;
            return 0;
        }

        public static double ToDouble(object o) {
            if (o is long) return (long)o;
            if (o is double) return (double)o;
            double d;
            if (o is string && double.TryParse((string)o, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            return 0;
        }

        // Strings stay strings; numbers/bools are rendered as text; null becomes "".
        public static string Str(object o) {
            if (o == null) return "";
            if (o is string) return (string)o;
            if (o is long) return ((long)o).ToString(CultureInfo.InvariantCulture);
            if (o is double) return ((double)o).ToString(CultureInfo.InvariantCulture);
            if (o is bool) return (bool)o ? "true" : "false";
            return "";
        }

        private static void Ws(string s, ref int i) {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }

        private static object ParseValue(string s, ref int i) {
            Ws(s, ref i);
            char c = s[i];

            if (c == '{') {
                i++;
                var d = new Dictionary<string, object>();
                Ws(s, ref i);
                if (s[i] == '}') { i++; return d; }
                while (true) {
                    Ws(s, ref i);
                    string key = ParseString(s, ref i);
                    Ws(s, ref i);
                    i++;                                   // ':'
                    d[key] = ParseValue(s, ref i);
                    Ws(s, ref i);
                    if (s[i] == ',') { i++; continue; }
                    i++;                                   // '}'
                    return d;
                }
            }
            if (c == '[') {
                i++;
                var list = new List<object>();
                Ws(s, ref i);
                if (s[i] == ']') { i++; return list; }
                while (true) {
                    list.Add(ParseValue(s, ref i));
                    Ws(s, ref i);
                    if (s[i] == ',') { i++; continue; }
                    i++;                                   // ']'
                    return list;
                }
            }
            if (c == '"') return ParseString(s, ref i);
            if (string.CompareOrdinal(s, i, "true", 0, 4) == 0) { i += 4; return true; }
            if (string.CompareOrdinal(s, i, "false", 0, 5) == 0) { i += 5; return false; }
            if (string.CompareOrdinal(s, i, "null", 0, 4) == 0) { i += 4; return null; }

            int start = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            if (i == start) throw new FormatException("Unexpected character '" + c + "' in JSON at position " + i);
            string num = s.Substring(start, i - start);
            long l;
            if (long.TryParse(num, NumberStyles.Integer, CultureInfo.InvariantCulture, out l)) return l;
            double dbl;
            double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out dbl);
            return dbl;
        }

        private static string ParseString(string s, ref int i) {
            var sb = new StringBuilder();
            i++;                                           // opening quote
            while (i < s.Length && s[i] != '"') {
                char c = s[i++];
                if (c != '\\') { sb.Append(c); continue; }
                char e = s[i++];
                switch (e) {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: sb.Append(e); break;          // \" \\ \/
                }
            }
            i++;                                           // closing quote
            return sb.ToString();
        }
    }
}
