using Godot;
using Squad.Bots;
using Squad.Sim;
using static RigMath;

/// <summary>One soldier: the concept's model (M81 for blue, OPFOR for red) posed every frame by <see cref="SoldierRig"/>
/// from the interpolated truth of the simulation, plus the debug label with strategy, intent and health.
/// The barrel always points along the simulated AimYaw/AimPitch; only the legs lag behind it (up to ±60°) as in the
/// concept, so the picture never disagrees with the rules about where a soldier aims.</summary>
sealed class SoldierView
{
    public readonly int Id, Team;
    public readonly Node3D Root;
    public readonly Node3D Model;
    readonly Skeleton3D _sk;
    public readonly SoldierRig Rig;
    readonly Sling _sling;
    readonly Label _label;
    readonly ColorRect _hpBg, _hpFill;
    readonly IBrain? _brain;
    readonly string _name;
    readonly double _maxHp;

    // presentation state
    public float Yaw;              // shown heading of the legs (world)
    float _prevYaw;
    Vector3 _prevVel;
    bool _first = true;
    public bool ReloadEmpty;
    public bool Shot;              // set by the viewer when the simulation fired this soldier's rifle this frame
    public (Vector3 dir, Vector3 at, HitZone zone)? Flinch;   // world space, consumed by the next update
    public (Vector3 point, Vector3 dir, HitZone zone)? Killed;   // the killing bullet, set with the Kill event
    public bool Dead;
    public Vector3 MuzzleW, BarrelW;   // where the drawn rifle points (world), for tracers

    readonly Fx _fx;
    readonly Ragdoll _rag;
    readonly Vector3[] _jPrev, _jCur;
    float _deadT, _poolT, _thumpT, _sampleDt = 1 / 60f;
    bool _sampled;
    MeshInstance3D? _pool;
    Vector3 _stag, _stagV;              // "a step back" when hit: a small visual offset on a spring
    float _prevReloadT;
    readonly Random _rnd;

    // enemy.js: m/s given to the hit joint by the killing bullet, and the stagger of a hit
    static float Kick(HitZone z) => z == HitZone.Head ? 3.6f : z == HitZone.Torso ? 2.8f : 2.6f;
    static float Stagger(HitZone z) => z == HitZone.Head ? 0.6f : z == HitZone.Torso ? 1.0f : 0.6f;

    public SoldierView(int id, int team, IBrain? brain, double maxHp, Node3D parent, Control overlay, Fx fx, Ragdoll.Collider[] walls)
    {
        _fx = fx; _rnd = new Random(id * 31 + 7);
        Id = id; Team = team; _brain = brain; _maxHp = maxHp;
        _name = $"{id} {brain?.Name ?? ""}";
        Root = new Node3D { Name = $"Soldier{id}" };
        parent.AddChild(Root);
        Model = Art.Model(team == 0 ? "soldier_m81.glb" : "soldier_opfor.glb");
        Root.AddChild(Model);
        _sk = Art.FindSkeleton(Model)!;
        var pose = new Pose(_sk);
        _rag = new Ragdoll(pose, walls);      // reads the bind pose, so it comes before any posing
        _jPrev = new Vector3[_rag.Count]; _jCur = new Vector3[_rag.Count];
        Rig = new SoldierRig(pose, 7919 * (id + 1));
        _sling = new Sling(parent, team);

        _label = Main.MakeLabel(12);
        _label.AddThemeColorOverride("font_color", Main.TeamCol[team].Lightened(0.45f));
        _hpBg = new ColorRect { Color = new Color(0, 0, 0, 0.6f), Size = new Vector2(34, 4), MouseFilter = Control.MouseFilterEnum.Ignore };
        _hpFill = new ColorRect { Size = new Vector2(34, 4), MouseFilter = Control.MouseFilterEnum.Ignore };
        overlay.AddChild(_hpBg); overlay.AddChild(_hpFill); overlay.AddChild(_label);
    }

    public void Free()
    {
        _label.QueueFree(); _hpBg.QueueFree(); _hpFill.QueueFree();
        // 3D nodes go away with the world root
    }

    /// <summary>a, b: truth at the previous and current tick; t: interpolation between them; dt: simulated seconds since
    /// the last frame (real time × playback speed), so slow motion slows the animation too.</summary>
    public void Update(in SoldierTruth a, in SoldierTruth b, float t, float dt, Camera3D cam, bool labels)
    {
        var pos = a.Pos + (b.Pos - a.Pos) * t;
        float simYaw = Mathf.LerpAngle((float)a.Yaw, (float)b.Yaw, t);
        float aimYaw = Mathf.LerpAngle((float)a.AimYaw, (float)b.AimYaw, t);
        float pitch = Mathf.Lerp((float)a.AimPitch, (float)b.AimPitch, t);
        float crouch = Mathf.Lerp((float)a.Crouch, (float)b.Crouch, t);
        float raise = Mathf.Lerp((float)a.Raise, (float)b.Raise, t);

        if (_first) { Yaw = _prevYaw = simYaw; _first = false; }
        // the legs follow the simulated heading with a lag while aiming; the rifle swings ±60° ahead of them
        Yaw = DampA(Yaw, simYaw, b.Aiming ? 7 : 14, dt);
        float rel = WrapA(aimYaw - Yaw);
        if (MathF.Abs(rel) > 1.0f) Yaw = WrapA(aimYaw - MathF.Sign(rel) * 1.0f);
        float yawRate = dt > 0 ? WrapA(Yaw - _prevYaw) / dt : 0;
        _prevYaw = Yaw;

        // a body stays where it fell: the ragdoll is posed in the frame the soldier died in
        if (!b.Alive) { UpdateDead(dt, cam); return; }
        Root.Position = new Vector3((float)pos.X, 0, (float)pos.Y);
        Root.Rotation = new Vector3(0, Yaw, 0);

        // velocity of the drawn body: between two ticks it moves at exactly the newer tick's velocity
        var vel = new Vector3((float)b.Vel.X, 0, (float)b.Vel.Y);
        var acc = dt > 0 ? (vel - _prevVel) / dt : Vector3.Zero;
        _prevVel = vel;
        float c = MathF.Cos(Yaw), s = MathF.Sin(Yaw);
        Vector3 ToLocal(Vector3 v) => new(v.X * c - v.Z * s, 0, v.X * s + v.Z * c);
        Vector3 ToLocal3(Vector3 v) => new(v.X * c - v.Z * s, v.Y, v.X * s + v.Z * c);

        var inp = new SoldierRig.Input
        {
            VelLocal = ToLocal(vel), AccLocal = ToLocal(acc), YawRate = yawRate,
            Raise = raise, HasAim = raise > 0.01f, AimYaw = WrapA(aimYaw - Yaw), AimPitch = pitch,
            Crouch = crouch, Shot = Shot,
            Reloading = b.Reloading, ReloadT = b.Reloading ? MathF.Max(0, (float)b.ReloadT - (1 - t) * (float)Match.Dt) : 0, ReloadEmpty = ReloadEmpty,
            RootPos = Root.Position, RootYaw = Yaw,
        };
        if (Flinch is { } f)
        {
            inp.Flinch = true; inp.FlinchDir = ToLocal3(f.dir); inp.FlinchAt = ToLocal3(f.at - Root.Position); inp.FlinchPower = 1;
            _stagV += new Vector3(f.dir.X, 0, f.dir.Z).Normalized() * Stagger(f.zone) * 0.6f;
            Flinch = null;
        }
        // the stagger: shown as a few centimetres of offset that springs back, while the legs step with it
        if (dt > 0) { _stagV += (-90 * _stag - 13 * _stagV) * dt; _stag += _stagV * dt; }
        Model.Position = new Vector3(_stag.X * c - _stag.Z * s, 0, _stag.X * s + _stag.Z * c);
        inp.VelLocal += ToLocal(_stagV);
        bool shot = Shot;
        Shot = false;
        Rig.Update(dt, inp);
        Rig.Pose.Apply(_sk);
        var body = Root.Transform * Model.Transform;
        _sling.Step(dt, body * Rig.Out.SwivelF, body * Rig.Out.SwivelR, -cam.GlobalBasis.Z);
        MuzzleW = body * Rig.Out.Muzzle; BarrelW = Root.Basis * Rig.Out.Barrel;
        if (shot)
        {
            Sfx.At(MuzzleW, (s, p, v) => s.Shot(p, v));
            _fx.MuzzleFlash(MuzzleW, BarrelW);
            _fx.Eject(body * Rig.Out.Eject, Root.Basis * Rig.Out.EjectDir, vel);
        }
        // the empty magazine drops out at the start of every reload; the reload's sounds at its phases
        bool Crossed(float at) => inp.Reloading && _prevReloadT < at && inp.ReloadT >= at;
        if (Crossed(SoldierRig.Reload.MagOut))
        {
            _fx.DropMag(body * Rig.Out.Mag, vel * 0.8f);
            Sfx.At(Root.Position, (s, p, v) => s.MagOut(p, v));
        }
        if (Crossed(SoldierRig.Reload.Grab)) Sfx.At(Root.Position, (s, p, v) => s.Click(p, v));
        if (Crossed(SoldierRig.Reload.Seat)) Sfx.At(Root.Position, (s, p, v) => s.MagIn(p, v));
        if (ReloadEmpty && Crossed(SoldierRig.Reload.Bolt)) Sfx.At(Root.Position, (s, p, v) => s.Bolt(p, v));
        if (Rig.Out.Steps != 0)
        {
            float loud = Clamp(vel.Length() / 2, 0.4f, 1.2f);
            Sfx.At(Root.Position, (s, p, v) => s.Step(p, v), loud);
            if (vel.Length() > 2.2f) _fx.Emit("dust", Root.Position + new Vector3(0, 0.04f, 0), -vel.Normalized() * 0.3f, 3, 0.5f, 0.3f);
        }
        _prevReloadT = inp.Reloading ? inp.ReloadT : 0;
        // joint velocities of the living body, for the ragdoll
        if (dt > 0) { Array.Copy(_jCur, _jPrev, _jCur.Length); _rag.Sample(body, _jCur); _sampleDt = dt; _sampled = true; }
        if (ProbeFeet) MeasureFeet();

        var headP = Root.Position + new Vector3(0, 2.0f - 0.7f * crouch, 0);
        bool show = labels && !cam.IsPositionBehind(headP);
        _label.Visible = _hpBg.Visible = _hpFill.Visible = show;
        if (!show) return;
        var sp = cam.UnprojectPosition(headP);
        string intent = _brain?.Intent ?? "";
        _label.Text = $"{_name} {intent}{(b.Reloading ? " ⟳" : "")}";
        _label.Position = sp + new Vector2(-_label.Size.X / 2, -22);
        _hpBg.Position = sp + new Vector2(-17, -4);
        float frac = (float)Math.Clamp(b.Hp / _maxHp, 0, 1);
        _hpFill.Position = _hpBg.Position;
        _hpFill.Size = new Vector2(34 * frac, 4);
        _hpFill.Color = frac > 0.5f ? new Color("7cd65a") : frac > 0.25f ? new Color("e8c547") : new Color("e04a3a");
    }

    void UpdateDead(float dt, Camera3D cam)
    {
        _label.Visible = _hpBg.Visible = _hpFill.Visible = false;
        var body = Root.Transform * Model.Transform;
        if (!Dead)
        {
            Dead = true; _deadT = 0; _poolT = 0;
            Vector3[]? vel = null;
            if (_sampled) { vel = new Vector3[_jCur.Length]; for (int k = 0; k < vel.Length; k++) vel[k] = (_jCur[k] - _jPrev[k]) / _sampleDt; }
            var (point, dir, zone) = Killed ?? (body * new Vector3(0, 1.2f, 0), -(Root.Basis * Vector3.Back), HitZone.Torso);
            var push = new Vector3(dir.X, MathF.Max(dir.Y, -0.15f), dir.Z).Normalized();
            var side = new Vector3(-push.Z, 0, push.X) * (_rnd.NextDouble() < 0.5 ? -1 : 1);
            _rag.Start(body, vel, new[]
            {
                new Ragdoll.Impulse { Point = point, Dir = push, Speed = Kick(zone), Spread = 0.3f },
                // a little off-axis shove at one shoulder, so the body twists as it goes
                new Ragdoll.Impulse { Point = body * new Vector3(0, 1.42f, 0) + side * 0.2f, Dir = side, Speed = 0.7f + (float)_rnd.NextDouble() * 0.6f, Spread = 0 },
            });
        }
        if (dt <= 0) return;
        _deadT += dt;
        if (_rag.Sleep < 1.5f) { _rag.Step(dt); Rig.Pose.Apply(_sk); }
        if (_rag.ImpactSpeed > 2 && _deadT - _thumpT > 0.07f)
        {
            float k = Clamp(_rag.ImpactSpeed / 4, 0.3f, 1.2f);
            Sfx.At(_rag.Spine, (s, p, v) => s.Impact("body", p, v), k);
            _thumpT = _deadT;
        }
        _rag.ImpactSpeed = 0;
        var rifle = body * Rig.Pose.Local(Rig.Pose.Find("Rifle"));
        _sling.Step(dt, rifle * SoldierRig.Gun.SwivelF, rifle * SoldierRig.Gun.SwivelR, -cam.GlobalBasis.Z);
        // blood spreads under the chest once the body has come to rest
        if (_deadT > 0.9f)
        {
            if (_pool == null)
            {
                var sp = _rag.Spine;
                _pool = new MeshInstance3D { Mesh = PoolMesh, MaterialOverride = PoolMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
                Root.GetParent().AddChild(_pool);
                _pool.Position = new Vector3(sp.X, 0.012f, sp.Z);
                _pool.Rotation = new Vector3(-Mathf.Pi / 2, 0, (float)_rnd.NextDouble() * 6);
            }
            _poolT += dt;
            _pool.Scale = Vector3.One * (0.15f + 1.05f * (1 - MathF.Exp(-_poolT / 3.5f)));
        }
    }

    static readonly QuadMesh PoolMesh = new() { Size = Vector2.One };
    static StandardMaterial3D? _poolMat;
    static StandardMaterial3D PoolMat => _poolMat ??= new StandardMaterial3D
    {
        AlbedoTexture = Art.Texture("tex/fx_pool.png"), Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        Roughness = 0.25f, Metallic = 0.1f, TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmaps,
    };
    public static void ClearMaterials() => _poolMat = null;

    // --probe-feet: how far a planted foot travels in mid-stance compared with the body (it should stay put)
    public static bool ProbeFeet;
    public static double SlipSum, BodySum;
    readonly Vector3?[] _ankle = new Vector3?[2];
    Vector3? _rootPrev;
    void MeasureFeet()
    {
        var g = Rig.Gait;
        var root = Root.Position;
        for (int k = 0; k < 2; k++)
        {
            var f = g.Feet[k];
            var p = ToWorld(Rig.Pose.WorldPos(Rig.FootBone(k)));
            bool mid = f.Stance && g.MoveW > 0.8f && f.Bell > 0.6f;   // heel and toe roll excluded
            if (mid && _ankle[k] is { } a && _rootPrev is { } r)
            {
                SlipSum += new Vector2(p.X - a.X, p.Z - a.Z).Length();
                BodySum += new Vector2(root.X - r.X, root.Z - r.Z).Length() / 2;
            }
            _ankle[k] = mid ? p : null;
        }
        _rootPrev = root;
    }

    /// <summary>World transform of a point given in the soldier's model space.</summary>
    public Vector3 ToWorld(Vector3 p) => Root.Transform * p;
}
