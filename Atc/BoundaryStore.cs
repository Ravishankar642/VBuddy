using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace VBuddy.Atc {

    internal sealed class AirportInfo {
        public string Icao;
        public GeoPoint Position;
        public string Fir;
    }

    internal sealed class FirEntry {
        public string Id;
        public string Name;
        public string Prefix;
        public string BoundaryId;
    }

    // Estimated area a controller may cover: either FIR/sector polygons from the
    // community VATSpy data, or (for airport-based positions) a circle around the airport.
    internal sealed class Coverage {
        public string Label;                 // human-readable name, e.g. "Chennai Control"
        public string Basis;                 // "FIR boundary" / "airport radius"
        public List<GeoPolygon> Polygons;    // null for circles
        public GeoPoint Center;
        public double RadiusNm;

        public bool Contains(GeoPoint p) {
            if (Polygons != null) {
                foreach (GeoPolygon poly in Polygons) if (poly.Contains(p.Lat, p.Lon)) return true;
                return false;
            }
            return GeoMath.DistanceNm(Center, p) <= RadiusNm;
        }
    }

    // Loads the community-maintained VATSpy data (FIR polygons, FIR callsign prefixes,
    // airport coordinates) from vatsimnetwork/vatspy-data-project. Files are cached next
    // to the plugin and refreshed every few days. Nothing here needs Navigraph.
    internal sealed class BoundaryStore {

        private const string DatUrl = "https://raw.githubusercontent.com/vatsimnetwork/vatspy-data-project/master/VATSpy.dat";
        private const string GeoUrl = "https://raw.githubusercontent.com/vatsimnetwork/vatspy-data-project/master/Boundaries.geojson";

        private readonly string _dir;
        private readonly Action<string> _log;
        private readonly int _refreshDays;

        private readonly Dictionary<string, AirportInfo> _airports = new Dictionary<string, AirportInfo>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, FirEntry> _firByPrefix = new Dictionary<string, FirEntry>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<GeoPolygon>> _boundaries = new Dictionary<string, List<GeoPolygon>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Coverage> _coverageCache = new Dictionary<string, Coverage>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _unresolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private volatile bool _loaded;
        private DateTime _nextAttemptUtc = DateTime.MinValue;

        public BoundaryStore(string dataDir, int refreshDays, Action<string> log) {
            _dir = dataDir;
            _refreshDays = refreshDays;
            _log = log ?? (_ => { });
        }

        public bool IsLoaded { get { return _loaded; } }
        public int AirportCount { get { return _airports.Count; } }
        public int BoundaryCount { get { return _boundaries.Count; } }

        private string DatPath { get { return Path.Combine(_dir, "VATSpy.dat"); } }
        private string GeoPath { get { return Path.Combine(_dir, "Boundaries.geojson"); } }

        public async Task<bool> EnsureLoadedAsync(CancellationToken ct) {
            if (_loaded) return true;
            if (DateTime.UtcNow < _nextAttemptUtc) return false;

            try {
                Directory.CreateDirectory(_dir);
                await EnsureFileAsync(DatPath, DatUrl, ct).ConfigureAwait(false);
                await EnsureFileAsync(GeoPath, GeoUrl, ct).ConfigureAwait(false);
                await Task.Run(() => LoadFromDisk(), ct).ConfigureAwait(false);
                _loaded = true;
                _log($"[ATC Ahead] Airspace data loaded: {_boundaries.Count} boundaries, {_firByPrefix.Count} FIR/sector prefixes, {_airports.Count} airports");
                return true;
            } catch (OperationCanceledException) {
                throw;
            } catch (Exception ex) {
                _nextAttemptUtc = DateTime.UtcNow.AddMinutes(5);
                _log($"[ATC Ahead] Could not load airspace data (will retry in 5 min): {ex.GetBaseException().Message}");
                return false;
            }
        }

        private async Task EnsureFileAsync(string path, string url, CancellationToken ct) {
            bool exists = File.Exists(path);
            bool stale = !exists || (DateTime.UtcNow - File.GetLastWriteTimeUtc(path)).TotalDays >= _refreshDays;
            if (!stale) return;

            try {
                _log($"[ATC Ahead] Downloading {Path.GetFileName(path)}");
                using (var response = await Http.Client.GetAsync(url, ct).ConfigureAwait(false)) {
                    response.EnsureSuccessStatusCode();
                    byte[] bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    if (bytes.Length < 10000) throw new InvalidDataException("downloaded file is unexpectedly small");
                    string tmp = path + ".tmp";
                    File.WriteAllBytes(tmp, bytes);
                    if (File.Exists(path)) File.Delete(path);
                    File.Move(tmp, path);
                }
            } catch (OperationCanceledException) {
                throw;
            } catch (Exception ex) {
                if (!exists) throw;   // nothing cached to fall back to
                _log($"[ATC Ahead] Refresh of {Path.GetFileName(path)} failed, using cached copy: {ex.GetBaseException().Message}");
            }
        }

        // Public for tests / offline use: parse both files already present in the data dir.
        public void LoadFromDisk() {
            try {
                ParseVatSpy(File.ReadAllLines(DatPath));
                ParseBoundaries(File.ReadAllText(GeoPath));
            } catch {
                // Corrupt cache: drop it so the next attempt downloads fresh copies.
                _airports.Clear(); _firByPrefix.Clear(); _boundaries.Clear();
                try { File.Delete(DatPath); File.Delete(GeoPath); } catch { }
                throw;
            }
        }

        // VATSpy.dat is '|' separated with [Section] headers and ';' comments:
        //   [Airports]  ICAO|Name|Lat|Lon|IATA|FIR|IsPseudo
        //   [FIRs]      ID|Name|CallsignPrefix|BoundaryId   (empty prefix => the ID itself)
        private void ParseVatSpy(string[] lines) {
            string section = "";
            foreach (string raw in lines) {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == ';') continue;
                if (line[0] == '[') { section = line.Trim('[', ']'); continue; }

                string[] p = line.Split('|');
                if (section == "Airports" && p.Length >= 6) {
                    double lat, lon;
                    if (TryD(p[2], out lat) && TryD(p[3], out lon)) {
                        _airports[p[0]] = new AirportInfo { Icao = p[0], Position = new GeoPoint(lat, lon), Fir = p[5] };
                    }
                } else if (section == "FIRs" && p.Length >= 4) {
                    string prefix = string.IsNullOrWhiteSpace(p[2]) ? p[0] : p[2];
                    if (!_firByPrefix.ContainsKey(prefix)) {
                        _firByPrefix[prefix] = new FirEntry { Id = p[0], Name = p[1], Prefix = prefix, BoundaryId = p[3] };
                    }
                }
            }
            if (_airports.Count == 0 || _firByPrefix.Count == 0) throw new InvalidDataException("VATSpy.dat has no airports/FIRs (format changed?)");
        }

        // Boundaries.geojson: FeatureCollection of MultiPolygon features, properties.id = boundary id.
        private void ParseBoundaries(string json) {
            var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 200 };
            var root = ser.DeserializeObject(json) as Dictionary<string, object>;
            if (root == null || !root.ContainsKey("features")) throw new InvalidDataException("Boundaries.geojson: no features");

            foreach (object f in (IEnumerable)root["features"]) {
                var feature = f as Dictionary<string, object>;
                if (feature == null) continue;
                var props = feature["properties"] as Dictionary<string, object>;
                var geom = feature["geometry"] as Dictionary<string, object>;
                if (props == null || geom == null || !props.ContainsKey("id")) continue;

                string id = Convert.ToString(props["id"], CultureInfo.InvariantCulture);
                string type = Convert.ToString(geom["type"], CultureInfo.InvariantCulture);
                var polys = new List<GeoPolygon>();

                if (type == "MultiPolygon") {
                    foreach (object poly in (IEnumerable)geom["coordinates"]) AddPolygon(polys, poly);
                } else if (type == "Polygon") {
                    AddPolygon(polys, geom["coordinates"]);
                }
                if (polys.Count > 0) _boundaries[id] = polys;
            }
            if (_boundaries.Count == 0) throw new InvalidDataException("Boundaries.geojson produced no polygons");
        }

        private static void AddPolygon(List<GeoPolygon> list, object polygonRings) {
            var poly = new GeoPolygon();
            bool first = true;
            foreach (object ringObj in (IEnumerable)polygonRings) {
                GeoRing ring = ToRing(ringObj);
                if (ring.Lon.Length < 3) continue;
                if (first) { poly.Outer = ring; first = false; } else poly.Holes.Add(ring);
            }
            if (poly.Outer == null) return;
            poly.Finish();
            list.Add(poly);
        }

        private static GeoRing ToRing(object ringObj) {
            var lons = new List<double>();
            var lats = new List<double>();
            foreach (object ptObj in (IEnumerable)ringObj) {
                var pt = new List<object>();
                foreach (object o in (IEnumerable)ptObj) pt.Add(o);
                if (pt.Count < 2) continue;
                lons.Add(Convert.ToDouble(pt[0], CultureInfo.InvariantCulture));
                lats.Add(Convert.ToDouble(pt[1], CultureInfo.InvariantCulture));
            }
            return new GeoRing { Lon = lons.ToArray(), Lat = lats.ToArray() };
        }

        private static bool TryD(string s, out double d) {
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d);
        }

        public bool TryGetAirport(string icao, out AirportInfo airport) {
            airport = null;
            return !string.IsNullOrEmpty(icao) && _airports.TryGetValue(icao.Trim(), out airport);
        }

        // Maps an online controller to the area it *may* cover. Returns null when the
        // callsign can't be tied to any known FIR/sector/airport. Facility ids follow the
        // VATSIM feed: 1 FSS, 2 DEL, 3 GND, 4 TWR, 5 APP/DEP, 6 CTR.
        public Coverage Resolve(string callsign, int facility, double visualRangeNm) {
            if (string.IsNullOrEmpty(callsign)) return null;
            string key = callsign + "|" + facility;
            Coverage cached;
            if (_coverageCache.TryGetValue(key, out cached)) return cached;
            if (_unresolved.Contains(key)) return null;

            Coverage cov = ResolveInternal(callsign.ToUpperInvariant(), facility, visualRangeNm);
            if (cov == null) _unresolved.Add(key); else _coverageCache[key] = cov;
            return cov;
        }

        private Coverage ResolveInternal(string callsign, int facility, double visualRangeNm) {
            int last = callsign.LastIndexOf('_');
            string prefix = last > 0 ? callsign.Substring(0, last) : callsign;

            // ACC / FSS, and TMA-type approach positions that have their own polygon.
            if (facility == 6 || facility == 1 || facility == 5) {
                string p = prefix;
                while (true) {
                    FirEntry fir;
                    List<GeoPolygon> polys;
                    if (_firByPrefix.TryGetValue(p, out fir) && _boundaries.TryGetValue(fir.BoundaryId, out polys)) {
                        return new Coverage {
                            Label = MakeLabel(fir.Name, facility),
                            Basis = "FIR boundary",
                            Polygons = polys
                        };
                    }
                    int u = p.LastIndexOf('_');
                    if (u <= 0) break;
                    p = p.Substring(0, u);
                }
            }

            // Airport-based positions (and approach with no polygon): circle around the airport.
            if (facility >= 2 && facility <= 5) {
                string icao = prefix.Split('_')[0];
                AirportInfo ap;
                if (_airports.TryGetValue(icao, out ap)) {
                    double radius = facility == 5 ? 40 : 10;
                    if (visualRangeNm > 0) radius = Math.Min(Math.Max(visualRangeNm, radius), 60);
                    return new Coverage {
                        Label = callsign,
                        Basis = "airport radius",
                        Center = ap.Position,
                        RadiusNm = radius
                    };
                }
            }
            return null;
        }

        private static string MakeLabel(string firName, int facility) {
            if (string.IsNullOrWhiteSpace(firName)) return "Control";
            string n = firName.Trim();
            string lower = n.ToLowerInvariant();
            if (lower.Contains("control") || lower.Contains("radar") || lower.Contains("acc") ||
                lower.Contains("center") || lower.Contains("centre") || lower.Contains("information")) return n;
            return n + (facility == 1 ? " Information" : " Control");
        }
    }
}
