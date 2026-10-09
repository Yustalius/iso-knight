namespace Squad.Sim;

/// <summary>Deterministic math. Math.Sin/Atan2/Exp/Log call the C runtime, whose last bits differ between
/// Windows and Linux, so a replay recorded by the trainer on Linux would drift on a Windows PC.
/// These use only + − × ÷ and sqrt (exactly rounded by IEEE 754), so every platform gets the same bits.
/// Accuracy is ~1e-15, far below anything the game can notice.</summary>
public static class DMath
{
    public const double Pi = Math.PI;
    public const double TwoPi = 2 * Math.PI;
    public const double HalfPi = Math.PI / 2;
    public const double Deg = Math.PI / 180;
    const double Ln2 = 0.69314718055994530942;
    const double InvLn2 = 1.44269504088896340736;
    const double PiOver4 = Math.PI / 4;

    public static double Sqrt(double x) => Math.Sqrt(x);
    public static double Abs(double x) => x < 0 ? -x : x;
    public static double Min(double a, double b) => a < b ? a : b;
    public static double Max(double a, double b) => a > b ? a : b;
    public static double Clamp(double x, double lo, double hi) => x < lo ? lo : x > hi ? hi : x;
    public static double Clamp01(double x) => x < 0 ? 0 : x > 1 ? 1 : x;
    public static double Lerp(double a, double b, double t) => a + (b - a) * t;
    public static double Smooth(double x) { x = Clamp01(x); return x * x * (3 - 2 * x); }
    public static int Sign(double x) => x > 0 ? 1 : x < 0 ? -1 : 0;

    /// <summary>Round half away from zero, without relying on the platform.</summary>
    public static double Round(double x) => x >= 0 ? Math.Floor(x + 0.5) : -Math.Floor(-x + 0.5);

    /// <summary>Angle wrapped into (−π, π].</summary>
    public static double WrapAngle(double a)
    {
        if (a > -Pi && a <= Pi) return a;
        a -= TwoPi * Math.Floor((a + Pi) / TwoPi);
        if (a <= -Pi) a += TwoPi;
        return a;
    }

    /// <summary>Exponential approach of a toward b with rate k: a + (b − a)(1 − e^(−k·dt)).</summary>
    public static double Damp(double a, double b, double k, double dt) => a + (b - a) * (1 - Exp(-k * dt));
    public static double DampAngle(double a, double b, double k, double dt) => a + WrapAngle(b - a) * (1 - Exp(-k * dt));

    public static double Sin(double x) { SinCos(x, out double s, out _); return s; }
    public static double Cos(double x) { SinCos(x, out _, out double c); return c; }

    public static void SinCos(double x, out double sin, out double cos)
    {
        // reduce to r in [−π/4, π/4] and a quadrant; the split of π/2 keeps the reduction exact for |x| < 1e5
        double q = Round(x / HalfPi);
        double r = (x - q * 1.5707963267341256) - q * 6.077100506506192e-11;
        double r2 = r * r;
        double s = r * (1 + r2 * (-1.0 / 6 + r2 * (1.0 / 120 + r2 * (-1.0 / 5040 + r2 * (1.0 / 362880 + r2 * (-1.0 / 39916800 + r2 * (1.0 / 6227020800)))))));
        double c = 1 + r2 * (-0.5 + r2 * (1.0 / 24 + r2 * (-1.0 / 720 + r2 * (1.0 / 40320 + r2 * (-1.0 / 3628800 + r2 * (1.0 / 479001600 + r2 * (-1.0 / 87178291200)))))));
        long qi = (long)q & 3;
        switch (qi)
        {
            case 0: sin = s; cos = c; break;
            case 1: sin = c; cos = -s; break;
            case 2: sin = -s; cos = -c; break;
            default: sin = -c; cos = s; break;
        }
    }

    public static double Atan(double x)
    {
        bool neg = x < 0; if (neg) x = -x;
        bool inv = x > 1; if (inv) x = 1 / x;
        // two half-angle steps: atan(x) = 2·atan(x / (1 + √(1 + x²))) bring x under tan(π/16) ≈ 0.199
        x = x / (1 + Math.Sqrt(1 + x * x));
        x = x / (1 + Math.Sqrt(1 + x * x));
        double x2 = x * x;
        double p = x * (1 + x2 * (-1.0 / 3 + x2 * (1.0 / 5 + x2 * (-1.0 / 7 + x2 * (1.0 / 9 + x2 * (-1.0 / 11 + x2 * (1.0 / 13 + x2 * (-1.0 / 15 + x2 * (1.0 / 17 + x2 * (-1.0 / 19))))))))));
        p *= 4;
        if (inv) p = HalfPi - p;
        return neg ? -p : p;
    }

    /// <summary>Same contract as Math.Atan2(y, x).</summary>
    public static double Atan2(double y, double x)
    {
        if (x > 0) return Atan(y / x);
        if (x < 0) return y >= 0 ? Atan(y / x) + Pi : Atan(y / x) - Pi;
        if (y > 0) return HalfPi;
        if (y < 0) return -HalfPi;
        return 0;
    }

    public static double Exp(double x)
    {
        if (x > 709) return double.PositiveInfinity;
        if (x < -745) return 0;
        double k = Round(x * InvLn2);
        double r = (x - k * 0.6931471803691238) - k * 1.9082149292705877e-10;
        double p = 1 + r * (1 + r * (1.0 / 2 + r * (1.0 / 6 + r * (1.0 / 24 + r * (1.0 / 120 + r * (1.0 / 720 + r * (1.0 / 5040 + r * (1.0 / 40320 + r * (1.0 / 362880 + r * (1.0 / 3628800 + r * (1.0 / 39916800 + r * (1.0 / 479001600))))))))))));
        return Math.ScaleB(p, (int)k);
    }

    public static double Log(double x)
    {
        if (x <= 0) return x == 0 ? double.NegativeInfinity : double.NaN;
        if (double.IsPositiveInfinity(x)) return x;
        int e = Math.ILogB(x);
        double m = Math.ScaleB(x, -e);          // [1, 2)
        if (m > 1.4142135623730951) { m *= 0.5; e++; }
        double s = (m - 1) / (m + 1), s2 = s * s;
        double p = 2 * s * (1 + s2 * (1.0 / 3 + s2 * (1.0 / 5 + s2 * (1.0 / 7 + s2 * (1.0 / 9 + s2 * (1.0 / 11 + s2 * (1.0 / 13 + s2 * (1.0 / 15 + s2 * (1.0 / 17 + s2 * (1.0 / 19))))))))));
        return e * Ln2 + p;
    }

    /// <summary>Standard normal CDF Φ(x) (Abramowitz–Stegun 7.1.26, error below 1.5e-7).</summary>
    public static double NormCdf(double x)
    {
        double z = Abs(x) / 1.4142135623730951;
        double t = 1 / (1 + 0.3275911 * z);
        double erf = 1 - t * (0.254829592 + t * (-0.284496736 + t * (1.421413741 + t * (-1.453152027 + t * 1.061405429)))) * Exp(-z * z);
        return x >= 0 ? 0.5 * (1 + erf) : 0.5 * (1 - erf);
    }

    /// <summary>Chance that a shot with Gaussian angular error σ, aimed e off-centre, lands within ±θ.</summary>
    public static double HitChance(double theta, double aimErr, double sigma)
    {
        if (sigma < 1e-9) return Abs(aimErr) <= theta ? 1 : 0;
        return NormCdf((theta - aimErr) / sigma) - NormCdf((-theta - aimErr) / sigma);
    }
}
