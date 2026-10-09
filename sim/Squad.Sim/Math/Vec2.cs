namespace Squad.Sim;

/// <summary>A point or direction on the ground plane. X is world X, Y is world Z (Godot: Vector3(X, height, Y)).
/// Yaw 0 faces +Y; <see cref="FromYaw"/> and <see cref="Yaw"/> follow the prototypes' atan2(dx, dz).</summary>
public readonly struct Vec2 : IEquatable<Vec2>
{
    public readonly double X, Y;
    public Vec2(double x, double y) { X = x; Y = y; }

    public static readonly Vec2 Zero = new(0, 0);

    public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Vec2 operator -(Vec2 a) => new(-a.X, -a.Y);
    public static Vec2 operator *(Vec2 a, double k) => new(a.X * k, a.Y * k);
    public static Vec2 operator *(double k, Vec2 a) => new(a.X * k, a.Y * k);
    public static Vec2 operator /(Vec2 a, double k) => new(a.X / k, a.Y / k);
    public static bool operator ==(Vec2 a, Vec2 b) => a.X == b.X && a.Y == b.Y;
    public static bool operator !=(Vec2 a, Vec2 b) => !(a == b);

    public double LengthSq => X * X + Y * Y;
    public double Length => Math.Sqrt(X * X + Y * Y);
    public double Dot(Vec2 b) => X * b.X + Y * b.Y;
    public double Cross(Vec2 b) => X * b.Y - Y * b.X;
    /// <summary>Rotated 90° counter-clockwise when seen from above (+Y up on screen, +X right).</summary>
    public Vec2 Perp => new(-Y, X);
    public double Yaw => DMath.Atan2(X, Y);

    public Vec2 Normalized()
    {
        double l = Length;
        return l > 1e-12 ? new Vec2(X / l, Y / l) : Zero;
    }

    /// <summary>Same direction, length at most <paramref name="max"/>.</summary>
    public Vec2 ClampLength(double max)
    {
        double l2 = LengthSq;
        if (l2 <= max * max) return this;
        double k = max / Math.Sqrt(l2);
        return new Vec2(X * k, Y * k);
    }

    public static Vec2 FromYaw(double yaw)
    {
        DMath.SinCos(yaw, out double s, out double c);
        return new Vec2(s, c);
    }

    public static double Distance(Vec2 a, Vec2 b) => (a - b).Length;
    public static double DistanceSq(Vec2 a, Vec2 b) => (a - b).LengthSq;
    public static Vec2 Lerp(Vec2 a, Vec2 b, double t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

    public bool Equals(Vec2 o) => this == o;
    public override bool Equals(object? o) => o is Vec2 v && this == v;
    public override int GetHashCode() => HashCode.Combine(X, Y);
    public override string ToString() => FormattableString.Invariant($"({X:0.###}, {Y:0.###})");
}
