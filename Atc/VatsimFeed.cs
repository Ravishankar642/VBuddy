using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace VBuddy.Atc {

    internal sealed class FeedPilot {
        public string Cid;
        public string Callsign;
        public double Lat, Lon, Altitude, GroundSpeed, Heading;
        public string Departure, Arrival, Route;   // from the filed flight plan, may be null
    }

    internal sealed class FeedController {
        public string Cid;
        public string Callsign;
        public string Frequency;
        public int Facility;
        public double VisualRange;
        public string LogonTime;
    }

    internal sealed class FeedResult {
        public bool NotModified;
        public FeedPilot Me;                       // null if the pilot is not (yet) in the feed
        public List<FeedController> Controllers = new List<FeedController>();
    }

    internal interface IVatsimFeed {
        Task<FeedResult> FetchAsync(string callsign, string cid, CancellationToken ct);
    }

    // Reads https://data.vatsim.net/v3/vatsim-data.json. Every field is treated as
    // optional: a missing/null/odd-typed field never throws, it just leaves a default.
    internal sealed class VatsimFeedClient : IVatsimFeed {

        public const string FeedUrl = "https://data.vatsim.net/v3/vatsim-data.json";

        private static readonly HttpClient Client = CreateClient();
        private string _etag;

        private static HttpClient CreateClient() {
            var handler = new HttpClientHandler {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };
            var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("VBuddy-AtcAhead");
            c.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            return c;
        }

        public async Task<FeedResult> FetchAsync(string callsign, string cid, CancellationToken ct) {
            var req = new HttpRequestMessage(HttpMethod.Get, FeedUrl);
            if (_etag != null) req.Headers.TryAddWithoutValidation("If-None-Match", _etag);

            using (req)
            using (HttpResponseMessage resp = await Client.SendAsync(req, ct).ConfigureAwait(false)) {
                if (resp.StatusCode == HttpStatusCode.NotModified) return new FeedResult { NotModified = true };
                resp.EnsureSuccessStatusCode();
                _etag = resp.Headers.ETag != null ? resp.Headers.ETag.ToString() : null;
                string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                return await Task.Run(() => Parse(json, callsign, cid), ct).ConfigureAwait(false);
            }
        }

        // Public for tests.
        public static FeedResult Parse(string json, string callsign, string cid) {
            var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 100 };
            var root = ser.DeserializeObject(json) as Dictionary<string, object>;
            if (root == null) throw new FormatException("VATSIM feed: unexpected JSON root");

            var result = new FeedResult();

            foreach (Dictionary<string, object> p in Objects(root, "pilots")) {
                string pCall = Str(p, "callsign");
                string pCid = Str(p, "cid");
                bool match = !string.IsNullOrEmpty(cid)
                    ? pCid == cid
                    : (callsign != null && string.Equals(pCall, callsign, StringComparison.OrdinalIgnoreCase));
                if (!match) continue;

                var me = new FeedPilot {
                    Cid = pCid, Callsign = pCall,
                    Lat = Num(p, "latitude"), Lon = Num(p, "longitude"),
                    Altitude = Num(p, "altitude"), GroundSpeed = Num(p, "groundspeed"), Heading = Num(p, "heading")
                };
                object fpObj;
                var fp = p.TryGetValue("flight_plan", out fpObj) ? fpObj as Dictionary<string, object> : null;
                if (fp != null) {
                    me.Departure = Str(fp, "departure");
                    me.Arrival = Str(fp, "arrival");
                    me.Route = Str(fp, "route");
                }
                result.Me = me;
                break;
            }

            foreach (Dictionary<string, object> c in Objects(root, "controllers")) {
                double fac = Num(c, "facility");
                result.Controllers.Add(new FeedController {
                    Cid = Str(c, "cid"),
                    Callsign = Str(c, "callsign"),
                    Frequency = Str(c, "frequency"),
                    Facility = double.IsNaN(fac) ? -1 : (int)fac,
                    VisualRange = double.IsNaN(Num(c, "visual_range")) ? 0 : Num(c, "visual_range"),
                    LogonTime = Str(c, "logon_time")
                });
            }
            return result;
        }

        private static IEnumerable<Dictionary<string, object>> Objects(Dictionary<string, object> root, string key) {
            object v;
            if (!root.TryGetValue(key, out v) || v == null || v is string) yield break;
            var list = v as IEnumerable;
            if (list == null) yield break;
            foreach (object o in list) {
                var d = o as Dictionary<string, object>;
                if (d != null) yield return d;
            }
        }

        private static string Str(Dictionary<string, object> d, string key) {
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return null;
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        private static double Num(Dictionary<string, object> d, string key) {
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return double.NaN;
            double r;
            return double.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out r) ? r : double.NaN;
        }
    }
}
