using System;
using System.Collections.Generic;
using System.Globalization;

namespace VBuddy.Atc {

    // Settings for the ATC Ahead feature, read from the [AtcAhead] section of
    // VBuddy.ini. Every value has a safe default and is range-checked, so a
    // typo in the ini can never crash the plugin. Missing section == feature disabled.
    internal class AtcAheadSettings {

        public const string Section = "AtcAhead";

        public bool Enabled;                 // master switch (default off)
        public string Cid;                   // optional: identify the pilot by CID instead of callsign
        public double DistanceNm = 150;      // alert when the controller's area is this close ahead
        public double MinDistanceNm = 0;     // ignore controllers whose area is closer than this
        public int PollIntervalSeconds = 30; // VATSIM feed poll interval (min 15)
        public bool NotifyOnce = true;       // one alert per controller logon
        public int CooldownMinutes = 30;     // when NotifyOnce=false: minimum time between repeats
        public bool RouteRelevantOnly = true;// true: follow filed route only; false: also look along current heading
        public HashSet<int> Facilities = new HashSet<int> { 1, 6 }; // FSS + CTR
        public int Priority = 1;             // passed to the driver like other message types
        public string Sound;                 // optional (Pushover / Bark)
        public bool TestOnConnect;           // send one sample alert after connecting
        public int BoundaryRefreshDays = 14; // how often to refresh the cached VATSpy data
        public string FixesFile;             // optional CSV of waypoints (see README)
        public bool Verbose;                 // log every poll cycle to the vPilot debug console

        // Facility ids used by the VATSIM data feed.
        private static readonly Dictionary<string, int> FacilityNames =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) {
                { "FSS", 1 }, { "DEL", 2 }, { "GND", 3 }, { "TWR", 4 },
                { "APP", 5 }, { "DEP", 5 }, { "CTR", 6 }
            };

        public static AtcAheadSettings Load(IniFile ini) {
            var s = new AtcAheadSettings();
            s.Enabled = ParseBool(ini.Read("Enabled", Section, null), false);
            s.Cid = Clean(ini.Read("Cid", Section, null));
            s.DistanceNm = Clamp(ParseDouble(ini.Read("DistanceNm", Section, null), 150), 10, 1000);
            s.MinDistanceNm = Clamp(ParseDouble(ini.Read("MinDistanceNm", Section, null), 0), 0, s.DistanceNm);
            s.PollIntervalSeconds = (int)Clamp(ParseDouble(ini.Read("PollIntervalSeconds", Section, null), 30), 15, 600);
            s.NotifyOnce = ParseBool(ini.Read("NotifyOnce", Section, null), true);
            s.CooldownMinutes = (int)Clamp(ParseDouble(ini.Read("CooldownMinutes", Section, null), 30), 1, 720);
            s.RouteRelevantOnly = ParseBool(ini.Read("RouteRelevantOnly", Section, null), true);
            s.Priority = (int)ParseDouble(ini.Read("Priority", Section, null), 1);
            s.Sound = Clean(ini.Read("Sound", Section, null));
            s.TestOnConnect = ParseBool(ini.Read("TestOnConnect", Section, null), false);
            s.BoundaryRefreshDays = (int)Clamp(ParseDouble(ini.Read("BoundaryRefreshDays", Section, null), 14), 1, 365);
            s.FixesFile = Clean(ini.Read("FixesFile", Section, null));
            s.Verbose = ParseBool(ini.Read("Verbose", Section, null), false);

            HashSet<int> fac = ParseFacilities(ini.Read("Facilities", Section, null));
            if (fac.Count > 0) s.Facilities = fac;
            return s;
        }

        public static HashSet<int> ParseFacilities(string value) {
            var set = new HashSet<int>();
            if (string.IsNullOrWhiteSpace(value)) return set;
            foreach (string part in value.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)) {
                int id;
                if (FacilityNames.TryGetValue(part.Trim(), out id)) set.Add(id);
            }
            return set;
        }

        private static string Clean(string v) {
            return string.IsNullOrWhiteSpace(v) ? null : v.Trim().Trim('"');
        }

        private static bool ParseBool(string v, bool def) {
            bool b;
            return v != null && bool.TryParse(v.Trim().Trim('"'), out b) ? b : def;
        }

        private static double ParseDouble(string v, double def) {
            double d;
            return v != null && double.TryParse(v.Trim().Trim('"'), NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : def;
        }

        private static double Clamp(double v, double min, double max) {
            return v < min ? min : (v > max ? max : v);
        }
    }
}
