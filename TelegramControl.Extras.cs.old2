// TelegramControl.Extras.cs  -  SimBrief OFP, in-chat toggle buttons, optional SimConnect telemetry.
//
// Add this file and SimLink.cs next to Main.TelegramControl.cs. No extra package references needed.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace VBuddy {

    // ===================================================================== extra settings

    internal sealed partial class TelegramControlSettings {
        // [SimConnect]
        public bool SimEnabled;                // master switch for live telemetry
        public bool TodAlert = true;           // Top of Descent push
        public bool TouchdownReport = true;    // landing rate push
        public int TodLeadNm;                  // warn this many nm before TOD (0 = at TOD)
        public string SimDllPath;              // optional folder or full path of SimConnect.dll
        // [SimBrief]
        public string SbUserName;
        public string SbUserId;
        public bool SbAutoFetch = true;        // refresh the OFP when you connect

        private static string R(IniFile ini, string section, string key) {
            return Clean(ini.Read(key, section, null));
        }

        private static bool RB(IniFile ini, string section, string key, bool def) {
            string v = R(ini, section, key);
            bool b;
            return v != null && bool.TryParse(v, out b) ? b : def;
        }

        internal void LoadExtras(IniFile ini) {
            SimEnabled = RB(ini, "SimConnect", "Enabled", false);
            TodAlert = RB(ini, "SimConnect", "TodAlert", true);
            TouchdownReport = RB(ini, "SimConnect", "TouchdownReport", true);
            SimDllPath = R(ini, "SimConnect", "DllPath");
            int lead;
            string lv = R(ini, "SimConnect", "TodLeadNm");
            if (lv != null && int.TryParse(lv, NumberStyles.Integer, CultureInfo.InvariantCulture, out lead))
                TodLeadNm = Math.Max(0, Math.Min(100, lead));

            SbUserName = R(ini, "SimBrief", "UserName");
            SbUserId = R(ini, "SimBrief", "UserId");
            SbAutoFetch = RB(ini, "SimBrief", "AutoFetch", true);
            LoadFlight(ini);
        }
    }

    // ===================================================================== small helpers

    // Edits one key in VBuddy.ini, keeping every comment and the file's line endings.
    internal static class IniWriter {
        private static readonly object Gate = new object();

        public static void Set(string path, string section, string key, string value) {
            lock (Gate) {
                string text = File.Exists(path) ? File.ReadAllText(path) : "";
                string nl = (text.Length == 0 || text.Contains("\r\n")) ? "\r\n" : "\n";
                var lines = new List<string>(text.Replace("\r\n", "\n").Split(new[] { '\n' }));
                while (lines.Count > 0 && lines[lines.Count - 1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);

                int start = -1, end = lines.Count;
                for (int i = 0; i < lines.Count; i++) {
                    string t = lines[i].Trim();
                    if (start < 0) { if (t.Equals("[" + section + "]", StringComparison.OrdinalIgnoreCase)) start = i + 1; }
                    else if (t.StartsWith("[")) { end = i; break; }
                }

                if (start < 0) {
                    lines.Add("");
                    lines.Add("[" + section + "]");
                    lines.Add(key + "=" + value);
                } else {
                    int found = -1;
                    for (int i = start; i < end; i++) {
                        string t = lines[i].Trim();
                        if (t.StartsWith(";") || t.StartsWith("#")) continue;
                        int eq = t.IndexOf('=');
                        if (eq > 0 && t.Substring(0, eq).Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) { found = i; break; }
                    }
                    if (found >= 0) lines[found] = key + "=" + value;
                    else {
                        int ins = end;
                        while (ins > start && lines[ins - 1].Trim().Length == 0) ins--;
                        lines.Insert(ins, key + "=" + value);
                    }
                }

                string tmp = path + ".tmp";
                File.WriteAllText(tmp, string.Join(nl, lines.ToArray()) + nl, new UTF8Encoding(false));
                File.Copy(tmp, path, true);
                try { File.Delete(tmp); } catch { }
            }
        }
    }

    internal static class GeoMath {
        public static double DistanceNm(double lat1, double lon1, double lat2, double lon2) {
            const double R = 3440.065;   // nautical miles
            double p1 = lat1 * Math.PI / 180, p2 = lat2 * Math.PI / 180;
            double dp = p2 - p1, dl = (lon2 - lon1) * Math.PI / 180;
            double a = Math.Sin(dp / 2) * Math.Sin(dp / 2) + Math.Cos(p1) * Math.Cos(p2) * Math.Sin(dl / 2) * Math.Sin(dl / 2);
            return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        }
    }

    // ===================================================================== SimBrief

    // The OFP as kept in memory (flat, read-only once built).
    internal sealed class SimBriefData {
        public DateTime FetchedUtc;
        public string Callsign, Airline, FlightNo, AircraftIcao, AircraftName, Registration, Selcal, Equipment;
        public string OriginIcao, OriginName, DestIcao, DestName, AltnIcao;
        public double OriginElevFt, OriginLat, OriginLon, DestElevFt, DestLat, DestLon;
        public int CruiseAltFt, CruiseTas;
        public double DistanceNm;
        public int EteSec, EnduranceSec;
        public DateTime SchedOutUtc;
        public string Route, Remarks, Units;
        public double PlanRamp, PlanTakeoff, PlanLanding, Reserve, Taxi, EnrouteBurn;
        public int Passengers, CostIndex;
    }

    internal static class SimBriefPlan {

        // The cached OFP. Swapped as a whole, so readers always see a complete plan.
        public static volatile SimBriefData Current;

        private static readonly HttpClient Http = Create();

        private static HttpClient Create() {
            var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate };
            var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("VBuddy-SimBrief/2.1");
            return c;
        }

        public static async Task<SimBriefData> FetchAsync(string userName, string userId, string cachePath) {
            string q;
            if (!string.IsNullOrWhiteSpace(userId)) q = "userid=" + Uri.EscapeDataString(userId.Trim());
            else if (!string.IsNullOrWhiteSpace(userName)) q = "username=" + Uri.EscapeDataString(userName.Trim());
            else throw new InvalidOperationException("No SimBrief username or pilot ID is set.");

            string url = "https://www.simbrief.com/api/xml.fetcher.php?" + q + "&json=1";
            string json;
            using (var resp = await Http.GetAsync(url)) {
                json = await resp.Content.ReadAsStringAsync();   // SimBrief explains errors in the body
            }

            SimBriefData d = Parse(json);
            Current = d;
            try { if (!string.IsNullOrEmpty(cachePath)) File.WriteAllText(cachePath, json, new UTF8Encoding(false)); } catch { }
            return d;
        }

        public static void LoadCached(string path, Action<string> log) {
            try {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
                SimBriefData d = Parse(File.ReadAllText(path));
                Current = d;
                log("[SimBrief] Loaded the saved OFP: " + d.OriginIcao + " to " + d.DestIcao);
            } catch (Exception ex) {
                log("[SimBrief] Ignored the saved OFP: " + ex.Message);
            }
        }

        // ---------------------------------------------------------- JSON -> data object

        private static Dictionary<string, object> N(Dictionary<string, object> d, string k) {
            return MiniJson.Get(d, k) as Dictionary<string, object>;
        }
        private static string S(Dictionary<string, object> d, string k) { return MiniJson.Str(MiniJson.Get(d, k)).Trim(); }
        private static double D(Dictionary<string, object> d, string k) { return MiniJson.ToDouble(MiniJson.Get(d, k)); }

        public static SimBriefData Parse(string json) {
            var root = MiniJson.Parse(json) as Dictionary<string, object>;
            if (root == null) throw new InvalidOperationException("SimBrief returned something that isn't JSON.");

            string status = S(N(root, "fetch"), "status");
            if (status.StartsWith("Error", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException(status);

            var origin = N(root, "origin"); var dest = N(root, "destination"); var altn = N(root, "alternate");
            var general = N(root, "general"); var times = N(root, "times"); var fuel = N(root, "fuel");
            var acft = N(root, "aircraft"); var atc = N(root, "atc"); var prm = N(root, "params"); var weights = N(root, "weights");
            if (origin == null || dest == null) throw new InvalidOperationException("SimBrief returned no flight plan. Generate one at simbrief.com first.");

            var d = new SimBriefData();
            d.FetchedUtc = DateTime.UtcNow;
            d.OriginIcao = S(origin, "icao_code").ToUpperInvariant();
            d.OriginName = S(origin, "name");
            d.OriginElevFt = D(origin, "elevation");
            d.OriginLat = D(origin, "pos_lat"); d.OriginLon = D(origin, "pos_long");
            d.DestIcao = S(dest, "icao_code").ToUpperInvariant();
            d.DestName = S(dest, "name");
            d.DestElevFt = D(dest, "elevation");
            d.DestLat = D(dest, "pos_lat"); d.DestLon = D(dest, "pos_long");
            d.AltnIcao = S(altn, "icao_code").ToUpperInvariant();

            d.Airline = S(general, "icao_airline");
            d.FlightNo = S(general, "flight_number");
            d.Callsign = S(atc, "callsign");
            if (d.Callsign.Length == 0) d.Callsign = d.Airline + d.FlightNo;
            d.CruiseAltFt = (int)Math.Round(D(general, "initial_altitude"));
            d.CruiseTas = (int)Math.Round(D(general, "cruise_tas"));
            d.DistanceNm = D(general, "route_distance");
            d.Route = S(general, "route");
            d.CostIndex = (int)D(general, "costindex");
            d.Passengers = (int)D(general, "passengers");
            if (d.Passengers == 0) d.Passengers = (int)D(weights, "pax_count");
            d.Remarks = S(atc, "section18");

            d.AircraftIcao = S(acft, "icaocode");
            if (d.AircraftIcao.Length == 0) d.AircraftIcao = S(acft, "icao_code");
            d.AircraftName = S(acft, "name");
            d.Registration = S(acft, "reg");
            d.Selcal = S(acft, "selcal");
            d.Equipment = S(acft, "equip");

            d.EteSec = (int)D(times, "est_time_enroute");
            d.EnduranceSec = (int)D(times, "endurance");
            double out0 = D(times, "sched_out");
            d.SchedOutUtc = out0 > 0 ? new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(out0) : DateTime.MinValue;

            d.Units = S(prm, "units");
            if (d.Units.Length == 0) d.Units = "kgs";
            d.PlanRamp = D(fuel, "plan_ramp");
            d.PlanTakeoff = D(fuel, "plan_takeoff");
            d.PlanLanding = D(fuel, "plan_landing");
            d.Reserve = D(fuel, "reserve");
            d.Taxi = D(fuel, "taxi");
            d.EnrouteBurn = D(fuel, "enroute_burn");
            return d;
        }

        // ---------------------------------------------------------- text output

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string Level(int ft) {
            return ft >= 18000 ? "FL" + (ft / 100).ToString("000", Inv) : ft.ToString("N0", Inv) + " ft";
        }

        // 3:1 rule: 3 nm for every 1000 ft to lose.
        public static double TodDistanceNm(SimBriefData d) {
            return Math.Max(0, (d.CruiseAltFt - d.DestElevFt) / 1000.0 * 3.0);
        }

        private static string Hm(int seconds) {
            if (seconds <= 0) return "-";
            return (seconds / 3600) + "h " + ((seconds % 3600) / 60).ToString("00", Inv) + "m";
        }

        private static string Hhmm(int seconds) {
            return (seconds / 3600).ToString("00", Inv) + ":" + ((seconds % 3600) / 60).ToString("00", Inv);
        }

        private static string W(SimBriefData d, double v) {
            return v.ToString("N0", Inv) + " " + d.Units;
        }

        public static string Summary(SimBriefData d) {
            var sb = new StringBuilder();
            sb.Append("\u2708 SimBrief OFP  ").Append(d.Callsign);
            if (d.AircraftIcao.Length > 0) sb.Append("  ").Append(d.AircraftIcao);
            sb.Append('\n').Append(d.OriginIcao).Append(" \u2192 ").Append(d.DestIcao);
            if (d.AltnIcao.Length > 0) sb.Append("   (alternate ").Append(d.AltnIcao).Append(")");
            if (d.Route.Length > 0) sb.Append("\nRoute: ").Append(d.Route);
            sb.Append("\nCruise ").Append(Level(d.CruiseAltFt));
            if (d.CruiseTas > 0) sb.Append("  \u00B7  TAS ").Append(d.CruiseTas).Append(" kt");
            if (d.DistanceNm > 0) sb.Append("  \u00B7  ").Append(d.DistanceNm.ToString("N0", Inv)).Append(" nm");
            sb.Append("\nEnroute ").Append(Hm(d.EteSec));
            if (d.SchedOutUtc != DateTime.MinValue) sb.Append("  \u00B7  Off-block ").Append(d.SchedOutUtc.ToString("HHmm'Z' dd MMM", Inv));
            sb.Append("\nFuel: ramp ").Append(W(d, d.PlanRamp)).Append("  \u00B7  reserve ").Append(W(d, d.Reserve));
            if (d.Passengers > 0) sb.Append("\nPassengers ").Append(d.Passengers).Append("  \u00B7  CI ").Append(d.CostIndex);
            sb.Append("\nTOD about ").Append(TodDistanceNm(d).ToString("N0", Inv)).Append(" nm before ").Append(d.DestIcao).Append(" (3:1 rule)");
            return sb.ToString();
        }

        // Everything vPilot's "File Flight Plan" window asks for, ready to copy across.
        public static string FilingSheet(SimBriefData d) {
            var sb = new StringBuilder();
            sb.Append("\uD83D\uDCCB Flight plan for vPilot  (").Append(d.Callsign).Append(")\n\n");
            sb.Append("Departure:  ").Append(d.OriginIcao).Append('\n');
            sb.Append("Arrival:  ").Append(d.DestIcao).Append('\n');
            sb.Append("Alternate:  ").Append(d.AltnIcao.Length > 0 ? d.AltnIcao : "-").Append('\n');
            sb.Append("Cruise altitude:  ").Append(Level(d.CruiseAltFt)).Append("  (").Append(d.CruiseAltFt.ToString(Inv)).Append(")\n");
            sb.Append("Cruise speed:  ").Append(d.CruiseTas).Append(" kt\n");
            sb.Append("Departure time (Z):  ").Append(d.SchedOutUtc != DateTime.MinValue ? d.SchedOutUtc.ToString("HHmm", Inv) : "-").Append('\n');
            sb.Append("Time en route:  ").Append(Hhmm(d.EteSec)).Append('\n');
            sb.Append("Fuel on board:  ").Append(Hhmm(d.EnduranceSec)).Append("  (endurance)\n");
            sb.Append("Route:  ").Append(d.Route.Length > 0 ? d.Route : "-").Append('\n');
            if (d.Remarks.Length > 0) sb.Append("Remarks:  ").Append(d.Remarks).Append('\n');
            sb.Append("\nFuel load: ramp ").Append(W(d, d.PlanRamp)).Append(", takeoff ").Append(W(d, d.PlanTakeoff)).Append('\n');
            sb.Append("\nNote: this vPilot plugin SDK has no function for filing a plan, so VBuddy can't submit it for you. ")
              .Append("Open Flight Plan in vPilot and enter these values.");
            return sb.ToString();
        }
    }

    // ===================================================================== the bot: extras

    internal sealed partial class TelegramControl {

        // ---- state
        private string _dir, _iniPath, _briefPath;
        private SimLink _sim;
        private readonly object _simLock = new object();
        private SimSample _lastSample, _prevSample;
        private bool _haveSample, _havePrev;
        private bool _airborne, _cruiseReached, _todSent;
        private readonly Queue<KeyValuePair<DateTime, double>> _vsBuf = new Queue<KeyValuePair<DateTime, double>>();

        private sealed class Tog {
            public string Id, Label, Section, Key;
            public Func<bool> Get;
            public Action<bool> Set;
        }
        private List<Tog> _togs;

        private void InitExtrasPaths(string pluginDir) {
            _dir = pluginDir;
            _iniPath = Path.Combine(pluginDir, "VBuddy.ini");
            _briefPath = Path.Combine(pluginDir, "VBuddy.simbrief.json");
        }

        private void StartExtras() {
            _togs = new List<Tog> {
                new Tog { Id = "bc", Label = "Broadcasts", Section = "TelegramControl", Key = "RelayBroadcast", Get = () => _s.RelayBroadcast, Set = v => _s.RelayBroadcast = v },
                new Tog { Id = "nc", Label = "Connect notice", Section = "TelegramControl", Key = "NotifyConnect", Get = () => _s.NotifyConnect, Set = v => _s.NotifyConnect = v },
                new Tog { Id = "nd", Label = "Disconnect notice", Section = "TelegramControl", Key = "NotifyDisconnect", Get = () => _s.NotifyDisconnect, Set = v => _s.NotifyDisconnect = v },
                new Tog { Id = "cf", Label = "Confirm disconnect", Section = "TelegramControl", Key = "ConfirmDisconnect", Get = () => _s.ConfirmDisconnect, Set = v => _s.ConfirmDisconnect = v },
                new Tog { Id = "ls", Label = "All radio text", Section = "TelegramControl", Key = "ListenAllRadio", Get = () => _listen, Set = v => { _listen = v; _s.ListenAllRadio = v; } },
                new Tog { Id = "td", Label = "TOD alert", Section = "SimConnect", Key = "TodAlert", Get = () => _s.TodAlert, Set = v => _s.TodAlert = v },
                new Tog { Id = "tr", Label = "Touchdown report", Section = "SimConnect", Key = "TouchdownReport", Get = () => _s.TouchdownReport, Set = v => _s.TouchdownReport = v }
            };

            AddFlightToggles();

            LoadProfiles();
            SimBriefPlan.LoadCached(_briefPath, _log);

            if (_s.SimEnabled) {
                _sim = SimLinkHost.TryCreate(_s.SimDllPath, _dir, _log, OnSample);
                if (_sim == null) _log("[SimConnect] Enabled in VBuddy.ini but could not start. See the message above.");
                else { _sim.SimEvent += OnSimEvent; _sim.Tick += OnSimTick; }
            }
        }

        private void StopExtras() {
            try { if (_sim != null) _sim.Dispose(); } catch { }
            _sim = null;
        }

        // ------------------------------------------------------ SimBrief commands

        private void AutoFetchSimBrief() {
            if (!_s.SbAutoFetch || (string.IsNullOrEmpty(_s.SbUserName) && string.IsNullOrEmpty(_s.SbUserId))) return;
            Task.Run(async () => {
                try {
                    SimBriefData d = await SimBriefPlan.FetchAsync(_s.SbUserName, _s.SbUserId, _briefPath);
                    ResetTod();
                    _log("[SimBrief] Loaded OFP " + d.OriginIcao + " to " + d.DestIcao);
                    if (!IsMuted)
                        await SendAsync("\uD83D\uDCCB SimBrief OFP loaded: " + d.OriginIcao + " \u2192 " + d.DestIcao + ", " +
                                        SimBriefPlan.Level(d.CruiseAltFt) + ". (/simbrief for details, /fileplan for the vPilot values)");
                } catch (Exception ex) {
                    _log("[SimBrief] Auto-fetch failed: " + Scrub(ex.Message));
                }
            });
        }

        private async Task<string> CmdSimBrief(string[] parts) {
            string user = _s.SbUserName, id = _s.SbUserId;
            bool remember = false;
            if (parts.Length > 1) {
                string a = parts[1].Trim();
                if (Regex.IsMatch(a, @"^\d+$")) { id = a; user = null; } else { user = a; id = null; }
                remember = true;
            }
            if (string.IsNullOrEmpty(user) && string.IsNullOrEmpty(id))
                return "Tell me your SimBrief name once:  /simbrief YOURNAME  (or your numeric pilot ID).\n" +
                       "I'll remember it. You can also set UserName under [SimBrief] in VBuddy.ini.";

            SimBriefData d;
            try { d = await SimBriefPlan.FetchAsync(user, id, _briefPath); }
            catch (Exception ex) { return "SimBrief fetch failed: " + Scrub(ex.Message); }

            ResetTod();
            if (remember) {
                _s.SbUserName = user; _s.SbUserId = id;
                try {
                    IniWriter.Set(_iniPath, "SimBrief", "UserName", user ?? "");
                    IniWriter.Set(_iniPath, "SimBrief", "UserId", id ?? "");
                } catch (Exception ex) { _log("[SimBrief] Couldn't save your SimBrief name: " + ex.Message); }
            }
            return SimBriefPlan.Summary(d);
        }

        private async Task<string> CmdFilePlanSheet() {
            SimBriefData d = SimBriefPlan.Current;
            if (d == null || (DateTime.UtcNow - d.FetchedUtc).TotalMinutes > 30) {
                if (!string.IsNullOrEmpty(_s.SbUserName) || !string.IsNullOrEmpty(_s.SbUserId)) {
                    try { d = await SimBriefPlan.FetchAsync(_s.SbUserName, _s.SbUserId, _briefPath); ResetTod(); }
                    catch (Exception ex) { if (d == null) return "SimBrief fetch failed: " + Scrub(ex.Message); }
                }
            }
            if (d == null) return "No SimBrief plan loaded yet. Send  /simbrief YOURNAME  first.";
            return SimBriefPlan.FilingSheet(d);
        }

        private string CmdFp() {
            SimBriefData d = SimBriefPlan.Current;
            if (d == null)
                return "No plan loaded. Send  /simbrief YOURNAME  to load your SimBrief OFP, or /where to see your filed plan from the VATSIM data feed.";
            return SimBriefPlan.Summary(d);
        }

        // ------------------------------------------------------ in-chat toggle buttons

        private static KeyValuePair<string, string> KV(string k, string v) { return new KeyValuePair<string, string>(k, v); }

        private async Task<bool> PostApi(string method, params KeyValuePair<string, string>[] fields) {
            try {
                using (var resp = await Client.PostAsync(Api(method), new FormUrlEncodedContent(fields))) {
                    if (resp.IsSuccessStatusCode) return true;
                    string body = await resp.Content.ReadAsStringAsync();
                    if (body.IndexOf("not modified", StringComparison.OrdinalIgnoreCase) < 0)
                        _log("[TelegramControl] " + method + " failed: HTTP " + (int)resp.StatusCode + " " + Scrub(body));
                    return false;
                }
            } catch (Exception ex) {
                _log("[TelegramControl] " + method + " error: " + Scrub(ex.Message));
                return false;
            }
        }

        private string SettingsMarkup() {
            var sb = new StringBuilder("{\"inline_keyboard\":[");
            for (int i = 0; i < _togs.Count; i += 2) {
                if (i > 0) sb.Append(',');
                sb.Append('[');
                for (int j = i; j < Math.Min(i + 2, _togs.Count); j++) {
                    if (j > i) sb.Append(',');
                    Tog t = _togs[j];
                    sb.Append("{\"text\":\"").Append(t.Get() ? "\u2705 " : "\u274C ").Append(TgFormat.JsonEscape(t.Label))
                      .Append("\",\"callback_data\":\"tg:").Append(t.Id).Append("\"}");
                }
                sb.Append(']');
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private Task SendSettingsAsync() {
            return PostApi("sendMessage",
                KV("chat_id", _s.ChatId.ToString(CultureInfo.InvariantCulture)),
                KV("text", "\u2699 Alert settings\nTap a button to switch it on or off. Changes apply straight away and are saved to VBuddy.ini."),
                KV("reply_markup", SettingsMarkup()));
        }

        private async Task HandleCallback(Dictionary<string, object> cq) {
            string cqId = MiniJson.Str(MiniJson.Get(cq, "id"));
            string data = MiniJson.Str(MiniJson.Get(cq, "data"));
            var msg = MiniJson.Get(cq, "message") as Dictionary<string, object>;
            var chat = MiniJson.Get(msg, "chat") as Dictionary<string, object>;
            long chatId = MiniJson.ToLong(MiniJson.Get(chat, "id"));
            long msgId = MiniJson.ToLong(MiniJson.Get(msg, "message_id"));

            if (chatId != _s.ChatId) {
                _log("[TelegramControl] Ignored a button press from chat id " + chatId + ".");
                return;
            }
            if (_togs == null || !data.StartsWith("tg:")) { await AnswerCallback(cqId, ""); return; }

            Tog tog = _togs.Find(t => "tg:" + t.Id == data);
            if (tog == null) { await AnswerCallback(cqId, "That button is out of date. Send /settings again."); return; }

            bool now;
            lock (_lock) { now = !tog.Get(); tog.Set(now); }

            string note = "";
            try { IniWriter.Set(_iniPath, tog.Section, tog.Key, now ? "true" : "false"); }
            catch (Exception ex) { note = " (not saved to the ini: " + ex.Message + ")"; _log("[TelegramControl] Couldn't save " + tog.Key + ": " + ex.Message); }
            if (tog.Section == "SimConnect" && !_s.SimEnabled) note += " (SimConnect is off in VBuddy.ini)";

            await AnswerCallback(cqId, tog.Label + ": " + (now ? "ON" : "OFF") + note);
            if (msgId != 0)
                await PostApi("editMessageReplyMarkup",
                    KV("chat_id", chatId.ToString(CultureInfo.InvariantCulture)),
                    KV("message_id", msgId.ToString(CultureInfo.InvariantCulture)),
                    KV("reply_markup", SettingsMarkup()));
        }

        private Task AnswerCallback(string id, string text) {
            return PostApi("answerCallbackQuery", KV("callback_query_id", id), KV("text", text ?? ""));
        }

        // ------------------------------------------------------ SimConnect telemetry

        private void ResetTod() {
            lock (_simLock) { _todSent = false; _cruiseReached = false; }
        }

        // Runs on the SimConnect thread, about once a second (every few frames near the ground).
        private void OnSample(SimSample s) {
            string toSend = null;
            var outMsgs = new List<string>();
            lock (_simLock) {
                bool prevGround = _havePrev ? _prevSample.OnGround : s.OnGround;
                _lastSample = s; _haveSample = true;

                _vsBuf.Enqueue(new KeyValuePair<DateTime, double>(s.Utc, s.VsFpm));
                while (_vsBuf.Count > 0 && (s.Utc - _vsBuf.Peek().Key).TotalSeconds > 6) _vsBuf.Dequeue();

                if (!s.OnGround && s.AglFt > 200) _airborne = true;

                // ---- touchdown
                if (_havePrev && !prevGround && s.OnGround && _airborne) {
                    double buffered = 0;
                    foreach (var kv in _vsBuf) if (kv.Value < buffered) buffered = kv.Value;
                    double simFpm = s.TouchdownFps != 0 ? -Math.Abs(s.TouchdownFps) * 60.0 : 0;
                    double rate = simFpm != 0 ? simFpm : buffered;
                    _airborne = false; _cruiseReached = false; _todSent = false;
                    if (_s.TouchdownReport && !IsMuted) toSend = TouchdownText(rate, simFpm != 0, buffered, s);
                }

                // ---- top of descent
                SimBriefData plan = SimBriefPlan.Current;
                if (toSend == null && _airborne && plan != null && plan.CruiseAltFt > 0 && !(plan.DestLat == 0 && plan.DestLon == 0)) {
                    if (s.AltFt >= plan.CruiseAltFt - 1500) _cruiseReached = true;
                    double dist = GeoMath.DistanceNm(s.Lat, s.Lon, plan.DestLat, plan.DestLon);
                    double tod = SimBriefPlan.TodDistanceNm(plan);
                    if (!_todSent && _cruiseReached && dist <= tod + _s.TodLeadNm) {
                        _todSent = true;
                        bool stillLevel = s.AltFt >= plan.CruiseAltFt - 1500;
                        if (stillLevel && _s.TodAlert && !IsMuted) toSend = TodText(plan, dist, tod, s);
                    }
                }

                MonitorSample(s, outMsgs);
                LogSample(s, outMsgs);

                _prevSample = s; _havePrev = true;
            }
            if (toSend != null) outMsgs.Insert(0, toSend);
            foreach (string m in outMsgs) _ = SendAsync(m);
        }

        private static string TodText(SimBriefData p, double dist, double tod, SimSample s) {
            return "\u2B07 Top of descent\n" +
                   dist.ToString("N0", CultureInfo.InvariantCulture) + " nm to " + p.DestIcao + ". " +
                   "From " + SimBriefPlan.Level(p.CruiseAltFt) + " down to " + p.DestIcao + " (" +
                   p.DestElevFt.ToString("N0", CultureInfo.InvariantCulture) + " ft) needs about " +
                   tod.ToString("N0", CultureInfo.InvariantCulture) + " nm (3:1 rule).\n" +
                   "Ground speed " + ((int)Math.Round(s.GsKt)) + " kt. Time to start down.";
        }

        private static string TouchdownText(double fpm, bool fromSim, double buffered, SimSample s) {
            double a = Math.Abs(fpm);
            string rating = a <= 60 ? "butter" : a <= 180 ? "smooth" : a <= 300 ? "normal" : a <= 500 ? "firm" : a <= 700 ? "hard" : "very hard, check the aircraft";
            var sb = new StringBuilder("\uD83D\uDEEC Touchdown\n");
            sb.Append("Landing rate ").Append(((int)Math.Round(fpm)).ToString(CultureInfo.InvariantCulture)).Append(" fpm  (").Append(rating).Append(")\n");
            sb.Append("Ground speed ").Append((int)Math.Round(s.GsKt)).Append(" kt  \u00B7  ").Append(DateTime.UtcNow.ToString("HHmm'Z'", CultureInfo.InvariantCulture));
            if (fromSim && buffered < 0 && Math.Abs(buffered - fpm) > 40)
                sb.Append("\nLast-seconds vertical speed ").Append(((int)Math.Round(buffered)).ToString(CultureInfo.InvariantCulture)).Append(" fpm");
            return sb.ToString();
        }

        private string SimState() {
            if (!_s.SimEnabled) return "off (set Enabled=true under [SimConnect] in VBuddy.ini)";
            if (_sim == null) return "unavailable (see the vPilot debug window)";
            return _sim.Connected ? "connected" : "waiting for the simulator";
        }

        private string ExtrasStatus() {
            var sb = new StringBuilder();
            sb.Append("\nSimConnect: ").Append(SimState());
            SimBriefData p = SimBriefPlan.Current;
            sb.Append("\nSimBrief: ").Append(p == null ? "no plan loaded" : p.OriginIcao + " \u2192 " + p.DestIcao);
            sb.Append(FlightStatus());
            return sb.ToString();
        }
    }
}
