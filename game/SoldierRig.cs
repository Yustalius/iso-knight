using Godot;
using static RigMath;

/// <summary>Rifleman animation ported from the concept's soldier-rig.js. The legs use the speed-driven gait; the upper
/// body is driven by the rifle: a pose is chosen (ready / port arms / shouldered / reload), placed in chest space, then
/// both hands are solved onto its grips with two-bone IK. Everything is computed in model space (the soldier's root is
/// the identity); world-space inputs (root position and yaw) are only used for the secondary motion.</summary>
public sealed class SoldierRig
{
    // ---------- model data (soldier-model.js) ----------
    public static class Sole { public const float Bottom = -0.0905f, Heel = -0.0765f, Toe = 0.1885f; }
    // rifle-space points: origin at the trigger, +Z to the muzzle, +Y up, +X the rifle's left side
    public static class Gun
    {
        public static readonly Vector3 Butt = new(0, -0.01f, -0.378f), Muzzle = new(0, 0.03f, 0.61f), Sight = new(0, 0.108f, -0.075f);
        public static readonly Vector3 Eject = new(-0.024f, 0.036f, -0.01f), EjectDir = new Vector3(-1, 0.45f, -0.3f).Normalized();
        public static readonly Vector3 SwivelF = new(0, -0.006f, 0.445f), SwivelR = new(0, -0.088f, -0.33f);
        public static readonly Vector3 MagWell = new(0, -0.032f, 0.05f);
    }

    // hand frames in rifle space: -Y runs wrist→knuckles, X is the palm normal (right hand) or its opposite (left)
    static Quaternion HandFrame(Vector3 fingers, Vector3 palm, bool left)
    {
        var p = palm.Normalized(); var f = fingers.Normalized();
        f = (f - p * f.Dot(p)).Normalized();
        var x = left ? -p : p; var y = -f; var z = x.Cross(y).Normalized();
        return FromBasis(x, y, z);
    }
    // rifle orientation from a forward direction and an "up" hint (+Z forward, +Y top of the rifle)
    static Quaternion RifleQ(Vector3 dir, Vector3 up)
    {
        var z = dir.Normalized(); var x = up.Cross(z).Normalized(); var y = z.Cross(x);
        return FromBasis(x, y, z);
    }

    static readonly Vector3 GripRPos = new(-0.04f, -0.056f, -0.09f), GripLPos = new(0.043f, -0.026f, 0.15f);
    static readonly Quaternion GripRQ = HandFrame(new(0, -0.39f, 0.92f), new(1, 0, 0), false);
    static readonly Quaternion GripLQ = HandFrame(new(-0.3f, 0.25f, 0.92f), new(-0.35f, 0.94f, 0), true);
    // support-hand frames for the reload: rifle-relative except the pouch, which is body-relative
    enum LF { Grip, Mag, Bolt, Pouch }
    static readonly Quaternion[] LFrame =
    {
        GripLQ,
        HandFrame(new(0, 0.2f, 1), new(-0.3f, 1, 0), true),       // cupping a magazine's floorplate
        HandFrame(new(0, 0.55f, 0.8f), new(-1, 0, 0), true),      // palm on the receiver's left side
        HandFrame(new(0, -1, 0.25f), new(0, -0.2f, -1), true),    // fingers down into the belt pouch
    };

    // rifle carry poses in chest space: butt position, muzzle direction, rifle-top hint
    struct CarryPose { public Vector3 Butt, Dir, Up; }
    static readonly CarryPose Ready = new() { Butt = new(-0.145f, 0.055f, 0.11f), Dir = new(0.55f, -0.55f, 0.63f), Up = new(-0.2f, 1, 0.2f) };
    static readonly CarryPose Port = new() { Butt = new(-0.17f, -0.29f, 0.14f), Dir = new(0.585f, 0.66f, 0.48f), Up = new(-0.25f, -0.1f, 1) };
    static readonly CarryPose ReloadPose = new() { Butt = new(-0.215f, -0.06f, -0.05f), Dir = new(0.4f, 0.06f, 0.92f), Up = new(-0.55f, 0.84f, 0) };
    static readonly Vector3 Pocket = new(-0.118f, 0.17f, 0.078f);   // shoulder pocket for the shouldered rifle

    /// <summary>Magazine change (RELOAD of soldier-rig.js). The support hand walks a chain of anchors that are
    /// re-evaluated every frame, so it follows the moving rifle.</summary>
    public static class Reload
    {
        public const float MagOut = 0.17f, Grab = 0.48f, Seat = 1.02f, Bolt = 1.37f, FullDur = 1.72f, TacticalDur = 1.38f;
        public enum A { Guard, Drop, Pouch, Below, Well, Slap, Bolt, BoltIn }
        public static readonly (float t, A anchor, int frame)[] Full =
        {
            (0, A.Guard, 0), (0.17f, A.Drop, 0), (0.36f, A.Pouch, 3), (0.5f, A.Pouch, 3), (0.76f, A.Below, 1), (0.92f, A.Below, 1),
            (1.02f, A.Well, 1), (1.1f, A.Slap, 1), (1.18f, A.Well, 1), (1.31f, A.Bolt, 2), (1.4f, A.BoltIn, 2), (1.72f, A.Guard, 0)
        };
        public static readonly (float t, A anchor, int frame)[] Tactical =
        {
            (0, A.Guard, 0), (0.17f, A.Drop, 0), (0.36f, A.Pouch, 3), (0.5f, A.Pouch, 3), (0.76f, A.Below, 1), (0.92f, A.Below, 1),
            (1.02f, A.Well, 1), (1.1f, A.Slap, 1), (1.18f, A.Well, 1), (1.38f, A.Guard, 0)
        };
    }

    public struct Input
    {
        public Vector3 VelLocal, AccLocal;   // character space
        public float YawRate;
        public float Raise;                  // 0 lowered … 1 shouldered (the simulation's Raise)
        public bool HasAim;                  // aim angles are valid (the barrel follows them while raised)
        public float AimYaw, AimPitch;       // character space: yaw from +Z toward +X, pitch up
        public float Crouch;
        public bool Shot;
        public bool Reloading; public float ReloadT; public bool ReloadEmpty;
        public bool Flinch; public Vector3 FlinchDir, FlinchAt; public float FlinchPower;   // character space
        public Vector3 RootPos; public float RootYaw;   // world placement, for the secondary motion
    }

    public struct Output
    {
        public Vector3 Muzzle, Barrel, Eject, EjectDir, SwivelF, SwivelR;   // character space
        public Transform3D Rifle, Mag;
        public bool MagVisible;
        public int Steps;          // footfalls this frame (bit 0 left, bit 1 right)
    }

    readonly Pose _p;
    readonly int hips, spine, chest, neck, head, canteen, buttpack, rifle, mag;
    readonly int[] upper = new int[2], fore = new int[2], hand = new int[2], thigh = new int[2], shin = new int[2], foot = new int[2], strap = new int[2];
    readonly Gait _gait = new();
    readonly Random _rnd;

    public float Aim, CrouchW, AimYawS, AimPitchS, ReloadW, Time;
    float _reloadT;
    Vector3 _acc, _pocket = new(-0.14f, 1.36f, 0);
    readonly Spring kickZ = new(420, 30), kickP = new(300, 17), kickY = new(300, 20), shoulder = new(200, 16);
    readonly Spring canteenX = new(90, 5), canteenZ = new(90, 5), strapX = new(60, 3.2f), strapZ = new(60, 3.2f);
    readonly Spring impact = new(260, 18);
    readonly Spring flP = new(140, 11), flR = new(140, 11), flT = new(120, 10), flH = new(180, 9);
    Vector3? _headPrev; Vector3 _headVel;
    public Output Out;

    // [x, z, foot yaw] of the feet standing still, relaxed and in the bladed aiming stance; index 0 is the left foot
    static readonly float[][] IdleFeet = { new[] { 0.125f, 0.05f, 0.16f }, new[] { -0.13f, -0.04f, -0.12f } };
    static readonly float[][] AimFeet = { new[] { 0.12f, 0.15f, -0.12f }, new[] { -0.15f, -0.12f, -0.62f } };
    readonly float[][] _feet = { new float[3], new float[3] };

    public Pose Pose => _p;

    public SoldierRig(Pose pose, int seed)
    {
        _p = pose; _rnd = new Random(seed);
        hips = _p.Find("Hips"); spine = _p.Find("Spine"); chest = _p.Find("Chest"); neck = _p.Find("Neck"); head = _p.Find("Head");
        canteen = _p.Find("Canteen"); buttpack = _p.Find("ButtPack"); rifle = _p.Find("Rifle"); mag = _p.Find("Magazine");
        string[] sides = { "L", "R" };
        for (int k = 0; k < 2; k++)
        {
            upper[k] = _p.Find("UpperArm_" + sides[k]); fore[k] = _p.Find("Forearm_" + sides[k]); hand[k] = _p.Find("Hand_" + sides[k]);
            thigh[k] = _p.Find("Thigh_" + sides[k]); shin[k] = _p.Find("Shin_" + sides[k]); foot[k] = _p.Find("Foot_" + sides[k]);
            strap[k] = _p.Find("ChinStrap_" + sides[k]);
        }
    }

    float R() => (float)_rnd.NextDouble();

    Transform3D _chestM, _hipsM;
    Vector3 ChestPoint(Vector3 v) => _chestM * v;
    Vector3 ChestDir(Vector3 v) => _chestM.Basis * v;
    (Vector3 butt, Quaternion q) FromChest(in CarryPose c) => (ChestPoint(c.Butt), RifleQ(ChestDir(c.Dir), ChestDir(c.Up)));
    static (Vector3, Quaternion) Blend((Vector3 b, Quaternion q) a, (Vector3 b, Quaternion q) c, float w) => (a.b.Lerp(c.b, w), a.q.Slerp(c.q, w));

    public void Update(float dt, in Input input)
    {
        if (dt <= 0) return;
        Time += dt;
        var vl = input.VelLocal;
        _acc.X = Damp(_acc.X, Clamp(input.AccLocal.X, -12, 12), 10, dt);
        _acc.Z = Damp(_acc.Z, Clamp(input.AccLocal.Z, -12, 12), 10, dt);
        Aim = Clamp(input.Raise, 0, 1);
        CrouchW = Clamp(input.Crouch, 0, 1);
        ReloadW = Damp(ReloadW, input.Reloading ? 1 : 0, 12, dt);
        float cr = CrouchW, aw = Smooth(Aim);

        // standing still while aiming, the feet settle into a bladed stance
        for (int k = 0; k < 2; k++)
        {
            float sw0 = aw * (1 - _gait.MoveW);
            for (int c = 0; c < 3; c++) _feet[k][c] = Lerp(IdleFeet[k][c], AimFeet[k][c], sw0);
        }
        _gait.Step(dt, vl, input.YawRate, _feet, cr, 0.115f + 0.03f * cr);
        var gL = _gait.Feet[0]; var gR = _gait.Feet[1];
        float mw = _gait.MoveW, rw = _gait.RunW * (1 - aw);

        // aim angles; the rifle can swing ±60° before the body must turn
        float yawT = 0, pitchT = -0.03f;
        if (input.HasAim) { yawT = input.AimYaw; pitchT = Clamp(input.AimPitch, -0.85f, 0.6f); }
        AimYawS += WrapA(Clamp(yawT, -1.05f, 1.05f) - AimYawS) * (1 - MathF.Exp(-22 * dt));
        AimPitchS = Damp(AimPitchS, pitchT, 22, dt);

        // ---------- recoil and impacts ----------
        if (input.Shot)
        {   // spring units: metres for kickZ, radians for the pitch and yaw kicks
            kickZ.V -= 1 + R() * 0.25f; kickP.V += 1.7f + R() * 0.7f; kickY.V += (R() - 0.5f) * 0.7f;
            shoulder.V += 1.5f;
        }
        float kz = kickZ.Step(0, dt), kp = kickP.Step(0, dt), ky = kickY.Step(0, dt), ksh = shoulder.Step(0, dt);
        if (input.Flinch)
        {
            // the torso is driven along the bullet, twisted by the off-centre torque (τy = rz·Fx − rx·Fz)
            var dir = input.FlinchDir; var at = input.FlinchAt; float power = input.FlinchPower, up = Clamp((at.Y - 0.9f) / 0.6f, 0, 1);
            flP.V += dir.Z * 11 * power * (0.4f + up); flR.V -= dir.X * 9 * power * (0.4f + up);
            flT.V += (at.Z * dir.X - at.X * dir.Z) * 40 * power;
            if (at.Y > 1.5f) flH.V -= 14 * power * MathF.Sign(-dir.Z == 0 ? 1 : -dir.Z);
            impact.V += 5 * power;
        }
        float imp = impact.Step(0, dt);
        float fp = flP.Step(0, dt), fr = flR.Step(0, dt), ft = flT.Step(0, dt), fh = flH.Step(0, dt);

        // ---------- body ----------
        float breath = MathF.Sin(Time * 2 * MathF.PI / 3.1f);
        float bob = Lerp(0.014f, 0.032f, _gait.RunW) * MathF.Cos(4 * MathF.PI * (gR.P - 0.18f * _gait.RunW)) * mw * (1 - 0.35f * aw);
        float hx = 0.018f * (gL.Bell - gR.Bell) * mw * (1 - _gait.RunW * 0.6f);
        float hy = Lerp(0.85f + 0.003f * breath, Lerp(0.855f, 0.83f, _gait.RunW), mw) - bob - 0.025f * aw - 0.26f * cr;
        float hz = -0.04f * cr;
        // a right-handed shooter blades the body: hips and chest turn right of the line of fire
        float pelvisYaw = -0.12f * (gL.Z - gR.Z) * mw * (1 - aw) + aw * (AimYawS * 0.45f - 0.42f);
        float twist = -1.7f * (-0.12f * (gL.Z - gR.Z) * mw) * (1 - aw) + aw * (AimYawS * 0.55f + 0.02f) - 0.12f * (1 - aw) * (1 - rw);
        float lean = Lerp(0.02f + 0.006f * breath, Lerp(0.04f, 0.2f, _gait.RunW), mw) + 0.15f * aw + 0.2f * cr + Clamp(_acc.Z * 0.018f, -0.12f, 0.14f) - ksh * 0.25f;
        float bank = Clamp(-_acc.X * 0.02f, -0.14f, 0.14f);
        float roll = 0.022f * (gL.Bell - gR.Bell) * mw + bank;
        lean += fp; roll += fr; twist += ft; hz += fp * 0.08f;
        if (input.Reloading) lean += 0.04f * ReloadW;

        _p.Reset();
        _p.Pos[hips] = new Vector3(hx, hy, hz);
        _p.Rot[hips] = EulerXYZ(0, pelvisYaw, roll);
        _p.Rot[spine] = EulerXYZ(lean * 0.35f, 0, -roll * 0.5f);
        _p.Rot[chest] = EulerXYZ(lean * 0.65f, twist, -roll * 0.3f);
        _chestM = _p.World(chest);
        _hipsM = _p.World(hips);

        // ---------- legs ----------
        int steps = 0;
        for (int k = 0; k < 2; k++)
        {
            bool left = k == 0;
            var g = _gait.Feet[k]; var id = _feet[k];
            float x = Lerp(id[0], g.X, mw), z = Lerp(id[1], g.Z, mw);
            float lift = g.Lift * mw, pitch = g.Pitch * mw;
            x += (left ? 0.035f : -0.035f) * cr * (1 - mw);
            if (!left && cr > 0.01f) z -= 0.06f * cr * (1 - mw);
            // ankle placement that pivots the sole about the heel (pitch < 0) or the toe (pitch > 0)
            var fq = EulerYXZ(pitch, _gait.FootYaw[k], 0);
            float pivot = pitch < 0 ? Sole.Heel : Sole.Toe;
            var c = fq * new Vector3(0, Sole.Bottom, pivot);
            var fpos = new Vector3(x, -c.Y + lift, z + pivot - c.Z);
            _p.SolveLimb(thigh[k], shin[k], foot[k], fpos, new Vector3(left ? 0.15f : -0.15f, 0.5f, 1.2f + 0.4f * cr));
            _p.SetWorldRot(foot[k], fq);
            if (g.Strike) steps |= 1 << k;
        }

        // ---------- rifle ----------
        var ready = Blend(FromChest(Ready), FromChest(Port), Smooth(rw * 1.2f));
        // swing and breathing sway of the carried rifle
        ready.Item2 *= EulerXYZ(0.03f * breath * (1 - mw) + 0.05f * bob * 20, 0.06f * MathF.Sin(_gait.Phase * MathF.PI * 2) * mw, 0);
        var aimDir = new Vector3(MathF.Sin(AimYawS) * MathF.Cos(AimPitchS), MathF.Sin(AimPitchS), MathF.Cos(AimYawS) * MathF.Cos(AimPitchS));
        (Vector3 b, Quaternion q) aimPose = (ChestPoint(Pocket), RifleQ(aimDir, Vector3.Up));
        _pocket = aimPose.b + aimPose.q * new Vector3(0, 0.04f, 0);   // the bore line at the butt
        var pose = Blend(ready, aimPose, aw);
        if (input.Reloading) pose = Blend(pose, FromChest(ReloadPose), Smooth(ReloadW));
        // recoil pivots about the butt: muzzle climb, a little yaw, and the rifle driving back into the shoulder
        var qk = pose.Item2 * EulerXYZ(-kp, ky, 0);
        var butt = pose.Item1 + pose.Item2 * new Vector3(0, 0, kz - imp * 0.04f);
        _p.Rot[rifle] = qk;
        _p.Pos[rifle] = butt - qk * Gun.Butt;
        var rifleM = new Transform3D(new Basis(qk), _p.Pos[rifle]);
        Vector3 Rp(Vector3 v) => rifleM * v;

        // ---------- hands ----------
        // right hand never leaves the pistol grip
        _p.SolveLimb(upper[1], fore[1], hand[1], Rp(GripRPos), new Vector3(-0.75f, Lerp(0.55f, 1.25f, aw), -0.25f));
        _p.SetWorldRot(hand[1], qk * GripRQ);
        // left hand: the handguard, or the reload path through the belt pouch and the magazine well
        int magState = 0;   // 0 seated, 1 in the hand, 2 gone (dropped)
        {
            var target = Rp(GripLPos); var hq = qk * GripLQ;
            if (input.Reloading)
            {
                float t = input.ReloadT; bool empty = input.ReloadEmpty;
                var seq = empty ? Reload.Full : Reload.Tactical;
                float dur = empty ? Reload.FullDur : Reload.TacticalDur;
                Vector3 Anchor(Reload.A a) => a switch
                {
                    Reload.A.Guard => Rp(GripLPos),
                    Reload.A.Drop => Rp(new(0.07f, -0.12f, 0.12f)),
                    Reload.A.Pouch => _hipsM * new Vector3(0.092f, 0.1f, 0.15f),
                    Reload.A.Below => Rp(new(0.012f, -0.3f, 0.095f)),
                    Reload.A.Well => Rp(new(0.012f, -0.235f, 0.095f)),
                    Reload.A.Slap => Rp(new(0.012f, -0.205f, 0.095f)),
                    Reload.A.Bolt => Rp(new(0.085f, -0.01f, -0.03f)),
                    _ => Rp(new(0.05f, -0.01f, -0.03f)),
                };
                var keys = new float[seq.Length][];
                for (int k = 0; k < seq.Length; k++) { var v = Anchor(seq[k].anchor); keys[k] = new[] { seq[k].t, v.X, v.Y, v.Z }; }
                Span<float> xyz = stackalloc float[3];
                Curve(keys, t, xyz);
                float w = Smooth(t / 0.06f) * (1 - Smooth((t - (dur - 0.1f)) / 0.1f));
                target = target.Lerp(new Vector3(xyz[0], xyz[1], xyz[2]), w);
                bool holding = t > Reload.Grab && t < Reload.Seat + 0.02f;
                // hand orientation eases between the frames of the surrounding keys
                int i = 0; while (i < seq.Length - 2 && t > seq[i + 1].t) i++;
                Quaternion Frame(int f) => f == (int)LF.Pouch ? LFrame[f] : qk * LFrame[f];
                hq = Frame(seq[i].frame).Slerp(Frame(seq[i + 1].frame), Smooth((t - seq[i].t) / (seq[i + 1].t - seq[i].t)));
                // the rifle jolts when the magazine is slapped home and when the bolt slams forward
                if (t < _reloadT) _reloadT = 0;   // a new reload started
                foreach (float ev in new[] { Reload.Seat + 0.06f, empty ? Reload.Bolt : -1 })
                    if (_reloadT < ev && t >= ev) { kickP.V += 0.9f; kickZ.V += 0.25f; }
                _reloadT = t;
                magState = t < Reload.MagOut ? 0 : holding ? 1 : t >= Reload.Seat ? 0 : 2;
            }
            else _reloadT = 0;
            _p.SolveLimb(upper[0], fore[0], hand[0], target, new Vector3(0.55f, Lerp(0.35f, 0.7f, aw), 0.2f));
            _p.SetWorldRot(hand[0], hq);
        }
        // magazine: seated, in the support hand, or gone (dropped)
        if (magState == 0) { _p.Pos[mag] = Rp(Gun.MagWell); _p.Rot[mag] = qk; }
        else if (magState == 1)
        {
            // held by the floorplate: the same hand→magazine offset as when it is seated from the 'well' anchor
            _p.Pos[mag] = _p.WorldPos(hand[0]) + qk * new Vector3(-0.012f, 0.203f, -0.045f);
            _p.Rot[mag] = qk;
        }
        else { _p.Pos[mag] = new Vector3(0, -5, 0); _p.Scale[mag] = Vector3.One * 0.001f; }

        // ---------- head: look along the line of fire, cheek welded to the stock ----------
        {
            _p.Pos[neck] += new Vector3(0, 0, 0.025f * aw);
            _p.Rot[neck] = EulerXYZ(0.4f * aw + 0.1f * cr + fh * 0.5f, -(pelvisYaw + twist) * 0.55f * (1 - aw), 0);
            float lookYaw = AimYawS * aw + (input.Reloading ? 0.35f * ReloadW : 0);
            float lookPitch = AimPitchS * aw + (input.Reloading ? -0.55f * ReloadW : -0.04f) - 0.3f * aw - 0.08f * lean - fh;
            var want = EulerYXZ(-lookPitch, lookYaw, 0.42f * aw);
            var cur = _p.WorldRot(head);
            _p.SetWorldRot(head, cur.Slerp(want, Clamp(aw + ReloadW * 0.6f + 0.35f, 0, 1)));
        }

        // ---------- secondary motion: canteen, butt pack, chin straps ----------
        {
            var rootQ = new Quaternion(Vector3.Up, input.RootYaw);
            var hp = input.RootPos + rootQ * _p.WorldPos(head);
            if (_headPrev is { } prev)
            {
                var hv = (hp - prev) / dt;
                var ha = rootQ.Inverse() * ((hv - _headVel) / dt);
                _headVel = hv;
                ha = new Vector3(Clamp(ha.X, -40, 40), Clamp(ha.Y, -40, 40), Clamp(ha.Z, -40, 40));
                strapX.Step(0, dt, ha.Z * 1.6f); strapZ.Step(0, dt, -ha.X * 1.6f + input.YawRate * 2);
                canteenX.Step(0, dt, ha.Z * 1.1f + ha.Y * 0.6f); canteenZ.Step(0, dt, -ha.X * 1.1f);
            }
            _headPrev = hp;
            for (int k = 0; k < 2; k++) _p.Rot[strap[k]] = EulerXYZ(Clamp(strapX.X, -0.9f, 0.9f) + 0.1f, 0, Clamp(strapZ.X, -0.7f, 0.7f));
            var tl = _p.Rot[thigh[0]];
            float swing = 2 * MathF.Atan2(tl.X, tl.W);
            _p.Rot[canteen] = EulerXYZ(Clamp(canteenX.X, -0.6f, 0.6f) + MathF.Max(0, -swing) * 0.3f, 0.95f, Clamp(canteenZ.X, -0.5f, 0.5f));
            _p.Rot[buttpack] = EulerXYZ(Clamp(canteenX.X, -0.5f, 0.5f) * 0.4f - lean * 0.2f, 0, 0);
        }

        // ---------- outputs for the effects ----------
        Out.Rifle = rifleM;
        Out.Muzzle = rifleM * Gun.Muzzle;
        Out.Barrel = (rifleM.Basis * Vector3.Back).Normalized();
        Out.Eject = rifleM * Gun.Eject;
        Out.EjectDir = rifleM.Basis * Gun.EjectDir;
        Out.SwivelF = rifleM * Gun.SwivelF;
        Out.SwivelR = rifleM * Gun.SwivelR;
        Out.Mag = _p.Local(mag);
        Out.MagVisible = magState != 2;
        Out.Steps = steps;
    }
}
