using System;
using System.Collections.Generic;

namespace VBuddy.Atc {

    internal struct GeoPoint {
        public readonly double Lat;
        public readonly double Lon;
        public GeoPoint(double lat, double lon) { Lat = lat; Lon = lon; }
        public override string ToString() { return Lat.ToString("0.####") + "," + Lon.ToString("0.####"); }
    }

    // Great-circle helpers on a spherical earth (radius in nautical miles).
    // Spherical maths is accurate to well under 1% here, which is far tighter than
    // the accuracy of the airspace data itself.
    internal static class GeoMath {

        public const double EarthRadiusNm = 3440.065;

        public static double ToRad(double deg) { return deg * Math.PI / 180.0; }
        public static double ToDeg(double rad) { return rad * 180.0 / Math.PI; }

        public static double NormalizeLon(double lon) {
            while (lon > 180.0) lon -= 360.0;
            while (lon < -180.0) lon += 360.0;
            return lon;
        }

        public static double DistanceNm(GeoPoint a, GeoPoint b) {
            double p1 = ToRad(a.Lat), p2 = ToRad(b.Lat);
            double dp = p2 - p1, dl = ToRad(b.Lon - a.Lon);
            double h = Math.Sin(dp / 2) * Math.Sin(dp / 2) +
                       Math.Cos(p1) * Math.Cos(p2) * Math.Sin(dl / 2) * Math.Sin(dl / 2);
            return 2 * EarthRadiusNm * Math.Asin(Math.Min(1.0, Math.Sqrt(h)));
        }

        public static double BearingDeg(GeoPoint a, GeoPoint b) {
            double p1 = ToRad(a.Lat), p2 = ToRad(b.Lat), dl = ToRad(b.Lon - a.Lon);
            double y = Math.Sin(dl) * Math.Cos(p2);
            double x = Math.Cos(p1) * Math.Sin(p2) - Math.Sin(p1) * Math.Cos(p2) * Math.Cos(dl);
            return (ToDeg(Math.Atan2(y, x)) + 360.0) % 360.0;
        }

        public static GeoPoint Destination(GeoPoint p, double bearingDeg, double distNm) {
            double d = distNm / EarthRadiusNm, b = ToRad(bearingDeg);
            double p1 = ToRad(p.Lat), l1 = ToRad(p.Lon);
            double p2 = Math.Asin(Math.Sin(p1) * Math.Cos(d) + Math.Cos(p1) * Math.Sin(d) * Math.Cos(b));
            double l2 = l1 + Math.Atan2(Math.Sin(b) * Math.Sin(d) * Math.Cos(p1),
                                        Math.Cos(d) - Math.Sin(p1) * Math.Sin(p2));
            return new GeoPoint(ToDeg(p2), NormalizeLon(ToDeg(l2)));
        }

        // Point a fraction f (0..1) of the way along the great circle from a to b.
        public static GeoPoint Intermediate(GeoPoint a, GeoPoint b, double f) {
            double d = DistanceNm(a, b) / EarthRadiusNm;
            if (d < 1e-9) return a;
            double sd = Math.Sin(d);
            double A = Math.Sin((1 - f) * d) / sd, B = Math.Sin(f * d) / sd;
            double p1 = ToRad(a.Lat), l1 = ToRad(a.Lon), p2 = ToRad(b.Lat), l2 = ToRad(b.Lon);
            double x = A * Math.Cos(p1) * Math.Cos(l1) + B * Math.Cos(p2) * Math.Cos(l2);
            double y = A * Math.Cos(p1) * Math.Sin(l1) + B * Math.Cos(p2) * Math.Sin(l2);
            double z = A * Math.Sin(p1) + B * Math.Sin(p2);
            return new GeoPoint(ToDeg(Math.Atan2(z, Math.Sqrt(x * x + y * y))), ToDeg(Math.Atan2(y, x)));
        }

        // Distance from point p to the great-circle segment a-b (clamped to the segment).
        // alongNm receives the distance from a to the closest point on the segment.
        public static double DistanceToSegmentNm(GeoPoint p, GeoPoint a, GeoPoint b, out double alongNm) {
            double dab = DistanceNm(a, b);
            double dap = DistanceNm(a, p);
            if (dab < 1e-6) { alongNm = 0; return dap; }

            double brgAp = ToRad(BearingDeg(a, p)), brgAb = ToRad(BearingDeg(a, b));
            double d13 = dap / EarthRadiusNm;
            double xtk = Math.Asin(Math.Max(-1, Math.Min(1, Math.Sin(d13) * Math.Sin(brgAp - brgAb))));
            double cx = Math.Cos(xtk);
            double at = Math.Acos(Math.Max(-1, Math.Min(1, Math.Cos(d13) / (Math.Abs(cx) < 1e-12 ? 1e-12 : cx))));
            double along = at * EarthRadiusNm;
            if (Math.Cos(brgAp - brgAb) < 0) along = -along;

            if (along <= 0) { alongNm = 0; return dap; }
            if (along >= dab) { alongNm = dab; return DistanceNm(b, p); }
            alongNm = along;
            return Math.Abs(xtk) * EarthRadiusNm;
        }
    }

    internal sealed class GeoRing {
        public double[] Lon;
        public double[] Lat;
    }

    // One polygon (outer ring + optional holes) with a cheap bounding-box reject.
    // Polygons that straddle the antimeridian are stored with longitudes unwrapped
    // into 0..360 and points are shifted the same way when tested.
    internal sealed class GeoPolygon {
        public GeoRing Outer;
        public List<GeoRing> Holes = new List<GeoRing>();
        public double MinLat, MaxLat, MinLon, MaxLon;
        public bool Wrapped;

        public void Finish() {
            double minLon = double.MaxValue, maxLon = double.MinValue;
            MinLat = double.MaxValue; MaxLat = double.MinValue;
            foreach (double x in Outer.Lon) { if (x < minLon) minLon = x; if (x > maxLon) maxLon = x; }
            if (maxLon - minLon > 180.0) {
                Wrapped = true;
                ShiftRing(Outer);
                foreach (GeoRing h in Holes) ShiftRing(h);
            }
            minLon = double.MaxValue; maxLon = double.MinValue;
            for (int i = 0; i < Outer.Lon.Length; i++) {
                double x = Outer.Lon[i], y = Outer.Lat[i];
                if (x < minLon) minLon = x; if (x > maxLon) maxLon = x;
                if (y < MinLat) MinLat = y; if (y > MaxLat) MaxLat = y;
            }
            MinLon = minLon; MaxLon = maxLon;
        }

        private static void ShiftRing(GeoRing r) {
            for (int i = 0; i < r.Lon.Length; i++) if (r.Lon[i] < 0) r.Lon[i] += 360.0;
        }

        public bool Contains(double lat, double lon) {
            if (Wrapped && lon < 0) lon += 360.0;
            if (lat < MinLat || lat > MaxLat || lon < MinLon || lon > MaxLon) return false;
            if (!InRing(Outer, lat, lon)) return false;
            foreach (GeoRing h in Holes) if (InRing(h, lat, lon)) return false;
            return true;
        }

        private static bool InRing(GeoRing r, double lat, double lon) {
            bool inside = false;
            int n = r.Lon.Length;
            for (int i = 0, j = n - 1; i < n; j = i++) {
                double yi = r.Lat[i], yj = r.Lat[j], xi = r.Lon[i], xj = r.Lon[j];
                if (((yi > lat) != (yj > lat)) && (lon < (xj - xi) * (lat - yi) / (yj - yi) + xi))
                    inside = !inside;
            }
            return inside;
        }
    }
}
