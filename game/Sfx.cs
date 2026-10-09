using Godot;

/// <summary>Procedural sound, ported from the concept's sfx.js: no samples, every sound is filtered noise bursts and
/// swept tones with WebAudio-like envelopes (linear attack, exponential decay), mixed here into an
/// AudioStreamGenerator. Each sound goes through a bus with a gain, an equal-power pan and a send to an outdoor
/// slap-back echo (a low-passed feedback delay); a soft limiter stands in for the concept's compressor.
/// Volume falls with the distance from the camera's centre, pan follows the position on screen.</summary>
sealed class Sfx
{
    const int Rate = 44100;
    readonly AudioStreamPlayer _player;
    AudioStreamGeneratorPlayback? _pb;
    readonly float[] _noise = new float[Rate];
    readonly List<Voice> _voices = new();
    readonly Random _r = new(1729);
    // echo: 0.19 s feedback delay through a 1.4 kHz low-pass (sfx.js)
    readonly float[] _dl = new float[(int)(0.19 * Rate)], _dr = new float[(int)(0.19 * Rate)];
    int _di; float _lpL, _lpR;
    Vector2[] _buf = Array.Empty<Vector2>();
    double _clock;            // seconds of audio produced
    double _lastTink;
    public bool Enabled = true;
    public float Peak; public int MaxVoices;   // for --bench

    /// <summary>The viewer's sound and how loud and where a world point is heard (null in --shots runs).</summary>
    public static Sfx? Current;
    public static Func<Vector3, (float pan, float vol)>? Locate;
    public static void At(Vector3 p, Action<Sfx, float, float> play, float v = 1)
    {
        if (Current == null || Locate == null) return;
        var (pan, vol) = Locate(p);
        if (vol * v > 0.02f) play(Current, pan, vol * v);
    }

    public Sfx(Node parent)
    {
        for (int i = 0; i < _noise.Length; i++) _noise[i] = (float)(_r.NextDouble() * 2 - 1);
        _player = new AudioStreamPlayer { Stream = new AudioStreamGenerator { MixRate = Rate, BufferLength = 0.12f }, VolumeDb = 0 };
        parent.AddChild(_player);
        _player.Play();
        _pb = _player.GetStreamPlayback() as AudioStreamGeneratorPlayback;
    }

    float J(float x, float j = 0.08f) => x * (1 + ((float)_r.NextDouble() - 0.5f) * j);

    // ───────── voices ─────────

    sealed class Bus { public float L, R, Wet; }
    abstract class Voice
    {
        public Bus B = null!;
        public int Start;          // sample index (of _clock) where it begins
        public float A, D, G;      // attack, decay to 0.0005, peak gain
        public float Env(float t) => t < 0 ? 0 : t < A ? G * t / A : G * MathF.Pow(0.0005f / G, (t - A) / D);
        public float End => A + D + 0.05f;
        public abstract float Sample(float t);
    }
    sealed class Tone : Voice
    {
        public float F, F2; double _ph;
        public override float Sample(float t)
        {
            float f = F * MathF.Pow(F2 / F, Math.Clamp(t / (A + D), 0, 1));
            _ph += 2 * Math.PI * f / Rate;
            return MathF.Sin((float)_ph) * Env(t);
        }
    }
    sealed class Burst : Voice
    {
        public int Type;          // 0 bandpass, 1 lowpass, 2 highpass
        public float F, F2, Q; public int Off; public float[] Noise = null!;
        float b0, b1, b2, a1, a2, x1, x2, y1, y2; int _n;
        void Coeffs(float f)
        {
            // RBJ biquads, as WebAudio's BiquadFilterNode
            float w = 2 * MathF.PI * Math.Clamp(f, 20, Rate * 0.45f) / Rate, cs = MathF.Cos(w), sn = MathF.Sin(w);
            float alpha = sn / (2 * MathF.Max(0.0001f, Q)), a0;
            switch (Type)
            {
                case 1: b0 = (1 - cs) / 2; b1 = 1 - cs; b2 = b0; break;
                case 2: b0 = (1 + cs) / 2; b1 = -(1 + cs); b2 = b0; break;
                default: b0 = alpha; b1 = 0; b2 = -alpha; break;
            }
            a0 = 1 + alpha; a1 = -2 * cs; a2 = 1 - alpha;
            b0 /= a0; b1 /= a0; b2 /= a0; a1 /= a0; a2 /= a0;
        }
        public override float Sample(float t)
        {
            if ((_n++ & 31) == 0) Coeffs(F * MathF.Pow(F2 / F, Math.Clamp(t / D, 0, 1)));
            float x = Noise[(Off + _n) % Noise.Length];
            float y = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
            x2 = x1; x1 = x; y2 = y1; y1 = y;
            return y * Env(t);
        }
    }

    bool Ready => Enabled && _pb != null;
    Bus MakeBus(float pan, float gain, float wet = 0)
    {
        // equal-power pan like StereoPannerNode
        float a = (Math.Clamp(pan, -1, 1) + 1) * MathF.PI / 4;
        return new Bus { L = MathF.Cos(a) * gain, R = MathF.Sin(a) * gain, Wet = wet };
    }
    int Now => (int)(_clock * Rate);
    void Noise(Bus b, float delay, int type, float f, float f2 = -1, float q = 0.8f, float a = 0.002f, float d = 0.1f, float g = 1)
        => _voices.Add(new Burst { B = b, Start = Now + (int)(delay * Rate), Type = type, F = f, F2 = f2 < 0 ? f : MathF.Max(40, f2), Q = q, A = a, D = d, G = g, Noise = _noise, Off = _r.Next(Rate / 2) });
    void Sine(Bus b, float delay, float f, float f2 = -1, float a = 0.001f, float d = 0.1f, float g = 0.5f)
        => _voices.Add(new Tone { B = b, Start = Now + (int)(delay * Rate), F = f, F2 = f2 < 0 ? f : MathF.Max(20, f2), A = a, D = d, G = g });
    const int BP = 0, LP = 1, HP = 2;

    // ───────── the concept's sounds ─────────

    public void Shot(float pan, float v)
    {
        if (!Ready) return; var b = MakeBus(pan, 0.9f * v, 0.55f);
        Noise(b, 0, HP, 2600, q: 0.5f, a: 0.0008f, d: 0.03f, g: 1.1f);                  // supersonic crack
        Noise(b, 0, LP, J(2600), 320, 0.7f, 0.002f, 0.32f, 0.95f);                      // muzzle blast
        Sine(b, 0, J(150), 42, 0.002f, 0.2f, 0.9f);                                     // chest thump
        Noise(b, 0.006f, BP, J(5200), q: 3, a: 0.0005f, d: 0.025f, g: 0.25f);           // action cycling
    }
    public void Dry(float pan, float v) { if (!Ready) return; var b = MakeBus(pan, 0.5f * v); Noise(b, 0, BP, 3200, q: 4, a: 0.0005f, d: 0.02f, g: 0.8f); Sine(b, 0, 1900, 1400, d: 0.025f, g: 0.15f); }
    public void Click(float pan, float v) { if (!Ready) return; var b = MakeBus(pan, 0.45f * v); Noise(b, 0, BP, 2400, q: 5, a: 0.0005f, d: 0.018f, g: 0.7f); }
    public void MagOut(float pan, float v)
    {
        if (!Ready) return; var b = MakeBus(pan, 0.5f * v);
        Noise(b, 0, BP, 2800, q: 4, a: 0.0005f, d: 0.02f, g: 0.7f); Noise(b, 0.02f, BP, 900, 600, 1.5f, 0.005f, 0.08f, 0.35f);
    }
    public void MagIn(float pan, float v)
    {
        if (!Ready) return; var b = MakeBus(pan, 0.6f * v);
        Noise(b, 0, BP, 1500, q: 2, a: 0.001f, d: 0.04f, g: 0.9f); Sine(b, 0, 260, 140, d: 0.06f, g: 0.35f);
        Noise(b, 0.055f, BP, 3000, q: 4, a: 0.0005f, d: 0.02f, g: 0.5f);
    }
    public void Bolt(float pan, float v)
    {
        if (!Ready) return; var b = MakeBus(pan, 0.7f * v);
        Noise(b, 0, BP, 2200, q: 2, a: 0.0008f, d: 0.035f, g: 1); Sine(b, 0, 3100, 2600, d: 0.05f, g: 0.12f);
        Noise(b, 0.03f, BP, 1200, q: 2, a: 0.001f, d: 0.05f, g: 0.5f);
    }
    public void Tink(float pan, float v)
    {
        if (!Ready || _clock - _lastTink < 0.025) return; _lastTink = _clock;
        var b = MakeBus(pan, 0.12f * v); float f = 4200 + (float)_r.NextDouble() * 2200;
        Sine(b, 0, f, f * 0.97f, d: 0.07f, g: 0.5f); Sine(b, 0, f * 1.52f, f * 1.5f, d: 0.04f, g: 0.25f);
    }
    public void Mag(float pan, float v) { if (!Ready) return; var b = MakeBus(pan, 0.35f * v); Noise(b, 0, BP, 900, q: 1.2f, a: 0.002f, d: 0.07f, g: 1); Sine(b, 0, 1700, 1500, d: 0.05f, g: 0.15f); }
    public void Step(float pan, float v) { if (!Ready) return; var b = MakeBus(pan, 0.06f * v); Noise(b, 0, LP, 600, 250, a: 0.004f, d: 0.07f, g: 1); }
    public void Impact(string kind, float pan, float v)
    {
        if (!Ready) return; var b = MakeBus(pan, 0.35f * v);
        switch (kind)
        {
            case "wood": Noise(b, 0, BP, 900, q: 1.4f, a: 0.001f, d: 0.06f, g: 1); Sine(b, 0, 320, 180, d: 0.05f, g: 0.3f); break;
            case "steel": Sine(b, 0, J(1900, 0.3f), 1700, d: 0.18f, g: 0.25f); Noise(b, 0, HP, 2500, a: 0.0005f, d: 0.02f, g: 0.6f); break;
            case "concrete": Noise(b, 0, BP, 2000, q: 1, a: 0.0005f, d: 0.05f, g: 0.9f); break;
            case "flesh": Noise(b, 0, LP, 1300, 260, a: 0.001f, d: 0.07f, g: 1.3f); Sine(b, 0, 170, 70, d: 0.07f, g: 0.55f); break;
            case "body": Noise(b, 0, LP, 600, 120, a: 0.004f, d: 0.16f, g: 1.4f); Sine(b, 0, 95, 45, d: 0.14f, g: 0.7f); break;
            case "rifle": Noise(b, 0, BP, 1500, q: 1.5f, a: 0.001f, d: 0.06f, g: 1); Sine(b, 0, 900, 700, d: 0.07f, g: 0.15f); break;
            case "leaf": Noise(b, 0, HP, 3000, 1800, a: 0.003f, d: 0.08f, g: 0.4f); break;
            default: Noise(b, 0, LP, 700, 200, a: 0.002f, d: 0.09f, g: 1); break;
        }
    }

    // ───────── mixing ─────────

    /// <summary>Fill the generator with what it can take now.</summary>
    public void Mix()
    {
        if (_pb == null) return;
        int n = _pb.GetFramesAvailable();
        if (n <= 0) return;
        if (_buf.Length < n) _buf = new Vector2[n];
        int t0 = Now;
        for (int i = 0; i < n; i++)
        {
            float l = 0, r = 0, wl = 0, wr = 0;
            int s = t0 + i;
            for (int k = 0; k < _voices.Count; k++)
            {
                var v = _voices[k];
                if (s < v.Start) continue;
                float x = v.Sample((s - v.Start) / (float)Rate);
                l += x * v.B.L; r += x * v.B.R; wl += x * v.B.L * v.B.Wet; wr += x * v.B.R * v.B.Wet;
            }
            // outdoor slap-back
            float el = _dl[_di], er = _dr[_di];
            _lpL += (el - _lpL) * 0.18f; _lpR += (er - _lpR) * 0.18f;   // ≈1.4 kHz one-pole
            _dl[_di] = wl + _lpL * 0.3f; _dr[_di] = wr + _lpR * 0.3f;
            _di = (_di + 1) % _dl.Length;
            l = (l + _lpL) * 0.55f; r = (r + _lpR) * 0.55f;
            // soft limiter in place of the dynamics compressor
            _buf[i] = new Vector2(l / (1 + MathF.Abs(l) * 0.6f), r / (1 + MathF.Abs(r) * 0.6f));
            Peak = MathF.Max(Peak, MathF.Max(MathF.Abs(_buf[i].X), MathF.Abs(_buf[i].Y)));
        }
        MaxVoices = Math.Max(MaxVoices, _voices.Count);
        _pb.PushBuffer(n == _buf.Length ? _buf : _buf[..n]);
        _clock += n / (double)Rate;
        int now = Now;
        _voices.RemoveAll(v => now - v.Start > v.End * Rate);
    }
}
