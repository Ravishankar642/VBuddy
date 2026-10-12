// TelegramControl.Autopilot.cs  -  /alt /spd /hdg, /pushalt /pullalt /pushhdg /pullhdg /pushspd /pullspd,
//                                   /ap /lnav /vnav, driven by per-aircraft profiles (VBuddy.aircraft.ini).
//
// A profile action is one of
//     event:NAME[:DATA]   a SimConnect event (NAME may be #12345 for an add-on's custom event ID), or
//     rpn:CODE            RPN code run through the MobiFlight WASM module (L-vars, H-events, K-events).
// Several steps can be chained with ;;   {v} is replaced by the value sent.
//
// Setting an exact value on a knob (altitude, heading, speed) is closed-loop:
//   1. read the number the aircraft is showing,
//   2. send one click and read again, which also teaches VBuddy how big one click is on this aircraft,
//   3. send the clicks still needed, read again, and correct if it is off,
// so a value is only reported as set once it has been read back from the aircraft.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace VBuddy {

    internal sealed partial class TelegramControlSettings {
        public bool ApControl = true;    // allow the autopilot commands

        internal void LoadAutopilot(IniFile ini) {
            ApControl = RB(ini, "SimConnect", "ApControl", true);
        }
    }

    // One aircraft profile from VBuddy.aircraft.ini.
    internal sealed class ApProfile {
        public string Id = "", Name = "", Verify = "none", Notes = "";
        public string[] Match = new string[0];
        public readonly Dictionary<string, string> Actions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string Get(string key) { string v; return Actions.TryGetValue(key, out v) && v.Length > 0 ? v : null; }
        public bool Matches(string title) {
            if (string.IsNullOrEmpty(title)) return false;
            foreach (string m in Match)
                if (m.Length > 0 && title.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }
    }

    internal static class ApProfiles {

        private static readonly string[] MetaKeys = { "Name", "Match", "Verify", "Notes" };

        private static Dictionary<string, Dictionary<string, string>> Parse(string text) {
            var all = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> cur = null;
            foreach (string raw in text.Replace("\r\n", "\n").Split(new[] { '\n' })) {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#")) continue;
                if (line.StartsWith("[") && line.EndsWith("]")) {
                    cur = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    all[line.Substring(1, line.Length - 2).Trim()] = cur;
                    continue;
                }
                int eq = line.IndexOf('=');
                if (cur == null || eq <= 0) continue;
                cur[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
            return all;
        }

        public static List<ApProfile> Load(string path, Action<string> log) {
            var list = new List<ApProfile>();
            try {
                if (File.Exists(path)) list = FromText(File.ReadAllText(path));
                else log("[Autopilot] VBuddy.aircraft.ini was not found. Using the standard autopilot events only.");
            } catch (Exception ex) { log("[Autopilot] Couldn't read VBuddy.aircraft.ini: " + ex.Message); }
            if (list.Count == 0) list = FromText(Builtin);
            return list;
        }

        public static List<ApProfile> FromText(string text) {
            var all = Parse(text);
            var list = new List<ApProfile>();
            Dictionary<string, string> p;
            string order = all.TryGetValue("Profiles", out p) && p.ContainsKey("List") ? p["List"] : "";
            foreach (string idRaw in order.Split(',')) {
                string id = idRaw.Trim();
                Dictionary<string, string> sec;
                if (id.Length == 0 || !all.TryGetValue(id, out sec)) continue;
                var pr = new ApProfile { Id = id };
                string v;
                pr.Name = sec.TryGetValue("Name", out v) && v.Length > 0 ? v : id;
                if (sec.TryGetValue("Verify", out v)) pr.Verify = v.ToLowerInvariant();
                if (sec.TryGetValue("Notes", out v)) pr.Notes = v;
                if (sec.TryGetValue("Match", out v)) pr.Match = v.Split(',').Select(w => w.Trim()).Where(w => w.Length > 0).ToArray();
                foreach (var kv in sec)
                    if (Array.IndexOf(MetaKeys, kv.Key) < 0 && kv.Value.Length > 0) pr.Actions[kv.Key] = kv.Value;
                list.Add(pr);
            }
            return list;
        }

        // Used only if the profile file is missing or empty.
        public const string Builtin =
            "[Profiles]\nList=STANDARD\n[STANDARD]\nName=Standard autopilot events\nMatch=\nVerify=standard\n" +
            "Alt=event:AP_ALT_VAR_SET_ENGLISH:{v}\nSpd=event:AP_SPD_VAR_SET:{v}\nHdg=event:HEADING_BUG_SET:{v}\n" +
            "ApOn=event:AUTOPILOT_ON\nApOff=event:AUTOPILOT_OFF\nLnavOn=event:AP_NAV1_HOLD_ON\nLnavOff=event:AP_NAV1_HOLD_OFF\n";
    }

    internal sealed partial class TelegramControl {

        private List<ApProfile> _profiles = new List<ApProfile>();
        private readonly SemaphoreSlim _apLock = new SemaphoreSlim(1, 1);
        private readonly Dictionary<string, int> _readerPick = new Dictionary<string, int>();
        private readonly HashSet<string> _stateTrusted = new HashSet<string>();
        private int _wasmSeq;
        internal static double DelayScale = 1.0;                     // tests shrink the waits
        private static Task Pause(int ms) { return Task.Delay(Math.Max(1, (int)(ms * DelayScale))); }
        private ISimIo _testIo;                                     // tests substitute a fake simulator
        private ISimIo Io { get { return _testIo ?? (ISimIo)_sim; } }

        private void LoadProfiles() {
            _profiles = ApProfiles.Load(Path.Combine(_dir, "VBuddy.aircraft.ini"), _log);
        }

        private ApProfile CurrentProfile() { return ProfileFor(Io != null ? Io.Title : ""); }

        private ApProfile ProfileFor(string title) {
            ApProfile fallback = null;
            foreach (ApProfile p in _profiles) {
                if (p.Match.Length == 0) { if (fallback == null) fallback = p; continue; }
                if (p.Matches(title)) return p;
            }
            return fallback;
        }

        private string ApGate() {
            if (!_s.ApControl) return "Autopilot commands are switched off ([SimConnect] ApControl=false, or the button in /settings).";
            return RemoteGate();
        }

        private static readonly string NoMapping =
            "This aircraft has no mapping for that in VBuddy.aircraft.ini (codes for most add-ons are on MobiFlight's HubHop).";

        // ===================================================================== sending actions

        // Runs one action once. Returns null on success or the reason it couldn't be sent.
        private string RunOne(string action, double value) {
            if (action == null) return "no action";
            action = action.Trim();
            int colon = action.IndexOf(':');
            if (colon < 0) return "malformed action \"" + action + "\"";
            string kind = action.Substring(0, colon).Trim().ToLowerInvariant();
            string rest = action.Substring(colon + 1).Trim();
            string v = ((long)Math.Round(value)).ToString(CultureInfo.InvariantCulture);

            if (kind == "event") {
                string name = rest, data = "{v}";
                int c2 = rest.IndexOf(':');
                if (c2 > 0) { name = rest.Substring(0, c2).Trim(); data = rest.Substring(c2 + 1).Trim(); }
                data = data.Replace("{v}", v);
                long d;
                if (!long.TryParse(data, NumberStyles.Integer, CultureInfo.InvariantCulture, out d) || d < 0 || d > uint.MaxValue) d = 0;
                return Io.SendEvent(name, (uint)d) ? null : "the simulator isn't connected";
            }
            if (kind == "rpn") return SendRpn(rest.Replace("{v}", v));
            return "unknown action type \"" + kind + "\"";
        }

        // MobiFlight ignores a command identical to the previous one, so every command carries a changing number.
        private string SendRpn(string code) {
            string cmd = "MF.SimVars.Set." + code + " " + Interlocked.Increment(ref _wasmSeq) + " (>L:VBUDDY_SEQ)";
            if (cmd.Length > 900) return "the command is too long for the MobiFlight module";
            return Io.SendWasm(cmd) ? null : "the simulator isn't connected";
        }

        // Runs "a ;; b ;; c" with a short pause between the steps.
        private async Task<string> RunSequence(string action, double value) {
            string[] steps = action.Split(new[] { ";;" }, StringSplitOptions.RemoveEmptyEntries);
            string lastKind = "rpn:";
            for (int i = 0; i < steps.Length; i++) {
                string step = steps[i].Trim();
                // A step without its own prefix uses the same type as the step before it.
                if (step.StartsWith("rpn:", StringComparison.OrdinalIgnoreCase)) lastKind = "rpn:";
                else if (step.StartsWith("event:", StringComparison.OrdinalIgnoreCase)) lastKind = "event:";
                else step = lastKind + step;
                string fail = RunOne(step, value);
                if (fail != null) return fail;
                if (i < steps.Length - 1) await Pause(90);
            }
            return null;
        }

        // Sends 'n' clicks of a knob action, grouping them into as few commands as the module allows.
        private async Task<string> SendClicks(string action, int n, int burst, int delayMs) {
            action = action.Trim();
            if (action.StartsWith("rpn:", StringComparison.OrdinalIgnoreCase)) {
                string body = action.Substring(4).Trim();
                int perCmd = Math.Max(1, Math.Min(burst, 800 / (body.Length + 1)));
                while (n > 0) {
                    int b = Math.Min(n, perCmd);
                    string fail = SendRpn(string.Join(" ", Enumerable.Repeat(body, b).ToArray()));
                    if (fail != null) return fail;
                    n -= b;
                    if (n > 0) await Pause(delayMs);
                }
                return null;
            }
            for (int i = 0; i < n; i++) {
                string fail = RunOne(action, 0);
                if (fail != null) return fail;
                if ((i + 1) % Math.Max(1, burst) == 0) await Pause(delayMs);
            }
            return null;
        }

        // ===================================================================== reading values back

        private struct Reader { public string Datum, Units; }

        private static List<Reader> StandardReaders(string knob) {
            var l = new List<Reader>();
            if (knob == "alt") {
                l.Add(new Reader { Datum = "AUTOPILOT ALTITUDE LOCK VAR:3", Units = "feet" });
                l.Add(new Reader { Datum = "AUTOPILOT ALTITUDE LOCK VAR:1", Units = "feet" });
            } else if (knob == "hdg") l.Add(new Reader { Datum = "AUTOPILOT HEADING LOCK DIR:1", Units = "degrees" });
            else if (knob == "spd") l.Add(new Reader { Datum = "AUTOPILOT AIRSPEED HOLD VAR:1", Units = "knots" });
            else l.Add(new Reader { Datum = "AUTOPILOT MASTER", Units = "bool" });
            return l;
        }

        // spec: null/"standard" = the simulator's own autopilot variables, "none", or a list like
        //       lvar:N_FCU_ALTITUDE | simvar:AUTOPILOT ALTITUDE LOCK VAR:3,feet | standard
        private static List<Reader> ParseReaders(string spec, string knob) {
            if (spec == null) return StandardReaders(knob);
            var list = new List<Reader>();
            foreach (string raw in spec.Split('|')) {
                string it = raw.Trim();
                if (it.Length == 0 || it.Equals("none", StringComparison.OrdinalIgnoreCase)) continue;
                if (it.Equals("standard", StringComparison.OrdinalIgnoreCase)) { list.AddRange(StandardReaders(knob)); continue; }
                int c = it.IndexOf(':');
                if (c < 0) continue;
                string kind = it.Substring(0, c).Trim().ToLowerInvariant(), rest = it.Substring(c + 1).Trim();
                if (kind == "lvar") list.Add(new Reader { Datum = "L:" + rest, Units = "number" });
                else if (kind == "simvar") {
                    int comma = rest.LastIndexOf(',');
                    if (comma > 0) list.Add(new Reader { Datum = rest.Substring(0, comma).Trim(), Units = rest.Substring(comma + 1).Trim() });
                }
            }
            return list;
        }

        private async Task<double?[]> ReadAll(List<Reader> readers) {
            var tasks = readers.Select(r => Io.ReadAsync(r.Datum, r.Units, 1500)).ToArray();
            return await Task.WhenAll(tasks);
        }

        private static double? FirstValue(double?[] v) { foreach (double? x in v) if (x != null) return x; return null; }

        // ===================================================================== knobs: alt / hdg / spd

        private sealed class Knob {
            public string Id, Label, Unit, Inc, Dec, CoarseOn, CoarseOff;
            public double Step, Tol, CoarseStep, Min, Max;
            public int Burst, DelayMs;
            public bool Wrap;
            public List<Reader> Readers;
        }

        private static double Num(ApProfile p, string key, double def) {
            string v = p.Get(key); double d;
            return v != null && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : def;
        }

        private Knob KnobFor(ApProfile p, string id) {
            string k = id == "alt" ? "Alt" : id == "hdg" ? "Hdg" : "Spd";
            var kn = new Knob {
                Id = id, Label = id == "alt" ? "Altitude" : id == "hdg" ? "Heading" : "Speed",
                Unit = id == "alt" ? " ft" : id == "hdg" ? "\u00B0" : " kt",
                Inc = p.Get(k + "Inc"), Dec = p.Get(k + "Dec"),
                CoarseOn = p.Get(k + "CoarseOn"), CoarseOff = p.Get(k + "CoarseOff"),
                Step = Num(p, k + "Step", id == "alt" ? 100 : 1),
                CoarseStep = Num(p, k + "CoarseStep", 1000),
                Burst = (int)Num(p, k + "Burst", 1), DelayMs = (int)Num(p, k + "Delay", 60),
                Wrap = id == "hdg", Min = id == "alt" ? 0 : id == "hdg" ? 0 : 80, Max = id == "alt" ? 50000 : id == "hdg" ? 360 : 400
            };
            kn.Tol = Num(p, k + "Tol", kn.Step / 2);
            kn.Readers = ParseReaders(p.Get(k + "Read"), id);
            return kn;
        }

        private static double Delta(double from, double to, bool wrap) {
            double d = to - from;
            if (wrap) d = ((d % 360.0) + 540.0) % 360.0 - 180.0;
            return d;
        }

        private static bool ParseAlt(string s, out int ft, out string err) {
            ft = 0; err = null;
            var m = Regex.Match(s.Trim(), @"^(fl)?\s*(\d{1,5})$", RegexOptions.IgnoreCase);
            if (!m.Success) { err = "Use feet or a flight level:  /alt 35000   or   /alt FL350   (or a change:  /alt +1000)"; return false; }
            int n = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            if (m.Groups[1].Success) n *= 100;
            else if (n < 1000 && n != 0) { err = "Did you mean FL" + n.ToString("000") + "?  Send  /alt FL" + n.ToString("000") + "  or  /alt " + (n * 100); return false; }
            n = (int)(Math.Round(n / 100.0) * 100);
            if (n < 0 || n > 50000) { err = "Altitude must be between 0 and 50,000 ft."; return false; }
            ft = n; return true;
        }

        private async Task<string> CmdApSet(string what, string[] parts) {
            string gate = ApGate(); if (gate != null) return gate;
            if (parts.Length < 2) return "Usage:\n/alt 35000 | FL350 | +1000 | -500\n/spd 280 | +10 | -10   (knots)\n/hdg 270 | +10 | -20";
            if (!await _apLock.WaitAsync(0)) return "Still busy with the previous autopilot command. Try again in a moment.";
            try {
                ApProfile p = CurrentProfile();
                if (p == null) return "No aircraft profile is loaded.";
                Knob k = KnobFor(p, what);

                // ---- what was asked for: an exact value or a change
                string arg = parts[1].Trim();
                bool relative = Regex.IsMatch(arg, @"^[+-]\d+$");
                double target = 0, change = 0; string err;
                if (relative) change = double.Parse(arg, CultureInfo.InvariantCulture);
                else if (what == "alt") { int ft; if (!ParseAlt(arg, out ft, out err)) return err; target = ft; }
                else {
                    int n;
                    if (!int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return "Enter a number.";
                    if (what == "hdg") { if (n < 0 || n > 360) return "Heading must be 1 to 360."; target = n == 0 ? 360 : n; }
                    else { if (n < 80 || n > 400) return "Speed must be 80 to 400 knots. (Mach isn't supported yet.)"; target = n; }
                }

                bool hasKnob = k.Inc != null && k.Dec != null;
                string absolute = p.Get(what == "alt" ? "Alt" : what == "hdg" ? "Hdg" : "Spd");
                if (!hasKnob && absolute == null) return k.Label + " can't be set on " + p.Name + ". " + NoMapping;
                return hasKnob ? await SetByKnob(p, k, target, relative, change) : await SetAbsolute(p, k, absolute, target, relative, change);
            } finally { _apLock.Release(); }
        }

        // ---- aircraft that accept "set to X" directly (the simulator's standard autopilot)
        private async Task<string> SetAbsolute(ApProfile p, Knob k, string action, double target, bool relative, double change) {
            if (relative) {
                double? cur = FirstValue(await ReadAll(k.Readers));
                if (cur == null) return "I can't read the current " + k.Label.ToLowerInvariant() + " to apply a change to. Send the exact value instead.";
                target = k.Wrap ? ((cur.Value + change) % 360 + 360) % 360 : cur.Value + change;
                if (k.Wrap && target == 0) target = 360;
                if (k.Id == "alt") target = Math.Round(target / 100.0) * 100;
            }
            string fail = RunOne(action, target);
            if (fail != null) return "Couldn't send that: " + fail + ".";
            string shown = ((long)Math.Round(target)).ToString("N0", Inv) + k.Unit;
            await Pause(1800);
            double? now = FirstValue(await ReadAll(k.Readers));
            if (now == null) return "\u2705 " + k.Label + " " + shown + " sent. I couldn't read it back to confirm.";
            if (Math.Abs(Delta(now.Value, target, k.Wrap)) <= Math.Max(k.Tol, k.Id == "alt" ? 60 : 1.5)) return "\u2705 " + k.Label + " set to " + shown + ".";
            return "Sent, but the aircraft still shows " + ((long)Math.Round(now.Value)).ToString("N0", Inv) + k.Unit + ". This aircraft may ignore outside " +
                   k.Label.ToLowerInvariant() + " changes. Set it in the cockpit, or map it in VBuddy.aircraft.ini.";
        }

        // ---- aircraft with click-type knobs (Fenix, PMDG, iniBuilds, iFly...)
        private async Task<string> SetByKnob(ApProfile p, Knob k, double target, bool relative, double change) {
            string key = p.Id + "/" + k.Id;
            string label = k.Label;

            // A change ("+1000") needs no reading: just the right number of clicks.
            if (relative) {
                int n = (int)Math.Round(Math.Abs(change) / k.Step);
                if (n < 1) return "That change is smaller than one click (" + k.Step + k.Unit + ").";
                if (n > 700) return "That's too many clicks in one go. Try a smaller change.";
                string fail = await SendClicks(change > 0 ? k.Inc : k.Dec, n, k.Burst, k.DelayMs);
                if (fail != null) return "Couldn't send that: " + fail + ".";
                string tail = "";
                int idx;
                if (_readerPick.TryGetValue(key, out idx) && idx < k.Readers.Count) {
                    await Pause(700);
                    double? v = await Io.ReadAsync(k.Readers[idx].Datum, k.Readers[idx].Units, 1500);
                    if (v != null) tail = " It now shows " + ((long)Math.Round(v.Value)).ToString("N0", Inv) + k.Unit + ".";
                }
                return "\u2705 " + label + " " + (change > 0 ? "+" : "-") + Math.Abs((long)change).ToString("N0", Inv) + k.Unit + " sent (" + n + " clicks)." + tail;
            }

            if (k.Readers.Count == 0)
                return "I can't read " + p.Name + "'s " + label.ToLowerInvariant() + " display, so I can't set an exact value. Send a change instead, for example  /" + (k.Id) + " +" + (k.Id == "alt" ? "1000" : "10") + ".";

            double?[] v0 = await ReadAll(k.Readers);
            int chosen; if (!_readerPick.TryGetValue(key, out chosen)) chosen = -1;
            double? cur = (chosen >= 0 && chosen < v0.Length && v0[chosen] != null) ? v0[chosen] : FirstValue(v0);
            if (cur == null)
                return "I couldn't read " + p.Name + "'s " + label.ToLowerInvariant() + " display. Is the aircraft powered up and the MobiFlight module installed? " +
                       "You can still send a change:  /" + k.Id + " +" + (k.Id == "alt" ? "1000" : "10") + ".";
            if (k.Id == "spd" && cur.Value < 10) return "The FCU is showing Mach (" + cur.Value.ToString("0.00", Inv) + "). Switch it to IAS first.";

            double perClick = 0;       // how much the displayed number changes per INC click
            bool coarse = false;
            double start = cur.Value;

            for (int round = 0; round < 10; round++) {
                double d = Delta(cur.Value, target, k.Wrap);
                if (Math.Abs(d) <= k.Tol) break;

                bool wantCoarse = k.CoarseOn != null && k.CoarseOff != null && Math.Abs(d) >= k.CoarseStep * 2.5;
                if (wantCoarse != coarse) {
                    string f = await RunSequence(wantCoarse ? k.CoarseOn : k.CoarseOff, 0);
                    if (f != null) return "Couldn't send that: " + f + ".";
                    coarse = wantCoarse; perClick = 0;
                    await Pause(450);
                    double?[] vv = await ReadAll(k.Readers);
                    double? nv0 = (chosen >= 0 && vv[chosen] != null) ? vv[chosen] : FirstValue(vv);
                    if (nv0 == null) return "I lost the " + label.ToLowerInvariant() + " reading. Check the aircraft.";
                    cur = nv0;
                    continue;
                }

                if (perClick == 0) {
                    // One click, then see which reading moved and by how much.
                    bool inc = d > 0;
                    double?[] before = await ReadAll(k.Readers);
                    string f = await SendClicks(inc ? k.Inc : k.Dec, 1, 1, 0);
                    if (f != null) return "Couldn't send that: " + f + ".";
                    await Pause(750);
                    double?[] after = await ReadAll(k.Readers);

                    int best = -1; double moved = 0;
                    for (int i = 0; i < after.Length; i++) {
                        if (before[i] == null || after[i] == null) continue;
                        double o = Delta(before[i].Value, after[i].Value, k.Wrap);
                        if (Math.Abs(o) >= k.Step * 0.4 && (best < 0 || i == chosen)) { best = i; moved = o; }
                    }
                    if (best < 0)
                        return "I clicked the " + label.ToLowerInvariant() + " knob once but nothing I can read changed. Either " + p.Name +
                               " ignored it (is the MobiFlight module installed? is the aircraft powered?) or its display can't be read back. " +
                               "Try  /" + k.Id + " +" + (k.Id == "alt" ? "1000" : "10") + "  and check the panel.";
                    chosen = best; _readerPick[key] = best;
                    perClick = inc ? moved : -moved;
                    cur = after[best];
                    continue;
                }

                int clicks = (int)Math.Round(Math.Abs(d) / Math.Abs(perClick));
                if (clicks < 1) break;
                clicks = Math.Min(clicks, 700);
                bool useInc = (d > 0) == (perClick > 0);
                string f2 = await SendClicks(useInc ? k.Inc : k.Dec, clicks, k.Burst, k.DelayMs);
                if (f2 != null) return "Couldn't send that: " + f2 + ".";
                await Pause(500 + Math.Min(2500, clicks * 6));
                double? nv = await Io.ReadAsync(k.Readers[chosen].Datum, k.Readers[chosen].Units, 1500);
                if (nv == null) return "I lost the " + label.ToLowerInvariant() + " reading. Check the aircraft.";
                if (Math.Abs(Delta(cur.Value, nv.Value, k.Wrap)) < Math.Abs(perClick) * 0.25)
                    return label + " stopped responding at " + ((long)Math.Round(nv.Value)).ToString("N0", Inv) + k.Unit + ". Check the aircraft.";
                cur = nv;
            }

            if (coarse && k.CoarseOff != null) await RunSequence(k.CoarseOff, 0);   // leave the knob on its fine setting

            double left = Delta(cur.Value, target, k.Wrap);
            string shown = ((long)Math.Round(target)).ToString("N0", Inv) + k.Unit;
            string now = ((long)Math.Round(cur.Value)).ToString("N0", Inv) + k.Unit;
            if (Math.Abs(left) <= k.Tol) return "\u2705 " + label + " set to " + shown + (Math.Abs(Delta(start, target, k.Wrap)) <= k.Tol ? " (it already was)." : " (read back from the aircraft).");
            return label + " is now " + now + ", " + ((long)Math.Round(Math.Abs(left))).ToString("N0", Inv) + k.Unit + " " + (left > 0 ? "short of" : "past") + " the " + shown +
                   " you asked for. The knob steps in " + ((long)Math.Round(Math.Abs(perClick))).ToString("N0", Inv) + k.Unit + ", so send a change to finish.";
        }

        // ===================================================================== push / pull (Airbus managed / selected)

        private async Task<string> CmdPushPull(string what, bool pull) {
            string gate = ApGate(); if (gate != null) return gate;
            if (!await _apLock.WaitAsync(0)) return "Still busy with the previous autopilot command. Try again in a moment.";
            try {
                ApProfile p = CurrentProfile();
                if (p == null) return "No aircraft profile is loaded.";
                string k = what == "alt" ? "Alt" : what == "hdg" ? "Hdg" : "Spd";
                string action = p.Get((pull ? "Pull" : "Push") + k);
                if (action == null)
                    return "/" + (pull ? "pull" : "push") + what + " is for Airbus FCUs (managed / selected). " + p.Name + " has no push/pull knob mapped. " +
                           "Boeing aircraft use /lnav /vnav for this.";
                string fail = await RunSequence(action, 0);
                if (fail != null) return "Couldn't send that: " + fail + ".";
                string text = what == "alt" ? (pull ? "Altitude knob pulled: open climb / descent (selected)." : "Altitude knob pushed: managed climb / descent.")
                            : what == "hdg" ? (pull ? "Heading knob pulled: selected heading (HDG / TRK)." : "Heading knob pushed: managed lateral guidance (NAV).")
                            : (pull ? "Speed knob pulled: selected speed." : "Speed knob pushed: managed speed.");
                return "\u2705 " + text + "\nI can't read the FMA, so check it on the PFD.";
            } finally { _apLock.Release(); }
        }

        // ===================================================================== /ap /lnav /vnav

        private async Task<string> CmdApMode(string what, string[] parts) {
            string gate = ApGate(); if (gate != null) return gate;
            ApProfile p = CurrentProfile();
            if (p == null) return "No aircraft profile is loaded.";
            string arg = parts.Length > 1 ? parts[1].ToLowerInvariant() : "";
            if (what == "ap" && arg.Length == 0) return await ApStatus(p);
            bool? on = arg == "on" || arg == "engage" || arg == "1" ? true : arg == "off" || arg == "disengage" || arg == "0" ? (bool?)false : null;
            if (on == null) return "Usage:  /" + what + " on   or   /" + what + " off";
            if (!await _apLock.WaitAsync(0)) return "Still busy with the previous autopilot command. Try again in a moment.";
            try {
                string prefix = what == "ap" ? "Ap" : what == "lnav" ? "Lnav" : "Vnav";
                string label = what == "ap" ? "Autopilot" : what.ToUpperInvariant();
                string direct = p.Get(prefix + (on.Value ? "On" : "Off"));
                string toggle = p.Get(prefix + "Toggle");
                if (direct == null && toggle == null) return label + " can't be switched " + (on.Value ? "on" : "off") + " on " + p.Name + ". " + NoMapping;

                List<Reader> readers = ParseReaders(p.Get(prefix + "State") ?? (what == "ap" && p.Verify == "standard" ? "standard" : "none"), what == "ap" ? "ap" : what);
                string trustKey = p.Id + "/" + prefix;
                bool trusted = p.Verify == "standard" || _stateTrusted.Contains(trustKey);

                double? s0 = readers.Count > 0 ? FirstValue(await ReadAll(readers)) : null;
                if (s0 != null && trusted && (s0.Value > 0.5) == on.Value) return "\u2705 " + label + " is already " + (on.Value ? "on" : "off") + ".";

                string action = direct ?? toggle;
                string fail = await RunSequence(action, 0);
                if (fail != null) return "Couldn't send that: " + fail + ".";

                if (readers.Count == 0 || s0 == null) {
                    return direct != null
                        ? "\u2705 " + label + " " + (on.Value ? "ON" : "OFF") + " sent to the " + p.Name + ". I can't read its state, so check the panel."
                        : "\u2705 " + label + " button pressed on the " + p.Name + ". It is a toggle and I can't read its state, so check that it is " +
                          (on.Value ? "ON" : "OFF") + " (send it again if not).";
                }

                await Pause(1400);
                double? s1 = FirstValue(await ReadAll(readers));
                if (s1 == null) return "\u2705 " + label + " " + (on.Value ? "ON" : "OFF") + " sent. I couldn't read it back to confirm.";
                if ((s1.Value > 0.5) != (s0.Value > 0.5)) _stateTrusted.Add(trustKey);   // the reading follows the aircraft
                if ((s1.Value > 0.5) == on.Value && (trusted || (s1.Value > 0.5) != (s0.Value > 0.5))) return "\u2705 " + label + " is " + (on.Value ? "on" : "off") + ".";
                return "Sent, but " + label.ToLowerInvariant() + " still reads " + (s1.Value > 0.5 ? "on" : "off") + ". " + p.Name + " may ignore outside commands for this. Check the panel.";
            } finally { _apLock.Release(); }
        }

        private async Task<string> ApStatus(ApProfile p) {
            var sb = new StringBuilder("\uD83E\uDDED Autopilot\n");
            string title = Io != null ? Io.Title : "";
            sb.Append("Aircraft: ").Append(title.Length > 0 ? title : "unknown").Append("\nProfile: ").Append(p.Name).Append('\n');

            var parts = new List<string>();
            foreach (string id in new[] { "alt", "hdg", "spd" }) {
                Knob k = KnobFor(p, id);
                if (k.Readers.Count == 0) continue;
                int idx; double?[] v = await ReadAll(k.Readers);
                double? val = (_readerPick.TryGetValue(p.Id + "/" + id, out idx) && idx < v.Length && v[idx] != null) ? v[idx] : FirstValue(v);
                if (val != null && (val.Value != 0 || p.Verify == "standard"))
                    parts.Add(id.ToUpperInvariant() + " " + ((long)Math.Round(val.Value)).ToString("N0", Inv) + (id == "hdg" ? "\u00B0" : id == "spd" ? " kt" : ""));
            }
            if (parts.Count > 0) sb.Append(string.Join("  \u00B7  ", parts.ToArray())).Append('\n');
            if (p.Verify == "standard" && Io != null && Io.Ap != null) sb.Append("AP ").Append(Io.Ap.Master ? "ON" : "OFF").Append('\n');

            var can = new List<string>();
            foreach (string id in new[] { "Alt", "Hdg", "Spd" })
                if ((p.Get(id + "Inc") != null && p.Get(id + "Dec") != null) || p.Get(id) != null) can.Add(id.ToLowerInvariant());
            sb.Append("Set: ").Append(can.Count > 0 ? string.Join(" ", can.ToArray()) : "nothing mapped yet").Append('\n');
            var pp = new List<string>();
            foreach (string id in new[] { "Alt", "Hdg", "Spd" }) if (p.Get("Pull" + id) != null) pp.Add(id.ToLowerInvariant());
            if (pp.Count > 0) sb.Append("Push/pull: ").Append(string.Join(" ", pp.ToArray())).Append('\n');
            var md = new List<string>();
            foreach (string id in new[] { "Ap", "Lnav", "Vnav" })
                if (p.Get(id + "On") != null || p.Get(id + "Toggle") != null) md.Add(id.ToLowerInvariant());
            if (md.Count > 0) sb.Append("On/off: ").Append(string.Join(" ", md.ToArray())).Append('\n');
            if (p.Notes.Length > 0) sb.Append(p.Notes);
            return sb.ToString().TrimEnd();
        }
    }
}
