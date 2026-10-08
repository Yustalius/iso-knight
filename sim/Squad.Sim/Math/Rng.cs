namespace Squad.Sim;

/// <summary>PCG32 (O'Neill): 64-bit state, 32-bit output, independent streams. A value type so the match can keep
/// one per purpose in its state (the world, each soldier's perception noise) and the replay stays exact.</summary>
public struct Rng
{
    ulong _state, _inc;

    public Rng(ulong seed, ulong stream = 0)
    {
        _state = 0; _inc = (stream << 1) | 1;
        NextU32(); _state += seed; NextU32();
    }

    public ulong State => _state;
    public ulong Inc => _inc;

    public uint NextU32()
    {
        ulong old = _state;
        _state = unchecked(old * 6364136223846793005UL + _inc);
        uint xs = (uint)(((old >> 18) ^ old) >> 27);
        int rot = (int)(old >> 59);
        return (xs >> rot) | (xs << ((-rot) & 31));
    }

    /// <summary>Uniform in [0, 1).</summary>
    public double NextDouble() => NextU32() * (1.0 / 4294967296.0);
    public double Range(double lo, double hi) => lo + (hi - lo) * NextDouble();
    /// <summary>Uniform integer in [0, n).</summary>
    public int Int(int n) => n <= 0 ? 0 : (int)(((ulong)NextU32() * (ulong)n) >> 32);
    public int Range(int lo, int hiExclusive) => lo + Int(hiExclusive - lo);
    public bool Chance(double p) => NextDouble() < p;
    /// <summary>±1 with equal odds.</summary>
    public double Sign() => (NextU32() & 1) == 0 ? -1 : 1;

    /// <summary>Standard normal (Box–Muller, one value per call so the stream position is easy to reason about).</summary>
    public double Gauss()
    {
        double u1 = 1.0 - NextDouble(), u2 = NextDouble();
        DMath.SinCos(DMath.TwoPi * u2, out _, out double c);
        return DMath.Sqrt(-2 * DMath.Log(u1)) * c;
    }

    /// <summary>A point uniformly distributed in the unit disk.</summary>
    public Vec2 InDisk()
    {
        double r = DMath.Sqrt(NextDouble()), a = DMath.TwoPi * NextDouble();
        DMath.SinCos(a, out double s, out double c);
        return new Vec2(r * c, r * s);
    }

    /// <summary>Derive an independent generator for a sub-purpose (a map, a bot) from this one.</summary>
    public Rng Fork(ulong stream) => new(((ulong)NextU32() << 32) | NextU32(), stream);
}

/// <summary>FNV-1a over raw bits: the match state hash stored in replays.</summary>
public struct Fnv
{
    ulong _h;
    public Fnv() { _h = 14695981039346656037UL; }
    public ulong Value => _h;

    public void Add(ulong v)
    {
        for (int i = 0; i < 8; i++) { _h ^= (byte)(v >> (i * 8)); _h = unchecked(_h * 1099511628211UL); }
    }
    public void Add(double v) => Add((ulong)BitConverter.DoubleToInt64Bits(v));
    public void Add(int v) => Add((ulong)(uint)v);
    public void Add(bool v) => Add(v ? 1UL : 0UL);
    public void Add(Vec2 v) { Add(v.X); Add(v.Y); }
}
