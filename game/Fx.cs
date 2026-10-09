using Godot;

/// <summary>Everything a shot leaves behind, ported from the concept's gunfx.js: muzzle flash and light, smoke, the
/// bullet with its tracer, spent brass and dropped magazines as small rigid bodies, bullet holes, impact debris by
/// material, blood. Visual only: positions come from the simulation's events, randomness from System.Random.
/// Particles of one kind share a MultiMesh; the ground is the plane y = 0.</summary>
sealed class Fx
{
    readonly Node3D _root;
    readonly Random _r = new(1993);
    float R() => (float)_r.NextDouble();
    static Vector3 V(float x, float y, float z) => new(x, y, z);

    public Fx(Node parent)
    {
        _root = new Node3D { Name = "Fx" };
        parent.AddChild(_root);
        BuildParticles();
        BuildFlash();
        BuildBullets();
        BuildBodies();
        BuildDecals();
    }

    public void Clear()
    {
        foreach (var k in _kinds) k.N = 0;
        foreach (var b in _bullets) b.On = false;
        _brassN = 0; _brassI = 0;
        foreach (var m in _mags) m.Node.QueueFree();
        _mags.Clear();
        foreach (var d in _decals.Values) { d.Mm.VisibleInstanceCount = 0; d.I = 0; d.N = 0; }
        foreach (var f in _flashes) f.Node.Visible = false;
        foreach (var l in _lights) l.LightEnergy = 0;
    }

    public void Update(float dt)
    {
        if (dt <= 0) return;
        UpdateFlash(dt);
        UpdateBullets(dt);
        UpdateBodies(dt);
        UpdateParticles(dt);
    }

    static StandardMaterial3D Mat(string hex, float rough = 1, float metal = 0, float alpha = 1, bool unshaded = false)
    {
        var m = new StandardMaterial3D { AlbedoColor = new Color(hex) { A = alpha }, Roughness = rough, Metallic = metal };
        if (alpha < 1) { m.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; }
        if (unshaded) m.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
        return m;
    }

    // ───────── particles (gunfx.js KIND) ─────────

    sealed class Kind
    {
        public float Life0, Life1, Drag, G, Spin, Grow, Sc; public bool Stain;
        public MultiMesh Mm = null!;
        public int N;
        public readonly Vector3[] P = new Vector3[Cap], Vel = new Vector3[Cap], W = new Vector3[Cap];
        public readonly Basis[] B = new Basis[Cap];
        public readonly float[] Life = new float[Cap], Max = new float[Cap];
        public const int Cap = 256;
    }
    readonly Dictionary<string, Kind> _kind = new();
    readonly List<Kind> _kinds = new();

    void BuildParticles()
    {
        Mesh cube = new BoxMesh { Size = V(0.03f, 0.03f, 0.03f) }, sliver = new BoxMesh { Size = V(0.012f, 0.06f, 0.012f) };
        Mesh puff = new SphereMesh { Radius = 0.06f, Height = 0.12f, RadialSegments = 6, Rings = 3 }, flake = new BoxMesh { Size = V(0.05f, 0.004f, 0.04f) };
        var leaf = Mat("5d683b"); leaf.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        void K(string name, Mesh mesh, StandardMaterial3D mat, float l0, float l1, float drag, float g, float spin, float grow, float sc, bool stain = false)
        {
            var k = new Kind { Life0 = l0, Life1 = l1, Drag = drag, G = g, Spin = spin, Grow = grow, Sc = sc, Stain = stain };
            k.Mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = mesh, InstanceCount = Kind.Cap, VisibleInstanceCount = 0 };
            var mi = new MultiMeshInstance3D { Multimesh = k.Mm, MaterialOverride = mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            _root.AddChild(mi);
            _kind[name] = k; _kinds.Add(k);
        }
        K("spark", cube, Mat("ffe0a0", unshaded: true), 0.12f, 0.3f, 1, 9.8f, 0, 0, 0.6f);
        K("smoke", puff, Mat("b9b8ae", alpha: 0.32f), 0.7f, 1.3f, 3.2f, -0.5f, 1, 3.2f, 0.5f);
        K("dust", puff, Mat("8c7a5a", alpha: 0.55f), 0.45f, 0.8f, 4, -0.3f, 1, 2.6f, 0.6f);
        K("cdust", puff, Mat("a09d92", alpha: 0.5f), 0.4f, 0.7f, 4, -0.3f, 1, 2.2f, 0.5f);
        K("clod", cube, Mat("5d4a33"), 0.5f, 0.9f, 1, 9.8f, 14, 0, 1);
        K("splinter", sliver, Mat("bfa477"), 0.6f, 1, 2, 8, 22, 0, 1);
        K("paint", flake, Mat("4f5b31"), 0.6f, 1.1f, 3, 5, 16, 0, 0.6f);
        K("chip", cube, Mat("9c998e"), 0.4f, 0.8f, 1, 9.8f, 14, 0, 0.7f);
        K("leaf", flake, leaf, 1.2f, 2, 5, 1.6f, 6, 0, 1);
        K("fiber", cube, Mat("a39470"), 0.4f, 0.7f, 2, 6, 10, 0, 0.5f);
        K("blood", cube, Mat("6e0f0b", 0.35f, 0.1f), 0.6f, 1.1f, 1.2f, 9.8f, 8, 0, 0.5f, true);
        K("mist", puff, Mat("8a1d16", alpha: 0.3f), 0.14f, 0.28f, 7, -0.2f, 1, 1.8f, 0.22f);
    }

    public void Emit(string kind, Vector3 p, Vector3 dir, int n, float speed = 2, float spread = 1)
    {
        var k = _kind[kind];
        for (int i = 0; i < n; i++)
        {
            int j = k.N < Kind.Cap ? k.N++ : _r.Next(Kind.Cap);   // full: overwrite a random one
            k.P[j] = p;
            k.B[j] = Basis.FromEuler(V(R() * 6, R() * 6, R() * 6));
            k.Life[j] = k.Max[j] = k.Life0 + R() * (k.Life1 - k.Life0);
            k.Vel[j] = V(dir.X * speed + (R() - 0.5f) * spread * 2, dir.Y * speed + (R() - 0.2f) * spread * 1.5f, dir.Z * speed + (R() - 0.5f) * spread * 2);
            k.W[j] = V((R() - 0.5f) * k.Spin, (R() - 0.5f) * k.Spin, (R() - 0.5f) * k.Spin);
        }
    }

    void UpdateParticles(float dt)
    {
        foreach (var k in _kinds)
        {
            for (int j = 0; j < k.N; j++)
            {
                k.Life[j] -= dt;
                if (k.Life[j] <= 0) { Kill(k, j--); continue; }
                k.Vel[j].Y -= k.G * dt; k.Vel[j] *= MathF.Exp(-k.Drag * dt);
                k.P[j] += k.Vel[j] * dt;
                float wl = k.W[j].Length();
                if (wl > 1e-4f) k.B[j] = new Basis(k.W[j] / wl, wl * dt) * k.B[j];
                if (k.P[j].Y < 0.01f)
                {
                    if (k.Stain) { Stain(k.P[j], 0.05f + R() * 0.07f); Kill(k, j--); continue; }   // a drop becomes a spot
                    k.P[j].Y = 0.01f; k.Vel[j].Y *= -0.25f; k.Vel[j].X *= 0.5f; k.Vel[j].Z *= 0.5f; k.W[j] *= 0.4f;
                }
            }
            for (int j = 0; j < k.N; j++)
            {
                float f = k.Life[j] / k.Max[j];
                float s = k.Grow > 0 ? k.Sc * (1 + (1 - f) * k.Grow) : f < 0.3f ? k.Sc * f / 0.3f : k.Sc;
                k.Mm.SetInstanceTransform(j, new Transform3D(k.B[j].Scaled(V(s, s, s)), k.P[j]));
            }
            k.Mm.VisibleInstanceCount = k.N;
        }
    }

    static void Kill(Kind k, int j)
    {
        int last = --k.N;
        k.P[j] = k.P[last]; k.Vel[j] = k.Vel[last]; k.W[j] = k.W[last]; k.B[j] = k.B[last]; k.Life[j] = k.Life[last]; k.Max[j] = k.Max[last];
    }

    // ───────── muzzle flash: crossed star sprites + a short-lived light ─────────

    sealed class Flash { public Node3D Node = null!; public MeshInstance3D Star = null!; public float T; }
    readonly List<Flash> _flashes = new();
    readonly List<OmniLight3D> _lights = new();
    int _flashI, _lightI;

    void BuildFlash()
    {
        StandardMaterial3D Add(string tex) => new()
        {
            AlbedoTexture = Art.Texture(tex), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha, BlendMode = BaseMaterial3D.BlendModeEnum.Add,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled, NoDepthTest = false,
        };
        var starMat = Add("tex/fx_flash.png"); var coneMat = Add("tex/fx_cone.png");
        var starMesh = new QuadMesh { Size = new Vector2(0.2f, 0.2f) };
        var coneMesh = new QuadMesh { Size = new Vector2(0.32f, 0.16f), CenterOffset = V(0.16f, 0, 0) };
        for (int i = 0; i < 10; i++)
        {
            var f = new Flash { Node = new Node3D { Visible = false } };
            _root.AddChild(f.Node);
            f.Star = new MeshInstance3D { Mesh = starMesh, MaterialOverride = starMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            f.Node.AddChild(f.Star);
            foreach (float r in new[] { 0f, Mathf.Pi / 2 })
            {
                // the cone quad runs along +X; turned to run along the barrel (+Z), the two planes crossed
                var c = new MeshInstance3D { Mesh = coneMesh, MaterialOverride = coneMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
                c.Quaternion = RigMath.EulerYXZ(r, -Mathf.Pi / 2, 0);
                f.Node.AddChild(c);
            }
            _flashes.Add(f);
        }
        for (int i = 0; i < 4; i++)
        {
            var l = new OmniLight3D { LightColor = new Color("ffb060"), OmniRange = 6, OmniAttenuation = 2, LightEnergy = 0, ShadowEnabled = false };
            _root.AddChild(l); _lights.Add(l);
        }
    }

    public void MuzzleFlash(Vector3 p, Vector3 dir)
    {
        var f = _flashes[_flashI++ % _flashes.Count];
        f.Node.Position = p;
        f.Node.Quaternion = RigMath.FromUnitVectors(Vector3.Back, dir.Normalized());
        float s = 0.7f + R() * 0.6f;
        f.Node.Scale = V(s, s, 0.8f + R() * 0.5f);
        f.Star.Rotation = V(0, 0, R() * 6);
        f.Node.Visible = true; f.T = 0.04f;
        var l = _lights[_lightI++ % _lights.Count];
        l.Position = p + dir * 0.1f; l.LightEnergy = FlashLight;
        Emit("smoke", p + dir * 0.05f, dir, 3, 0.9f, 0.25f);
    }
    public static float FlashLight = 2.5f;

    void UpdateFlash(float dt)
    {
        foreach (var f in _flashes) if (f.Node.Visible && (f.T -= dt) <= 0) f.Node.Visible = false;
        foreach (var l in _lights) { l.LightEnergy *= MathF.Exp(-55 * dt); if (l.LightEnergy < 0.01f) l.LightEnergy = 0; l.Visible = l.LightEnergy > 0; }
    }

    // ───────── bullets and tracers ─────────

    sealed class Bullet { public bool On, Live; public Vector3 From, P, D, End; public float Fade; public Action? Hit; }
    readonly List<Bullet> _bullets = new();
    MultiMesh _tracers = null!;
    const float BulletSpeed = 420;   // m/s on screen: one or two frames across a fight, readable as a streak

    void BuildBullets()
    {
        _tracers = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = true, Mesh = new BoxMesh { Size = V(0.012f, 0.012f, 1) }, InstanceCount = 48, VisibleInstanceCount = 0 };
        var mat = new StandardMaterial3D
        {
            AlbedoColor = new Color("ffe7a6"), VertexColorUseAsAlbedo = true, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha, BlendMode = BaseMaterial3D.BlendModeEnum.Add,
        };
        _root.AddChild(new MultiMeshInstance3D { Multimesh = _tracers, MaterialOverride = mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        for (int i = 0; i < 48; i++) _bullets.Add(new Bullet());
    }

    int _bulletI;
    /// <summary>A bullet from the muzzle to where the simulation stopped it; age: seconds since it left the muzzle.
    /// hit runs when its head arrives.</summary>
    public void Tracer(Vector3 from, Vector3 end, float age, Action? hit)
    {
        var b = _bullets[_bulletI++ % _bullets.Count];
        if (b.On && b.Live) b.Hit?.Invoke();   // recycled mid-flight: let it land
        b.On = true; b.Live = true; b.From = from; b.End = end; b.Hit = hit;
        var d = end - from; float L = d.Length();
        b.D = L > 1e-4f ? d / L : Vector3.Forward;
        b.P = from + b.D * MathF.Min(L, BulletSpeed * age);
    }

    void UpdateBullets(float dt)
    {
        int n = 0;
        foreach (var b in _bullets)
        {
            if (!b.On) continue;
            float a = 0.85f;
            if (b.Live)
            {
                float left = (b.End - b.P).Length(), step = BulletSpeed * dt;
                if (step >= left) { b.P = b.End; b.Live = false; b.Fade = 0.06f; b.Hit?.Invoke(); b.Hit = null; }
                else b.P += b.D * step;
            }
            else { b.Fade -= dt; a = MathF.Max(0, b.Fade / 0.06f) * 0.85f; if (b.Fade <= 0) { b.On = false; continue; } }
            float len = MathF.Min(2.2f, (b.P - b.From).Length());
            if (len < 0.01f) len = 0.01f;
            var basis = new Basis(RigMath.FromUnitVectors(Vector3.Back, b.D)) * Basis.FromScale(V(1, 1, len));
            _tracers.SetInstanceTransform(n, new Transform3D(basis, b.P - b.D * (len / 2)));
            _tracers.SetInstanceColor(n, new Color(1, 1, 1, a));
            n++;
        }
        _tracers.VisibleInstanceCount = n;
    }

    // ───────── spent brass and dropped magazines: tiny rigid bodies ─────────

    struct Body { public Vector3 P, V, W; public Quaternion Q; public bool Awake; public int Bounces; public float Rest; }
    const int BrassCap = 400;
    readonly Body[] _brass = new Body[BrassCap];
    int _brassN, _brassI;
    MultiMesh _brassMm = null!;
    sealed class Mag { public Node3D Node = null!; public Body B; }
    readonly List<Mag> _mags = new();

    void BuildBodies()
    {
        // ~1.4× real 5.56 brass so it reads at game zoom
        _brassMm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = new CylinderMesh { TopRadius = 0.009f, BottomRadius = 0.009f, Height = 0.066f, RadialSegments = 6, Rings = 1 }, InstanceCount = BrassCap, VisibleInstanceCount = 0 };
        _root.AddChild(new MultiMeshInstance3D { Multimesh = _brassMm, MaterialOverride = Mat("c9a14a", 0.35f, 0.75f) });
    }

    public void Eject(Vector3 p, Vector3 dir, Vector3 carrierVel)
    {
        int i = _brassI++ % BrassCap;
        var v = dir * (1.5f + R() * 0.7f); v.Y += 0.9f + R() * 0.5f; v += carrierVel;
        var ax = V(-dir.Z, 0, dir.X); if (ax.LengthSquared() < 1e-6f) ax = Vector3.Right;   // the casing lies along the barrel
        _brass[i] = new Body { P = p, V = v, Q = RigMath.FromUnitVectors(Vector3.Up, ax.Normalized()), W = V((R() - 0.5f) * 50, (R() - 0.5f) * 30, (R() - 0.5f) * 50), Awake = true };
        _brassN = Math.Min(BrassCap, Math.Max(_brassN, i + 1));
    }

    public void DropMag(Transform3D t, Vector3 vel)
    {
        var node = Art.Model("magazine.glb");
        _root.AddChild(node);
        node.Transform = t;
        _mags.Add(new Mag { Node = node, B = new Body { P = t.Origin, Q = t.Basis.GetRotationQuaternion(), V = vel + V(0, -0.6f, 0), W = V((R() - 0.5f) * 6, (R() - 0.5f) * 3, (R() - 0.5f) * 6), Awake = true } });
        if (_mags.Count > 40) { _mags[0].Node.QueueFree(); _mags.RemoveAt(0); }
    }

    // shared integrator: gravity, spin, bounce on the floor with friction, settle flat
    void Step(ref Body b, float dt, float half, float restitution, bool brass)
    {
        if (!b.Awake) return;
        b.V.Y -= 9.8f * dt; b.P += b.V * dt;
        float wl = b.W.Length(); if (wl > 1e-4f) b.Q = (new Quaternion(b.W / wl, wl * dt) * b.Q).Normalized();
        if (b.P.Y < half)
        {
            b.P.Y = half;
            if (b.V.Y < -0.4f)
            {
                float s = MathF.Min(1, -b.V.Y / 3);
                if (brass) { if (b.Bounces < 2) Sfx.At(b.P, (x, p, v) => x.Tink(p, v), s); }
                else Sfx.At(b.P, (x, p, v) => x.Mag(p, v), s);
                b.Bounces++; b.V.Y *= -restitution;
            }
            else b.V.Y = 0;
            b.V.X *= 0.6f; b.V.Z *= 0.6f; b.W *= 0.55f;
            var ax = b.Q * Vector3.Up; var flat = V(ax.X, 0, ax.Z);
            if (flat.LengthSquared() < 1e-4f) flat = Vector3.Right;
            b.Q = (RigMath.FromUnitVectors(ax, ax.Lerp(flat.Normalized(), 0.25f).Normalized()) * b.Q).Normalized();
            if (b.V.LengthSquared() < 0.01f) { b.Rest += dt; if (b.Rest > 0.25f) b.Awake = false; }
        }
    }

    void UpdateBodies(float dt)
    {
        for (int i = 0; i < _brassN; i++)
        {
            if (!_brass[i].Awake) continue;
            Step(ref _brass[i], dt, 0.009f, 0.38f, true);
            _brassMm.SetInstanceTransform(i, new Transform3D(new Basis(_brass[i].Q), _brass[i].P));
        }
        _brassMm.VisibleInstanceCount = _brassN;
        foreach (var m in _mags)
        {
            if (!m.B.Awake) continue;
            Step(ref m.B, dt, 0.016f, 0.25f, false);
            m.Node.Transform = new Transform3D(new Basis(m.B.Q), m.B.P);
        }
    }

    // ───────── bullet holes and blood spots ─────────

    sealed class Decals { public MultiMesh Mm = null!; public int I, N; }
    readonly Dictionary<string, Decals> _decals = new();
    const int DecalCap = 600;

    void BuildDecals()
    {
        var quad = new QuadMesh { Size = Vector2.One };
        foreach (var name in new[] { "hole", "pit", "splat", "blood" })
        {
            var mat = new StandardMaterial3D
            {
                AlbedoTexture = Art.Texture($"tex/fx_{name}.png"), Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor,
                AlphaScissorThreshold = name == "blood" ? 0.25f : 0.4f, Roughness = name == "blood" ? 0.3f : 1,
                TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmaps,
            };
            var d = new Decals { Mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = quad, InstanceCount = DecalCap, VisibleInstanceCount = 0 } };
            _root.AddChild(new MultiMeshInstance3D { Multimesh = d.Mm, MaterialOverride = mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
            _decals[name] = d;
        }
    }

    void Decal(string tex, Vector3 p, Vector3 n, float size)
    {
        var d = _decals[tex];
        int i = d.I++ % DecalCap; d.N = Math.Min(DecalCap, Math.Max(d.N, i + 1));
        var q = RigMath.FromUnitVectors(Vector3.Back, n) * new Quaternion(Vector3.Back, R() * 6);
        d.Mm.SetInstanceTransform(i, new Transform3D(new Basis(q).Scaled(V(size, size, size)), p + n * (0.004f + (i % 7) * 0.0004f)));
        d.Mm.VisibleInstanceCount = d.N;
    }

    /// <summary>A blood spot on the ground under a point.</summary>
    public void Stain(Vector3 p, float size) => Decal("blood", V(p.X, 0.002f, p.Z), Vector3.Up, size * (0.8f + R() * 0.5f));

    /// <summary>A bullet through flesh: a red puff at the wound, drops thrown on through and a little back-spray.</summary>
    public void Blood(Vector3 p, Vector3 dir, float amount = 1)
    {
        Emit("mist", p, dir, (int)MathF.Round(3 + 3 * amount), 0.9f, 0.35f);
        Emit("blood", p, dir, (int)MathF.Round(7 * amount), 2.4f, 0.8f);
        Emit("blood", p, (-dir + V(0, 0.6f, 0)).Normalized(), (int)MathF.Round(2 * amount), 1.2f, 0.5f);
    }

    /// <summary>Debris and a hole by material: steel, wood, concrete, sand, leaf, dirt (default).</summary>
    public void Impact(string kind, Vector3 p, Vector3 n, Vector3 dir)
    {
        var refl = (dir - 2 * dir.Dot(n) * n) * 0.5f + n; refl = refl.Normalized();
        string tex = "pit"; float size = 0.045f;
        switch (kind)
        {
            case "steel": Emit("spark", p, refl, 7, 2.6f, 1.2f); Emit("cdust", p, n, 1, 0.4f, 0.2f); tex = "splat"; size = 0.07f; break;
            case "wood": Emit("splinter", p, refl, 6, 1.8f, 1); Emit("dust", p, n, 1, 0.3f, 0.2f); tex = "hole"; size = 0.035f; break;
            case "concrete": Emit("chip", p, refl, 6, 2.2f, 1); Emit("cdust", p, n, 3, 0.5f, 0.3f); Emit("spark", p, refl, 1, 2, 1); break;
            case "sand": Emit("fiber", p, refl, 4, 1.4f, 0.8f); Emit("dust", p, n, 3, 0.5f, 0.4f); break;
            case "leaf": Emit("leaf", p, refl, 6, 1, 0.9f); return;
            default: Emit("clod", p, refl, 7, 2.6f, 1); Emit("dust", p, n, 4, 0.9f, 0.4f); size = 0.07f; break;
        }
        Decal(tex, p, n, size);
    }
}
