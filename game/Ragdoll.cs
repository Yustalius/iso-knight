using Godot;

/// <summary>Position-based ragdoll of the concept's ragdoll.js for the soldier skeleton. Joints are verlet particles, bones
/// rigid distance constraints, joint limits distance ranges plus hinge/back-bend projections; the ground (y = 0) and the
/// map's solid obstacles push particles out with friction. A short-lived "muscle tone" keeps the trunk's death pose for a
/// moment while the legs give out, so a body crumples and topples along the hit. Every correction between particles is
/// central and mass-weighted: the body gains momentum only from the bullet, gravity and the ground. The rifle falls as a
/// separate rigid three-point body. Visual only (not deterministic, not part of the rules).
/// Particles live in world space; the soldier's root stays where he died and the bones are posed in its frame.</summary>
sealed class Ragdoll
{
    public struct Collider { public float Ax, Az, Bx, Bz, R, H; }

    const float G = 9.8f; const int Sub = 4, Iter = 8;

    // [name, bone, offset in bone space, radius, mass kg]
    static readonly (string name, string bone, Vector3 off, float r, float m)[] Parts =
    {
        ("hipL", "Thigh_L", new(0, 0, 0), 0.1f, 10), ("hipR", "Thigh_R", new(0, 0, 0), 0.1f, 10),
        ("spine", "Spine", new(0, 0.02f, 0.03f), 0.14f, 14),
        ("shL", "UpperArm_L", new(0, 0, 0), 0.085f, 6), ("shR", "UpperArm_R", new(0, 0, 0), 0.085f, 6),
        ("neck", "Neck", new(0, 0, 0), 0.07f, 4), ("head", "Head", new(0, 0.1f, 0.01f), 0.13f, 5),
        ("elL", "Forearm_L", new(0, 0, 0), 0.055f, 2), ("haL", "Hand_L", new(0, -0.06f, 0), 0.05f, 1.2f),
        ("elR", "Forearm_R", new(0, 0, 0), 0.055f, 2), ("haR", "Hand_R", new(0, -0.06f, 0), 0.05f, 1.2f),
        ("knL", "Shin_L", new(0, 0, 0), 0.07f, 4), ("anL", "Foot_L", new(0, 0, 0), 0.06f, 2), ("toL", "Foot_L", new(0, -0.07f, 0.19f), 0.045f, 0.8f),
        ("knR", "Shin_R", new(0, 0, 0), 0.07f, 4), ("anR", "Foot_R", new(0, 0, 0), 0.06f, 2), ("toR", "Foot_R", new(0, -0.07f, 0.19f), 0.045f, 0.8f),
    };

    sealed class Pt { public int Bone; public Vector3 Off, P, Q, Rest; public float R, W, Cn; public bool Contact; }
    struct Con { public int A, B; public float Min, Max; }
    struct Tone { public int A, B; public bool Leg; public float L; }

    readonly Pose _pose;
    readonly Pt[] _p;
    readonly Pt[] _rp;          // rifle: butt, muzzle, sight
    readonly Pt[] _all;
    readonly Dictionary<string, int> _i = new();
    readonly List<Con> _c = new();
    readonly List<Tone> _tone = new();
    readonly (int a, int b, float L)[] _rc;
    readonly Quaternion _pelvisRest, _chestRest, _rifleRest;
    readonly Vector3 _hipsOffset;
    readonly int _hips, _spine, _chest, _neck, _head, _rifle, _mag;
    readonly int[] _upper = new int[2], _fore = new int[2], _hand = new int[2], _thigh = new int[2], _shin = new int[2], _foot = new int[2];
    readonly Collider[] _walls;
    Transform3D _root, _rootInv; Quaternion _rootQi;
    float _hPrev = 1 / 240f, _t;
    public float Sleep;
    public bool Active;

    Pt At(string n) => _p[_i[n]];

    public Ragdoll(Pose pose, Collider[] walls)
    {
        _pose = pose; _walls = walls;
        _p = new Pt[Parts.Length];
        for (int k = 0; k < Parts.Length; k++)
        {
            var (name, bone, off, r, m) = Parts[k];
            int b = pose.Find(bone);
            _p[k] = new Pt { Bone = b, Off = off, R = r, W = 1 / m, Rest = RestWorld(b) * off };
            _i[name] = k;
        }
        _hips = pose.Find("Hips"); _spine = pose.Find("Spine"); _chest = pose.Find("Chest"); _neck = pose.Find("Neck"); _head = pose.Find("Head");
        _rifle = pose.Find("Rifle"); _mag = pose.Find("Magazine");
        string[] sides = { "L", "R" };
        for (int k = 0; k < 2; k++)
        {
            _upper[k] = pose.Find("UpperArm_" + sides[k]); _fore[k] = pose.Find("Forearm_" + sides[k]); _hand[k] = pose.Find("Hand_" + sides[k]);
            _thigh[k] = pose.Find("Thigh_" + sides[k]); _shin[k] = pose.Find("Shin_" + sides[k]); _foot[k] = pose.Find("Foot_" + sides[k]);
        }

        // ---------- constraints ----------
        float RestD(string a, string b) => At(a).Rest.DistanceTo(At(b).Rest);
        void Rigid(params string[] n) { for (int a = 0; a < n.Length; a++) for (int b = a + 1; b < n.Length; b++) { float L = RestD(n[a], n[b]); _c.Add(new Con { A = _i[n[a]], B = _i[n[b]], Min = L, Max = L }); } }
        void Range(string a, string b, float lo, float hi) { float L = RestD(a, b); _c.Add(new Con { A = _i[a], B = _i[b], Min = L * lo, Max = L * hi }); }
        void AtLeast(string a, string b, float d) => _c.Add(new Con { A = _i[a], B = _i[b], Min = d, Max = float.PositiveInfinity });
        Rigid("hipL", "hipR", "spine"); Rigid("shL", "shR", "neck", "spine"); Rigid("neck", "head");
        foreach (var s in sides)
        {
            string o = s == "L" ? "R" : "L";
            Rigid("sh" + s, "el" + s); Rigid("el" + s, "ha" + s);
            Rigid("hip" + s, "kn" + s); Rigid("kn" + s, "an" + s); Rigid("an" + s, "to" + s);
            Range("neck", "hip" + s, 0.8f, 1.03f);                     // spine flexion and side bend
            Range("sh" + s, "hip" + s, 0.78f, 1.05f); Range("sh" + s, "hip" + o, 0.84f, 1.08f);   // and twist
            Range("head", "sh" + s, 0.8f, 1.14f);
            Range("to" + s, "kn" + s, 0.86f, 1.06f);                   // ankle
            AtLeast("ha" + s, "sh" + s, 0.12f); AtLeast("ha" + s, "spine", 0.12f); AtLeast("el" + s, "spine", 0.14f);
            AtLeast("ha" + s, "sh" + o, 0.16f); AtLeast("kn" + s, "hip" + o, 0.16f); AtLeast("kn" + s, "spine", 0.3f);
            AtLeast("an" + s, "hip" + s, 0.26f);                        // knee fully flexed
        }
        Range("head", "spine", 0.82f, 1.04f);
        AtLeast("knL", "knR", 0.13f); AtLeast("anL", "anR", 0.1f); AtLeast("toL", "toR", 0.08f);

        _pelvisRest = PelvisBasis(true).Inverse();
        _chestRest = ChestBasis(true).Inverse();
        _hipsOffset = pose.RestPos[_hips] - (At("hipL").Rest + At("hipR").Rest) * 0.5f;

        // rifle: butt, muzzle and rear sight form a rigid triangle
        Vector3[] g = { SoldierRig.Gun.Butt, SoldierRig.Gun.Muzzle, SoldierRig.Gun.Sight };
        float[] rr = { 0.035f, 0.02f, 0.03f };
        _rp = new Pt[3];
        for (int k = 0; k < 3; k++) _rp[k] = new Pt { Off = g[k], R = rr[k], W = 1 };
        {
            var z = (g[1] - g[0]).Normalized(); var y = g[2] - g[0]; y = (y - z * y.Dot(z)).Normalized();
            _rifleRest = RigMath.FromBasis(y.Cross(z), y, z).Inverse();
        }
        _rc = new[] { (0, 1, g[0].DistanceTo(g[1])), (1, 2, g[1].DistanceTo(g[2])), (0, 2, g[0].DistanceTo(g[2])) };
        _all = _p.Concat(_rp).ToArray();
    }

    Transform3D RestWorld(int b) => _pose.Parent[b] < 0 ? new Transform3D(new Basis(_pose.RestRot[b]), _pose.RestPos[b]) : RestWorld(_pose.Parent[b]) * new Transform3D(new Basis(_pose.RestRot[b]), _pose.RestPos[b]);

    Vector3 Pos(string n, bool rest) => rest ? At(n).Rest : At(n).P;
    Quaternion PelvisBasis(bool rest)
    {
        var x = (Pos("hipL", rest) - Pos("hipR", rest)).Normalized();
        var y = Pos("spine", rest) - (Pos("hipL", rest) + Pos("hipR", rest)) * 0.5f;
        y = (y - x * y.Dot(x)).Normalized();
        return RigMath.FromBasis(x, y, x.Cross(y));
    }
    Quaternion ChestBasis(bool rest)
    {
        var x = (Pos("shL", rest) - Pos("shR", rest)).Normalized();
        var y = Pos("neck", rest) - Pos("spine", rest);
        y = (y - x * y.Dot(x)).Normalized();
        return RigMath.FromBasis(x, y, x.Cross(y));
    }

    /// <summary>World positions of the joints in the current pose (to give the ragdoll the living body's velocity).</summary>
    public void Sample(Transform3D root, Vector3[] into)
    {
        for (int k = 0; k < _p.Length; k++) into[k] = root * (_pose.World(_p[k].Bone) * _p[k].Off);
    }
    public int Count => _p.Length;

    public struct Impulse { public Vector3 Point, Dir; public float Speed, Spread; }

    /// <summary>Start from the current pose; vel: joint velocities of the living body (world, may be null).</summary>
    public void Start(Transform3D root, Vector3[]? vel, Impulse[] impulses)
    {
        _root = root; _rootInv = root.AffineInverse(); _rootQi = root.Basis.GetRotationQuaternion().Inverse();
        for (int k = 0; k < _p.Length; k++)
        {
            var x = _p[k];
            x.P = root * (_pose.World(x.Bone) * x.Off);
            x.Q = vel != null ? x.P - vel[k] * _hPrev : x.P;
        }
        // the pose the muscles try to keep: every pair inside the trunk, a few weak braces on the limbs
        string[] up = { "hipL", "hipR", "spine", "shL", "shR", "neck", "head" };
        (string, string)[] leg = { ("hipL", "anL"), ("hipR", "anR"), ("knL", "spine"), ("knR", "spine"), ("knL", "hipR"), ("knR", "hipL"), ("anL", "spine"), ("anR", "spine"), ("elL", "spine"), ("elR", "spine"), ("haL", "shL"), ("haR", "shR") };
        _tone.Clear();
        for (int a = 0; a < up.Length; a++) for (int b = a + 1; b < up.Length; b++) _tone.Add(new Tone { A = _i[up[a]], B = _i[up[b]] });
        foreach (var (a, b) in leg) _tone.Add(new Tone { A = _i[a], B = _i[b], Leg = true });
        for (int k = 0; k < _tone.Count; k++) { var t = _tone[k]; t.L = _p[t.A].P.DistanceTo(_p[t.B].P); _tone[k] = t; }
        foreach (var im in impulses) Kick(im.Point, im.Dir, im.Speed, im.Spread);
        // the rifle leaves the hands with the hands' velocity
        var rifleW = root * _pose.World(_rifle);
        var hv = (At("haR").P - At("haR").Q) / _hPrev;
        foreach (var r in _rp) { r.P = rifleW * r.Off; r.Q = r.P - hv * _hPrev * 0.8f; }
        Active = true; _t = 0; Sleep = 0;
    }

    /// <summary>A velocity change at a world point: the nearest particles take most of it.</summary>
    public void Kick(Vector3 point, Vector3 dir, float speed, float spread = 0.25f)
    {
        int near = 0; float best = float.MaxValue;
        for (int k = 0; k < _p.Length; k++) { float d = _p[k].P.DistanceTo(point); if (d < best) { best = d; near = k; } }
        for (int k = 0; k < _p.Length; k++)
        {
            float kk = k == near ? 1 : MathF.Max(spread, MathF.Exp(-_p[k].P.DistanceTo(point) / 0.25f) * 0.7f);
            _p[k].Q -= dir * (speed * kk * _hPrev);
        }
    }

    void SolveDistance(int a, int b, float min, float max, float stiff = 1, float exact = -1)
    {
        var A = _p[a]; var B = _p[b]; var d = B.P - A.P;
        float L = d.Length(); if (L < 1e-6f) return;
        float target = exact >= 0 ? exact : L < min ? min : L > max ? max : -1; if (target < 0) return;
        float k = stiff * (L - target) / (L * (A.W + B.W));
        A.P += d * (k * A.W); B.P -= d * (k * B.W);
    }

    // joint-limit projections move both sides by inverse mass, so they never add momentum
    void Push(string[] movers, string[] counter, Vector3 dir, float amount)
    {
        float wa = 0, wb = 0;
        foreach (var n in movers) wa += 1 / At(n).W;
        foreach (var n in counter) wb += 1 / At(n).W;
        float ka = amount * wb / (wa + wb), kb = amount * wa / (wa + wb);
        foreach (var n in movers) At(n).P += dir * ka;
        foreach (var n in counter) At(n).P -= dir * kb;
    }
    // keep the middle joint of a limb on one side of its root–tip line (knees forward, elbows back)
    void Hinge(string r, string m, string t, Vector3 axis, float sign)
    {
        Vector3 R = At(r).P, M = At(m).P, T = At(t).P;
        var a = T - R; float aL = a.LengthSquared(); if (aL < 1e-6f) return;
        var bend = a.Cross(axis); if (bend.LengthSquared() < 1e-8f) return; bend = bend.Normalized();
        var off = M - R; off -= a * (off.Dot(a) / aL);
        float c = off.Dot(bend) * sign;
        if (c < 0) Push(new[] { m }, new[] { r, t }, bend, -sign * c * 0.35f);
    }
    // bend limits between two frames: push the tip along fwd until its tilt is back inside [lo, hi]
    void Tilt(string b, string tip, Vector3 fwd, float lo, float hi, string[] movers, string[] counter)
    {
        var u = At(tip).P - At(b).P; float L = u.Length(); if (L < 1e-6f) return;
        float d = u.Dot(fwd) / L, fix = d < lo ? lo - d : d > hi ? hi - d : 0;
        if (fix != 0) Push(movers, counter, fwd, fix * L * 0.3f);
    }
    static void Axes(Vector3 xa, Vector3 xb, Vector3 ya, Vector3 yb, out Vector3 x, out Vector3 y, out Vector3 z)
    {
        x = (xa - xb).Normalized(); y = ya - yb; y = (y - x * y.Dot(x)).Normalized(); z = x.Cross(y);
    }

    // ground every iteration (inelastic: pushing out never launches); obstacles once per substep
    void Collide(Pt x, float h, bool walls)
    {
        float floor = x.R;
        if (x.P.Y < floor)
        {
            float vy = (x.P.Y - x.Q.Y) / h;
            x.Cn = MathF.Max(x.Cn, -vy); x.Contact = true;
            x.P.Y = floor; if (x.Q.Y < floor) x.Q.Y = floor;
        }
        if (!walls) return;
        foreach (var c in _walls)
        {
            if (x.P.Y > c.H) continue;
            float abx = c.Bx - c.Ax, abz = c.Bz - c.Az, L2 = abx * abx + abz * abz;
            float t = L2 > 0 ? Math.Clamp(((x.P.X - c.Ax) * abx + (x.P.Z - c.Az) * abz) / L2, 0, 1) : 0;
            float qx = c.Ax + abx * t, qz = c.Az + abz * t, dx = x.P.X - qx, dz = x.P.Z - qz, d = MathF.Sqrt(dx * dx + dz * dz), r = c.R + x.R * 0.6f;
            if (d < r && d > 1e-5f) { x.P.X = qx + dx / d * r; x.P.Z = qz + dz / d * r; x.Contact = true; }
        }
    }
    // Coulomb friction: sliding slows by μ·(weight + the impact the contact absorbed), never reverses
    static void Friction(Pt x, float h)
    {
        if (!x.Contact) return;
        float vx = (x.P.X - x.Q.X) / h, vz = (x.P.Z - x.Q.Z) / h, vt = MathF.Sqrt(vx * vx + vz * vz);
        float k = vt > 1e-6f ? MathF.Max(0, 1 - 0.8f * (MathF.Max(0, x.Cn) + G * h) / vt) : 0;
        x.Q.X = x.P.X - vx * k * h; x.Q.Z = x.P.Z - vz * k * h;
        x.Contact = false; x.Cn = 0;
    }

    static readonly string[] NeckSh = { "neck", "shL", "shR" }, Hips2 = { "hipL", "hipR" }, HeadOnly = { "head" }, Sh2 = { "shL", "shR" };

    public void Step(float dt)
    {
        if (!Active || dt <= 0) return;
        _t += dt;
        float h = MathF.Min(dt, 1 / 30f) / Sub;
        // muscle tone: the upper body keeps its shape for a moment, the legs give out almost at once
        float toneUp = 0.5f * MathF.Exp(-_t / 0.3f), toneLeg = 0.25f * MathF.Exp(-_t / 0.1f);
        float moving = 0;
        for (int s = 0; s < Sub; s++)
        {
            float scale = h / _hPrev;
            foreach (var x in _all)
            {
                var v = (x.P - x.Q) * (scale * 0.998f);
                x.Q = x.P; x.P += v; x.P.Y -= G * h * h;
                moving = MathF.Max(moving, v.LengthSquared());
            }
            _hPrev = h;
            for (int it = 0; it < Iter; it++)
            {
                if (toneUp > 0.003f) foreach (var t in _tone) SolveDistance(t.A, t.B, 0, 0, t.Leg ? toneLeg : toneUp, t.L);
                foreach (var c in _c) SolveDistance(c.A, c.B, c.Min, c.Max);
                Axes(At("hipL").P, At("hipR").P, At("spine").P, (At("hipL").P + At("hipR").P) * 0.5f, out var px, out _, out var pz);
                Axes(At("shL").P, At("shR").P, At("neck").P, At("spine").P, out var cx, out _, out var cz);
                Hinge("hipL", "knL", "anL", px, 1); Hinge("hipR", "knR", "anR", px, 1);
                Hinge("shL", "elL", "haL", cx, -1); Hinge("shR", "elR", "haR", cx, -1);
                Tilt("spine", "neck", pz, -0.38f, 0.95f, NeckSh, Hips2);   // no breaking backwards
                Tilt("neck", "head", cz, -0.55f, 0.8f, HeadOnly, Sh2);
                foreach (var (a, b, L) in _rc)
                {
                    var A = _rp[a]; var B = _rp[b]; var d = B.P - A.P; float l = d.Length(); if (l < 1e-6f) continue;
                    var k = d * ((l - L) / (2 * l)); A.P += k; B.P -= k;
                }
                bool last = it == Iter - 1;
                foreach (var x in _all) Collide(x, h, last);
            }
            // no joint may move faster than a body can (guards against constraint fights)
            float vmax = 9 * h;
            foreach (var x in _all)
            {
                Friction(x, h);
                var d = x.P - x.Q; float l = d.Length();
                if (l > vmax) x.Q += d * (1 - vmax / l);
            }
        }
        Sleep = moving < (0.03f * h) * (0.03f * h) ? Sleep + dt : 0;
        PoseBones();
    }

    // ---------- particles → bones (in the root's frame) ----------
    Vector3 RestDir(string a, string b) => (At(b).Rest - At(a).Rest).Normalized();
    static Vector3 Local(Quaternion q, Vector3 v) => (q.Inverse() * v).Normalized();
    Quaternion Limb(Quaternion parentQ, int bone, string from, string to)
    {
        var cur = At(to).P - At(from).P;
        var q = parentQ * RigMath.FromUnitVectors(RestDir(from, to), Local(parentQ, cur));
        _pose.SetWorldRot(bone, _rootQi * q);
        return q;
    }

    void PoseBones()
    {
        var qHips = PelvisBasis(false) * _pelvisRest;
        var qChest = ChestBasis(false) * _chestRest;
        var mid = (At("hipL").P + At("hipR").P) * 0.5f;
        _pose.Pos[_hips] = _rootInv * (mid + qHips * _hipsOffset);
        _pose.SetWorldRot(_hips, _rootQi * qHips);
        _pose.SetWorldRot(_spine, _rootQi * qHips.Slerp(qChest, 0.5f));
        _pose.SetWorldRot(_chest, _rootQi * qChest);
        var headRel = RigMath.FromUnitVectors(RestDir("neck", "head"), Local(qChest, At("head").P - At("neck").P));
        _pose.SetWorldRot(_neck, _rootQi * (qChest * Quaternion.Identity.Slerp(headRel, 0.4f)));
        _pose.SetWorldRot(_head, _rootQi * (qChest * headRel));
        string[] sd = { "L", "R" };
        for (int k = 0; k < 2; k++)
        {
            string s = sd[k];
            var qU = Limb(qChest, _upper[k], "sh" + s, "el" + s);
            var qF = Limb(qU, _fore[k], "el" + s, "ha" + s);
            _pose.SetWorldRot(_hand[k], _rootQi * (qF * new Quaternion(Vector3.Right, 0.35f)));   // limp wrist
            var qT = Limb(qHips, _thigh[k], "hip" + s, "kn" + s);
            var qS = Limb(qT, _shin[k], "kn" + s, "an" + s);
            Limb(qS, _foot[k], "an" + s, "to" + s);
        }
        // rifle and its magazine
        var z = (_rp[1].P - _rp[0].P).Normalized(); var y = _rp[2].P - _rp[0].P; y = (y - z * y.Dot(z)).Normalized();
        var qR = RigMath.FromBasis(y.Cross(z), y, z) * _rifleRest;
        var qRm = (_rootQi * qR).Normalized();
        _pose.Rot[_rifle] = qRm;
        _pose.Pos[_rifle] = _rootInv * (_rp[0].P - qR * SoldierRig.Gun.Butt);
        _pose.Rot[_mag] = qRm; _pose.Scale[_mag] = Vector3.One;
        _pose.Pos[_mag] = _pose.Pos[_rifle] + qRm * SoldierRig.Gun.MagWell;
    }

    /// <summary>World position of the chest particle (for the blood pool).</summary>
    public Vector3 Spine => At("spine").P;
}
