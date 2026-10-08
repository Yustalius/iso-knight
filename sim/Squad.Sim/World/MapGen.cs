namespace Squad.Sim;

public sealed class MapGenOptions
{
    public int TeamA = 4, TeamB = 4;
    /// <summary>Point-symmetric (180° rotation about the centre): both teams get the same map.</summary>
    public bool Symmetric = true;
    public double Density = 1, Buildings = 1, Foliage = 1, LowCover = 1;
    /// <summary>Overrides the side length that is otherwise derived from the number of players.</summary>
    public double Size;
}

/// <summary>Procedural arenas, so bots never learn one map by heart. Size grows with the number of players
/// (~24 m for 1×1, ~60 m for 10×10). Content: houses with doors and windows, low walls, crates, bushes, trees.
/// A map is kept only if both spawns are connected and a second route exists when the shortest one is blocked.</summary>
public static class MapGen
{
    public static double SideFor(int players) =>
        DMath.Clamp(24 + 36 * (Math.Sqrt(players) - Math.Sqrt(2)) / (Math.Sqrt(20) - Math.Sqrt(2)), 22, 64);

    public static MapData Generate(ulong seed, MapGenOptions o)
    {
        var root = new Rng(seed, 0x4D41500);
        for (int attempt = 0; attempt < 60; attempt++)
        {
            var r = root.Fork((ulong)attempt);
            var m = TryOnce(ref r, o);
            m.Name = $"gen-{seed}" + (attempt > 0 ? $"-{attempt}" : "");
            if (Validate(m, out _)) return m;
        }
        throw new InvalidOperationException($"No valid map for seed {seed} after 60 attempts.");
    }

    static MapData TryOnce(ref Rng r, MapGenOptions o)
    {
        int players = o.TeamA + o.TeamB;
        double side = o.Size > 0 ? o.Size : SideFor(players);
        double W = DMath.Round(side), H = DMath.Round(side * 1.15);
        var m = new MapData { Width = W, Height = H };
        const double spawnDepth = 3, clear = 6;
        m.Spawns.Add(new SpawnZone { Min = new Vec2(W * 0.15, 1), Max = new Vec2(W * 0.85, 1 + spawnDepth) });
        m.Spawns.Add(new SpawnZone { Min = new Vec2(W * 0.15, H - 1 - spawnDepth), Max = new Vec2(W * 0.85, H - 1) });

        // region to fill: the whole field, or the lower half when it will be mirrored
        double y0 = clear, y1 = o.Symmetric ? H / 2 - 0.3 : H - clear;
        double area = W * (y1 - y0) / 100;   // in 100 m² units
        var obs = new List<Obstacle>();

        int nb = Amount(ref r, 0.14 * o.Buildings * area * o.Density);
        for (int k = 0; k < nb; k++) TryPlace(ref r, obs, W, y0, y1, 2.2, (ref Rng g, Vec2 c) => Building(ref g, c));
        int nl = Amount(ref r, 0.7 * o.LowCover * area * o.Density);
        for (int k = 0; k < nl; k++) TryPlace(ref r, obs, W, y0, y1, 1.3, (ref Rng g, Vec2 c) => LowWall(ref g, c));
        int nc = Amount(ref r, 0.45 * o.LowCover * area * o.Density);
        for (int k = 0; k < nc; k++) TryPlace(ref r, obs, W, y0, y1, 1.3, (ref Rng g, Vec2 c) => Crates(ref g, c));
        int nt = Amount(ref r, 0.25 * o.Foliage * area * o.Density);
        for (int k = 0; k < nt; k++) TryPlace(ref r, obs, W, y0, y1, 1.3, (ref Rng g, Vec2 c) => new List<Obstacle> { Obstacle.Make(ObstacleKind.Tree, c, c, g.Range(0.22, 0.35)) });
        int nh = Amount(ref r, 0.45 * o.Foliage * area * o.Density);
        for (int k = 0; k < nh; k++) TryPlace(ref r, obs, W, y0, y1, 0.5, (ref Rng g, Vec2 c) => Bushes(ref g, c));

        m.Obstacles.AddRange(obs);
        if (o.Symmetric)
            foreach (var x in obs)
                m.Obstacles.Add(new Obstacle { Kind = x.Kind, A = new Vec2(W - x.B.X, H - x.B.Y), B = new Vec2(W - x.A.X, H - x.A.Y), R = x.R, H = x.H });
        return m;
    }

    /// <summary>x items on average: the integer part, plus one more with the fractional part as probability.</summary>
    static int Amount(ref Rng r, double x) { int n = (int)x; return n + (r.Chance(x - n) ? 1 : 0); }

    delegate List<Obstacle> Maker(ref Rng r, Vec2 at);

    /// <summary>Try a few positions for a group of obstacles; keep the first that leaves a walkable gap to everything else.</summary>
    static void TryPlace(ref Rng r, List<Obstacle> obs, double W, double y0, double y1, double gap, Maker make)
    {
        for (int t = 0; t < 12; t++)
        {
            var c = new Vec2(r.Range(2, W - 2), r.Range(y0, y1));
            var group = make(ref r, c);
            bool ok = true;
            foreach (var g in group)
            {
                double minX = Math.Min(g.A.X, g.B.X) - g.R, maxX = Math.Max(g.A.X, g.B.X) + g.R;
                double minY = Math.Min(g.A.Y, g.B.Y) - g.R, maxY = Math.Max(g.A.Y, g.B.Y) + g.R;
                if (minX < 1.2 || maxX > W - 1.2 || minY < y0 - 0.5 || maxY > y1) { ok = false; break; }
                foreach (var e in obs)
                {
                    bool soft = g.Kind == ObstacleKind.Bush || e.Kind == ObstacleKind.Bush;
                    if (Geometry.Gap(g, e) < (soft ? Math.Min(gap, 0.6) : gap)) { ok = false; break; }
                }
                if (!ok) break;
            }
            if (!ok) continue;
            obs.AddRange(group);
            return;
        }
    }

    /// <summary>A house: four high walls, at least two doors on different sides, maybe windows (low wall segments) and a crate inside.</summary>
    static List<Obstacle> Building(ref Rng r, Vec2 c)
    {
        double w = r.Range(4.5, 8), h = r.Range(4.5, 7);
        Vec2 p0 = c + new Vec2(-w / 2, -h / 2), p1 = c + new Vec2(w / 2, -h / 2), p2 = c + new Vec2(w / 2, h / 2), p3 = c + new Vec2(-w / 2, h / 2);
        var sides = new[] { (p0, p1), (p1, p2), (p2, p3), (p3, p0) };
        int d0 = r.Int(4), d1 = (d0 + 1 + r.Int(3)) % 4;
        var list = new List<Obstacle>();
        for (int s = 0; s < 4; s++)
        {
            var (a, b) = sides[s];
            bool door = s == d0 || s == d1 || r.Chance(0.2);
            bool window = !door ? r.Chance(0.6) : r.Chance(0.25);
            Side(ref r, list, a, b, door, window);
        }
        if (w > 5.5 && h > 5.5 && r.Chance(0.5))
        {
            Vec2 p = c + new Vec2(r.Range(-w / 6, w / 6), r.Range(-h / 6, h / 6));
            list.Add(Obstacle.Make(ObstacleKind.Crate, p, p));
        }
        return list;
    }

    static void Side(ref Rng r, List<Obstacle> list, Vec2 a, Vec2 b, bool door, bool window)
    {
        double L = Vec2.Distance(a, b);
        Vec2 u = (b - a) / L;
        const double doorW = 1.5, winW = 1.4, margin = 0.9;
        // openings as [start, end, kind] along the side, sorted
        var open = new List<(double s, double e, bool win)>();
        if (door) { double s = r.Range(margin, L - margin - doorW); open.Add((s, s + doorW, false)); }
        if (window)
        {
            for (int t = 0; t < 4; t++)
            {
                double s = r.Range(margin, L - margin - winW);
                if (open.TrueForAll(o => s + winW + 0.6 < o.s || s > o.e + 0.6)) { open.Add((s, s + winW, true)); break; }
            }
        }
        open.Sort((x, y) => x.s.CompareTo(y.s));
        double pos = 0;
        foreach (var o in open)
        {
            if (o.s - pos > 0.05) list.Add(Obstacle.Make(ObstacleKind.HighWall, a + u * pos, a + u * o.s));
            if (o.win) list.Add(Obstacle.Make(ObstacleKind.LowWall, a + u * o.s, a + u * o.e, 0.12));
            pos = o.e;
        }
        if (L - pos > 0.05) list.Add(Obstacle.Make(ObstacleKind.HighWall, a + u * pos, b));
    }

    static List<Obstacle> LowWall(ref Rng r, Vec2 c)
    {
        double len = r.Range(2, 5);
        double roll = r.NextDouble();
        double yaw = roll < 0.6 ? DMath.HalfPi : roll < 0.8 ? 0 : r.Range(0, DMath.Pi);
        Vec2 half = Vec2.FromYaw(yaw) * (len / 2);
        return new List<Obstacle> { Obstacle.Make(ObstacleKind.LowWall, c - half, c + half) };
    }

    static List<Obstacle> Crates(ref Rng r, Vec2 c)
    {
        int n = 1 + r.Int(3);
        var list = new List<Obstacle>();
        Vec2 dir = Vec2.FromYaw(r.Range(0, DMath.TwoPi));
        for (int k = 0; k < n; k++)
        {
            Vec2 p = c + dir * (k * 0.9) + dir.Perp * r.Range(-0.15, 0.15);
            list.Add(Obstacle.Make(ObstacleKind.Crate, p, p));
        }
        return list;
    }

    static List<Obstacle> Bushes(ref Rng r, Vec2 c)
    {
        Vec2 half = Vec2.FromYaw(r.Range(0, DMath.Pi)) * r.Range(0, 1.3);
        return new List<Obstacle> { Obstacle.Make(ObstacleKind.Bush, c - half, c + half, r.Range(0.7, 1.1)) };
    }

    /// <summary>Spawns connected, few sealed pockets, and a second route that avoids a 3 m corridor around the shortest one.</summary>
    public static bool Validate(MapData m, out string reason)
    {
        var w = new World(m);
        var nav = w.Nav;
        Vec2 s0 = m.Spawns[0].Center, s1 = m.Spawns[1].Center;
        if (!nav.NearestFree(s0, 2, out s0) || !nav.NearestFree(s1, 2, out s1)) { reason = "spawn blocked"; return false; }
        var reach = nav.Reachable(s0);
        if (!reach[nav.IndexOf(s1)]) { reason = "spawns not connected"; return false; }
        int free = 0, seen = 0;
        for (int i = 0; i < reach.Length; i++) if (!nav.Blocked[i]) { free++; if (reach[i]) seen++; }
        if (seen < free * 0.85) { reason = "sealed pockets"; return false; }

        // shortest route as cells, then forbid a corridor around it (away from the spawns)
        var scratch = new NavScratch(nav);
        var path = new Vec2[256];
        var none = new NoExtraCost();
        int n = nav.FindPath(s0, s1, scratch, path, ref none);
        if (n == 0) { reason = "no path"; return false; }
        var blocked = new bool[nav.W * nav.H];
        Vec2 prev = s0;
        int rad = (int)Math.Ceiling(3.0 / NavGrid.Cell);
        for (int k = 0; k < n; k++)
        {
            double len = Vec2.Distance(prev, path[k]);
            int steps = Math.Max(1, (int)(len / (NavGrid.Cell * 0.5)));
            for (int q = 0; q <= steps; q++)
            {
                Vec2 p = Vec2.Lerp(prev, path[k], q / (double)steps);
                if (Vec2.Distance(p, s0) < 6 || Vec2.Distance(p, s1) < 6) continue;
                int c = nav.IndexOf(p), cx = c % nav.W, cy = c / nav.W;
                for (int dy = -rad; dy <= rad; dy++)
                    for (int dx = -rad; dx <= rad; dx++)
                    {
                        if (dx * dx + dy * dy > rad * rad) continue;
                        int x = cx + dx, y = cy + dy;
                        if (x >= 0 && y >= 0 && x < nav.W && y < nav.H) blocked[y * nav.W + x] = true;
                    }
            }
            prev = path[k];
        }
        var alt = nav.Reachable(s0, blocked);
        if (!alt[nav.IndexOf(s1)]) { reason = "single route"; return false; }
        reason = "";
        return true;
    }
}
