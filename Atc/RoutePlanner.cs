using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace VBuddy.Atc {

    // Optional user-supplied waypoint list (IDENT,lat,lon). Named fixes cannot be
    // resolved without navdata, so without this file only coordinates and airports in the
    // route are used. Accepts a simple CSV or OurAirports' navaids.csv (ident,latitude_deg,longitude_deg).
    internal sealed class FixDatabase {
        private readonly Dictionary<string, List<GeoPoint>> _fixes = new Dictionary<string, List<GeoPoint>>(StringComparer.OrdinalIgnoreCase);
        public int Count { get; private set; }

        public static FixDatabase Load(string path) {
            var db = new FixDatabase();
            int idCol = 0, latCol = 1, lonCol = 2;
            bool first = true;
            foreach (string raw in File.ReadLines(path)) {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                List<string> f = SplitCsv(line);
                if (first) {
                    first = false;
                    int i = f.FindIndex(x => x.Equals("ident", StringComparison.OrdinalIgnoreCase));
                    int la = f.FindIndex(x => x.Equals("latitude_deg", StringComparison.OrdinalIgnoreCase));
                    int lo = f.FindIndex(x => x.Equals("longitude_deg", StringComparison.OrdinalIgnoreCase));
                    if (i >= 0 && la >= 0 && lo >= 0) { idCol = i; latCol = la; lonCol = lo; continue; }
                }
                if (f.Count <= Math.Max(idCol, Math.Max(latCol, lonCol))) continue;
                double lat, lon;
                if (!double.TryParse(f[latCol], NumberStyles.Float, CultureInfo.InvariantCulture, out lat)) continue;
                if (!double.TryParse(f[lonCol], NumberStyles.Float, CultureInfo.InvariantCulture, out lon)) continue;
                string id = f[idCol].Trim().ToUpperInvariant();
                if (id.Length == 0) continue;
                List<GeoPoint> list;
                if (!db._fixes.TryGetValue(id, out list)) { list = new List<GeoPoint>(1); db._fixes[id] = list; }
                list.Add(new GeoPoint(lat, lon));
                db.Count++;
            }
            return db;
        }

        // Identifiers are not globally unique, so take the candidate closest to the
        // previous route point and reject anything implausibly far away.
        public bool TryResolve(string ident, GeoPoint reference, out GeoPoint result) {
            result = default(GeoPoint);
            List<GeoPoint> list;
            if (!_fixes.TryGetValue(ident, out list)) return false;
            double best = double.MaxValue;
            foreach (GeoPoint p in list) {
                double d = GeoMath.DistanceNm(reference, p);
                if (d < best) { best = d; result = p; }
            }
            return best <= 2500;
        }

        private static List<string> SplitCsv(string line) {
            var res = new List<string>();
            char sep = line.IndexOf('|') >= 0 ? '|' : (line.IndexOf(';') >= 0 && line.IndexOf(',') < 0 ? ';' : ',');
            var sb = new System.Text.StringBuilder();
            bool q = false;
            foreach (char c in line) {
                if (c == '"') q = !q;
                else if (c == sep && !q) { res.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(c);
            }
            res.Add(sb.ToString());
            return res;
        }
    }

    internal sealed class RoutePlan {
        public List<GeoPoint> Points = new List<GeoPoint>();
        public int Resolved;
        public int Total;
        public string Description;   // shown in the log / "Confidence" line
        public bool HasRoutePoints;  // true if at least one en-route point was resolved
    }

    internal static class RoutePlanner {

        private static readonly Regex CoordShort = new Regex(@"^(\d{2})([NS])(\d{3})([EW])$", RegexOptions.Compiled);
        private static readonly Regex CoordLong = new Regex(@"^(\d{2})(\d{2})([NS])(\d{3})(\d{2})([EW])$", RegexOptions.Compiled);
        private static readonly Regex SpeedLevel = new Regex(@"^[NKM]\d{3,4}([FAMS]\d{3,4})?$", RegexOptions.Compiled);
        private static readonly Regex AirwayLike = new Regex(@"^[A-Z]{1,2}\d{1,4}[A-Z]?$", RegexOptions.Compiled);
        private static readonly Regex ProcedureLike = new Regex(@"^[A-Z]{3,6}\d[A-Z]?$", RegexOptions.Compiled);

        // Builds departure -> resolvable route points -> destination. Returns null if the
        // departure or destination airport position is unknown.
        public static RoutePlan Build(string dep, string arr, string route, BoundaryStore data, FixDatabase fixes) {
            AirportInfo depAp, arrAp;
            if (!data.TryGetAirport(dep, out depAp) || !data.TryGetAirport(arr, out arrAp)) return null;

            var plan = new RoutePlan();
            plan.Points.Add(depAp.Position);
            GeoPoint last = depAp.Position;

            if (!string.IsNullOrWhiteSpace(route)) {
                foreach (string rawToken in route.ToUpperInvariant().Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)) {
                    string t = rawToken;
                    int slash = t.IndexOf('/');
                    if (slash >= 0) t = t.Substring(0, slash);
                    if (t.Length == 0 || t == "DCT" || t == "VFR" || t == "IFR" || SpeedLevel.IsMatch(t)) continue;

                    GeoPoint p;
                    if (TryParseCoordinate(t, out p)) {
                        plan.Total++; plan.Resolved++; plan.Points.Add(p); last = p;
                        continue;
                    }
                    if (AirwayLike.IsMatch(t) || ProcedureLike.IsMatch(t)) continue;   // straight-lined between neighbours

                    plan.Total++;
                    AirportInfo ap;
                    if (fixes != null && fixes.TryResolve(t, last, out p)) {
                        plan.Resolved++; plan.Points.Add(p); last = p;
                    } else if (t.Length == 4 && t != dep && t != arr && data.TryGetAirport(t, out ap)) {
                        plan.Resolved++; plan.Points.Add(ap.Position); last = ap.Position;
                    }
                }
            }
            plan.Points.Add(arrAp.Position);
            plan.HasRoutePoints = plan.Resolved > 0;
            plan.Description = plan.HasRoutePoints
                ? $"filed route ({plan.Resolved}/{plan.Total} waypoints resolved)"
                : (plan.Total > 0 ? $"great-circle to {arr} (route waypoints not resolvable, 0/{plan.Total})" : $"great-circle to {arr}");
            return plan;
        }

        public static bool TryParseCoordinate(string t, out GeoPoint p) {
            p = default(GeoPoint);
            Match m = CoordShort.Match(t);
            if (m.Success) {
                double lat = int.Parse(m.Groups[1].Value), lon = int.Parse(m.Groups[3].Value);
                if (m.Groups[2].Value == "S") lat = -lat;
                if (m.Groups[4].Value == "W") lon = -lon;
                p = new GeoPoint(lat, lon);
                return Math.Abs(lat) <= 90 && Math.Abs(lon) <= 180;
            }
            m = CoordLong.Match(t);
            if (m.Success) {
                double lat = int.Parse(m.Groups[1].Value) + int.Parse(m.Groups[2].Value) / 60.0;
                double lon = int.Parse(m.Groups[4].Value) + int.Parse(m.Groups[5].Value) / 60.0;
                if (m.Groups[3].Value == "S") lat = -lat;
                if (m.Groups[6].Value == "W") lon = -lon;
                p = new GeoPoint(lat, lon);
                return Math.Abs(lat) <= 90 && Math.Abs(lon) <= 180;
            }
            return false;
        }
    }

    // The part of the flight still ahead of the aircraft, as a polyline starting at
    // the aircraft's current position.
    internal sealed class ForwardPath {
        public readonly List<GeoPoint> Pts = new List<GeoPoint>();
        private double[] _cum;
        public double Total { get; private set; }
        public string Description;

        private void Finish() {
            _cum = new double[Pts.Count];
            for (int i = 1; i < Pts.Count; i++) _cum[i] = _cum[i - 1] + GeoMath.DistanceNm(Pts[i - 1], Pts[i]);
            Total = _cum[Pts.Count - 1];
        }

        // Follows the plan from the point of the route closest to the aircraft. Returns
        // null if the aircraft is further than maxOffRouteNm from the route (e.g. flying
        // a different route than filed) so the caller can fall back.
        public static ForwardPath FromRoute(RoutePlan plan, GeoPoint pos, double maxOffRouteNm) {
            if (plan == null || plan.Points.Count < 2) return null;
            double bestDist = double.MaxValue; int bestSeg = -1;
            for (int i = 0; i < plan.Points.Count - 1; i++) {
                double along;
                double d = GeoMath.DistanceToSegmentNm(pos, plan.Points[i], plan.Points[i + 1], out along);
                if (d < bestDist) { bestDist = d; bestSeg = i; }
            }
            if (bestSeg < 0 || bestDist > maxOffRouteNm) return null;

            var fp = new ForwardPath { Description = plan.Description };
            fp.Pts.Add(pos);
            for (int i = bestSeg + 1; i < plan.Points.Count; i++) fp.Pts.Add(plan.Points[i]);
            fp.Finish();
            return fp;
        }

        public static ForwardPath Direct(GeoPoint pos, GeoPoint target, string description) {
            var fp = new ForwardPath { Description = description };
            fp.Pts.Add(pos); fp.Pts.Add(target);
            fp.Finish();
            return fp;
        }

        public static ForwardPath Heading(GeoPoint pos, double headingDeg, double lengthNm, string description) {
            return Direct(pos, GeoMath.Destination(pos, headingDeg, lengthNm), description);
        }

        public GeoPoint PointAt(double distNm) {
            if (distNm <= 0) return Pts[0];
            if (distNm >= Total) return Pts[Pts.Count - 1];
            int i = 1;
            while (i < _cum.Length - 1 && _cum[i] < distNm) i++;
            double seg = _cum[i] - _cum[i - 1];
            double f = seg < 1e-9 ? 0 : (distNm - _cum[i - 1]) / seg;
            return GeoMath.Intermediate(Pts[i - 1], Pts[i], f);
        }

        // Distance along the path at which it first enters the coverage area, searching
        // up to maxNm ahead. Samples every stepNm, then bisects to refine the crossing.
        public bool TryFindEntry(Coverage cov, double maxNm, double stepNm, out double entryNm) {
            entryNm = 0;
            double limit = Math.Min(maxNm, Total);
            if (limit <= 0) return false;
            double prev = 0;
            for (double d = stepNm; ; d += stepNm) {
                double dd = Math.Min(d, limit);
                if (cov.Contains(PointAt(dd))) {
                    double lo = prev, hi = dd;
                    for (int k = 0; k < 6; k++) {
                        double mid = (lo + hi) / 2;
                        if (cov.Contains(PointAt(mid))) hi = mid; else lo = mid;
                    }
                    entryNm = hi;
                    return true;
                }
                prev = dd;
                if (dd >= limit) return false;
            }
        }
    }
}
