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
    public (Vector3 dir, Vector3 at, float power)? Flinch;   // world space, consumed by the next update
    public bool Dead;
    float _fall;

    public SoldierView(int id, int team, IBrain? brain, double maxHp, Node3D parent, Control overlay)
    {
        Id = id; Team = team; _brain = brain; _maxHp = maxHp;
        _name = $"{id} {brain?.Name ?? ""}";
        Root = new Node3D { Name = $"Soldier{id}" };
        parent.AddChild(Root);
        Model = Art.Model(team == 0 ? "soldier_m81.glb" : "soldier_opfor.glb");
        Root.AddChild(Model);
        _sk = Art.FindSkeleton(Model)!;
        Rig = new SoldierRig(new Pose(_sk), 7919 * (id + 1));
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

        Root.Position = new Vector3((float)pos.X, 0, (float)pos.Y);
        Root.Rotation = new Vector3(0, Yaw, 0);

        if (!b.Alive)
        {
            // placeholder until the ragdoll: topple backwards
            Dead = true;
            _fall = Math.Min(1, _fall + dt * 3);
            Model.Rotation = new Vector3(-Mathf.Pi / 2 * _fall * _fall, 0, 0);
            Model.Position = new Vector3(0, 0.12f * _fall, 0);
            _label.Visible = _hpBg.Visible = _hpFill.Visible = false;
            return;
        }
        Dead = false;
        if (_fall != 0) { _fall = 0; Model.Rotation = Vector3.Zero; Model.Position = Vector3.Zero; }

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
            inp.Flinch = true; inp.FlinchDir = ToLocal3(f.dir); inp.FlinchAt = ToLocal3(f.at - Root.Position); inp.FlinchPower = f.power;
            Flinch = null;
        }
        Shot = false;
        Rig.Update(dt, inp);
        Rig.Pose.Apply(_sk);
        _sling.Step(dt, ToWorld(Rig.Out.SwivelF), ToWorld(Rig.Out.SwivelR), -cam.GlobalBasis.Z);
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
