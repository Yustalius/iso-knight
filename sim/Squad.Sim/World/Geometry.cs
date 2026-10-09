namespace Squad.Sim;

/// <summary>Exact 2D intersection tests the rest of the world is built on.</summary>
public static class Geometry
{
    /// <summary>Parameter interval where the line p + t·d is inside the capsule (a, b, r). A capsule is convex,
    /// so the line meets it in one interval: the hull of its parts (two discs and a rectangle).</summary>
    public static bool LineCapsule(Vec2 p, Vec2 d, Vec2 a, Vec2 b, double r, out double t0, out double t1)
    {
        t0 = double.PositiveInfinity; t1 = double.NegativeInfinity;
        Disc(p, d, a, r, ref t0, ref t1);
        Vec2 ab = b - a;
        double L2 = ab.LengthSq;
        if (L2 > 1e-12)
        {
            Disc(p, d, b, r, ref t0, ref t1);
            double L = Math.Sqrt(L2);
            Vec2 u = ab / L, n = u.Perp, ap = p - a;
            double pu = ap.Dot(u), pn = ap.Dot(n), du = d.Dot(u), dn = d.Dot(n);
            double lo = double.NegativeInfinity, hi = double.PositiveInfinity;
            if (Slab(pu, du, 0, L, ref lo, ref hi) && Slab(pn, dn, -r, r, ref lo, ref hi) && lo <= hi)
            {
                if (lo < t0) t0 = lo;
                if (hi > t1) t1 = hi;
            }
        }
        return t0 <= t1;
    }

    static void Disc(Vec2 p, Vec2 d, Vec2 c, double r, ref double t0, ref double t1)
    {
        Vec2 m = p - c;
        double A = d.LengthSq;
        if (A < 1e-18) { if (m.LengthSq <= r * r) { if (0 < t0) t0 = 0; if (0 > t1) t1 = 0; } return; }
        double B = m.Dot(d), C = m.LengthSq - r * r;
        double disc = B * B - A * C;
        if (disc < 0) return;
        double s = Math.Sqrt(disc);
        double e0 = (-B - s) / A, e1 = (-B + s) / A;
        if (e0 < t0) t0 = e0;
        if (e1 > t1) t1 = e1;
    }

    static bool Slab(double p, double d, double lo, double hi, ref double tlo, ref double thi)
    {
        if (Math.Abs(d) < 1e-15) return p >= lo && p <= hi;
        double a = (lo - p) / d, b = (hi - p) / d;
        if (a > b) (a, b) = (b, a);
        if (a > tlo) tlo = a;
        if (b < thi) thi = b;
        return tlo <= thi;
    }

    /// <summary>The segment p → p + d (t ∈ [0, 1]) against a capsule, clipped to the segment.</summary>
    public static bool SegmentCapsule(Vec2 p, Vec2 d, in Obstacle o, out double t0, out double t1)
    {
        if (!LineCapsule(p, d, o.A, o.B, o.R, out t0, out t1)) return false;
        if (t0 < 0) t0 = 0;
        if (t1 > 1) t1 = 1;
        return t0 <= t1;
    }

    /// <summary>Interval where the segment p → p + d is within distance r of c (a vertical cylinder seen from above).</summary>
    public static bool SegmentDisc(Vec2 p, Vec2 d, Vec2 c, double r, out double t0, out double t1)
    {
        t0 = double.PositiveInfinity; t1 = double.NegativeInfinity;
        Disc(p, d, c, r, ref t0, ref t1);
        if (t0 > t1) return false;
        if (t0 < 0) t0 = 0;
        if (t1 > 1) t1 = 1;
        return t0 <= t1;
    }

    public static Vec2 ClosestOnSegment(Vec2 p, Vec2 a, Vec2 b)
    {
        Vec2 ab = b - a;
        double L2 = ab.LengthSq;
        double t = L2 > 1e-12 ? DMath.Clamp01((p - a).Dot(ab) / L2) : 0;
        return a + ab * t;
    }

    public static double DistanceToSegment(Vec2 p, Vec2 a, Vec2 b) => (p - ClosestOnSegment(p, a, b)).Length;

    /// <summary>Signed parameter of the closest approach of segment p → p + d to point c, clamped to [0, 1].</summary>
    public static double ClosestT(Vec2 p, Vec2 d, Vec2 c)
    {
        double L2 = d.LengthSq;
        return L2 > 1e-12 ? DMath.Clamp01((c - p).Dot(d) / L2) : 0;
    }

    /// <summary>Shortest distance between segments a0–a1 and b0–b1.</summary>
    public static double SegmentDistance(Vec2 a0, Vec2 a1, Vec2 b0, Vec2 b1)
    {
        if (SegmentsCross(a0, a1, b0, b1)) return 0;
        return Math.Min(Math.Min(DistanceToSegment(a0, b0, b1), DistanceToSegment(a1, b0, b1)),
                        Math.Min(DistanceToSegment(b0, a0, a1), DistanceToSegment(b1, a0, a1)));
    }

    static bool SegmentsCross(Vec2 a0, Vec2 a1, Vec2 b0, Vec2 b1)
    {
        double d1 = (a1 - a0).Cross(b0 - a0), d2 = (a1 - a0).Cross(b1 - a0);
        double d3 = (b1 - b0).Cross(a0 - b0), d4 = (b1 - b0).Cross(a1 - b0);
        return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
    }

    /// <summary>Gap between the surfaces of two obstacles (negative if they overlap).</summary>
    public static double Gap(in Obstacle a, in Obstacle b) => SegmentDistance(a.A, a.B, b.A, b.B) - a.R - b.R;
}

