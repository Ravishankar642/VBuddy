// TelegramControl.Flight.cs  -  cockpit alerts, flight log, remote COM tuning / squawk, live data display.
//
// Everything here needs [SimConnect] Enabled=true. Add this file next to the other VBuddy plugin files.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace VBuddy {

    // ===================================================================== settings

    internal sealed partial class TelegramControlSettings {
        public bool AlertFuel = true;          // low fuel
        public int LowFuelMinutes = 30;        // warn below this many minutes of fuel at the current burn
        public bool AlertEngine = true;        // engine stopped in flight
        public bool AlertAirspeed = true;      // stall and overspeed warnings
        public bool AlertApproach = true;      // gear / flaps not set on approach
        public bool AlertPause = true;         // sim paused while connected, sim closed, crash
        public int PauseSeconds = 30;          // how long a pause lasts before you're told
        public bool PhaseNotices = true;       // off-block / airborne / on-block messages
        public bool FlightLog = true;          // keep a flight log and track
        public int TrackIntervalSeconds = 20;  // seconds between track points
        public bool RemoteControl = true;      // allow /tune and /squawk

        internal void LoadFlight(IniFile ini) {
            const string S = "SimConnect";
            AlertFuel = RB(ini, S, "AlertFuel", true);
            AlertEngine = RB(ini, S, "AlertEngine", true);
            AlertAirspeed = RB(ini, S, "AlertAirspeed", true);
            AlertApproach = RB(ini, S, "AlertApproach", true);
            AlertPause = RB(ini, S, "AlertPause", true);
            PhaseNotices = RB(ini, S, "PhaseNotices", true);
            FlightLog = RB(ini, S, "FlightLog", true);
            RemoteControl = RB(ini, S, "RemoteControl", true);
            LowFuelMinutes = RI(ini, S, "LowFuelMinutes", 30, 1, 600);
            PauseSeconds = RI(ini, S, "PauseSeconds", 30, 5, 600);
            TrackIntervalSeconds = RI(ini, S, "TrackIntervalSeconds", 20, 5, 300);
            LoadAutopilot(ini);
        }

        private static int RI(IniFile ini, string section, string key, int def, int min, int max) {
            string v = R(ini, section, key);
            int n;
            return v != null && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? Math.Max(min, Math.Min(max, n)) : def;
        }
    }

    // ===================================================================== the bot: flight features

    internal sealed partial class TelegramControl {

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // ---- cockpit alert state
        private readonly Dictionary<string, DateTime> _cool = new Dictionary<string, DateTime>();
        private readonly bool[] _engWasRunning = new bool[4], _engAlerted = new bool[4];
        private readonly DateTime[] _engStopSince = { DateTime.MinValue, DateTime.MinValue, DateTime.MinValue, DateTime.MinValue };
        private bool _prevStall, _prevOver, _apprArmed, _gearWarn, _flapWarn;
        private DateTime _pausedSince = DateTime.MinValue;
        private bool _pauseAlerted;

        // ---- flight log state
        private enum Phase { Parked, Out, Off, On }
        private struct TrackPt { public DateTime Utc; public double Lat, Lon, AltFt, Gs; }
        private sealed class FlightRec {
            public string Callsign = "", Dep = "", Arr = "";
            public DateTime Out = DateTime.MinValue, Off = DateTime.MinValue, On = DateTime.MinValue, In = DateTime.MinValue;
            public double FuelOutLbs, FuelInLbs, LandingFpm, MaxAltFt, MaxGsKt, DistNm;
            public bool HasFuel;
            public readonly List<TrackPt> Track = new List<TrackPt>();
        }
        private Phase _phase = Phase.Parked;
        private FlightRec _cur, _lastFlight;
        private DateTime _lastTrackUtc = DateTime.MinValue, _stillSince = DateTime.MinValue;
        private bool _logHavePrev, _logPrevGround;
        private double _logPrevLat, _logPrevLon;

        private void AddFlightToggles() {
            _togs.Add(new Tog { Id = "lf", Label = "Low fuel", Section = "SimConnect", Key = "AlertFuel", Get = () => _s.AlertFuel, Set = v => _s.AlertFuel = v });
            _togs.Add(new Tog { Id = "en", Label = "Engine failure", Section = "SimConnect", Key = "AlertEngine", Get = () => _s.AlertEngine, Set = v => _s.AlertEngine = v });
            _togs.Add(new Tog { Id = "as", Label = "Stall / overspeed", Section = "SimConnect", Key = "AlertAirspeed", Get = () => _s.AlertAirspeed, Set = v => _s.AlertAirspeed = v });
            _togs.Add(new Tog { Id = "ap", Label = "Gear / flaps check", Section = "SimConnect", Key = "AlertApproach", Get = () => _s.AlertApproach, Set = v => _s.AlertApproach = v });
            _togs.Add(new Tog { Id = "ps", Label = "Sim paused", Section = "SimConnect", Key = "AlertPause", Get = () => _s.AlertPause, Set = v => _s.AlertPause = v });
            _togs.Add(new Tog { Id = "ac", Label = "Autopilot commands", Section = "SimConnect", Key = "ApControl", Get = () => _s.ApControl, Set = v => _s.ApControl = v });
            _togs.Add(new Tog { Id = "ph", Label = "Flight notices", Section = "SimConnect", Key = "PhaseNotices", Get = () => _s.PhaseNotices, Set = v => _s.PhaseNotices = v });
        }

        private bool Cool(string key, int seconds) {
            DateTime last;
            if (_cool.TryGetValue(key, out last) && (DateTime.UtcNow - last).TotalSeconds < seconds) return false;
            _cool[key] = DateTime.UtcNow;
            return true;
        }

        private static string Num(double v) { return ((long)Math.Round(v)).ToString("N0", Inv); }

        private static string Fuel(double lbs) {
            return Num(lbs * 0.45359237) + " kg (" + Num(lbs) + " lb)";
        }

        private static string Clock(DateTime utc) { return utc == DateTime.MinValue ? "-" : utc.ToString("HHmm'Z'", Inv); }

        private static string Span(TimeSpan t) {
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            return ((int)t.TotalHours) + "h " + t.Minutes.ToString("00", Inv) + "m";
        }

        // ------------------------------------------------------ cockpit alerts

        // Called from OnSample with _simLock held. Adds any alerts to 'msgs'.
        private void MonitorSample(SimSample s, List<string> msgs) {
            bool quiet = IsMuted;
            Action<string> add = t => { if (!quiet) msgs.Add(t); };
            if (!s.HasExt) return;

            // Low fuel: minutes of fuel left at the current burn.
            if (_s.AlertFuel && !s.OnGround && s.AglFt > 300 && s.FuelFlowPph > 50) {
                double min = s.FuelLbs / s.FuelFlowPph * 60.0;
                if (min < _s.LowFuelMinutes && Cool("fuel", 600))
                    add("\u26FD Low fuel\nAbout " + Num(min) + " min left at the current burn.\nOn board " + Fuel(s.FuelLbs) + ", burning " + Fuel(s.FuelFlowPph) + "/h.");
            }

            // Engine stopped in flight.
            if (_s.AlertEngine) {
                int count = Math.Max(1, Math.Min(4, s.NumEngines));
                for (int i = 0; i < count; i++) {
                    bool run = ((s.EnginesRunning >> i) & 1) == 1;
                    if (run) { _engStopSince[i] = DateTime.MinValue; _engAlerted[i] = false; _engWasRunning[i] = true; }
                    else if (_engWasRunning[i]) {
                        if (_engStopSince[i] == DateTime.MinValue) _engStopSince[i] = s.Utc;
                        if (!s.OnGround && s.AglFt > 300 && !_engAlerted[i] && (s.Utc - _engStopSince[i]).TotalSeconds >= 3) {
                            _engAlerted[i] = true;
                            int still = 0; for (int k = 0; k < count; k++) if (((s.EnginesRunning >> k) & 1) == 1) still++;
                            add("\uD83D\uDD25 Engine " + (i + 1) + " has stopped\nAt " + TgFormat.Feet(s.AltFt) + ". " + still + " of " + count + " engine(s) still running.");
                        }
                        if (s.OnGround) _engWasRunning[i] = false;   // normal shutdown on the ground
                    }
                }
            }

            // Stall and overspeed warnings (rising edge).
            if (_s.AlertAirspeed && !s.OnGround && s.AglFt > 100) {
                if (s.Stall && !_prevStall && Cool("stall", 30))
                    add("\u26A0 Stall warning\nIAS " + Num(s.IasKt) + " kt at " + TgFormat.Feet(s.AltFt) + ".");
                if (s.Overspeed && !_prevOver && Cool("over", 30))
                    add("\u26A0 Overspeed warning\nIAS " + Num(s.IasKt) + " kt at " + TgFormat.Feet(s.AltFt) + ".");
            }
            _prevStall = s.Stall; _prevOver = s.Overspeed;

            // Gear and flaps on approach.
            if (_s.AlertApproach) {
                if (!s.OnGround && s.AglFt > 1500) { _apprArmed = true; _gearWarn = false; _flapWarn = false; }
                if (s.OnGround) _apprArmed = false;
                if (_apprArmed && !s.OnGround && s.AglFt < 1000 && s.VsFpm < -200) {
                    if (!_gearWarn && s.GearRetractable && !s.GearDown) {
                        _gearWarn = true;
                        add("\uD83D\uDEDE Gear is UP\n" + Num(s.AglFt) + " ft AGL on approach, IAS " + Num(s.IasKt) + " kt.");
                    }
                    if (!_flapWarn && s.AglFt < 600 && s.FlapsIndex == 0 && s.IasKt > 80) {
                        _flapWarn = true;
                        add("\u26A0 Flaps are UP\n" + Num(s.AglFt) + " ft AGL on approach, IAS " + Num(s.IasKt) + " kt.");
                    }
                }
            }
        }

        // Simulator notifications (SimLink thread).
        private void OnSimEvent(string name, uint data) {
            if (name == "Pause") {
                lock (_simLock) { _pausedSince = data == 1 ? DateTime.UtcNow : DateTime.MinValue; _pauseAlerted = false; }
            } else if (name == "Crashed") {
                if (_s.AlertPause) _ = SendAsync("\uD83D\uDCA5 The aircraft crashed in the simulator.");
            } else if (name == "Quit") {
                if (_s.AlertPause && _connected && !IsMuted) _ = SendAsync("\uD83D\uDD0C The simulator closed while you are still connected to VATSIM.");
            }
        }

        // ~5 times a second (SimLink thread).
        private void OnSimTick() {
            string msg = null;
            lock (_simLock) {
                if (_s.AlertPause && _connected && _pausedSince != DateTime.MinValue && !_pauseAlerted && !IsMuted &&
                    (DateTime.UtcNow - _pausedSince).TotalSeconds >= _s.PauseSeconds) {
                    _pauseAlerted = true;
                    msg = "\u23F8 The simulator has been paused for " + (int)(DateTime.UtcNow - _pausedSince).TotalSeconds + " s while you are connected to VATSIM.";
                }
            }
            if (msg != null) _ = SendAsync(msg);
        }

        // ------------------------------------------------------ flight log

        private double LandingRate(SimSample s, out double buffered) {
            buffered = 0;
            foreach (var kv in _vsBuf) if (kv.Value < buffered) buffered = kv.Value;
            return s.TouchdownFps != 0 ? -Math.Abs(s.TouchdownFps) * 60.0 : buffered;
        }

        // Called from OnSample with _simLock held.
        private void LogSample(SimSample s, List<string> msgs) {
            if (!_s.FlightLog) return;
            Action<string> add = t => { if (_s.PhaseNotices && !IsMuted) msgs.Add(t); };
            bool prevGround = _logHavePrev ? _logPrevGround : s.OnGround;

            switch (_phase) {
                case Phase.Parked:
                    if (!_logHavePrev && !s.OnGround && s.AglFt > 200) {
                        _cur = NewRec(s); _cur.Off = s.Utc; _phase = Phase.Off;   // joined the flight already airborne
                    } else if (s.OnGround && s.GsKt > 3 && (!s.HasExt || !s.ParkingBrake)) {
                        _cur = NewRec(s); _cur.Out = s.Utc; _phase = Phase.Out;
                        add("\uD83D\uDEAA Off-block " + Clock(s.Utc) + (s.HasExt ? "\nFuel " + Fuel(s.FuelLbs) : ""));
                    }
                    break;
                case Phase.Out:
                    if (!s.OnGround && s.AglFt > 30) {
                        _cur.Off = s.Utc; _phase = Phase.Off;
                        add("\uD83D\uDEEB Airborne " + Clock(s.Utc) + (_cur.Out != DateTime.MinValue ? "  (taxi " + Span(s.Utc - _cur.Out) + ")" : ""));
                    }
                    break;
                case Phase.Off:
                    if (s.OnGround && !prevGround) {
                        double buf; _cur.On = s.Utc; _cur.LandingFpm = LandingRate(s, out buf); _phase = Phase.On; _stillSince = DateTime.MinValue;
                    }
                    break;
                case Phase.On:
                    if (!s.OnGround && s.AglFt > 100) { _cur.On = DateTime.MinValue; _phase = Phase.Off; }   // touch and go
                    else if (s.GsKt < 1.5) {
                        if (_stillSince == DateTime.MinValue) _stillSince = s.Utc;
                        else if ((s.Utc - _stillSince).TotalSeconds >= 20) { FinishFlight(s, msgs); }
                    } else _stillSince = DateTime.MinValue;
                    break;
            }

            if (_phase != Phase.Parked && _cur != null) {
                if (s.AltFt > _cur.MaxAltFt) _cur.MaxAltFt = s.AltFt;
                if (s.GsKt > _cur.MaxGsKt) _cur.MaxGsKt = s.GsKt;
                if (_logHavePrev && (s.Lat != 0 || s.Lon != 0)) {
                    double step = GeoMath.DistanceNm(_logPrevLat, _logPrevLon, s.Lat, s.Lon);
                    if (step < 50) _cur.DistNm += step;   // ignore teleports
                }
                if ((s.Utc - _lastTrackUtc).TotalSeconds >= _s.TrackIntervalSeconds && _cur.Track.Count < 8000) {
                    _lastTrackUtc = s.Utc;
                    _cur.Track.Add(new TrackPt { Utc = s.Utc, Lat = s.Lat, Lon = s.Lon, AltFt = s.AltFt, Gs = s.GsKt });
                }
            }
            _logHavePrev = true; _logPrevGround = s.OnGround; _logPrevLat = s.Lat; _logPrevLon = s.Lon;
        }

        private FlightRec NewRec(SimSample s) {
            var r = new FlightRec { Callsign = _callsign ?? "" };
            SimBriefData p = SimBriefPlan.Current;
            if (p != null) { r.Dep = p.OriginIcao; r.Arr = p.DestIcao; if (r.Callsign.Length == 0) r.Callsign = p.Callsign; }
            if (s.HasExt) { r.FuelOutLbs = s.FuelLbs; r.HasFuel = true; }
            _lastTrackUtc = DateTime.MinValue;
            return r;
        }

        private void FinishFlight(SimSample s, List<string> msgs) {
            _cur.In = s.Utc;
            if (s.HasExt && _cur.HasFuel) _cur.FuelInLbs = s.FuelLbs;
            _cur.Track.Add(new TrackPt { Utc = s.Utc, Lat = s.Lat, Lon = s.Lon, AltFt = s.AltFt, Gs = s.GsKt });
            FlightRec done = _cur;
            _lastFlight = done; _cur = null; _phase = Phase.Parked; _stillSince = DateTime.MinValue;
            try { SaveFlight(done); } catch (Exception ex) { _log("[FlightLog] Couldn't save the flight log: " + ex.Message); }
            if (_s.PhaseNotices && !IsMuted) msgs.Add(FlightSummary(done, "\uD83C\uDD7F On blocks"));
        }

        private string FlightSummary(FlightRec r, string title) {
            var sb = new StringBuilder(title);
            if (r.Callsign.Length > 0) sb.Append("  ").Append(r.Callsign);
            if (r.Dep.Length > 0 || r.Arr.Length > 0) sb.Append("  ").Append(r.Dep.Length > 0 ? r.Dep : "?").Append(" \u2192 ").Append(r.Arr.Length > 0 ? r.Arr : "?");
            sb.Append("\nOff-block ").Append(Clock(r.Out)).Append("  \u00B7  Airborne ").Append(Clock(r.Off))
              .Append("  \u00B7  Landed ").Append(Clock(r.On)).Append("  \u00B7  On-block ").Append(Clock(r.In));
            DateTime end = r.In != DateTime.MinValue ? r.In : DateTime.UtcNow;
            DateTime start = r.Out != DateTime.MinValue ? r.Out : r.Off;
            if (start != DateTime.MinValue) sb.Append("\nBlock ").Append(Span(end - start));
            if (r.Off != DateTime.MinValue) sb.Append("  \u00B7  Air ").Append(Span((r.On != DateTime.MinValue ? r.On : end) - r.Off));
            sb.Append("  \u00B7  ").Append(Num(r.DistNm)).Append(" nm");
            if (r.HasFuel) {
                double used = (r.In != DateTime.MinValue ? r.FuelInLbs : _lastSample.FuelLbs);
                if (used > 0 || r.In != DateTime.MinValue) sb.Append("\nFuel used ").Append(Fuel(Math.Max(0, r.FuelOutLbs - used)));
            }
            if (r.On != DateTime.MinValue) sb.Append("\nLanding rate ").Append(Num(r.LandingFpm)).Append(" fpm");
            sb.Append("  \u00B7  Max ").Append(SimBriefPlan.Level((int)Math.Round(r.MaxAltFt / 100.0) * 100));
            return sb.ToString();
        }

        private static string Safe(string s) {
            var sb = new StringBuilder();
            foreach (char c in s) sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            return sb.Length == 0 ? "flight" : sb.ToString();
        }

        private static string BuildGpx(FlightRec r) {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<gpx version=\"1.1\" creator=\"VBuddy\" xmlns=\"http://www.topografix.com/GPX/1/1\">\n");
            sb.Append("<trk><name>").Append(System.Security.SecurityElement.Escape(r.Callsign + " " + r.Dep + "-" + r.Arr)).Append("</name><trkseg>\n");
            foreach (TrackPt p in r.Track)
                sb.Append("<trkpt lat=\"").Append(p.Lat.ToString("0.000000", Inv)).Append("\" lon=\"").Append(p.Lon.ToString("0.000000", Inv)).Append("\">")
                  .Append("<ele>").Append((p.AltFt * 0.3048).ToString("0.0", Inv)).Append("</ele>")
                  .Append("<time>").Append(p.Utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", Inv)).Append("</time></trkpt>\n");
            sb.Append("</trkseg></trk></gpx>\n");
            return sb.ToString();
        }

        private void SaveFlight(FlightRec r) {
            string dir = Path.Combine(_dir, "VBuddy.logs");
            Directory.CreateDirectory(dir);
            string stamp = (r.Out != DateTime.MinValue ? r.Out : r.Off).ToString("yyyyMMdd-HHmm", Inv);
            File.WriteAllText(Path.Combine(dir, stamp + "-" + Safe(r.Callsign) + ".gpx"), BuildGpx(r), new UTF8Encoding(false));

            string csv = Path.Combine(dir, "flights.csv");
            bool header = !File.Exists(csv);
            DateTime start = r.Out != DateTime.MinValue ? r.Out : r.Off;
            double block = start != DateTime.MinValue ? (r.In - start).TotalMinutes : 0;
            double air = r.Off != DateTime.MinValue && r.On != DateTime.MinValue ? (r.On - r.Off).TotalMinutes : 0;
            double usedKg = r.HasFuel ? Math.Max(0, r.FuelOutLbs - r.FuelInLbs) * 0.45359237 : 0;
            var sb = new StringBuilder();
            if (header) sb.Append("callsign,dep,arr,out_utc,off_utc,on_utc,in_utc,block_min,air_min,fuel_used_kg,landing_fpm,distance_nm,max_alt_ft\n");
            Func<DateTime, string> t = d => d == DateTime.MinValue ? "" : d.ToString("yyyy-MM-dd'T'HH:mm'Z'", Inv);
            sb.Append(r.Callsign).Append(',').Append(r.Dep).Append(',').Append(r.Arr).Append(',')
              .Append(t(r.Out)).Append(',').Append(t(r.Off)).Append(',').Append(t(r.On)).Append(',').Append(t(r.In)).Append(',')
              .Append(Math.Round(block).ToString(Inv)).Append(',').Append(Math.Round(air).ToString(Inv)).Append(',')
              .Append(Math.Round(usedKg).ToString(Inv)).Append(',').Append(Math.Round(r.LandingFpm).ToString(Inv)).Append(',')
              .Append(r.DistNm.ToString("0", Inv)).Append(',').Append(Math.Round(r.MaxAltFt).ToString(Inv)).Append('\n');
            File.AppendAllText(csv, sb.ToString(), new UTF8Encoding(false));
            _log("[FlightLog] Saved " + stamp + "-" + Safe(r.Callsign) + ".gpx and added a line to flights.csv");
        }

        private string CmdFlightLog() {
            lock (_simLock) {
                if (!_s.SimEnabled) return "The flight log needs SimConnect. Set Enabled=true under [SimConnect] in VBuddy.ini.";
                if (!_s.FlightLog) return "The flight log is switched off ([SimConnect] FlightLog=false).";
                if (_phase != Phase.Parked && _cur != null)
                    return FlightSummary(_cur, "\u2708 Flight in progress (" + _phase + ")");
                if (_lastFlight != null) return FlightSummary(_lastFlight, "\uD83D\uDCD2 Last flight");
                return "No flight logged yet. It starts when the aircraft first moves under its own power.";
            }
        }

        private async Task<string> CmdTrack() {
            FlightRec r; string name;
            lock (_simLock) {
                r = _cur ?? _lastFlight;
                if (r == null || r.Track.Count == 0) return "No track yet. Tracks are recorded during a flight (SimConnect must be on).";
                var copy = new FlightRec { Callsign = r.Callsign, Dep = r.Dep, Arr = r.Arr };
                copy.Track.AddRange(r.Track);
                r = copy;
            }
            name = Safe(r.Callsign) + "-" + r.Track[0].Utc.ToString("yyyyMMdd-HHmm", Inv) + ".gpx";
            var form = new MultipartFormDataContent();
            form.Add(new StringContent(_s.ChatId.ToString(Inv)), "chat_id");
            form.Add(new StringContent(r.Track.Count + " track points"), "caption");
            form.Add(new ByteArrayContent(new UTF8Encoding(false).GetBytes(BuildGpx(r))), "document", name);
            try {
                using (var resp = await Client.PostAsync(Api("sendDocument"), form)) {
                    if (!resp.IsSuccessStatusCode) return "Telegram wouldn't take the track file (HTTP " + (int)resp.StatusCode + ").";
                }
            } catch (Exception ex) { return "Couldn't send the track: " + Scrub(ex.Message); }
            return null;
        }

        // ------------------------------------------------------ remote tuning and squawk

        private static bool TryParseFreq(string s, out double mhz) {
            mhz = 0;
            if (string.IsNullOrEmpty(s)) return false;
            foreach (char c in s) if (!(char.IsDigit(c) || c == '.' || c == ',')) return false;
            double v;
            if (!double.TryParse(s.Replace(',', '.'), NumberStyles.Float, Inv, out v)) return false;
            if (v >= 118000 && v < 137000) v /= 1000.0;          // 118300 -> 118.300
            if (v < 118.0 || v >= 137.0) return false;
            mhz = Math.Round(v * 1000.0) / 1000.0;
            return true;
        }

        private double ReadCom(int radio) {
            lock (_simLock) { return radio == 1 ? _lastSample.Com1Mhz : _lastSample.Com2Mhz; }
        }

        private string RemoteGate() {
            if (!_s.RemoteControl) return "Remote tuning is switched off ([SimConnect] RemoteControl=false).";
            if (Io == null || !Io.Connected) return "Not connected to the simulator. SimConnect: " + SimState() + ".";
            return null;
        }

        private async Task<string> CmdTune(string[] parts) {
            string gate = RemoteGate(); if (gate != null) return gate;
            if (parts.Length < 2)
                return "Usage:\n/tune 118.300  or  /tune EGLL_TWR  (COM1)\nAdd 2 for COM2:  /tune 121.500 2\n/tune lists the controllers it finds if the name is not exact.";

            int radio = parts.Length > 2 && parts[2] == "2" ? 2 : 1;
            double mhz; string who = "";
            if (!TryParseFreq(parts[1], out mhz)) {
                Dictionary<string, object> feed;
                try { feed = await TgFeed.GetAsync(); }
                catch (Exception ex) { return "Couldn't reach the VATSIM data feed to look that controller up: " + Scrub(ex.Message); }

                string q = parts[1].ToUpperInvariant();
                var found = new List<KeyValuePair<string, double>>();
                var list = MiniJson.Get(feed, "controllers") as List<object>;
                if (list != null)
                    foreach (object o in list) {
                        var c = o as Dictionary<string, object>;
                        string cs = MiniJson.Str(MiniJson.Get(c, "callsign")).ToUpperInvariant();
                        double f;
                        if (cs.Length == 0 || cs.EndsWith("_ATIS") || !TryParseFreq(MiniJson.Str(MiniJson.Get(c, "frequency")), out f)) continue;
                        if (cs == q) { found.Clear(); found.Add(new KeyValuePair<string, double>(cs, f)); break; }
                        if (TgFormat.MatchesStation(cs, q)) found.Add(new KeyValuePair<string, double>(cs, f));
                    }
                if (found.Count == 0) return "No online controller matches \"" + parts[1] + "\". Try /atc to see who is online, or give the frequency (/tune 118.300).";
                if (found.Count > 1) {
                    var sb = new StringBuilder("Several controllers match. Send the exact callsign:\n");
                    for (int i = 0; i < Math.Min(found.Count, 8); i++) sb.Append("/tune ").Append(found[i].Key).Append("   (").Append(found[i].Value.ToString("0.000", Inv)).Append(")\n");
                    return sb.ToString();
                }
                mhz = found[0].Value; who = " (" + found[0].Key + ")";
            }

            uint hz = (uint)Math.Round(mhz * 1000000.0);
            _sim.SendEvent(radio == 1 ? "COM_RADIO_SET_HZ" : "COM2_RADIO_SET_HZ", hz);
            await Task.Delay(2500);
            if (Math.Abs(ReadCom(radio) - mhz) < 0.004) return "\u2705 COM" + radio + " tuned to " + mhz.ToString("0.000", Inv) + who + ".";

            // Some aircraft only accept the standby frequency: set it and swap.
            _sim.SendEvent(radio == 1 ? "COM_STBY_RADIO_SET_HZ" : "COM2_STBY_RADIO_SET_HZ", hz);
            await Task.Delay(700);
            _sim.SendEvent(radio == 1 ? "COM_STBY_RADIO_SWAP" : "COM2_RADIO_SWAP", 0);
            await Task.Delay(2500);
            double now = ReadCom(radio);
            if (Math.Abs(now - mhz) < 0.004) return "\u2705 COM" + radio + " tuned to " + mhz.ToString("0.000", Inv) + who + " (via standby swap).";
            return "The simulator didn't accept that. COM" + radio + " still reads " + now.ToString("0.000", Inv) + ".\n" +
                   "Some aircraft (add-ons, certain glass cockpits) ignore outside tuning. Tune it in the cockpit instead.";
        }

        private string CmdSquawk(string[] parts) {
            string gate = RemoteGate(); if (gate != null) return gate;
            if (parts.Length < 2 || !System.Text.RegularExpressions.Regex.IsMatch(parts[1], "^[0-7]{4}$"))
                return "Usage: /squawk 1234   (four digits, each 0 to 7)";
            _sim.SendEvent("XPNDR_SET", (uint)Convert.ToInt32(parts[1], 16));   // the sim expects the code as BCD
            return "\u2705 Squawk " + parts[1] + " sent to the transponder. Check the aircraft, some ignore outside changes.";
        }

        // ------------------------------------------------------ live data display

        private static string Qnh(double hpa) {
            return Num(hpa) + " hPa / " + (hpa * 0.02953).ToString("0.00", Inv) + " inHg";
        }

        private string CmdSim() {
            SimSample s; bool have;
            lock (_simLock) { s = _lastSample; have = _haveSample; }
            if (!have || (DateTime.UtcNow - s.Utc).TotalSeconds > 15)
                return "No live simulator data. SimConnect: " + SimState() + ".";

            var sb = new StringBuilder("\uD83D\uDEE9 Live aircraft data\n");
            if (_sim != null && _sim.Title.Length > 0) sb.Append(_sim.Title).Append('\n');
            sb.Append("Altitude ").Append(TgFormat.Feet(s.AltFt)).Append("  (").Append(TgFormat.Feet(s.AglFt)).Append(" AGL)\n");
            sb.Append(s.HasExt ? "IAS " + Num(s.IasKt) + " kt  \u00B7  " : "").Append("GS ").Append(Num(s.GsKt)).Append(" kt  \u00B7  VS ").Append(Num(s.VsFpm)).Append(" fpm\n");
            sb.Append("Heading ").Append(TgFormat.Heading(s.HdgTrue)).Append(" true  \u00B7  ").Append(s.OnGround ? "on the ground" : "airborne").Append('\n');
            sb.Append(TgFormat.LatLon(s.Lat, s.Lon));
            if (s.HasExt) {
                sb.Append("\n\nWind ").Append(Num(s.WindDir).PadLeft(3, '0')).Append("\u00B0 at ").Append(Num(s.WindKt)).Append(" kt  \u00B7  OAT ").Append(Num(s.TempC)).Append("\u00B0C");
                if (s.QnhHpa > 800) sb.Append("\nQNH ").Append(Qnh(s.QnhHpa));
                sb.Append("\nFuel ").Append(Fuel(s.FuelLbs));
                if (s.FuelFlowPph > 50) sb.Append("  \u00B7  ").Append(Fuel(s.FuelFlowPph)).Append("/h  \u00B7  ").Append(Span(TimeSpan.FromHours(s.FuelLbs / s.FuelFlowPph))).Append(" left");
                int count = Math.Max(1, Math.Min(4, s.NumEngines)), run = 0;
                for (int i = 0; i < count; i++) if (((s.EnginesRunning >> i) & 1) == 1) run++;
                sb.Append("\nEngines ").Append(run).Append('/').Append(count).Append(" running");
                if (s.GearRetractable) sb.Append("  \u00B7  gear ").Append(s.GearDown ? "down" : "up");
                sb.Append("  \u00B7  flaps ").Append(s.FlapsIndex);
                sb.Append("\nCOM1 ").Append(s.Com1Mhz.ToString("0.000", Inv)).Append("  \u00B7  COM2 ").Append(s.Com2Mhz.ToString("0.000", Inv));
            }
            SimBriefData p = SimBriefPlan.Current;
            if (p != null && !(p.DestLat == 0 && p.DestLon == 0)) {
                double dist = GeoMath.DistanceNm(s.Lat, s.Lon, p.DestLat, p.DestLon);
                double tod = SimBriefPlan.TodDistanceNm(p);
                sb.Append("\n\n").Append(Num(dist)).Append(" nm to ").Append(p.DestIcao).Append("  \u00B7  TOD at ").Append(Num(tod)).Append(" nm");
                if (dist > tod) sb.Append(" (").Append(Num(dist - tod)).Append(" nm to go)");
            } else sb.Append("\n\nTOD alerts need a SimBrief plan: /simbrief YOURNAME");
            return sb.ToString();
        }

        // Used by /where with no argument: your own live position straight from the sim.
        private bool TrySimWhere(out string text, out double lat, out double lon) {
            text = null; lat = 0; lon = 0;
            SimSample s; bool have;
            lock (_simLock) { s = _lastSample; have = _haveSample; }
            if (!have || (DateTime.UtcNow - s.Utc).TotalSeconds > 10) return false;
            lat = s.Lat; lon = s.Lon;
            var sb = new StringBuilder("\uD83D\uDCCD " + TgFormat.LatLon(s.Lat, s.Lon) + "\n");
            sb.Append("Altitude ").Append(TgFormat.Feet(s.AltFt)).Append("  \u00B7  GS ").Append(Num(s.GsKt)).Append(" kt\n");
            sb.Append("Heading ").Append(TgFormat.Heading(s.HdgTrue)).Append(" true  \u00B7  VS ").Append(Num(s.VsFpm)).Append(" fpm\n");
            if (s.HasExt && s.QnhHpa > 800) sb.Append("Wind ").Append(Num(s.WindDir).PadLeft(3, '0')).Append("\u00B0/").Append(Num(s.WindKt)).Append(" kt  \u00B7  QNH ").Append(Qnh(s.QnhHpa)).Append('\n');
            sb.Append(s.OnGround ? "On the ground" : "Airborne").Append("  (live from the simulator)");
            text = sb.ToString();
            return true;
        }

        private string FlightStatus() {
            lock (_simLock) {
                if (!_s.SimEnabled || !_s.FlightLog) return "";
                if (_phase != Phase.Parked && _cur != null) return "\nFlight: " + _phase + " (since " + Clock(_cur.Out != DateTime.MinValue ? _cur.Out : _cur.Off) + ")";
                return "\nFlight: parked";
            }
        }
    }
}
