using Godot;

/// <summary>Shared pieces of the procedural rig, ported from the concept's rig-core.js: Hermite key curves, damped
/// springs, analytic two-bone IK, heel/toe foot placement and the speed-driven gait. Units and axes as in three.js:
/// metres, +Y up, the character faces +Z, its right hand is at −X.</summary>
static class RigMath
{
    public static float Clamp(float x, float a, float b) => x < a ? a : x > b ? b : x;
    public static float Lerp(float a, float b, float t) => a + (b - a) * t;
    public static float Smooth(float x) { x = Clamp(x, 0, 1); return x * x * (3 - 2 * x); }
    public static float Damp(float a, float b, float k, float dt) => a + (b - a) * (1 - MathF.Exp(-k * dt));
    public static float Frac(float x) => x - MathF.Floor(x);
    public static float WrapA(float a) => MathF.Atan2(MathF.Sin(a), MathF.Cos(a));
    public static float DampA(float a, float b, float k, float dt) => a + WrapA(b - a) * (1 - MathF.Exp(-k * dt));
    public static float Hermite(float p0, float m0, float p1, float m1, float u)
    {
        float u2 = u * u, u3 = u2 * u;
        return (2 * u3 - 3 * u2 + 1) * p0 + (u3 - 2 * u2 + u) * m0 + (-2 * u3 + 3 * u2) * p1 + (u3 - u2) * m1;
    }

    /// <summary>Non-uniform Hermite through keys [t, values...]; momentum carries through interior keys,
    /// only the first and last keys come to rest.</summary>
    public static void Curve(float[][] keys, float time, Span<float> o)
    {
        int n = keys.Length;
        float t = Clamp(time, keys[0][0], keys[n - 1][0]);
        int i = 0; while (i < n - 2 && t > keys[i + 1][0]) i++;
        float[] a = keys[i], b = keys[i + 1], prev = keys[Math.Max(0, i - 1)], next = keys[Math.Min(n - 1, i + 2)];
        float span = b[0] - a[0], u = (t - a[0]) / span;
        for (int c = 1; c < a.Length; c++)
        {
            float m0 = i == 0 ? 0 : (b[c] - prev[c]) / (b[0] - prev[0]);
            float m1 = i == n - 2 ? 0 : (next[c] - a[c]) / (next[0] - a[0]);
            o[c - 1] = Hermite(a[c], span * m0, b[c], span * m1, u);
        }
    }

    public static float Curve1(float[][] keys, float time) { Span<float> o = stackalloc float[1]; Curve(keys, time, o); return o[0]; }

    // three.js Euler orders: XYZ is the default of Object3D.rotation, YXZ is used for heading-first rotations
    public static Quaternion EulerXYZ(float x, float y, float z) =>
        new Quaternion(Vector3.Right, x) * new Quaternion(Vector3.Up, y) * new Quaternion(Vector3.Back, z);
    public static Quaternion EulerYXZ(float x, float y, float z) =>
        new Quaternion(Vector3.Up, y) * new Quaternion(Vector3.Right, x) * new Quaternion(Vector3.Back, z);
    public static Quaternion FromBasis(Vector3 x, Vector3 y, Vector3 z) => new Basis(x, y, z).GetRotationQuaternion();

    /// <summary>Rotation taking unit vector a onto unit vector b (three's setFromUnitVectors).</summary>
    public static Quaternion FromUnitVectors(Vector3 a, Vector3 b)
    {
        float r = a.Dot(b) + 1;
        Quaternion q;
        if (r < 1e-6f)
        {
            q = MathF.Abs(a.X) > MathF.Abs(a.Z) ? new Quaternion(-a.Y, a.X, 0, 0) : new Quaternion(0, -a.Z, a.Y, 0);
        }
        else
        {
            var c = a.Cross(b);
            q = new Quaternion(c.X, c.Y, c.Z, r);
        }
        return q.Normalized();
    }
}

public sealed class Spring
{
    public float K, C, X, V;
    public Spring(float k, float c) { K = k; C = c; }
    public float Step(float target, float dt, float force = 0) { V += (K * (target - X) - C * V + force) * dt; X += V * dt; return X; }
}

/// <summary>A bone hierarchy with three.js semantics: local position/rotation/scale per bone, world (= model space here,
/// the root is the identity) computed on demand. The rig poses it and the view copies the locals into a Skeleton3D.</summary>
public sealed class Pose
{
    public readonly int Count;
    public readonly string[] Names;
    public readonly int[] Parent;
    public readonly Vector3[] RestPos, Pos, Scale;
    public readonly Quaternion[] RestRot, Rot;

    public Pose(Skeleton3D sk)
    {
        Count = sk.GetBoneCount();
        Names = new string[Count]; Parent = new int[Count];
        RestPos = new Vector3[Count]; Pos = new Vector3[Count]; Scale = new Vector3[Count];
        RestRot = new Quaternion[Count]; Rot = new Quaternion[Count];
        for (int i = 0; i < Count; i++)
        {
            Names[i] = sk.GetBoneName(i); Parent[i] = sk.GetBoneParent(i);
            var r = sk.GetBoneRest(i);
            RestPos[i] = r.Origin; RestRot[i] = r.Basis.GetRotationQuaternion();
        }
        Reset();
    }

    public int Find(string name) { int i = Array.IndexOf(Names, name); if (i < 0) throw new KeyNotFoundException(name); return i; }

    public void Reset()
    {
        for (int i = 0; i < Count; i++) { Pos[i] = RestPos[i]; Rot[i] = RestRot[i]; Scale[i] = Vector3.One; }
    }

    public Transform3D Local(int i) => new(new Basis(Rot[i]).Scaled(Scale[i]), Pos[i]);
    public Transform3D World(int i) => Parent[i] < 0 ? Local(i) : World(Parent[i]) * Local(i);
    public Vector3 WorldPos(int i) => Parent[i] < 0 ? Pos[i] : World(Parent[i]) * Pos[i];
    public Quaternion WorldRot(int i) => Parent[i] < 0 ? Rot[i] : WorldRot(Parent[i]) * Rot[i];

    /// <summary>Give bone i this world rotation (worldRotation of rig-core.js).</summary>
    public void SetWorldRot(int i, Quaternion q) => Rot[i] = Parent[i] < 0 ? q : (WorldRot(Parent[i]).Inverse() * q).Normalized();

    /// <summary>Analytic two-bone IK: the bend plane passes through pole (model space).</summary>
    public void SolveLimb(int upper, int lower, int end, Vector3 target, Vector3 pole)
    {
        var origin = WorldPos(upper);
        Vector3 upperAxis = Pos[lower], lowerAxis = Pos[end];
        float a = upperAxis.Length(), b = lowerAxis.Length();
        var dir = target - origin;
        float dist = RigMath.Clamp(dir.Length(), MathF.Abs(a - b) + 0.001f, a + b - 0.001f);
        dir = dir.Normalized();
        var bend = pole - origin;
        bend = (bend - dir * bend.Dot(dir)).Normalized();
        float along = (a * a - b * b + dist * dist) / (2 * dist), h = MathF.Sqrt(MathF.Max(0, a * a - along * along));
        var elbow = origin + dir * along + bend * h;
        // rest axes are expressed in the parent frame at rest; every limb bone rests unrotated in model space
        SetWorldRot(upper, RigMath.FromUnitVectors(upperAxis.Normalized(), (elbow - origin).Normalized()));
        var actual = WorldPos(lower);
        SetWorldRot(lower, RigMath.FromUnitVectors(lowerAxis.Normalized(), (origin + dir * dist - actual).Normalized()));
    }

    public void Apply(Skeleton3D sk)
    {
        for (int i = 0; i < Count; i++)
        {
            sk.SetBonePosePosition(i, Pos[i]);
            sk.SetBonePoseRotation(i, Rot[i]);
            sk.SetBonePoseScale(i, Scale[i]);
        }
    }
}

/// <summary>Speed-driven gait of rig-core.js: cadence, stride and duty factor follow speed, the stance foot slides back
/// exactly at body speed (no skating), turning on the spot shuffles the feet.</summary>
public sealed class Gait
{
    public float Phase, MoveW, RunW;
    public readonly float[] FootYaw = { 0.14f, -0.1f };   // [L, R]
    readonly bool[] _wasStance = { true, true };

    public struct Foot { public bool Strike, Stance; public float X, Z, Lift, Pitch, O, P, A, Bell; }
    public readonly Foot[] Feet = new Foot[2];             // [L, R]
    public float V;
    public Vector3 U;

    static readonly float[][] SwingPitch = { new[] { 0f, 0.3f }, new[] { 0.35f, -0.1f }, new[] { 0.72f, -0.24f }, new[] { 1f, -0.2f } };

    public void Step(float dt, Vector3 vl, float yawRate, float[][] idleFeet, float crouch, float width)
    {
        float v = MathF.Sqrt(vl.X * vl.X + vl.Z * vl.Z);
        float turn = RigMath.Smooth((MathF.Abs(yawRate) - 0.9f) / 2.2f) * 0.55f;
        float drive = MathF.Max(v, turn);
        MoveW = RigMath.Damp(MoveW, RigMath.Clamp(drive / 0.3f, 0, 1), 9, dt);
        RunW = RigMath.Damp(RunW, RigMath.Smooth((v - 1.6f) / 1.1f), 6, dt);
        float cadence = RigMath.Lerp(0.78f + 0.52f * RigMath.Clamp(drive / 1.4f, 0, 1), 1.48f, RunW) * (1 - 0.22f * crouch);
        float duty = RigMath.Lerp(0.6f, 0.37f, RunW) + 0.08f * crouch;
        // a stride longer than the legs allow would skate the planted foot (the concept's crouch walk did):
        // step faster instead, so the stance foot always moves back at exactly the body's speed
        float maxTravel = 0.78f - 0.2f * crouch;
        if (v / cadence * duty > maxTravel) cadence = v * duty / maxTravel;
        Phase += cadence * dt;
        float travel = v / cadence * duty;
        var u = v > 1e-3f ? new Vector3(vl.X / v, 0, vl.Z / v) : new Vector3(0, 0, 1);
        float fwd = u.Z;
        float lift = RigMath.Lerp(0.1f, 0.17f, RunW) * RigMath.Clamp(drive / 0.55f, 0.35f, 1) * (1 - 0.3f * crouch);
        float swingTan = -travel * (1 - duty) / duty;
        for (int k = 0; k < 2; k++)
        {
            bool left = k == 0;
            float p = RigMath.Frac(Phase + (left ? 0.5f : 0));
            float o, h = 0, pitch;
            bool stance = p < duty;
            if (stance)
            {
                float a = p / duty; o = travel / 2 - travel * a;
                pitch = -0.2f * (1 - RigMath.Smooth(a / 0.17f)) + 0.3f * RigMath.Smooth((a - 0.73f) / 0.27f);
            }
            else
            {
                float a = (p - duty) / (1 - duty);
                o = RigMath.Hermite(-travel / 2, swingTan, travel / 2, swingTan, a);
                h = lift * MathF.Pow(MathF.Sin(MathF.PI * a), 1.25f);
                pitch = RigMath.Curve1(SwingPitch, a);
            }
            // a planted foot keeps its world heading while the body turns; it re-aligns in the air
            float baseYaw = left ? 0.08f : -0.08f;
            if (stance && MoveW > 0.2f) FootYaw[k] = RigMath.Clamp(FootYaw[k] - yawRate * dt, baseYaw - 0.45f, baseYaw + 0.45f);
            else FootYaw[k] = RigMath.Damp(FootYaw[k], MoveW > 0.2f ? baseYaw : idleFeet[k][2], 10, dt);
            float bx = left ? width : -width;
            bool strike = stance && !_wasStance[k] && MoveW > 0.5f; _wasStance[k] = stance;
            Feet[k] = new Foot
            {
                Strike = strike, X = bx + u.X * o, Z = u.Z * o, Lift = h, Pitch = pitch * fwd * RigMath.Clamp(v / 0.6f, 0, 1),
                O = o, P = p, A = stance ? p / duty : 0, Stance = stance, Bell = stance ? MathF.Sin(MathF.PI * p / duty) : 0
            };
        }
        V = v; U = u;
    }
}
