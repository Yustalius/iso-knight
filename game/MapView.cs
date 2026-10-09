using Godot;
using Squad.Sim;

/// <summary>The map dressed like the concept's firing range, built from the simulation's obstacles. The rule is that the
/// picture never lies about the rules: every obstacle's footprint and height match its capsule (A–B, R, H) within 5 cm,
/// and only details above 2 m, where neither bullets nor sight matter, may stick out: lintels, roofs, tree crowns.
///   high wall 2.5 m — a brick or plaster house wall, timber trims at the openings;
///   window — the 1.1 m low wall in a house line: wall up to the sill, opening, lintel from 2.1 m;
///   low wall 1.1 m — sandbags or concrete blocks; crate 1.0 m — a wooden crate (octagon within 2 cm of the circle);
///   bush 1.4 m — foliage that conceals; tree — a trunk of the capsule's radius and a crown above 2 m.
/// Walls, roofs, crowns and bushes that hide a soldier from the camera fade out, as in Project Zomboid.</summary>
sealed class MapView
{
    public readonly Node3D Root;
    /// <summary>Per obstacle: what a bullet hitting it throws (Fx.Impact kinds).</summary>
    public readonly string[] Kinds;
    readonly List<Occluder> _occ = new();
    readonly List<(MeshInstance3D mesh, float top)> _cutWalls = new();
    readonly ShaderMaterial _grass;
    readonly Random _r;

    float R() => (float)_r.NextDouble();
    static Vector3 V(float x, float y, float z) => new(x, y, z);
    static Vector3 W(Vec2 p, double h = 0) => new((float)p.X, (float)h, (float)p.Y);

    // ---------- materials: the range's painted 256 px maps, projected in world space (one tile per `size` metres) ----------
    static readonly Dictionary<string, StandardMaterial3D> _mats = new();
    static StandardMaterial3D M(string tex, float size, float rough = 1, float metal = 0, bool triplanar = true)
    {
        string key = $"{tex}/{size}/{triplanar}";
        if (_mats.TryGetValue(key, out var m)) return m;
        m = new StandardMaterial3D
        {
            AlbedoTexture = Art.Texture($"tex/w_{tex}.png"), Roughness = rough, Metallic = metal,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic, TextureRepeat = true,
        };
        if (triplanar) { m.Uv1Triplanar = true; m.Uv1WorldTriplanar = true; m.Uv1Scale = Vector3.One / size; m.Uv1TriplanarSharpness = 4; }
        return _mats[key] = m;
    }
    public static void ClearMaterials() { _mats.Clear(); _weathered = null; }
    static StandardMaterial3D? _weathered;
    static StandardMaterial3D Tinted(StandardMaterial3D m, float k) { var t = (StandardMaterial3D)m.Duplicate(); t.AlbedoColor = new Color(k, k, k); return t; }

    /// <summary>Collects primitive meshes per material and bakes them into one ArrayMesh with a surface per material.</summary>
    sealed class Builder
    {
        readonly Dictionary<Material, SurfaceTool> _st = new();
        public void Add(Material m, Mesh mesh, Transform3D t)
        {
            if (!_st.TryGetValue(m, out var s)) { s = new SurfaceTool(); s.Begin(Mesh.PrimitiveType.Triangles); s.SetMaterial(m); _st[m] = s; }
            for (int i = 0; i < mesh.GetSurfaceCount(); i++) s.AppendFrom(mesh, i, t);
        }
        public void Box(Material m, Transform3D t, Vector3 size) => Add(m, new BoxMesh { Size = size }, t);
        public bool Empty => _st.Count == 0;
        public ArrayMesh Commit() { var mesh = new ArrayMesh(); foreach (var s in _st.Values) s.Commit(mesh); return mesh; }
    }

    MeshInstance3D Place(Builder b, Node3D parent, bool shadow = true)
    {
        var mi = new MeshInstance3D { Mesh = b.Commit(), CastShadow = shadow ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off };
        parent.AddChild(mi);
        return mi;
    }

    /// <summary>A frame on a segment: origin at a, +X along a→b, +Y up, +Z = X × Y.</summary>
    static Transform3D Along(Vec2 a, Vec2 b, float y = 0)
    {
        var d = W(b) - W(a); float L = d.Length();
        var x = L > 1e-6f ? d / L : Vector3.Right; var z = x.Cross(Vector3.Up);   // right-handed: x × y = z
        return new Transform3D(new Basis(x, Vector3.Up, z), W(a, y));
    }

    public MapView(Squad.Sim.World world, Node parent, ulong seed)
    {
        _r = new Random((int)(seed * 2654435761 % int.MaxValue));
        Root = new Node3D { Name = "Map" };
        parent.AddChild(Root);
        var obs = world.Obstacles;
        Kinds = new string[obs.Length];
        float ww = (float)world.Width, wh = (float)world.Height;

        BuildGround(world);
        BuildHouses(obs);
        var bags = new List<Transform3D>(); var bagTint = new List<Color>();
        for (int i = 0; i < obs.Length; i++)
        {
            var o = obs[i];
            switch (o.Kind)
            {
                case ObstacleKind.LowWall when o.R >= 0.15:
                    if (Hash(o) % 3 == 0) { ConcreteWall(o); Kinds[i] = "concrete"; }
                    else { Sandbags(o, bags, bagTint); Kinds[i] = "sand"; }
                    break;
                case ObstacleKind.Crate: Crate(o); Kinds[i] = "wood"; break;
                case ObstacleKind.Tree: Tree(o); Kinds[i] = "wood"; break;
                case ObstacleKind.Bush: Bush(o); Kinds[i] = "leaf"; break;
            }
        }
        BuildBags(bags, bagTint);
        _grass = BuildGrass(world);
    }

    static uint Hash(in Obstacle o)
    {
        unchecked
        {
            uint h = 2166136261;
            foreach (double v in new[] { o.A.X, o.A.Y, o.B.X, o.B.Y }) { h ^= (uint)(int)Math.Round(v * 1000); h *= 16777619; }
            return h ^ (h >> 13);
        }
    }

    /// <summary>The concept's ground colours are linear numbers; this matches their brightness to its frames.</summary>
    public static float GroundTint = 0.75f;

    // ───────── ground: grass with a macro tint (range-world.js), trodden spawns, a dark apron ─────────

    void BuildGround(Squad.Sim.World world)
    {
        float ww = (float)world.Width, wh = (float)world.Height, pad = 14;
        int nx = (int)((ww + 2 * pad) / 1.5f), nz = (int)((wh + 2 * pad) / 1.5f);
        var st = new SurfaceTool(); st.Begin(Mesh.PrimitiveType.Triangles);
        var bg = Main.Bg.SrgbToLinear();
        var spawns = world.Map.Spawns;
        for (int j = 0; j <= nz; j++)
            for (int i = 0; i <= nx; i++)
            {
                float x = -pad + i * (ww + 2 * pad) / nx, z = -pad + j * (wh + 2 * pad) / nz;
                float n = MathF.Sin(x * 0.55f + MathF.Sin(z * 0.4f) * 2) * 0.5f + MathF.Sin(z * 0.73f - x * 0.21f) * 0.5f;
                var c = new Color(0.38f + n * 0.05f, 0.46f + n * 0.05f, 0.28f + n * 0.03f);
                // spawn zones: the grass is a little trodden there, barely visible
                foreach (var sz in spawns)
                    if (x > sz.Min.X - 0.5 && x < sz.Max.X + 0.5 && z > sz.Min.Y - 0.5 && z < sz.Max.Y + 0.5) c = c.Lerp(new Color(0.46f, 0.42f, 0.3f), 0.18f);
                float out_ = MathF.Max(MathF.Max(-x, x - ww), MathF.Max(-z, z - wh));
                c = c.Lerp(bg, Mathf.SmoothStep(1, 12, out_));
                st.SetColor((c * GroundTint).LinearToSrgb()); st.SetUV(new Vector2(x, z) / 2.2f); st.SetNormal(Vector3.Up);
                st.AddVertex(V(x, 0, z));
            }
        for (int j = 0; j < nz; j++)
            for (int i = 0; i < nx; i++)
            {
                int a = j * (nx + 1) + i, b = a + 1, c = a + nx + 1, d = c + 1;
                st.AddIndex(a); st.AddIndex(b); st.AddIndex(c);
                st.AddIndex(b); st.AddIndex(d); st.AddIndex(c);
            }
        var mat = new StandardMaterial3D
        {
            AlbedoTexture = Art.Texture("tex/w_grass.png"), VertexColorUseAsAlbedo = true, VertexColorIsSrgb = true, Roughness = 1,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic, TextureRepeat = true,
        };
        // three.js multiplies by vertex colours in linear space; the colours above are linear numbers like the concept's
        var g = new MeshInstance3D { Mesh = st.Commit(), MaterialOverride = mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        Root.AddChild(g);
    }

    // ───────── houses: high walls and windows grouped by shared endpoints ─────────

    sealed class Occluder
    {
        public readonly List<MeshInstance3D> Meshes = new();
        public Vec2 A, B; public float R, Lo, Hi;   // footprint and the heights it covers
        public bool Box; public Vec2 Min, Max;        // a roof: occupied rectangle (soldiers inside hide it)
        public float Alpha = 1;
        public bool CutHide;
        public readonly Dictionary<Material, StandardMaterial3D> Faded = new();
    }

    void BuildHouses(Obstacle[] obs)
    {
        var idx = new List<int>();
        for (int i = 0; i < obs.Length; i++)
            if (obs[i].Kind == ObstacleKind.HighWall || (obs[i].Kind == ObstacleKind.LowWall && obs[i].R < 0.15)) idx.Add(i);
        static bool Same(Vec2 p, Vec2 q) => Vec2.DistanceSq(p, q) < 1e-6;
        // loose wall ends (not shared with another piece): door jambs, or the end of a free-standing wall
        var ends = new List<(int piece, Vec2 p, Vec2 dir)>();
        foreach (int i in idx)
        {
            var o = obs[i];
            foreach (var (p, q) in new[] { (o.A, o.B), (o.B, o.A) })
                if (!idx.Any(j => j != i && (Same(obs[j].A, p) || Same(obs[j].B, p)))) ends.Add((i, p, (q - p) / Vec2.Distance(p, q)));
        }
        // doors: two loose ends on one line, facing each other across a gap
        var doors = new List<(int a, int b, Vec2 p, Vec2 q)>();
        var used = new HashSet<int>();
        for (int a = 0; a < ends.Count; a++)
            for (int b = a + 1; b < ends.Count; b++)
            {
                if (used.Contains(a) || used.Contains(b)) continue;
                var (ia, p, dp) = ends[a]; var (ib, q, dq) = ends[b];
                double d = Vec2.Distance(p, q);
                if (d < 0.5 || d > 1.9 || dp.Dot(dq) > -0.99 || (q - p).Dot(dp) / d > -0.99) continue;
                used.Add(a); used.Add(b);
                doors.Add((ia, ib, p, q));
            }
        // houses: pieces joined at their ends or across a door
        var parent = idx.ToDictionary(i => i, i => i);
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        foreach (int a in idx) foreach (int b in idx)
            if (a < b && (Same(obs[a].A, obs[b].A) || Same(obs[a].A, obs[b].B) || Same(obs[a].B, obs[b].A) || Same(obs[a].B, obs[b].B)))
                parent[Find(a)] = Find(b);
        foreach (var dr in doors) parent[Find(dr.a)] = Find(dr.b);
        foreach (var group in idx.GroupBy(Find))
        {
            var pieces = group.ToList();
            bool brick = Hash(obs[pieces.Min()]) % 2 == 0;
            var wallMat = brick ? M("brick", 1.6f) : M("plaster", 2.2f);
            var trim = M("timber", 1.2f);
            Vec2 min = new(double.MaxValue, double.MaxValue), max = new(double.MinValue, double.MinValue);
            foreach (int i in pieces)
            {
                var o = obs[i];
                Kinds[i] = "concrete";   // brick or plaster: chips and grey dust
                min = new Vec2(Math.Min(min.X, Math.Min(o.A.X, o.B.X)), Math.Min(min.Y, Math.Min(o.A.Y, o.B.Y)));
                max = new Vec2(Math.Max(max.X, Math.Max(o.A.X, o.B.X)), Math.Max(max.Y, Math.Max(o.A.Y, o.B.Y)));
                if (o.Kind == ObstacleKind.HighWall) WallPiece(o, wallMat);
                else Window(o, wallMat, trim);
            }
            foreach (var dr in doors) if (Find(dr.a) == group.Key) Door(dr.p, dr.q, obs[dr.a].R, wallMat, trim);
            // a closed outline is a house: floor and roof; a lone wall stays a wall
            if (pieces.Count >= 4 && max.X - min.X > 2 && max.Y - min.Y > 2) { Floor(min, max); Roof(min, max); }
        }
    }

    void WallPiece(in Obstacle o, Material wallMat)
    {
        float len = (float)Vec2.Distance(o.A, o.B), r = (float)o.R, h = (float)o.H;
        var b = new Builder();
        var t = Along(o.A, o.B);
        b.Box(wallMat, t * new Transform3D(Basis.Identity, V(len / 2, h / 2, 0)), V(len + 2 * r, h, 2 * r));
        var mi = Place(b, Root);
        _cutWalls.Add((mi, h));
        AddOccluder(mi, o.A, o.B, r, 0, h);
    }

    /// <summary>Wall up to the 1.1 m sill (the low wall itself), a timber sill and trims, the opening, a lintel from 2.1 m.</summary>
    void Window(in Obstacle o, Material wallMat, Material trim)
    {
        float len = (float)Vec2.Distance(o.A, o.B), r = (float)o.R, h = (float)o.H;
        var t = Along(o.A, o.B);
        var low = new Builder();
        low.Box(wallMat, t * new Transform3D(Basis.Identity, V(len / 2, h / 2, 0)), V(len, h, 2 * r));
        low.Box(trim, t * new Transform3D(Basis.Identity, V(len / 2, h - 0.03f, 0)), V(len + 0.08f, 0.06f, 2 * r + 0.05f));   // sill
        var lowMi = Place(low, Root);
        _cutWalls.Add((lowMi, h));
        AddOccluder(lowMi, o.A, o.B, r, 0, h);
        Opening(t, len, r, h, 2.1f, wallMat, trim, o.A, o.B);
    }

    void Door(Vec2 p, Vec2 q, double r, Material wallMat, Material trim)
    {
        var t = Along(p, q); float len = (float)Vec2.Distance(p, q);
        // the wall boxes reach r into the opening, as the capsules' round ends do; trims sit on those faces
        var tt = t * new Transform3D(Basis.Identity, V((float)r, 0, 0));
        Opening(tt, len - 2 * (float)r, (float)r, 0, 2.1f, wallMat, trim, p, q);
    }

    /// <summary>Jambs from `bottom` to the head, a head trim and a lintel above 2 m over an opening of length len.</summary>
    void Opening(Transform3D t, float len, float r, float bottom, float head, Material wallMat, Material trim, Vec2 a, Vec2 b)
    {
        var top = new Builder();
        top.Box(wallMat, t * new Transform3D(Basis.Identity, V(len / 2, (head + 2.5f) / 2, 0)), V(len, 2.5f - head, 2 * r));
        top.Box(trim, t * new Transform3D(Basis.Identity, V(len / 2, head - 0.03f, 0)), V(len, 0.06f, 2 * r + 0.03f));
        var topMi = Place(top, Root);
        AddOccluder(topMi, a, b, r, head - 0.06f, 2.5f).CutHide = true;
        var jambs = new Builder();
        foreach (float x in new[] { 0.03f, len - 0.03f })
            jambs.Box(trim, t * new Transform3D(Basis.Identity, V(x, (bottom + head) / 2, 0)), V(0.06f, head - bottom, 2 * r + 0.03f));
        var jMi = Place(jambs, Root);
        _cutWalls.Add((jMi, head));
        AddOccluder(jMi, a, b, r, bottom, head);
    }

    void Floor(Vec2 min, Vec2 max)
    {
        var mi = new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2((float)(max.X - min.X), (float)(max.Y - min.Y)) },
            MaterialOverride = M("oak", 1.4f), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Position = W((min + max) * 0.5, 0.006),
        };
        Root.AddChild(mi);
    }

    /// <summary>A hip roof of corrugated tin from 2.55 m: above everything that matters for the rules.</summary>
    void Roof(Vec2 min, Vec2 max)
    {
        float o = 0.35f, y0 = 2.55f;
        float x0 = (float)min.X - o, x1 = (float)max.X + o, z0 = (float)min.Y - o, z1 = (float)max.Y + o;
        float rise = MathF.Min(x1 - x0, z1 - z0) * 0.32f;
        bool alongX = x1 - x0 >= z1 - z0;
        float cx = (x0 + x1) / 2, cz = (z0 + z1) / 2, half = MathF.Min(x1 - x0, z1 - z0) / 2;
        Vector3 r0 = alongX ? V(x0 + half, y0 + rise, cz) : V(cx, y0 + rise, z0 + half);
        Vector3 r1 = alongX ? V(x1 - half, y0 + rise, cz) : V(cx, y0 + rise, z1 - half);
        Vector3 c00 = V(x0, y0, z0), c10 = V(x1, y0, z0), c11 = V(x1, y0, z1), c01 = V(x0, y0, z1);
        var st = new SurfaceTool(); st.Begin(Mesh.PrimitiveType.Triangles);
        void Tri(Vector3 a, Vector3 b, Vector3 c)
        {
            var n = (b - a).Cross(c - a).Normalized(); if (n.Y < 0) { (b, c) = (c, b); n = -n; }
            // Godot's front faces wind clockwise seen from outside
            foreach (var p in new[] { a, c, b }) { st.SetNormal(n); st.SetUV(new Vector2(p.X, p.Z)); st.AddVertex(p); }
        }
        if (alongX) { Tri(c00, c10, r1); Tri(c00, r1, r0); Tri(c11, c01, r0); Tri(c11, r0, r1); Tri(c10, c11, r1); Tri(c01, c00, r0); }
        else { Tri(c01, c00, r0); Tri(c01, r0, r1); Tri(c10, c11, r1); Tri(c10, r1, r0); Tri(c00, c10, r0); Tri(c11, c01, r1); }
        st.SetMaterial(M("tin", 1.6f, 0.7f, 0.4f));
        var mi = new MeshInstance3D { Mesh = st.Commit() };
        // eaves trim: dark timber boards along the edges
        Root.AddChild(mi);
        var occ = new Occluder { Box = true, CutHide = true, Min = min, Max = max, Lo = y0, Hi = y0 + rise, A = new Vec2(cx, cz), B = new Vec2(cx, cz), R = MathF.Max(x1 - x0, z1 - z0) / 2 };
        occ.Meshes.Add(mi); _occ.Add(occ);
    }

    Occluder AddOccluder(MeshInstance3D mi, Vec2 a, Vec2 b, float r, float lo, float hi)
    {
        var o = new Occluder { A = a, B = b, R = r, Lo = lo, Hi = hi };
        o.Meshes.Add(mi); _occ.Add(o);
        return o;
    }

    // ───────── low walls: sandbags (range-world.js bags) or cast concrete blocks ─────────

    static ArrayMesh? _bag;
    static ArrayMesh BagMesh()
    {
        if (_bag != null) return _bag;
        // a filled bag: a superellipsoid, flat on top and bottom, pinched where the seams are
        var sphere = new SphereMesh { Radius = 0.5f, Height = 1, RadialSegments = 10, Rings = 6 };
        var arr = sphere.GetMeshArrays();
        var pos = (Vector3[])arr[(int)Mesh.ArrayType.Vertex];
        static float Sg(float v, float e) => MathF.Sign(v) * MathF.Pow(MathF.Abs(v), e);
        for (int i = 0; i < pos.Length; i++)
        {
            float x = pos[i].X * 2, y = pos[i].Y * 2, z = pos[i].Z * 2;
            pos[i] = V(Sg(x, 0.45f) * 0.5f * (1 - 0.12f * y * y), Sg(y, 0.8f) * 0.5f, Sg(z, 0.55f) * 0.5f * (1 - 0.2f * y * y));
        }
        arr[(int)Mesh.ArrayType.Vertex] = pos;
        arr[(int)Mesh.ArrayType.Normal] = default;
        arr[(int)Mesh.ArrayType.Tangent] = default;
        var tmp = new ArrayMesh(); tmp.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arr);
        var st = new SurfaceTool(); st.CreateFrom(tmp, 0); st.GenerateNormals();
        return _bag = st.Commit();
    }

    void Sandbags(in Obstacle o, List<Transform3D> bags, List<Color> tint)
    {
        // courses of 0.5 m bags from end to end of the capsule, the top course at 1.1 m, as deep as the capsule
        float len = (float)Vec2.Distance(o.A, o.B), r = (float)o.R, h = (float)o.H, total = len + 2 * r;
        var t = Along(o.A, o.B) * new Transform3D(Basis.Identity, V(-r, 0, 0));
        const float bagH = 0.17f;
        int courses = Math.Max(2, (int)MathF.Round(h / 0.135f));
        float step = (h - bagH) / (courses - 1);
        Color[] cols = { new("ffffff"), new("eee6d4"), new("f6efe0"), new("c4cba0"), new("d9d2bd") };
        for (int c = 0; c < courses; c++)
        {
            int n = Math.Max(1, (int)MathF.Round((total - (c % 2) * 0.25f) / 0.5f));
            float bl = (total - (c % 2) * 0.25f) / n;
            for (int k = 0; k < n; k++)
            {
                float s = (c % 2) * 0.125f + bl * (k + 0.5f);
                var basis = Basis.FromEuler(V((R() - 0.5f) * 0.06f, (R() - 0.5f) * 0.12f, (R() - 0.5f) * 0.06f)).Scaled(V(bl * 0.98f, bagH, 2 * r));
                bags.Add(t * new Transform3D(basis, V(s, bagH / 2 + c * step, (R() - 0.5f) * 0.02f)));
                tint.Add(cols[_r.Next(cols.Length)]);
            }
        }
    }

    void BuildBags(List<Transform3D> bags, List<Color> tint)
    {
        if (bags.Count == 0) return;
        var mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = true, Mesh = BagMesh(), InstanceCount = bags.Count };
        for (int i = 0; i < bags.Count; i++) { mm.SetInstanceTransform(i, bags[i]); mm.SetInstanceColor(i, tint[i]); }
        var mat = (StandardMaterial3D)M("sand", 0.6f, triplanar: false).Duplicate();
        mat.VertexColorUseAsAlbedo = true;
        Root.AddChild(new MultiMeshInstance3D { Multimesh = mm, MaterialOverride = mat });
    }

    /// <summary>Cast concrete blocks with round ends: exactly the capsule, in 1.2 m sections.</summary>
    void ConcreteWall(in Obstacle o)
    {
        float len = (float)Vec2.Distance(o.A, o.B), r = (float)o.R, h = (float)o.H;
        var t = Along(o.A, o.B);
        var b = new Builder();
        // weathered: the range's jersey barriers read darker than the bare texture under this sun
        var conc = _weathered ??= Tinted(M("concrete", 1.3f), 0.78f);
        b.Box(conc, t * new Transform3D(Basis.Identity, V(len / 2, h / 2, 0)), V(len, h, 2 * r));
        foreach (float x in new[] { 0f, len })
            b.Add(conc, new CylinderMesh { TopRadius = r, BottomRadius = r, Height = h, RadialSegments = 14, Rings = 1 }, t * new Transform3D(Basis.Identity, V(x, h / 2, 0)));
        // section joints: dark grooves 3 mm proud of the faces
        var joint = M("timber", 1);
        int n = Math.Max(1, (int)MathF.Round((len + 2 * r) / 1.2f));
        for (int k = 1; k < n; k++)
        {
            float x = -r + k * (len + 2 * r) / n;
            if (x > 0 && x < len) b.Box(joint, t * new Transform3D(Basis.Identity, V(x, h / 2, 0)), V(0.02f, h - 0.04f, 2 * r + 0.006f));
        }
        Place(b, Root);
    }

    /// <summary>An olive ammunition crate with a timber frame; an octagon within 2 cm of the 0.45 m circle of the rules.</summary>
    void Crate(in Obstacle o)
    {
        float r = (float)o.R, h = (float)o.H, apothem = 0.9606f * r, circ = apothem / MathF.Cos(MathF.PI / 8);
        var b = new Builder();
        var rot = new Basis(Vector3.Up, MathF.PI / 8 + R() * MathF.PI / 4);
        var t = new Transform3D(rot, W(o.A));
        b.Add(M("crate", 0.8f), new CylinderMesh { TopRadius = circ, BottomRadius = circ, Height = h, RadialSegments = 8, Rings = 1 }, t * new Transform3D(Basis.Identity, V(0, h / 2, 0)));
        // corner posts and a lid cross: timber strips no more than 1 cm proud of the faces
        var timber = M("timber", 0.9f);
        for (int k = 0; k < 8; k++)
        {
            float a = k * MathF.PI / 4;
            b.Box(timber, t * new Transform3D(new Basis(Vector3.Up, -a), V(MathF.Sin(a) * circ * 0.985f, h / 2, MathF.Cos(a) * circ * 0.985f)), V(0.05f, h, 0.05f));
        }
        foreach (float a in new[] { 0f, MathF.PI / 2 })
            b.Box(timber, t * new Transform3D(new Basis(Vector3.Up, a + MathF.PI / 8), V(0, h + 0.005f, 0)), V(0.07f, 0.012f, 2 * apothem));
        Place(b, Root);
    }

    // ───────── foliage: lumpy smooth blobs (range-world.js foliage()) ─────────

    static ArrayMesh[]? _blobs;
    static ArrayMesh Blob(Random r)
    {
        _blobs ??= Enumerable.Range(0, 4).Select(k => MakeBlob(new Random(100 + k), 0.12f)).ToArray();
        return _blobs[r.Next(_blobs.Length)];
    }
    static ArrayMesh MakeBlob(Random r, float lumps)
    {
        var sphere = new SphereMesh { Radius = 1, Height = 2, RadialSegments = 14, Rings = 8 };
        var arr = sphere.GetMeshArrays();
        var pos = (Vector3[])arr[(int)Mesh.ArrayType.Vertex];
        float a = (float)r.NextDouble() * 6, b = (float)r.NextDouble() * 6;
        for (int i = 0; i < pos.Length; i++)
        {
            var n = pos[i].Normalized();
            float k = 1 + lumps * (MathF.Sin(n.X * 3.1f + a) * MathF.Sin(n.Y * 2.7f + b) * MathF.Sin(n.Z * 3.3f + a + b)) + lumps * 0.5f * MathF.Sin(n.X * 7 + n.Z * 6 + b);
            pos[i] = pos[i] * k;
        }
        arr[(int)Mesh.ArrayType.Vertex] = pos;
        arr[(int)Mesh.ArrayType.Normal] = default;
        arr[(int)Mesh.ArrayType.Tangent] = default;
        var tmp = new ArrayMesh(); tmp.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arr);
        var st = new SurfaceTool(); st.CreateFrom(tmp, 0); st.GenerateNormals();
        return st.Commit();
    }
    Material Leaf() => M("leaf" + _r.Next(3), 1.3f, triplanar: false);

    /// <summary>Foliage filling the capsule up to 1.4 m: blobs along the segment, R wide and H tall.</summary>
    void Bush(in Obstacle o)
    {
        float len = (float)Vec2.Distance(o.A, o.B), r = (float)o.R, h = (float)o.H;
        int n = Math.Max(1, (int)MathF.Ceiling(len / (r * 0.6f)) + 1);
        var b = new Builder();
        for (int k = 0; k < n; k++)
        {
            var p = n == 1 ? (o.A + o.B) * 0.5 : o.A + (o.B - o.A) * ((double)k / (n - 1));
            // lumps reach ±12 %: the blob's mean radius is a little less than R so the bumps stay inside
            var basis = new Basis(Vector3.Up, R() * 6).Scaled(V(r * 0.93f, h * 0.5f * 0.93f, r * 0.93f));
            b.Add(Leaf(), Blob(_r), new Transform3D(basis, W(p, h * 0.5f)));
        }
        var mi = Place(b, Root);
        var occ = new Occluder { A = o.A, B = o.B, R = r, Lo = 0, Hi = h };
        occ.Meshes.Add(mi); _occ.Add(occ);
    }

    /// <summary>A trunk of the capsule's radius up to 3 m and a crown of blobs that starts above 2 m.</summary>
    void Tree(in Obstacle o)
    {
        float r = (float)o.R, h = (float)o.H, s = 1.25f + R() * 0.35f;
        var trunk = new Builder();
        trunk.Add(M("bark", 1.0f), new CylinderMesh { TopRadius = r, BottomRadius = r, Height = h, RadialSegments = 9, Rings = 1 }, new Transform3D(Basis.Identity, W(o.A, h / 2)));
        trunk.Add(M("bark", 1.0f), new CylinderMesh { TopRadius = r * 0.45f, BottomRadius = r, Height = 1.2f, RadialSegments = 9, Rings = 1 }, new Transform3D(Basis.Identity, W(o.A, h + 0.6f)));
        var branch = new Basis(Vector3.Back, -0.7f);
        trunk.Add(M("bark", 1.0f), new CylinderMesh { TopRadius = 0.04f * s, BottomRadius = 0.07f * s, Height = 0.8f * s, RadialSegments = 6, Rings = 1 }, new Transform3D(branch, W(o.A, 2.6f) + V(0.25f * s, 0, 0)));
        Place(trunk, Root);
        // range-world.js tree(): seven clumps, lifted so the lowest leaves stay above 2.05 m
        float[][] clumps = { new[] { 0f, 1.65f, 0f, 0.82f }, new[] { 0.36f, 2.05f, 0.14f, 0.66f }, new[] { -0.3f, 2.3f, -0.16f, 0.58f }, new[] { 0.05f, 2.7f, 0.05f, 0.46f }, new[] { -0.38f, 1.8f, 0.28f, 0.5f }, new[] { 0.3f, 1.6f, -0.3f, 0.5f }, new[] { -0.1f, 2.15f, 0.38f, 0.48f } };
        float lowest = clumps.Min(c => (c[1] - c[3] * 1.12f) * s), lift = MathF.Max(0, 2.05f - lowest);
        var crown = new Builder();
        var turn = new Basis(Vector3.Up, R() * 6);
        foreach (var c in clumps)
        {
            var basis = Basis.FromEuler(V(R() * 3, R() * 3, 0)).Scaled(V(c[3] * s, c[3] * s, c[3] * s));
            crown.Add(Leaf(), Blob(_r), new Transform3D(basis, W(o.A) + turn * V(c[0] * s, c[1] * s + lift, c[2] * s)));
        }
        var mi = Place(crown, Root);
        var occ = new Occluder { A = o.A, B = o.A, R = 1.0f * s, Lo = 2.05f, Hi = (2.7f + 0.46f * 1.12f) * s + lift };
        occ.Meshes.Add(mi); _occ.Add(occ);
    }

    // ───────── grass: instanced tufts swaying in the wind (range-world.js) ─────────

    ShaderMaterial BuildGrass(Squad.Sim.World world)
    {
        // one tuft = 9 curved, tapering blades, dark at the root and sunlit at the tip
        var st = new SurfaceTool(); st.Begin(Mesh.PrimitiveType.Triangles);
        var rnd = new Random(1993);
        float Rn() => (float)rnd.NextDouble();
        int vi = 0;
        for (int k = 0; k < 9; k++)
        {
            float ang = k / 9f * MathF.PI * 2 + Rn() * 0.6f, lean = 0.2f + Rn() * 0.4f, h = 0.13f + Rn() * 0.15f, w = 0.016f + Rn() * 0.008f;
            var off = V((Rn() - 0.5f) * 0.08f, 0, (Rn() - 0.5f) * 0.08f);
            var rot = new Basis(Vector3.Up, ang);
            for (int j = 0; j <= 2; j++)
            {
                float t = j / 2f, half = w * (1 - t * 0.95f) / 2, bend = lean * t * t * h, y = h * t * (1 - 0.12f * t), c = 0.38f + 0.62f * t;
                foreach (float sx in new[] { -1f, 1f })
                {
                    st.SetColor(new Color(c, c, c)); st.SetNormal(rot * V(0, 0.4f, 1).Normalized());
                    st.AddVertex(rot * V(sx * half, y, bend) + off);
                }
            }
            for (int j = 0; j < 2; j++) { int a = vi + j * 2; st.AddIndex(a); st.AddIndex(a + 2); st.AddIndex(a + 1); st.AddIndex(a + 1); st.AddIndex(a + 2); st.AddIndex(a + 3); }
            vi += 6;
        }
        var tuft = st.Commit();
        var mat = new ShaderMaterial { Shader = new Shader { Code = @"
shader_type spatial;
render_mode cull_disabled;
uniform float wind;
void vertex() {
    // blade tips sway with a slow travelling gust, roots stay put (range-world.js)
    vec3 ip = MODEL_MATRIX[3].xyz;
    float h = VERTEX.y * VERTEX.y * 30.0;
    VERTEX.x += h * (0.05 * sin(wind * 1.7 + ip.x * 0.6 + ip.z * 0.3) + 0.02 * sin(wind * 4.1 + ip.z * 1.7));
    VERTEX.z += h * 0.03 * sin(wind * 1.3 + ip.z * 0.5 - ip.x * 0.2);
}
void fragment() {
    ALBEDO = COLOR.rgb;
    if (!FRONT_FACING) NORMAL = -NORMAL;   // both sides of a blade are lit like its front (three.js DoubleSide)
    ROUGHNESS = 1.0;
    SPECULAR = 0.2;
}" } };
        // tall grass grows in patches (a smooth density field), short and sparse in between; none on floors and solids
        float ww = (float)world.Width, wh = (float)world.Height;
        int want = (int)(ww * wh * GrassDensity);
        var xf = new List<Transform3D>(want); var col = new List<Color>(want);
        Color[] cols = { new("55703a"), new("4a672f"), new("617c40"), new("6e7f46"), new("435e2a") };
        float Patchy(float x, float z) => Mathf.SmoothStep(-0.35f, 0.55f, MathF.Sin(x * 0.62f + MathF.Sin(z * 0.45f) * 2.1f) * 0.5f + MathF.Sin(z * 0.81f - x * 0.33f + 1.3f) * 0.5f);
        var solids = world.Obstacles.Where(o => o.Kind != ObstacleKind.Bush).ToArray();
        var houses = _occ.Where(o => o.Box).Select(o => (o.Min, o.Max)).ToArray();
        for (int tries = 0; xf.Count < want && tries < want * 4; tries++)
        {
            float x = -6 + R() * (ww + 12), z = -6 + R() * (wh + 12);
            float d = Patchy(x, z);
            if (R() > 0.3f + 0.7f * d) continue;
            var p = new Vec2(x, z);
            bool blocked = false;
            foreach (var (mn, mx) in houses) if (x > mn.X - 0.1 && x < mx.X + 0.1 && z > mn.Y - 0.1 && z < mx.Y + 0.1) { blocked = true; break; }
            if (!blocked) foreach (var o in solids) if (Geometry.DistanceToSegment(p, o.A, o.B) < o.R + 0.05) { blocked = true; break; }
            if (blocked) continue;
            float sc = 0.6f + 0.55f * d + R() * 0.5f;
            var basis = Basis.FromEuler(V((R() - 0.5f) * 0.25f, R() * 6, (R() - 0.5f) * 0.25f)).Scaled(V(sc * (0.9f + R() * 0.3f), sc, sc * (0.9f + R() * 0.3f)));
            xf.Add(new Transform3D(basis, V(x, 0, z)));
            col.Add(cols[_r.Next(cols.Length)] * (0.9f + R() * 0.2f));
        }
        var mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = true, Mesh = tuft, InstanceCount = xf.Count };
        for (int i = 0; i < xf.Count; i++) { mm.SetInstanceTransform(i, xf[i]); mm.SetInstanceColor(i, col[i]); }
        Root.AddChild(new MultiMeshInstance3D { Multimesh = mm, MaterialOverride = mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        GrassCount = xf.Count;
        return mat;
    }
    public static float GrassDensity = 12;   // tufts per m² of the field, as on the concept's range
    public int GrassCount;

    // ───────── per frame: wind and the Zomboid cut ─────────

    static readonly float Cot30 = MathF.Sqrt(3);

    /// <summary>soldiers: ground positions of the living soldiers; camYaw: the camera's heading; cut: the manual H cut.</summary>
    public void Update(float time, float dt, float camYaw, ReadOnlySpan<Vector3> soldiers, bool cut)
    {
        _grass.SetShaderParameter("wind", time);
        // ground axes of the view: u to the right on screen, d away from the camera
        var fwd = -V(MathF.Sin(camYaw), 0, MathF.Cos(camYaw)); var right = V(-fwd.Z, 0, fwd.X);
        foreach (var o in _occ)
        {
            bool hide = false;
            if (!(cut && o.CutHide))
                foreach (var s in soldiers)
                {
                    if (o.Box)
                    {
                        // a roof hides the soldiers inside its house and those it covers from the camera
                        if (s.X > o.Min.X - 0.6 && s.X < o.Max.X + 0.6 && s.Z > o.Min.Y - 0.6 && s.Z < o.Max.Y + 0.6) { hide = true; break; }
                    }
                    if (Occludes(o, s, fwd, right)) { hide = true; break; }
                }
            float want = hide ? 0.22f : 1;
            if (o.Box && hide || cut && o.CutHide) want = 0;
            o.Alpha = dt > 0 ? RigMath.Damp(o.Alpha, want, 10, dt) : want;
            ApplyAlpha(o);
        }
    }

    static bool Occludes(Occluder o, Vector3 s, Vector3 fwd, Vector3 right)
    {
        // the closest point of the footprint to the soldier along the screen's horizontal
        float us = s.Dot(right), ds = s.Dot(fwd);
        var a = W(o.A); var b = W(o.B);
        float ua = a.Dot(right), ub = b.Dot(right), da = a.Dot(fwd), db = b.Dot(fwd);
        float lo = MathF.Min(ua, ub) - o.R - 0.35f, hi = MathF.Max(ua, ub) + o.R + 0.35f;
        if (us < lo || us > hi) return false;
        float t = MathF.Abs(ub - ua) > 1e-4f ? Math.Clamp((us - ua) / (ub - ua), 0, 1) : 0;
        float d0 = da + (db - da) * t;
        // screen height of a point = depth·sin30 + height·cos30; the soldier's figure spans 0–1.8 m above his feet
        float gap = ds - d0;
        return gap > -o.R && gap < o.Hi * Cot30 + 0.2f && gap > (o.Lo - 1.8f) * Cot30;
    }

    static void ApplyAlpha(Occluder o)
    {
        foreach (var mi in o.Meshes)
        {
            if (o.Alpha > 0.985f) { for (int s = 0; s < mi.Mesh.GetSurfaceCount(); s++) mi.SetSurfaceOverrideMaterial(s, null); mi.Visible = true; continue; }
            mi.Visible = o.Alpha > 0.02f;
            for (int s = 0; s < mi.Mesh.GetSurfaceCount(); s++)
            {
                var baseMat = mi.Mesh.SurfaceGetMaterial(s);
                if (!o.Faded.TryGetValue(baseMat, out var f))
                {
                    f = (StandardMaterial3D)baseMat.Duplicate();
                    f.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
                    f.DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Always;
                    o.Faded[baseMat] = f;
                }
                var c = f.AlbedoColor; c.A = o.Alpha; f.AlbedoColor = c;
                mi.SetSurfaceOverrideMaterial(s, f);
            }
        }
    }

    /// <summary>The manual cut (H): house walls cut to 0.9 m; lintels and roofs are hidden by Update. Cosmetic only.</summary>
    public void ApplyCut(bool cut)
    {
        foreach (var (mesh, top) in _cutWalls)
        {
            float k = cut ? MathF.Min(1, 0.9f / top) : 1;
            mesh.Scale = V(1, k, 1);
        }
    }
}
