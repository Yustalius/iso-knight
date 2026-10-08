using SkiaSharp;
using Squad.Bots;
using Squad.Sim;

namespace Squad.Tools;

/// <summary>Top-down debug frames: obstacles by type, soldiers with facing and stance, vision cones, what one side
/// knows (contacts with their uncertainty), paths, tracers, bot intents. This is a debugging view, not the game.</summary>
sealed class Renderer
{
    public double Scale;
    public int FocusTeam = -1;          // whose knowledge to draw (−1: nobody's)
    public bool Cones = true, Paths = true, CoverPoints;
    readonly World _w;
    readonly int _wPx, _hPx;
    const int Header = 34;
    static SKTypeface? _face;

    static readonly SKColor Ground = new(0x34, 0x3b, 0x2e), Grid = new(0x3d, 0x45, 0x36);
    static readonly SKColor[] TeamCol = { new(0x4f, 0xa8, 0xff), new(0xff, 0x6a, 0x55) };
    static readonly SKColor[] TeamDark = { new(0x1d, 0x4f, 0x85), new(0x85, 0x2a, 0x1f) };

    public Renderer(World w, double scale)
    {
        _w = w; Scale = scale;
        _wPx = (int)Math.Ceiling(w.Width * scale);
        _hPx = (int)Math.Ceiling(w.Height * scale) + Header;
    }

    static SKTypeface Face()
    {
        if (_face != null) return _face;
        foreach (var path in new[] { "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf", "/usr/share/fonts/truetype/freefont/FreeSans.ttf", @"C:\Windows\Fonts\segoeui.ttf", @"C:\Windows\Fonts\arial.ttf", "/System/Library/Fonts/Helvetica.ttc" })
            if (File.Exists(path)) { _face = SKTypeface.FromFile(path); if (_face != null) return _face; }
        return _face = SKTypeface.Default;
    }

    float X(double x) => (float)(x * Scale);
    float Y(double y) => (float)((_w.Height - y) * Scale + Header);
    SKPoint P(Vec2 p) => new(X(p.X), Y(p.Y));
    float L(double m) => (float)(m * Scale);

    public SKBitmap Frame(Match m, BotRunner? bots, IReadOnlyList<GameEvent> recent, string title)
    {
        var bmp = new SKBitmap(_wPx, _hPx);
        using var c = new SKCanvas(bmp);
        c.Clear(new SKColor(0x1b, 0x1f, 0x19));
        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        using var line = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round };
        using var font = new SKFont(Face(), 11);
        using var small = new SKFont(Face(), 9);

        // ground, grid, zones
        fill.Color = Ground;
        c.DrawRect(0, Header, _wPx, _hPx - Header, fill);
        line.Color = Grid; line.StrokeWidth = 1;
        for (int gx = 5; gx < _w.Width; gx += 5) c.DrawLine(X(gx), Header, X(gx), _hPx, line);
        for (int gy = 5; gy < _w.Height; gy += 5) c.DrawLine(0, Y(gy), _wPx, Y(gy), line);
        for (int t = 0; t < _w.Map.Spawns.Count && t < 2; t++)
        {
            var z = _w.Map.Spawns[t];
            fill.Color = TeamCol[t].WithAlpha(28);
            c.DrawRect(SKRect.Create(X(z.Min.X), Y(z.Max.Y), L(z.Max.X - z.Min.X), L(z.Max.Y - z.Min.Y)), fill);
        }
        foreach (var z in _w.Map.Zones)
        {
            fill.Color = new SKColor(0xf0, 0xd0, 0x40, 50);
            c.DrawCircle(P(z.Center), L(z.Radius), fill);
        }

        // obstacles: a capsule is a stroke with round caps
        foreach (var o in _w.Obstacles)
        {
            line.StrokeWidth = Math.Max(1.5f, L(o.R * 2));
            line.Color = o.Kind switch
            {
                ObstacleKind.HighWall => new SKColor(0xd8, 0xd2, 0xc0),
                ObstacleKind.LowWall => new SKColor(0xb0, 0x93, 0x58),
                ObstacleKind.Crate => new SKColor(0x8f, 0x5e, 0x2e),
                ObstacleKind.Bush => new SKColor(0x4f, 0x9a, 0x3c, 150),
                _ => new SKColor(0x2a, 0x5e, 0x25)
            };
            c.DrawLine(P(o.A), P(o.B), line);
        }
        if (CoverPoints)
        {
            fill.Color = new SKColor(255, 255, 255, 70);
            foreach (var cp in _w.Cover) c.DrawCircle(P(cp.Pos), 1.5f, fill);
        }

        // what the focus team knows
        if (FocusTeam >= 0)
            for (int i = m.TeamStart(FocusTeam); i < m.TeamEnd(FocusTeam); i++)
            {
                if (!m.IsAlive(i)) continue;
                var v = m.View(i);
                foreach (var ct in v.Contacts)
                {
                    var col = ct.Visible ? new SKColor(0xff, 0xe0, 0x60, 200) : ct.Source == ContactSource.Hear ? new SKColor(0xc0, 0x90, 0xff, 150) : new SKColor(0xff, 0xff, 0xff, 110);
                    if (ct.Cleared) col = col.WithAlpha(50);
                    line.Color = col; line.StrokeWidth = 1;
                    line.PathEffect = ct.Visible ? null : SKPathEffect.CreateDash(new[] { 3f, 3f }, 0);
                    c.DrawCircle(P(ct.Pos), Math.Max(3, L(ct.Uncertainty)), line);
                    if (ct.Visible) { line.Color = col.WithAlpha(60); c.DrawLine(P(v.Self.Pos), P(ct.Pos), line); }
                    line.PathEffect = null;
                }
            }

        // soldiers
        var pb = m.B.Perception;
        for (int i = 0; i < m.Count; i++)
        {
            var s = m.Truth(i);
            var pos = P(s.Pos);
            if (!s.Alive)
            {
                line.Color = new SKColor(0x90, 0x90, 0x90); line.StrokeWidth = 2;
                float r0 = L(0.25);
                c.DrawLine(pos.X - r0, pos.Y - r0, pos.X + r0, pos.Y + r0, line);
                c.DrawLine(pos.X - r0, pos.Y + r0, pos.X + r0, pos.Y - r0, line);
                continue;
            }
            if (Cones && (FocusTeam < 0 || FocusTeam == s.Team))
            {
                // a wedge; Skia angles are clockwise from +X on screen, yaw is clockwise from +Y (up)
                float r = L(Math.Min(pb.ViewRange, 12));
                float start = (float)((s.AimYaw - pb.ConeHalf) / DMath.Deg) - 90, sweep = (float)(2 * pb.ConeHalf / DMath.Deg);
                fill.Color = TeamCol[s.Team].WithAlpha(22);
                c.DrawArc(new SKRect(pos.X - r, pos.Y - r, pos.X + r, pos.Y + r), start, sweep, true, fill);
            }
            if (Paths && (FocusTeam < 0 || FocusTeam == s.Team))
            {
                var path = m.Path(i);
                if (path.Length > 0)
                {
                    line.Color = TeamCol[s.Team].WithAlpha(110); line.StrokeWidth = 1;
                    line.PathEffect = SKPathEffect.CreateDash(new[] { 4f, 3f }, 0);
                    var prev = pos;
                    foreach (var wp in path) { var q = P(wp); c.DrawLine(prev, q, line); prev = q; }
                    line.PathEffect = null;
                }
            }
            float rad = L(s.Crouched ? 0.24 : 0.3);
            fill.Color = s.Crouched ? TeamDark[s.Team] : TeamCol[s.Team];
            c.DrawCircle(pos, rad, fill);
            line.Color = SKColors.Black.WithAlpha(160); line.StrokeWidth = 1;
            c.DrawCircle(pos, rad, line);
            // barrel
            line.Color = s.Aiming ? SKColors.White : new SKColor(0xdd, 0xdd, 0xdd, 150);
            line.StrokeWidth = s.Aiming ? 2 : 1.2f;
            c.DrawLine(pos, P(s.Pos + Vec2.FromYaw(s.AimYaw) * (s.Aiming ? 0.9 : 0.5)), line);
            // health
            float bw = L(0.7);
            fill.Color = new SKColor(0, 0, 0, 160); c.DrawRect(pos.X - bw / 2, pos.Y - rad - 6, bw, 3, fill);
            fill.Color = s.Hp > 50 ? new SKColor(0x7c, 0xd6, 0x5a) : s.Hp > 25 ? new SKColor(0xe8, 0xc5, 0x47) : new SKColor(0xe0, 0x4a, 0x3a);
            c.DrawRect(pos.X - bw / 2, pos.Y - rad - 6, (float)(bw * s.Hp / m.B.Damage.Hp), 3, fill);
            // label
            string intent = bots?.Brains[i]?.Intent ?? "";
            string name = bots?.Brains[i]?.Name ?? "";
            fill.Color = new SKColor(255, 255, 255, 210);
            c.DrawText($"{i} {Short(name)} {intent}{(s.Reloading ? " ⟳" : "")}", pos.X + rad + 2, pos.Y + 3, SKTextAlign.Left, small, fill);
        }

        // tracers and hits from recent events
        foreach (var e in recent)
        {
            float age = (float)Math.Clamp((m.Tick - e.Tick) / 10.0, 0, 1);
            if (e.Type == EventType.Shot)
            {
                line.Color = new SKColor(0xff, 0xf0, 0xa0, (byte)(220 * (1 - age))); line.StrokeWidth = 1;
                c.DrawLine(P(e.Pos), P(e.Pos2), line);
            }
            else if (e.Type == EventType.Hit)
            {
                fill.Color = new SKColor(0xff, 0x30, 0x30, (byte)(255 * (1 - age)));
                c.DrawCircle(P(e.Pos), 3, fill);
            }
        }

        // header
        fill.Color = new SKColor(0x12, 0x14, 0x10);
        c.DrawRect(0, 0, _wPx, Header, fill);
        fill.Color = SKColors.White;
        string res = m.Over ? $"  ·  END: {(m.Result.Winner < 0 ? "draw" : m.Result.Winner == 0 ? "blue wins" : "red wins")} ({m.Result.Reason})" : "";
        c.DrawText(title, 6, 14, SKTextAlign.Left, font, fill);
        c.DrawText($"t={m.Time:0.0}s  blue {m.AliveCount(0)}/{m.TeamSize(0)}  red {m.AliveCount(1)}/{m.TeamSize(1)}{res}", 6, 28, SKTextAlign.Left, font, fill);
        return bmp;
    }

    static string Short(string brain) => brain.Length > 4 ? brain[..4] : brain;

    public static void SavePng(SKBitmap bmp, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 90);
        using var fs = File.Create(path);
        data.SaveTo(fs);
    }

    public static SKBitmap Sheet(IReadOnlyList<SKBitmap> frames, int cols)
    {
        int w = frames[0].Width, h = frames[0].Height, rows = (frames.Count + cols - 1) / cols;
        var sheet = new SKBitmap(w * cols + (cols - 1) * 4, h * rows + (rows - 1) * 4);
        using var c = new SKCanvas(sheet);
        c.Clear(new SKColor(0x0c, 0x0d, 0x0b));
        for (int k = 0; k < frames.Count; k++)
        {
            using var img = SKImage.FromBitmap(frames[k]);
            c.DrawImage(img, (k % cols) * (w + 4), (k / cols) * (h + 4), new SKSamplingOptions(SKFilterMode.Nearest), null);
        }
        return sheet;
    }
}
