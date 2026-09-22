using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace VBuddy.Atc {

    // ATC Ahead: while connected, polls the public VATSIM data feed, works out which
    // online controllers' (estimated) areas lie ahead of the aircraft along the filed
    // route, and sends one notification through the plugin's existing notifier.
    //
    // Everything runs on the thread pool (never on vPilot's UI thread) and every cycle
    // is wrapped in try/catch, so a failure here can not affect the other relays.
    internal sealed class AtcAheadService {

        private const double MinGroundSpeedKt = 80;   // ignore taxiing / parked aircraft
        private const double MaxOffRouteNm = 150;     // further off the filed route => fall back to great-circle
        private const double SampleStepNm = 5;

        private readonly AtcAheadSettings _s;
        private readonly BoundaryStore _data;
        private readonly IVatsimFeed _feed;
        private readonly Action<string> _log;
        private readonly Func<string, string, int, string, Task> _send;   // (title, body, priority, sound)
        private FixDatabase _fixes;
        private readonly string _fixesPath;

        private readonly object _lock = new object();
        private readonly object _stateLock = new object();   // guards _notified / _loggedOnce
        private CancellationTokenSource _cts;
        private string _callsign;

        private readonly Dictionary<string, DateTime> _notified = new Dictionary<string, DateTime>();
        private readonly HashSet<string> _loggedOnce = new HashSet<string>();

        // Flight plan supplied by vPilot (fallback if the data feed has none yet).
        private string _sdkDep, _sdkArr, _sdkRoute;
        private string _planKey;
        private RoutePlan _plan;

        public AtcAheadService(AtcAheadSettings settings, string pluginDir, Action<string> log,
                               Func<string, string, int, string, Task> send)
            : this(settings, new BoundaryStore(Path.Combine(pluginDir, "VBuddy-data"), settings.BoundaryRefreshDays, log),
                   new VatsimFeedClient(), log, send,
                   string.IsNullOrEmpty(settings.FixesFile) ? null :
                       (Path.IsPathRooted(settings.FixesFile) ? settings.FixesFile : Path.Combine(pluginDir, settings.FixesFile))) {
        }

        internal AtcAheadService(AtcAheadSettings settings, BoundaryStore data, IVatsimFeed feed,
                                 Action<string> log, Func<string, string, int, string, Task> send, string fixesPath) {
            _s = settings;
            _data = data;
            _feed = feed;
            _log = log ?? (_ => { });
            _send = send;
            _fixesPath = fixesPath;
        }

        // ----- lifecycle ------------------------------------------------------------

        public void Start(string callsign) {
            lock (_lock) {
                StopInternal();
                _callsign = callsign;
                lock (_stateLock) { _notified.Clear(); _loggedOnce.Clear(); }
                _plan = null; _planKey = null;
                _cts = new CancellationTokenSource();
                CancellationToken ct = _cts.Token;
                Task.Run(() => RunAsync(ct));
            }
        }

        public void Stop() {
            lock (_lock) { StopInternal(); }
        }

        private void StopInternal() {
            if (_cts != null) {
                _cts.Cancel();
                _cts.Dispose();
                _cts = null;
                _log("[ATC Ahead] Stopped");
            }
        }

        public void SetFlightPlan(string dep, string arr, string route) {
            lock (_lock) { _sdkDep = dep; _sdkArr = arr; _sdkRoute = route; }
        }

        // Sends a sample alert through the normal notification path.
        public Task SendTestAsync() {
            string title, body;
            BuildMessage("Test Control", "TEST_CTR", "123.45", 150, "test notification - no real controller", true, out title, out body);
            _log("[ATC Ahead] Sending test notification");
            return _send(title, body, _s.Priority, _s.Sound);
        }

        // ----- main loop ------------------------------------------------------------

        private async Task RunAsync(CancellationToken ct) {
            _log($"[ATC Ahead] Started for {_callsign}: alert at {_s.DistanceNm:0} NM, polling every {_s.PollIntervalSeconds}s");

            if (_fixesPath != null && _fixes == null) {
                try {
                    _fixes = FixDatabase.Load(_fixesPath);
                    _log($"[ATC Ahead] Loaded {_fixes.Count} waypoints from {Path.GetFileName(_fixesPath)}");
                } catch (Exception ex) {
                    _log($"[ATC Ahead] Could not read FixesFile ({ex.Message}); named waypoints will not be resolved");
                }
            }

            if (_s.TestOnConnect) {
                try { await SendTestAsync().ConfigureAwait(false); } catch (Exception ex) { _log($"[ATC Ahead] Test notification failed: {ex.Message}"); }
            }

            int failures = 0;
            while (!ct.IsCancellationRequested) {
                int delay = _s.PollIntervalSeconds;
                try {
                    if (!_data.IsLoaded) {
                        if (!await _data.EnsureLoadedAsync(ct).ConfigureAwait(false)) delay = Math.Max(delay, 60);
                    }
                    if (_data.IsLoaded) {
                        await RunCycleAsync(ct).ConfigureAwait(false);
                        if (failures > 0) _log("[ATC Ahead] VATSIM data feed is reachable again");
                        failures = 0;
                    }
                } catch (OperationCanceledException) {
                    break;
                } catch (Exception ex) {
                    failures++;
                    if (failures == 1 || failures % 10 == 0)
                        _log($"[ATC Ahead] Poll failed ({failures}x), backing off: {ex.GetBaseException().Message}");
                    delay = Math.Min(600, _s.PollIntervalSeconds * (1 << Math.Min(failures, 4)));
                }

                try { await Task.Delay(TimeSpan.FromSeconds(delay), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }

        // One evaluation pass. Internal so it can be driven directly by tests.
        internal async Task RunCycleAsync(CancellationToken ct) {
            FeedResult res = await _feed.FetchAsync(_callsign, _s.Cid, ct).ConfigureAwait(false);
            if (res.NotModified) return;

            FeedPilot me = res.Me;
            if (me == null || double.IsNaN(me.Lat) || double.IsNaN(me.Lon)) {
                LogOnce("nopilot", $"[ATC Ahead] {_callsign} not found in the VATSIM data feed yet (it can lag ~1-2 minutes after connecting)");
                return;
            }
            lock (_stateLock) { _loggedOnce.Remove("nopilot"); }

            if (!double.IsNaN(me.GroundSpeed) && me.GroundSpeed < MinGroundSpeedKt) {
                Verbose($"[ATC Ahead] Groundspeed {me.GroundSpeed:0} kt - waiting for departure");
                return;
            }

            var pos = new GeoPoint(me.Lat, me.Lon);
            List<ForwardPath> paths = BuildPaths(me, pos);
            if (paths.Count == 0) {
                LogOnce("nopath", "[ATC Ahead] No flight plan and no heading available - cannot determine what is ahead");
                return;
            }

            var candidates = new List<Candidate>();
            int evaluated = 0;
            foreach (FeedController c in res.Controllers) {
                if (!_s.Facilities.Contains(c.Facility)) continue;
                if (string.IsNullOrEmpty(c.Callsign) || c.Callsign.EndsWith("_ATIS", StringComparison.OrdinalIgnoreCase)) continue;
                if (c.Frequency == "199.998") continue;

                Coverage cov = _data.Resolve(c.Callsign, c.Facility, c.VisualRange);
                if (cov == null) {
                    LogOnce("unres:" + c.Callsign, $"[ATC Ahead] {c.Callsign}: no matching FIR/airport in the airspace data - ignored");
                    continue;
                }
                evaluated++;
                if (cov.Contains(pos)) continue;   // already inside their area, not "ahead"

                double best = double.MaxValue;
                ForwardPath bestPath = null;
                foreach (ForwardPath path in paths) {
                    double entry;
                    if (path.TryFindEntry(cov, _s.DistanceNm, SampleStepNm, out entry) && entry < best) {
                        best = entry; bestPath = path;
                    }
                }
                if (bestPath == null || best < _s.MinDistanceNm) continue;
                candidates.Add(new Candidate { Controller = c, Coverage = cov, DistanceNm = best, Path = bestPath });
            }

            candidates.Sort((a, b) => a.DistanceNm.CompareTo(b.DistanceNm));
            Verbose($"[ATC Ahead] Poll: {evaluated} controller(s) evaluated, {candidates.Count} within {_s.DistanceNm:0} NM ahead" +
                    (candidates.Count > 0 ? $", nearest {candidates[0].Controller.Callsign} ~{candidates[0].DistanceNm:0} NM" : ""));

            // At most one alert per cycle: the nearest controller that hasn't been announced.
            foreach (Candidate cand in candidates) {
                string key = cand.Controller.Callsign + "|" + (cand.Controller.LogonTime ?? "");
                if (!ShouldNotify(key)) continue;

                string title, body;
                BuildMessage(cand.Coverage.Label, cand.Controller.Callsign, FormatFrequency(cand.Controller.Frequency),
                             cand.DistanceNm, cand.Path.Description + "; " + cand.Coverage.Basis, false, out title, out body);
                lock (_stateLock) { _notified[key] = DateTime.UtcNow; }
                _log($"[ATC Ahead] Alert: {cand.Controller.Callsign} ~{cand.DistanceNm:0} NM ahead ({cand.Path.Description})");
                await _send(title, body, _s.Priority, _s.Sound).ConfigureAwait(false);
                break;
            }
        }

        private sealed class Candidate {
            public FeedController Controller;
            public Coverage Coverage;
            public double DistanceNm;
            public ForwardPath Path;
        }

        private bool ShouldNotify(string key) {
            DateTime last;
            lock (_stateLock) {
                if (!_notified.TryGetValue(key, out last)) return true;
            }
            if (_s.NotifyOnce) return false;
            return (DateTime.UtcNow - last).TotalMinutes >= _s.CooldownMinutes;
        }

        // Route-following path if possible, else great-circle to the destination, else the
        // current heading. With RouteRelevantOnly=false the heading projection is added too.
        private List<ForwardPath> BuildPaths(FeedPilot me, GeoPoint pos) {
            var paths = new List<ForwardPath>();

            string dep = me.Departure, arr = me.Arrival, route = me.Route;
            lock (_lock) {
                if (string.IsNullOrWhiteSpace(arr)) { dep = _sdkDep; arr = _sdkArr; route = _sdkRoute; }
            }

            ForwardPath routePath = null;
            if (!string.IsNullOrWhiteSpace(arr) && !string.IsNullOrWhiteSpace(dep)) {
                string key = dep + "|" + arr + "|" + route;
                if (key != _planKey) {
                    _plan = RoutePlanner.Build(dep.Trim().ToUpperInvariant(), arr.Trim().ToUpperInvariant(), route, _data, _fixes);
                    _planKey = key;
                    _log(_plan == null
                        ? $"[ATC Ahead] Flight plan {dep}-{arr}: airport position unknown, using heading only"
                        : $"[ATC Ahead] Flight plan {dep}-{arr}: using {_plan.Description}");
                }
                if (_plan != null) {
                    routePath = ForwardPath.FromRoute(_plan, pos, MaxOffRouteNm);
                    if (routePath == null) {
                        routePath = ForwardPath.Direct(pos, _plan.Points[_plan.Points.Count - 1],
                                                       $"great-circle to {arr.Trim().ToUpperInvariant()} (aircraft is off the filed route)");
                    }
                }
            }
            if (routePath != null) paths.Add(routePath);

            if ((routePath == null || !_s.RouteRelevantOnly) && !double.IsNaN(me.Heading)) {
                paths.Add(ForwardPath.Heading(pos, me.Heading, _s.DistanceNm + 50,
                          routePath == null ? "current heading (no usable flight plan)" : "current heading"));
            }
            return paths;
        }

        // ----- output ---------------------------------------------------------------

        internal static void BuildMessage(string label, string callsign, string frequency, double distanceNm,
                                          string confidenceDetail, bool test, out string title, out string body) {
            double rounded = Math.Max(5, Math.Round(distanceNm / 5.0) * 5.0);
            title = "\U0001F4E1 ATC AHEAD" + (test ? " (TEST)" : "");
            body = $"Controller: {label} ({callsign})\n" +
                   $"Frequency: {frequency}\n" +
                   $"Distance: Approximately {rounded:0} NM\n" +
                   "Status: Online\n" +
                   $"Confidence: Estimated ({confidenceDetail})\n" +
                   "Not a guarantee this will be your next controller.";
        }

        internal static string FormatFrequency(string raw) {
            double f;
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out f))
                return f.ToString("0.0##", CultureInfo.InvariantCulture);
            return string.IsNullOrEmpty(raw) ? "unknown" : raw;
        }

        private void LogOnce(string key, string message) {
            bool isNew;
            lock (_stateLock) { isNew = _loggedOnce.Add(key); }
            if (isNew) _log(message);
        }

        private void Verbose(string message) {
            if (_s.Verbose) _log(message);
        }
    }
}
