namespace Squad.Sim;

public enum CoverKind : byte { Low, High, Bush }

/// <summary>A place to stand next to an obstacle. Normal points from the point toward the obstacle,
/// i.e. the direction it protects from (zero for a bush, which hides from everywhere).</summary>
public struct CoverPoint
{
    public Vec2 Pos, Normal;
    public CoverKind Kind;
    public int Obstacle;
    /// <summary>A wall end: step sideways to peek around it.</summary>
    public bool Corner;
}

/// <summary>Per-match scratch memory for world queries, so the immutable <see cref="World"/> can be shared
/// between matches and threads and queries allocate nothing.</summary>
public sealed class WorldScratch
{
    internal readonly int[] ObStamp;
    internal int ObGen;
    internal readonly NavScratch Nav;

    public WorldScratch(World w)
    {
        ObStamp = new int[Math.Max(1, w.Obstacles.Length)];
        Nav = new NavScratch(w.Nav);
    }

    internal int NextGen()
    {
        if (++ObGen == int.MaxValue) { Array.Clear(ObStamp); ObGen = 1; }
        return ObGen;
    }
}

internal interface IObstacleVisitor
{
    /// <summary>Return false to stop the traversal.</summary>
    bool Visit(int index);
}

/// <summary>A map prepared for queries: obstacle grid for rays and collisions, navigation grid, cover points.
/// Immutable after construction.</summary>
public sealed class World
{
    public readonly MapData Map;
    public readonly Obstacle[] Obstacles;
    public readonly NavGrid Nav;
    public readonly CoverPoint[] Cover;
    public readonly double SoldierRadius;
    public double Width => Map.Width;
    public double Height => Map.Height;

    const double Cell = 2.0;
    readonly int _gw, _gh;
    readonly int[] _cellStart, _cellItems;
    const double CoverCell = 4.0;
    readonly int _cw, _ch;
    readonly int[] _coverStart, _coverItems;

    public World(MapData map, double soldierRadius = 0.28)
    {
        Map = map;
        SoldierRadius = soldierRadius;
        Obstacles = map.Obstacles.ToArray();
        _gw = Math.Max(1, (int)Math.Ceiling(map.Width / Cell));
        _gh = Math.Max(1, (int)Math.Ceiling(map.Height / Cell));
        (_cellStart, _cellItems) = BuildObstacleGrid();
        Nav = new NavGrid(this, soldierRadius);
        Cover = BuildCover();
        _cw = Math.Max(1, (int)Math.Ceiling(map.Width / CoverCell));
        _ch = Math.Max(1, (int)Math.Ceiling(map.Height / CoverCell));
        (_coverStart, _coverItems) = BuildCoverGrid();
    }

    // ---------- obstacle grid ----------

    (int[] start, int[] items) BuildObstacleGrid()
    {
        var count = new int[_gw * _gh + 1];
        for (int pass = 0; pass < 2; pass++)
        {
            int[]? items = pass == 1 ? new int[count[^1]] : null;
            var fill = pass == 1 ? (int[])count.Clone() : null;
            for (int i = 0; i < Obstacles.Length; i++)
            {
                ref var o = ref Obstacles[i];
                int x0 = CellX(Math.Min(o.A.X, o.B.X) - o.R), x1 = CellX(Math.Max(o.A.X, o.B.X) + o.R);
                int y0 = CellY(Math.Min(o.A.Y, o.B.Y) - o.R), y1 = CellY(Math.Max(o.A.Y, o.B.Y) + o.R);
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        // keep only cells the capsule really touches
                        var c = new Vec2((x + 0.5) * Cell, (y + 0.5) * Cell);
                        if (Geometry.DistanceToSegment(c, o.A, o.B) > o.R + Cell * 0.7072) continue;
                        if (pass == 0) count[y * _gw + x]++;
                        else items![fill![y * _gw + x]++] = i;
                    }
            }
            if (pass == 0)
            {
                // prefix sums → start offsets
                int sum = 0;
                for (int k = 0; k < count.Length - 1; k++) { int c = count[k]; count[k] = sum; sum += c; }
                count[^1] = sum;
            }
            else return (count, items!);
        }
        throw new InvalidOperationException();
    }

    int CellX(double x) => Math.Clamp((int)Math.Floor(x / Cell), 0, _gw - 1);
    int CellY(double y) => Math.Clamp((int)Math.Floor(y / Cell), 0, _gh - 1);

    /// <summary>Visit each obstacle whose grid cells the segment p → q crosses, once (Amanatides–Woo DDA).</summary>
    internal void Traverse<T>(Vec2 p, Vec2 q, ref T v, WorldScratch s) where T : struct, IObstacleVisitor
    {
        int gen = s.NextGen();
        // clip the segment to the grid rectangle
        Vec2 d = q - p;
        double gwM = _gw * Cell, ghM = _gh * Cell;
        double t0 = 0, t1 = 1;
        if (!Clip(p.X, d.X, 0, gwM, ref t0, ref t1) || !Clip(p.Y, d.Y, 0, ghM, ref t0, ref t1)) return;
        Vec2 a = p + d * t0;
        int cx = CellX(a.X), cy = CellY(a.Y);
        int ex = CellX((p + d * t1).X), ey = CellY((p + d * t1).Y);
        int sx = d.X > 0 ? 1 : d.X < 0 ? -1 : 0, sy = d.Y > 0 ? 1 : d.Y < 0 ? -1 : 0;
        double tdx = sx != 0 ? Cell / Math.Abs(d.X) : double.PositiveInfinity;
        double tdy = sy != 0 ? Cell / Math.Abs(d.Y) : double.PositiveInfinity;
        double tmx = sx > 0 ? ((cx + 1) * Cell - p.X) / d.X : sx < 0 ? (cx * Cell - p.X) / d.X : double.PositiveInfinity;
        double tmy = sy > 0 ? ((cy + 1) * Cell - p.Y) / d.Y : sy < 0 ? (cy * Cell - p.Y) / d.Y : double.PositiveInfinity;
        int guard = _gw + _gh + 4;
        while (guard-- > 0)
        {
            int ci = cy * _gw + cx;
            for (int k = _cellStart[ci], e = _cellStart[ci + 1]; k < e; k++)
            {
                int oi = _cellItems[k];
                if (s.ObStamp[oi] == gen) continue;
                s.ObStamp[oi] = gen;
                if (!v.Visit(oi)) return;
            }
            if (cx == ex && cy == ey) return;
            if (tmx < tmy) { if (tmx > t1) return; cx += sx; tmx += tdx; }
            else { if (tmy > t1) return; cy += sy; tmy += tdy; }
            if (cx < 0 || cy < 0 || cx >= _gw || cy >= _gh) return;
        }
    }

    static bool Clip(double p, double d, double lo, double hi, ref double t0, ref double t1)
    {
        if (Math.Abs(d) < 1e-15) return p >= lo && p <= hi;
        double a = (lo - p) / d, b = (hi - p) / d;
        if (a > b) (a, b) = (b, a);
        if (a > t0) t0 = a;
        if (b < t1) t1 = b;
        return t0 <= t1;
    }

    /// <summary>Visit obstacles registered in the cells overlapping a disc.</summary>
    internal void Nearby<T>(Vec2 c, double r, ref T v, WorldScratch s) where T : struct, IObstacleVisitor
    {
        int gen = s.NextGen();
        int x0 = CellX(c.X - r), x1 = CellX(c.X + r), y0 = CellY(c.Y - r), y1 = CellY(c.Y + r);
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                int ci = y * _gw + x;
                for (int k = _cellStart[ci], e = _cellStart[ci + 1]; k < e; k++)
                {
                    int oi = _cellItems[k];
                    if (s.ObStamp[oi] == gen) continue;
                    s.ObStamp[oi] = gen;
                    if (!v.Visit(oi)) return;
                }
            }
    }

    // ---------- sight ----------

    struct SightVisitor : IObstacleVisitor
    {
        public Obstacle[] Obs;
        public Vec2 P, D;
        public double H0, Dh, Len, BushDepth, Factor;
        public bool SolidOnly;

        public bool Visit(int i)
        {
            ref var o = ref Obs[i];
            if (SolidOnly && !o.Solid) return true;
            if (!Geometry.SegmentCapsule(P, D, o, out double t0, out double t1)) return true;
            double y0 = H0 + Dh * t0, y1 = H0 + Dh * t1;
            if (o.Solid)
            {
                if (Math.Min(y0, y1) < o.H) { Factor = 0; return false; }
                return true;
            }
            // foliage: attenuate by the length of the line below the bush top
            double below;
            if (y0 < o.H && y1 < o.H) below = t1 - t0;
            else if (y0 >= o.H && y1 >= o.H) return true;
            else
            {
                double tc = t0 + (o.H - y0) / (y1 - y0) * (t1 - t0);
                below = y0 < o.H ? tc - t0 : t1 - tc;
            }
            Factor *= DMath.Exp(-below * Len / BushDepth);
            return Factor > 1e-3;
        }
    }

    /// <summary>How visible point (q, hq) is from eye (p, hp): 0 behind anything solid, attenuated through foliage, 1 in the open.</summary>
    public double Visibility(Vec2 p, double hp, Vec2 q, double hq, WorldScratch s, double bushDepth = 0.5)
    {
        var v = new SightVisitor { Obs = Obstacles, P = p, D = q - p, H0 = hp, Dh = hq - hp, Len = (q - p).Length, BushDepth = bushDepth, Factor = 1 };
        Traverse(p, q, ref v, s);
        return v.Factor;
    }

    /// <summary>True if something solid (not foliage) is between the two points.</summary>
    public bool SolidBetween(Vec2 p, double hp, Vec2 q, double hq, WorldScratch s)
    {
        var v = new SightVisitor { Obs = Obstacles, P = p, D = q - p, H0 = hp, Dh = hq - hp, Len = (q - p).Length, BushDepth = 1, Factor = 1, SolidOnly = true };
        Traverse(p, q, ref v, s);
        return v.Factor == 0;
    }

    // ---------- bullets ----------

    struct BulletVisitor : IObstacleVisitor
    {
        public Obstacle[] Obs;
        public Vec2 P, D;
        public double H0, Dh, BestT;
        public int Best;

        public bool Visit(int i)
        {
            ref var o = ref Obs[i];
            if (!o.BlocksBullets) return true;
            if (!Geometry.SegmentCapsule(P, D, o, out double t0, out double t1) || t0 >= BestT) return true;
            double y0 = H0 + Dh * t0, y1 = H0 + Dh * t1;
            double t;
            if (y0 < o.H) t = t0;                                    // enters the side
            else if (y1 < o.H) t = t0 + (y0 - o.H) / (y0 - y1) * (t1 - t0);   // drops onto the top
            else return true;
            if (t < BestT) { BestT = t; Best = i; }
            return true;
        }
    }

    /// <summary>First solid obstacle along a bullet's path p → p + d whose height changes from h0 by dh.
    /// Returns its index and the parameter t ∈ [0, 1], or −1.</summary>
    public int TraceBullet(Vec2 p, double h0, Vec2 d, double dh, WorldScratch s, out double t)
    {
        var v = new BulletVisitor { Obs = Obstacles, P = p, D = d, H0 = h0, Dh = dh, BestT = double.PositiveInfinity, Best = -1 };
        Traverse(p, p + d, ref v, s);
        t = v.BestT;
        return v.Best;
    }

    // ---------- movement ----------

    struct PushVisitor : IObstacleVisitor
    {
        public Obstacle[] Obs;
        public Vec2 Pos;
        public double Radius;
        public bool InBush;

        public bool Visit(int i)
        {
            ref var o = ref Obs[i];
            Vec2 q = Geometry.ClosestOnSegment(Pos, o.A, o.B);
            Vec2 dv = Pos - q;
            double d = dv.Length;
            if (!o.BlocksMove)
            {
                if (d < o.R) InBush = true;
                return true;
            }
            double r = o.R + Radius;
            if (d < r)
            {
                Vec2 n = d < 1e-5 ? new Vec2(1, 0) : dv / d;
                Pos = q + n * r;
            }
            return true;
        }
    }

    /// <summary>Push a body of the given radius out of obstacles and keep it inside the map (collide() of the prototype).</summary>
    public Vec2 Collide(Vec2 pos, double radius, WorldScratch s, out bool inBush)
    {
        var v = new PushVisitor { Obs = Obstacles, Pos = pos, Radius = radius };
        Nearby(pos, radius + 0.05, ref v, s);
        // a second pass settles corners where two capsules meet
        v.InBush = false;
        Nearby(v.Pos, radius + 0.05, ref v, s);
        inBush = v.InBush;
        return ClampToMap(v.Pos, radius);
    }

    public Vec2 ClampToMap(Vec2 p, double margin) =>
        new(DMath.Clamp(p.X, margin, Map.Width - margin), DMath.Clamp(p.Y, margin, Map.Height - margin));

    struct OverlapVisitor : IObstacleVisitor
    {
        public Obstacle[] Obs;
        public Vec2 Pos;
        public double Radius;
        public bool Hit;
        public bool Visit(int i)
        {
            ref var o = ref Obs[i];
            if (!o.BlocksMove) return true;
            if (Geometry.DistanceToSegment(Pos, o.A, o.B) < o.R + Radius) { Hit = true; return false; }
            return true;
        }
    }

    /// <summary>True if a body of this radius at p would overlap a solid obstacle.</summary>
    public bool Overlaps(Vec2 p, double radius, WorldScratch s)
    {
        var v = new OverlapVisitor { Obs = Obstacles, Pos = p, Radius = radius };
        Nearby(p, radius, ref v, s);
        return v.Hit;
    }

    // ---------- cover points ----------

    static readonly double[] Sides = { 1, -1 };

    CoverPoint[] BuildCover()
    {
        var list = new List<CoverPoint>();
        var scratch = new WorldScratch(this);
        double off = SoldierRadius + 0.12;
        for (int i = 0; i < Obstacles.Length; i++)
        {
            ref var o = ref Obstacles[i];
            Vec2 ab = o.B - o.A;
            double L = ab.Length;
            Vec2 u = L > 1e-6 ? ab / L : new Vec2(1, 0), n = u.Perp;
            if (o.Kind == ObstacleKind.Bush)
            {
                int k = Math.Max(1, (int)(L / 1.2) + 1);
                for (int j = 0; j < k; j++)
                {
                    Vec2 p = k == 1 ? (o.A + o.B) * 0.5 : o.A + ab * (j / (double)(k - 1));
                    list.Add(new CoverPoint { Pos = p, Normal = Vec2.Zero, Kind = CoverKind.Bush, Obstacle = i });
                }
                continue;
            }
            var kind = o.Kind is ObstacleKind.LowWall or ObstacleKind.Crate ? CoverKind.Low : CoverKind.High;
            if (L < 0.5)
            {
                // round props: eight points around
                for (int j = 0; j < 8; j++)
                {
                    Vec2 dir = Vec2.FromYaw(j * DMath.Pi / 4);
                    list.Add(new CoverPoint { Pos = (o.A + o.B) * 0.5 + dir * (o.R + L * 0.5 + off), Normal = -dir, Kind = kind, Obstacle = i });
                }
                continue;
            }
            int m = Math.Max(1, (int)(L / 1.2));
            for (int j = 0; j <= m; j++)
            {
                Vec2 c = o.A + ab * (j / (double)m);
                foreach (double side in Sides)
                    list.Add(new CoverPoint { Pos = c + n * (side * (o.R + off)), Normal = n * -side, Kind = kind, Obstacle = i });
            }
            if (kind == CoverKind.High)
            {
                // corners: just past each end, on both faces, protected from the wall's own side
                foreach (var (end, dir) in new[] { (o.A, -u), (o.B, u) })
                    foreach (double side in Sides)
                        list.Add(new CoverPoint { Pos = end + n * (side * (o.R + off)) + dir * 0.05, Normal = n * -side, Kind = kind, Obstacle = i, Corner = true });
            }
        }
        // keep reachable, non-overlapping, de-duplicated points
        var keep = new List<CoverPoint>();
        foreach (var c in list)
        {
            if (c.Pos.X < 0.5 || c.Pos.Y < 0.5 || c.Pos.X > Map.Width - 0.5 || c.Pos.Y > Map.Height - 0.5) continue;
            if (c.Kind != CoverKind.Bush && Overlaps(c.Pos, SoldierRadius - 0.02, scratch)) continue;
            if (!Nav.FreeAt(c.Pos)) continue;
            bool dup = false;
            foreach (var k in keep) if (Vec2.DistanceSq(k.Pos, c.Pos) < 0.5 * 0.5) { dup = true; break; }
            if (!dup) keep.Add(c);
        }
        return keep.ToArray();
    }

    (int[] start, int[] items) BuildCoverGrid()
    {
        var start = new int[_cw * _ch + 1];
        var cellOf = new int[Cover.Length];
        for (int i = 0; i < Cover.Length; i++)
        {
            int x = Math.Clamp((int)(Cover[i].Pos.X / CoverCell), 0, _cw - 1), y = Math.Clamp((int)(Cover[i].Pos.Y / CoverCell), 0, _ch - 1);
            cellOf[i] = y * _cw + x;
            start[cellOf[i]]++;
        }
        int sum = 0;
        for (int k = 0; k < start.Length - 1; k++) { int c = start[k]; start[k] = sum; sum += c; }
        start[^1] = sum;
        var fill = (int[])start.Clone();
        var items = new int[Cover.Length];
        for (int i = 0; i < Cover.Length; i++) items[fill[cellOf[i]]++] = i;
        return (start, items);
    }

    /// <summary>Indices of the cover points nearest to p within radius, closest first. Returns the count written.</summary>
    public int NearestCover(Vec2 p, double radius, Span<int> result, Span<double> dist2)
    {
        int n = 0, cap = result.Length;
        int x0 = Math.Clamp((int)((p.X - radius) / CoverCell), 0, _cw - 1), x1 = Math.Clamp((int)((p.X + radius) / CoverCell), 0, _cw - 1);
        int y0 = Math.Clamp((int)((p.Y - radius) / CoverCell), 0, _ch - 1), y1 = Math.Clamp((int)((p.Y + radius) / CoverCell), 0, _ch - 1);
        double r2 = radius * radius;
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                int ci = y * _cw + x;
                for (int k = _coverStart[ci], e = _coverStart[ci + 1]; k < e; k++)
                {
                    int idx = _coverItems[k];
                    double d2 = Vec2.DistanceSq(p, Cover[idx].Pos);
                    if (d2 > r2) continue;
                    if (n == cap && d2 >= dist2[n - 1]) continue;
                    int j = n < cap ? n++ : n - 1;
                    while (j > 0 && dist2[j - 1] > d2) { result[j] = result[j - 1]; dist2[j] = dist2[j - 1]; j--; }
                    result[j] = idx; dist2[j] = d2;
                }
            }
        return n;
    }

    public bool InBush(Vec2 p)
    {
        foreach (ref readonly var o in Obstacles.AsSpan())
            if (o.Kind == ObstacleKind.Bush && Geometry.DistanceToSegment(p, o.A, o.B) < o.R) return true;
        return false;
    }
}
