using Squad.Sim;
using Xunit;

namespace Squad.Tests;

public class MathTests
{
    [Fact]
    public void TrigMatchesTheRuntime()
    {
        var r = new Rng(1);
        for (int k = 0; k < 20000; k++)
        {
            double x = r.Range(-200, 200);
            DMath.SinCos(x, out double s, out double c);
            Assert.True(Math.Abs(s - Math.Sin(x)) < 1e-13, $"sin {x}");
            Assert.True(Math.Abs(c - Math.Cos(x)) < 1e-13, $"cos {x}");
            double y = r.Range(-5, 5), z = r.Range(-5, 5);
            Assert.True(Math.Abs(DMath.Atan2(y, z) - Math.Atan2(y, z)) < 1e-13, $"atan2 {y} {z}");
        }
        Assert.Equal(0, DMath.Atan2(0, 0));
        Assert.Equal(Math.PI / 2, DMath.Atan2(1, 0), 15);
        Assert.Equal(Math.PI, DMath.Atan2(0, -1), 15);
    }

    [Fact]
    public void ExpLogMatchTheRuntime()
    {
        var r = new Rng(2);
        for (int k = 0; k < 20000; k++)
        {
            double x = r.Range(-60, 60);
            double e = DMath.Exp(x), m = Math.Exp(x);
            Assert.True(Math.Abs(e - m) <= 4e-15 * m, $"exp {x}");
            double y = DMath.Exp(r.Range(-30, 30));
            Assert.True(Math.Abs(DMath.Log(y) - Math.Log(y)) < 1e-13, $"log {y}");
        }
    }

    /// <summary>The exact bits of the deterministic math on fixed inputs. If this fails on another OS or CPU,
    /// replays recorded elsewhere will not verify there.</summary>
    [Fact]
    public void BitsAreStable()
    {
        var h = new Fnv();
        for (int k = -500; k <= 500; k++)
        {
            double x = k * 0.0371;
            DMath.SinCos(x, out double s, out double c);
            h.Add(s); h.Add(c); h.Add(DMath.Atan2(s + 0.3, c - 0.1)); h.Add(DMath.Exp(x * 0.1)); h.Add(DMath.Log(Math.Abs(x) + 0.01));
        }
        var r = new Rng(42, 3);
        for (int k = 0; k < 1000; k++) h.Add(r.Gauss());
        Assert.Equal(Golden.MathBits, h.Value);
    }

    [Fact]
    public void RngIsReproducibleAndNormal()
    {
        var a = new Rng(5, 9); var b = new Rng(5, 9); var c = new Rng(5, 10);
        bool differ = false;
        for (int k = 0; k < 100; k++) { uint x = a.NextU32(); Assert.Equal(x, b.NextU32()); differ |= x != c.NextU32(); }
        Assert.True(differ);
        double sum = 0, sq = 0; int n = 200000;
        var g = new Rng(11);
        for (int k = 0; k < n; k++) { double v = g.Gauss(); sum += v; sq += v * v; }
        Assert.InRange(sum / n, -0.01, 0.01);
        Assert.InRange(sq / n, 0.98, 1.02);
    }

    [Fact]
    public void HitChanceFormula()
    {
        Assert.Equal(0.6827, DMath.HitChance(1, 0, 1), 3);
        Assert.True(DMath.HitChance(1, 2, 1) < DMath.HitChance(1, 0, 1));
        Assert.Equal(1, DMath.HitChance(1, 0, 0));
    }
}
