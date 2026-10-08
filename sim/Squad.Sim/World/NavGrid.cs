namespace Squad.Sim;

/// <summary>Extra cost of entering a navigation cell; lets a caller steer paths (e.g. away from known threats).</summary>
public interface IPathCost
{
    double Extra(int cell, Vec2 center);
}

public struct NoExtraCost : IPathCost
{
    public readonly double Extra(int cell, Vec2 center) => 0;
}

/// <summary>Per-match A* buffers.</summary>
public sealed class NavScratch
{
    internal readonly double[] G;
    internal readonly int[] Parent, Stamp;
    internal int Gen;
    internal readonly int[] Heap;
    internal readonly double[] HeapKey;
    internal readonly int[] Temp;
    public const int MaxExpand = 6000;

    public NavScratch(NavGrid nav)
    {
        int n = nav.W * nav.H;
        G = new double[n]; Parent = new int[n]; Stamp = new int[n];
        Heap = new int[MaxExpand * 8 + 16]; HeapKey = new double[Heap.Length];
        Temp = new int[n];
    }

    internal int NextGen()
    {
        if (++Gen == int.MaxValue) { Array.Clear(Stamp); Gen = 1; }
        return Gen;
    }
}

/// <summary>Walkable cells of 0.5 m: a cell is blocked when a soldier standing at its centre would overlap something solid.
/// 8-connected A* without corner cutting, then string pulling so paths are straight where the ground allows.</summary>
public sealed class NavGrid
{
    public const double Cell = 0.5;
    public readonly int W, H;
    public readonly bool[] Blocked;
    /// <summary>Movement cost multiplier per cell (foliage is slow).</summary>
    public readonly float[] Cost;

    internal NavGrid(World world, double radius)
    {
        W = Math.Max(1, (int)Math.Ceiling(world.Width / Cell));
        H = Math.Max(1, (int)Math.Ceiling(world.Height / Cell));
        Blocked = new bool[W * H];
        Cost = new float[W * H];
        var scratch = new WorldScratch(world);
        var v = new CellVisitor { Obs = world.Obstacles, Radius = radius };
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int i = y * W + x;
                Vec2 c = Center(i);
                if (c.X < radius || c.Y < radius || c.X > world.Width - radius || c.Y > world.Height - radius) { Blocked[i] = true; Cost[i] = 1; continue; }
                v.C = c; v.Blocked = false; v.Cost = 1;
                world.Nearby(c, radius + 0.05, ref v, scratch);
                Blocked[i] = v.Blocked;
                Cost[i] = v.Cost;
            }
    }

    struct CellVisitor : IObstacleVisitor
    {
        public Obstacle[] Obs;
        public Vec2 C;
        public double Radius;
        public bool Blocked;
        public float Cost;

        public bool Visit(int i)
        {
            ref var o = ref Obs[i];
            double d = Geometry.DistanceToSegment(C, o.A, o.B);
            if (o.BlocksMove) { if (d < o.R + Radius - 0.06) { Blocked = true; return false; } }
            else if (d < o.R) Cost = 1.6f;
            return true;
        }
    }

    public Vec2 Center(int i) => new(((i % W) + 0.5) * Cell, ((i / W) + 0.5) * Cell);
    public int IndexOf(Vec2 p) => Math.Clamp((int)(p.Y / Cell), 0, H - 1) * W + Math.Clamp((int)(p.X / Cell), 0, W - 1);
    public bool FreeAt(Vec2 p) => !Blocked[IndexOf(p)];

    /// <summary>Nearest free cell centre to p within maxRadius metres (p itself if its cell is free).</summary>
    public bool NearestFree(Vec2 p, double maxRadius, out Vec2 result)
    {
        int c = IndexOf(p);
        if (!Blocked[c]) { result = p; return true; }
        int cx = c % W, cy = c / W, R = (int)Math.Ceiling(maxRadius / Cell);
        double best = double.PositiveInfinity; int bi = -1;
        for (int r = 1; r <= R && bi < 0; r++)
            for (int y = cy - r; y <= cy + r; y++)
                for (int x = cx - r; x <= cx + r; x++)
                {
                    if (Math.Abs(x - cx) != r && Math.Abs(y - cy) != r) continue;
                    if (x < 0 || y < 0 || x >= W || y >= H) continue;
                    int i = y * W + x;
                    if (Blocked[i]) continue;
                    double d = Vec2.DistanceSq(Center(i), p);
                    if (d < best) { best = d; bi = i; }
                }
        result = bi >= 0 ? Center(bi) : p;
        return bi >= 0;
    }

    /// <summary>True if a soldier can walk straight from a to b: every cell the segment touches is free.</summary>
    public bool Walkable(Vec2 a, Vec2 b)
    {
        int x = Math.Clamp((int)(a.X / Cell), 0, W - 1), y = Math.Clamp((int)(a.Y / Cell), 0, H - 1);
        Vec2 d = b - a;
        int sx = d.X > 0 ? 1 : d.X < 0 ? -1 : 0, sy = d.Y > 0 ? 1 : d.Y < 0 ? -1 : 0;
        double tdx = sx != 0 ? Cell / Math.Abs(d.X) : double.PositiveInfinity, tdy = sy != 0 ? Cell / Math.Abs(d.Y) : double.PositiveInfinity;
        double tmx = sx > 0 ? ((x + 1) * Cell - a.X) / d.X : sx < 0 ? (x * Cell - a.X) / d.X : double.PositiveInfinity;
        double tmy = sy > 0 ? ((y + 1) * Cell - a.Y) / d.Y : sy < 0 ? (y * Cell - a.Y) / d.Y : double.PositiveInfinity;
        int guard = W + H + 4;
        while (guard-- > 0)
        {
            if (Blocked[y * W + x]) return false;
            // done once the next cell boundary lies at or past the end point
            if (Math.Min(tmx, tmy) >= 1 - 1e-9) return true;
            // passing exactly through a corner touches both neighbours
            if (Math.Abs(tmx - tmy) < 1e-12)
            {
                if (x + sx >= 0 && x + sx < W && Blocked[y * W + x + sx]) return false;
                if (y + sy >= 0 && y + sy < H && Blocked[(y + sy) * W + x]) return false;
                x += sx; y += sy; tmx += tdx; tmy += tdy;
            }
            else if (tmx < tmy) { x += sx; tmx += tdx; }
            else { y += sy; tmy += tdy; }
            if (x < 0 || y < 0 || x >= W || y >= H) return false;
        }
        return false;
    }

    /// <summary>Path from start to goal written as waypoints (excluding the start). If the goal cannot be reached within the
    /// expansion budget, the path leads to the explored cell closest to it. Returns the number of waypoints.</summary>
    public int FindPath<TCost>(Vec2 start, Vec2 goal, NavScratch s, Span<Vec2> output, ref TCost extra) where TCost : struct, IPathCost
    {
        if (output.Length == 0) return 0;
        NearestFree(start, 1.5, out Vec2 st);
        if (!NearestFree(goal, 3, out Vec2 gl)) return 0;
        int si = IndexOf(st), gi = IndexOf(gl);
        if (si == gi || Walkable(st, gl)) { output[0] = gl; return 1; }

        int gen = s.NextGen();
        int heapN = 0;
        s.G[si] = 0; s.Parent[si] = -1; s.Stamp[si] = gen;
        Push(s, ref heapN, si, Heuristic(si, gi));
        int bestI = si; double bestH = Heuristic(si, gi);
        int expanded = 0;
        int found = -1;
        // closed cells are marked with a negative stamp
        while (heapN > 0 && expanded < NavScratch.MaxExpand)
        {
            int cur = Pop(s, ref heapN, out double f);
            if (s.Stamp[cur] == -gen) continue;
            s.Stamp[cur] = -gen;
            expanded++;
            if (cur == gi) { found = cur; break; }
            double hcur = Heuristic(cur, gi);
            if (hcur < bestH) { bestH = hcur; bestI = cur; }
            int cx = cur % W, cy = cur / W;
            double gcur = s.G[cur];
            for (int k = 0; k < 8; k++)
            {
                int dx = DX[k], dy = DY[k];
                int nx = cx + dx, ny = cy + dy;
                if (nx < 0 || ny < 0 || nx >= W || ny >= H) continue;
                int ni = ny * W + nx;
                if (Blocked[ni] || s.Stamp[ni] == -gen) continue;
                if (dx != 0 && dy != 0 && (Blocked[cy * W + nx] || Blocked[ny * W + cx])) continue;
                double step = (dx != 0 && dy != 0 ? 1.41421356 : 1) * Cell * Cost[ni];
                double ng = gcur + step + extra.Extra(ni, Center(ni));
                if (s.Stamp[ni] == gen && s.G[ni] <= ng) continue;
                s.Stamp[ni] = gen; s.G[ni] = ng; s.Parent[ni] = cur;
                if (heapN < s.Heap.Length) Push(s, ref heapN, ni, ng + Heuristic(ni, gi));
            }
        }
        int end = found >= 0 ? found : bestI;
        if (end == si) return 0;

        // walk back, then string-pull greedily from the start
        int len = 0;
        for (int c = end; c != -1 && len < s.Temp.Length; c = s.Parent[c]) s.Temp[len++] = c;
        // Temp holds end … start; build the smoothed list forward
        int outN = 0;
        Vec2 anchor = st;
        int idx = len - 1;   // start cell
        while (idx > 0 && outN < output.Length)
        {
            // extend along the path while the straight line from the anchor stays walkable
            int far = idx - 1;
            for (int j = idx - 2; j >= 0; j--)
            {
                if (Walkable(anchor, Center(s.Temp[j]))) far = j;
                else break;
            }
            Vec2 wp = far == 0 && found >= 0 ? gl : Center(s.Temp[far]);
            output[outN++] = wp;
            anchor = wp;
            idx = far;
        }
        return outN;
    }

    static readonly int[] DX = { 1, -1, 0, 0, 1, 1, -1, -1 };
    static readonly int[] DY = { 0, 0, 1, -1, 1, -1, 1, -1 };

    double Heuristic(int a, int b)
    {
        int dx = Math.Abs(a % W - b % W), dy = Math.Abs(a / W - b / W);
        int mn = Math.Min(dx, dy), mx = Math.Max(dx, dy);
        return (mx - mn + mn * 1.41421356) * Cell;
    }

    static void Push(NavScratch s, ref int n, int item, double key)
    {
        int i = n++;
        while (i > 0)
        {
            int p = (i - 1) >> 1;
            if (s.HeapKey[p] <= key) break;
            s.Heap[i] = s.Heap[p]; s.HeapKey[i] = s.HeapKey[p]; i = p;
        }
        s.Heap[i] = item; s.HeapKey[i] = key;
    }

    static int Pop(NavScratch s, ref int n, out double key)
    {
        int top = s.Heap[0]; key = s.HeapKey[0];
        n--;
        if (n > 0)
        {
            int item = s.Heap[n]; double k = s.HeapKey[n];
            int i = 0;
            while (true)
            {
                int l = 2 * i + 1;
                if (l >= n) break;
                int r = l + 1;
                int m = r < n && s.HeapKey[r] < s.HeapKey[l] ? r : l;
                if (s.HeapKey[m] >= k) break;
                s.Heap[i] = s.Heap[m]; s.HeapKey[i] = s.HeapKey[m]; i = m;
            }
            s.Heap[i] = item; s.HeapKey[i] = k;
        }
        return top;
    }

    /// <summary>Cells reachable from p (flood fill), as a mask. Used by the map generator to reject broken maps.</summary>
    public bool[] Reachable(Vec2 p, bool[]? extraBlocked = null)
    {
        var seen = new bool[W * H];
        if (!NearestFree(p, 2, out Vec2 st)) return seen;
        var queue = new int[W * H];
        int qh = 0, qt = 0, s0 = IndexOf(st);
        if (extraBlocked != null && extraBlocked[s0]) return seen;
        seen[s0] = true; queue[qt++] = s0;
        while (qh < qt)
        {
            int c = queue[qh++], cx = c % W, cy = c / W;
            for (int k = 0; k < 4; k++)
            {
                int nx = cx + DX[k], ny = cy + DY[k];
                if (nx < 0 || ny < 0 || nx >= W || ny >= H) continue;
                int ni = ny * W + nx;
                if (seen[ni] || Blocked[ni] || (extraBlocked != null && extraBlocked[ni])) continue;
                seen[ni] = true; queue[qt++] = ni;
            }
        }
        return seen;
    }
}
