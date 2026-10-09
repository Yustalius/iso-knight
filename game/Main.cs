using Godot;
using Squad.Bots;
using Squad.Sim;

/// <summary>3D view of a bot match. The match runs in Squad.Sim at 30 Hz exactly as in the arena;
/// this node only draws Truth and Events, interpolating soldiers between ticks.
/// Sim (x, y) is Godot (x, height, y); yaw turns +Z toward +X, the same as Node3D.Rotation.Y.</summary>
public partial class Main : Node3D
{
    const double Dt = 1.0 / 30;
    const float TracerLife = 0.12f, HitLife = 0.35f;

    static readonly Color[] TeamCol = { new("4fa8ff"), new("ff6a55") };
    static readonly Color[] TeamDark = { new("1d4f85"), new("85301f") };

    // what to play
    string _simDir = "";
    string[] _scenarios = Array.Empty<string>();
    int _scenarioIdx;
    ulong _seed = 1;
    string _error = "";

    // the match
    BotRunner? _runner;
    Match? _m;
    string _title = "";
    SoldierTruth[] _prev = Array.Empty<SoldierTruth>(), _cur = Array.Empty<SoldierTruth>();
    double _acc, _speed = 1, _overFor;
    bool _paused, _labels = true, _cones = true, _auto = true, _cut;
    readonly List<(MeshInstance3D mesh, float h)> _tallWalls = new();  // cut down to waist height with H
    readonly List<(Vector3 a, Vector3 b, double t)> _tracers = new();
    readonly List<(Vector3 p, double t)> _hits = new();

    // scene
    Node3D? _worldRoot;
    SoldierView[] _soldiers = Array.Empty<SoldierView>();
    MeshInstance3D _fx = null!, _coneMesh = null!;
    ImmediateMesh _fxMesh = null!, _coneIm = null!;
    StandardMaterial3D _fxMat = null!, _coneMat = null!;
    Camera3D _cam = null!;
    Vector3 _target;
    float _camYaw = Mathf.Pi / 4, _camPitch = Mathf.DegToRad(30), _zoom = 30;
    float? _zoomArg;
    int _follow = -1;                  // soldier the camera tracks, -1: free camera
    Control _overlay = null!;
    Label _hud = null!, _banner = null!;

    // --shots 5,20 --shot-dir out: save frames at these sim times and quit (for checks without a person)
    readonly List<double> _shots = new();
    string _shotDir = "";
    int _shotPending = -1;

    public override void _Ready()
    {
        _simDir = Path.GetFullPath(Path.Combine(ProjectSettings.GlobalizePath("res://"), "..", "sim"));
        string? scenarioArg = null;
        var args = OS.GetCmdlineUserArgs();
        for (int i = 0; i < args.Length; i++)
        {
            string next = i + 1 < args.Length ? args[i + 1] : "";
            switch (args[i])
            {
                case "--sim": _simDir = Path.GetFullPath(next); i++; break;
                case "--scenario": scenarioArg = next; i++; break;
                case "--seed": _seed = ulong.Parse(next); i++; break;
                case "--speed": _speed = double.Parse(next, System.Globalization.CultureInfo.InvariantCulture); i++; break;
                case "--shot-dir": _shotDir = next; i++; break;
                case "--zoom": _zoomArg = float.Parse(next, System.Globalization.CultureInfo.InvariantCulture); i++; break;
                case "--follow": _follow = int.Parse(next); i++; break;
                case "--cut": _cut = true; break;
                case "--shots":
                    foreach (var s in next.Split(',')) _shots.Add(double.Parse(s, System.Globalization.CultureInfo.InvariantCulture));
                    i++; break;
            }
        }

        string scenDir = Path.Combine(_simDir, "scenarios");
        _scenarios = Directory.Exists(scenDir) ? Directory.GetFiles(scenDir, "*.json").OrderBy(p => p).ToArray() : Array.Empty<string>();
        string want = scenarioArg ?? "team-4v4-mixed";
        _scenarioIdx = Math.Max(0, Array.FindIndex(_scenarios, p => Path.GetFileNameWithoutExtension(p) == want || Path.GetFullPath(p) == Path.GetFullPath(want)));

        BuildStatic();
        StartMatch();
    }

    // ───────────────────────── match lifecycle ─────────────────────────

    void StartMatch()
    {
        _error = "";
        _tracers.Clear(); _hits.Clear();
        _acc = 0; _overFor = 0;
        if (_scenarios.Length == 0) { _error = "no scenarios in " + _simDir; return; }
        try
        {
            var sc = Scenario.Load(_scenarios[_scenarioIdx]);
            _runner = sc.Build(_seed).CreateRunner();
            _m = _runner.Match;
            _title = $"{sc.Name}  seed {_seed}";
        }
        catch (Exception e)
        {
            _runner = null; _m = null;
            _error = e.Message;
            GD.PushError(e.ToString());
            return;
        }
        _prev = new SoldierTruth[_m.Count];
        _cur = new SoldierTruth[_m.Count];
        for (int i = 0; i < _m.Count; i++) _prev[i] = _cur[i] = _m.Truth(i);
        BuildWorld(_m.World);
        ApplyCut();
        BuildSoldiers(_m);
        _target = new Vector3((float)_m.World.Width / 2, 0, (float)_m.World.Height / 2);
        _zoom = _zoomArg ?? (float)Math.Max(_m.World.Width, _m.World.Height) * 0.7f;
        if (_follow >= _m.Count) _follow = -1;
    }

    void StepSim()
    {
        var m = _m!;
        (_prev, _cur) = (_cur, _prev);
        _runner!.Step();
        for (int i = 0; i < m.Count; i++) _cur[i] = m.Truth(i);
        foreach (var e in m.Events)
        {
            if (e.Type == EventType.Shot)
                _tracers.Add((W(e.Pos, e.H), W(e.Pos2, e.H2), m.Time));
            else if (e.Type == EventType.Hit)
                _hits.Add((W(e.Pos, e.H > 0 ? e.H : 1.2), m.Time));
        }
        _tracers.RemoveAll(t => m.Time - t.t > TracerLife);
        _hits.RemoveAll(h => m.Time - h.t > HitLife);
    }

    public override void _Process(double delta)
    {
        if (_shotPending >= 0) { SaveShot(); return; }
        HandleCameraKeys((float)delta);

        if (_m != null)
        {
            if (!_paused && !_m.Over)
            {
                _acc += delta * _speed;
                int steps = 0;
                while (_acc >= Dt && !_m.Over && steps++ < 400)
                {
                    StepSim();
                    _acc -= Dt;
                    if (_shots.Count > 0 && _m.Time >= _shots[0]) { _shots.RemoveAt(0); _shotPending = 2; _acc = 0; break; }
                }
            }
            if (_m.Over)
            {
                _overFor += delta;
                if (_shots.Count > 0) { _shots.Clear(); _shotPending = 2; }
                else if (_auto && !_paused && _overFor > 5) { _seed++; StartMatch(); }
            }
        }

        float alpha = _m == null || _m.Over || _paused ? 1 : (float)Math.Clamp(_acc / Dt, 0, 1);
        UpdateCamera(alpha);
        if (_m != null)
        {
            for (int i = 0; i < _soldiers.Length; i++) _soldiers[i].Update(_prev[i], _cur[i], alpha, (float)delta, _cam, _labels);
            DrawFx();
            DrawCones(alpha);
        }
        UpdateHud();
    }

    void SaveShot()
    {
        // wait a couple of frames so the frame at the requested time has actually been drawn
        if (--_shotPending > 0) return;
        _shotPending = -1;
        string dir = string.IsNullOrEmpty(_shotDir) ? Path.Combine(_simDir, "out", "godot") : Path.GetFullPath(_shotDir, Path.Combine(_simDir, ".."));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(_scenarios[_scenarioIdx])}-{_seed}-t{_m!.Time.ToString("000.0", System.Globalization.CultureInfo.InvariantCulture)}.png");
        GetViewport().GetTexture().GetImage().SavePng(path);
        GD.Print("shot " + path);
        if (_shots.Count == 0) GetTree().Quit();
    }

    // ───────────────────────── input and camera ─────────────────────────

    public override void _UnhandledInput(InputEvent ev)
    {
        if (ev is InputEventKey { Pressed: true, Echo: false } k)
        {
            switch (k.Keycode)
            {
                case Key.Space: _paused = !_paused; break;
                case Key.Period: if (_m != null && !_m.Over) { StepSim(); _acc = 0; _paused = true; } break;
                case Key.Equal or Key.KpAdd: _speed = Math.Min(16, _speed * 2); break;
                case Key.Minus or Key.KpSubtract: _speed = Math.Max(0.125, _speed / 2); break;
                case Key.R: StartMatch(); break;
                case Key.N: _seed++; StartMatch(); break;
                case Key.Bracketright: _scenarioIdx = (_scenarioIdx + 1) % Math.Max(1, _scenarios.Length); StartMatch(); break;
                case Key.Bracketleft: _scenarioIdx = (_scenarioIdx + _scenarios.Length - 1) % Math.Max(1, _scenarios.Length); StartMatch(); break;
                case Key.L: _labels = !_labels; break;
                case Key.F: FollowNext(k.ShiftPressed ? -1 : 1); break;
                case Key.G: _follow = -1; break;
                case Key.H: _cut = !_cut; ApplyCut(); break;
                case Key.C: _cones = !_cones; break;
                case Key.A when k.ShiftPressed: _auto = !_auto; break;
                case Key.Escape: GetTree().Quit(); break;
            }
        }
        else if (ev is InputEventMouseButton { Pressed: true } mb)
        {
            if (mb.ButtonIndex == MouseButton.WheelUp) _zoom = Math.Max(4, _zoom / 1.12f);
            else if (mb.ButtonIndex == MouseButton.WheelDown) _zoom = Math.Min(200, _zoom * 1.12f);
        }
        else if (ev is InputEventMouseMotion mm && (mm.ButtonMask & (MouseButtonMask.Right | MouseButtonMask.Middle)) != 0)
        {
            float perPx = _zoom / GetViewport().GetVisibleRect().Size.Y;
            var (right, fwd) = GroundAxes();
            _follow = -1;
            _target -= right * mm.Relative.X * perPx;
            _target += fwd * mm.Relative.Y * perPx / Mathf.Sin(_camPitch);
        }
    }

    void FollowNext(int dir)
    {
        if (_m == null) return;
        int n = _m.Count;
        for (int k = 1; k <= n; k++)
        {
            int i = ((_follow < 0 ? (dir > 0 ? -1 : 0) : _follow) + dir * k + 2 * n) % n;
            if (_cur[i].Alive) { _follow = i; if (_zoom > 25) _zoom = 16; return; }
        }
    }

    void HandleCameraKeys(float dt)
    {
        var (right, fwd) = GroundAxes();
        Vector3 pan = Vector3.Zero;
        if (Input.IsKeyPressed(Key.W) || Input.IsKeyPressed(Key.Up)) pan += fwd;
        if (Input.IsKeyPressed(Key.S) || Input.IsKeyPressed(Key.Down)) pan -= fwd;
        if (Input.IsKeyPressed(Key.D) || Input.IsKeyPressed(Key.Right)) pan += right;
        if (Input.IsKeyPressed(Key.A) && !Input.IsKeyPressed(Key.Shift) || Input.IsKeyPressed(Key.Left)) pan -= right;
        if (pan != Vector3.Zero) _follow = -1;
        _target += pan * _zoom * 0.8f * dt;
        if (Input.IsKeyPressed(Key.Q)) _camYaw -= 1.6f * dt;
        if (Input.IsKeyPressed(Key.E)) _camYaw += 1.6f * dt;
    }

    (Vector3 right, Vector3 fwd) GroundAxes()
    {
        // the camera sits on the +offset side and looks back at the target
        var fwd = -new Vector3(Mathf.Sin(_camYaw), 0, Mathf.Cos(_camYaw));
        var right = new Vector3(-fwd.Z, 0, fwd.X);
        return (right, fwd);
    }

    void UpdateCamera(float alpha)
    {
        if (_follow >= 0 && _m != null)
        {
            var p = _prev[_follow].Pos + (_cur[_follow].Pos - _prev[_follow].Pos) * alpha;
            _target = _target.Lerp(W(p, 0.8), 0.25f);
        }
        var off = new Vector3(Mathf.Sin(_camYaw) * Mathf.Cos(_camPitch), Mathf.Sin(_camPitch), Mathf.Cos(_camYaw) * Mathf.Cos(_camPitch));
        _cam.Size = _zoom;
        _cam.LookAtFromPosition(_target + off * 120, _target, Vector3.Up);
    }

    // ───────────────────────── static scene ─────────────────────────

    void BuildStatic()
    {
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = new Color("15181300"),
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color("c8d0e0"),
            AmbientLightEnergy = 0.45f,
        };
        AddChild(new WorldEnvironment { Environment = env });
        var sun = new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-55, 35, 0),
            LightEnergy = 1.1f,
            LightColor = new Color("fff3dc"),
            ShadowEnabled = true,
        };
        sun.DirectionalShadowMode = DirectionalLight3D.ShadowMode.Orthogonal;
        sun.DirectionalShadowMaxDistance = 220;
        sun.ShadowBias = 0.03f;
        AddChild(sun);

        _cam = new Camera3D { Projection = Camera3D.ProjectionType.Orthogonal, Near = 1, Far = 500, Current = true };
        AddChild(_cam);

        _fxMesh = new ImmediateMesh();
        _fxMat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            VertexColorUseAsAlbedo = true,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        _fx = new MeshInstance3D { Mesh = _fxMesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(_fx);

        _coneIm = new ImmediateMesh();
        _coneMat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            VertexColorUseAsAlbedo = true,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            NoDepthTest = false,
        };
        _coneMesh = new MeshInstance3D { Mesh = _coneIm, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(_coneMesh);

        var layer = new CanvasLayer();
        AddChild(layer);
        _overlay = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        _overlay.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        layer.AddChild(_overlay);
        _hud = MakeLabel(15);
        _hud.Position = new Vector2(12, 8);
        layer.AddChild(_hud);
        _banner = MakeLabel(34);
        _banner.SetAnchorsPreset(Control.LayoutPreset.CenterTop);
        _banner.Position = new Vector2(-300, 60);
        _banner.Size = new Vector2(600, 50);
        _banner.HorizontalAlignment = HorizontalAlignment.Center;
        layer.AddChild(_banner);
    }

    internal static Label MakeLabel(int size)
    {
        var l = new Label { MouseFilter = Control.MouseFilterEnum.Ignore };
        l.AddThemeFontSizeOverride("font_size", size);
        l.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.85f));
        l.AddThemeConstantOverride("outline_size", Math.Max(3, size / 4));
        return l;
    }

    static StandardMaterial3D Mat(Color c, float rough = 0.9f)
    {
        var m = new StandardMaterial3D { AlbedoColor = c, Roughness = rough };
        if (c.A < 1) m.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
        return m;
    }

    static Vector3 W(Vec2 p, double h = 0) => new((float)p.X, (float)h, (float)p.Y);

    void BuildWorld(Squad.Sim.World w)
    {
        _worldRoot?.QueueFree();
        _worldRoot = new Node3D { Name = "World" };
        AddChild(_worldRoot);
        float ww = (float)w.Width, wh = (float)w.Height;

        _tallWalls.Clear();
        MeshInstance3D Add(Mesh mesh, Material mat, Vector3 pos, float yaw = 0, bool shadow = true)
        {
            var mi = new MeshInstance3D
            {
                Mesh = mesh, MaterialOverride = mat, Position = pos, Rotation = new Vector3(0, yaw, 0),
                CastShadow = shadow ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off,
            };
            _worldRoot!.AddChild(mi);
            return mi;
        }

        // ground: the playable field and a darker apron around it
        Add(new PlaneMesh { Size = new Vector2(ww + 40, wh + 40) }, Mat(new Color("20251d")), new Vector3(ww / 2, -0.02f, wh / 2), 0, false);
        Add(new PlaneMesh { Size = new Vector2(ww, wh) }, Mat(new Color("3a4232")), new Vector3(ww / 2, 0, wh / 2), 0, false);

        // 5 m grid
        var grid = new ImmediateMesh();
        grid.SurfaceBegin(Mesh.PrimitiveType.Lines);
        grid.SurfaceSetColor(new Color(1, 1, 1, 0.06f));
        for (int x = 5; x < ww; x += 5) { grid.SurfaceAddVertex(new Vector3(x, 0.01f, 0)); grid.SurfaceAddVertex(new Vector3(x, 0.01f, wh)); }
        for (int y = 5; y < wh; y += 5) { grid.SurfaceAddVertex(new Vector3(0, 0.01f, y)); grid.SurfaceAddVertex(new Vector3(ww, 0.01f, y)); }
        grid.SurfaceEnd();
        Add(grid, _coneMat, Vector3.Zero, 0, false);

        for (int t = 0; t < w.Map.Spawns.Count && t < 2; t++)
        {
            var z = w.Map.Spawns[t];
            var size = new Vector2((float)(z.Max.X - z.Min.X), (float)(z.Max.Y - z.Min.Y));
            var c = TeamCol[t]; c.A = 0.16f;
            var mat = Mat(c); mat.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
            Add(new PlaneMesh { Size = size }, mat, W(z.Center, 0.015), 0, false);
        }
        foreach (var z in w.Map.Zones)
        {
            var mat = Mat(new Color(0.95f, 0.8f, 0.25f, 0.3f)); mat.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
            Add(new CylinderMesh { TopRadius = (float)z.Radius, BottomRadius = (float)z.Radius, Height = 0.03f, RadialSegments = 48 }, mat, W(z.Center, 0.02), 0, false);
        }

        var highWall = Mat(new Color("d8d2c0"));
        var lowWall = Mat(new Color("b09358"));
        var crate = Mat(new Color("8f5e2e"));
        var bush = Mat(new Color(0.31f, 0.6f, 0.24f, 0.78f));
        var trunk = Mat(new Color("5a3d22"));
        var leaves = Mat(new Color(0.17f, 0.4f, 0.15f, 0.82f));

        foreach (var o in w.Obstacles)
        {
            var d = o.B - o.A;
            double len = d.Length;
            float h = (float)o.H, r = (float)o.R;
            var mid = (o.A + o.B) * 0.5;
            float yaw = len > 1e-6 ? (float)Math.Atan2(d.X, d.Y) : 0;
            switch (o.Kind)
            {
                case ObstacleKind.HighWall:
                    _tallWalls.Add((Add(new BoxMesh { Size = new Vector3(2 * r, h, (float)len + 2 * r) }, highWall, W(mid, h / 2), yaw), h));
                    break;
                case ObstacleKind.LowWall:
                    Add(new BoxMesh { Size = new Vector3(2 * r, h, (float)len + 2 * r) }, lowWall, W(mid, h / 2), yaw);
                    break;
                case ObstacleKind.Crate:
                    Add(new BoxMesh { Size = new Vector3(2 * r, h, (float)len + 2 * r) }, crate, W(mid, h / 2), yaw);
                    break;
                case ObstacleKind.Bush:
                {
                    // a capsule of foliage: blobs along the segment
                    int n = Math.Max(1, (int)Math.Ceiling(len / r) + 1);
                    for (int k = 0; k < n; k++)
                    {
                        var p = n == 1 ? mid : o.A + d * ((double)k / (n - 1));
                        Add(new SphereMesh { Radius = r, Height = h * 1.1f, RadialSegments = 12, Rings = 6 }, bush, W(p, h * 0.5f));
                    }
                    break;
                }
                case ObstacleKind.Tree:
                    Add(new CylinderMesh { TopRadius = r * 0.7f, BottomRadius = r, Height = h * 0.7f, RadialSegments = 8 }, trunk, W(mid, h * 0.35f));
                    Add(new SphereMesh { Radius = 1.2f, Height = 1.9f, RadialSegments = 12, Rings = 6 }, leaves, W(mid, h * 0.82f));
                    break;
            }
        }
    }

    void ApplyCut()
    {
        // cosmetic only: the simulation still treats them as 2.5 m walls
        foreach (var (mesh, h) in _tallWalls)
        {
            float k = _cut ? 0.9f / h : 1;
            mesh.Scale = new Vector3(1, k, 1);
            mesh.Position = new Vector3(mesh.Position.X, h * k / 2, mesh.Position.Z);
        }
    }

    void BuildSoldiers(Match m)
    {
        foreach (var s in _soldiers) s.Free();
        _soldiers = new SoldierView[m.Count];
        for (int i = 0; i < m.Count; i++)
        {
            var t = m.Truth(i);
            _soldiers[i] = new SoldierView(i, _runner!.Brains[i], m.B.Damage.Hp, Mat(TeamCol[t.Team], 0.7f), Mat(TeamDark[t.Team]), _worldRoot!, _overlay);
        }
    }

    // ───────────────────────── per-frame effects ─────────────────────────

    void DrawFx()
    {
        var m = _m!;
        _fxMesh.ClearSurfaces();
        if (_tracers.Count == 0 && _hits.Count == 0) return;
        var camDir = -_cam.GlobalBasis.Z;
        _fxMesh.SurfaceBegin(Mesh.PrimitiveType.Triangles, _fxMat);
        foreach (var (a, b, t) in _tracers)
        {
            float k = 1 - (float)((m.Time - t) / TracerLife);
            var side = (b - a).Cross(camDir).Normalized() * Math.Max(0.02f, _zoom * 0.0016f);
            Quad(a - side, a + side, b + side, b - side, new Color(1, 0.95f, 0.6f, 0.95f * k), new Color(1, 0.9f, 0.5f, 0.15f * k));
        }
        float hs = Math.Max(0.12f, _zoom * 0.005f);
        var up = _cam.GlobalBasis.Y * hs; var right = _cam.GlobalBasis.X * hs;
        foreach (var (p, t) in _hits)
        {
            float k = 1 - (float)((m.Time - t) / HitLife);
            var c = new Color(1, 0.2f, 0.15f, k);
            float s = 1 + (1 - k) * 1.5f;
            Quad(p - right * s, p + up * s, p + right * s, p - up * s, c, c);
        }
        _fxMesh.SurfaceEnd();
    }

    void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color ca, Color cc)
    {
        _fxMesh.SurfaceSetColor(ca); _fxMesh.SurfaceAddVertex(a);
        _fxMesh.SurfaceSetColor(ca); _fxMesh.SurfaceAddVertex(b);
        _fxMesh.SurfaceSetColor(cc); _fxMesh.SurfaceAddVertex(c);
        _fxMesh.SurfaceSetColor(ca); _fxMesh.SurfaceAddVertex(a);
        _fxMesh.SurfaceSetColor(cc); _fxMesh.SurfaceAddVertex(c);
        _fxMesh.SurfaceSetColor(cc); _fxMesh.SurfaceAddVertex(d);
    }

    void DrawCones(float alpha)
    {
        _coneIm.ClearSurfaces();
        if (!_cones) return;
        var m = _m!;
        float half = (float)m.B.Perception.ConeHalf, range = (float)Math.Min(m.B.Perception.ViewRange, 12);
        const int seg = 14;
        bool any = false;
        for (int i = 0; i < m.Count; i++)
        {
            if (!_cur[i].Alive) continue;
            if (!any) { _coneIm.SurfaceBegin(Mesh.PrimitiveType.Triangles, _coneMat); any = true; }
            var p = _prev[i].Pos + (_cur[i].Pos - _prev[i].Pos) * alpha;
            float yaw = Mathf.LerpAngle((float)_prev[i].AimYaw, (float)_cur[i].AimYaw, alpha);
            var o = W(p, 0.03);
            var c0 = TeamCol[_cur[i].Team]; c0.A = 0.16f;
            var c1 = c0; c1.A = 0;
            for (int k = 0; k < seg; k++)
            {
                float a0 = yaw - half + 2 * half * k / seg, a1 = yaw - half + 2 * half * (k + 1) / seg;
                _coneIm.SurfaceSetColor(c0); _coneIm.SurfaceAddVertex(o);
                _coneIm.SurfaceSetColor(c1); _coneIm.SurfaceAddVertex(o + new Vector3(Mathf.Sin(a0), 0, Mathf.Cos(a0)) * range);
                _coneIm.SurfaceSetColor(c1); _coneIm.SurfaceAddVertex(o + new Vector3(Mathf.Sin(a1), 0, Mathf.Cos(a1)) * range);
            }
        }
        if (any) _coneIm.SurfaceEnd();
    }

    void UpdateHud()
    {
        if (_m == null) { _hud.Text = _error.Length > 0 ? "Error: " + _error : "no match"; _banner.Text = ""; return; }
        var m = _m;
        string state = _paused ? "  PAUSED" : "";
        _hud.Text =
            $"{_title}    [{_scenarioIdx + 1}/{_scenarios.Length}]\n" +
            $"t = {m.Time:0.0} s    blue {m.AliveCount(0)}/{m.TeamSize(0)}    red {m.AliveCount(1)}/{m.TeamSize(1)}    speed ×{_speed:0.###}{state}\n" +
            "Space pause · . step · +/- speed · R restart · N next seed · [ ] scenario · L labels · C cones · H cut walls · Shift+A autoplay " + (_auto ? "on" : "off") + "\n" +
            "WASD / RMB drag pan · Q/E rotate · wheel zoom · F / Shift+F follow soldier · G free camera" + (_follow >= 0 ? $"    following {_follow}" : "");
        _banner.Text = m.Over
            ? (m.Result.Winner < 0 ? "DRAW" : m.Result.Winner == 0 ? "BLUE WINS" : "RED WINS") + $"  ({m.Result.Reason}, {m.Time:0.0} s)"
            : "";
        _banner.AddThemeColorOverride("font_color", m.Over && m.Result.Winner >= 0 ? TeamCol[m.Result.Winner].Lightened(0.3f) : Colors.White);
    }
}

/// <summary>One soldier: a capsule body that sinks when crouching, a head that turns with the aim, a rifle that is
/// lowered or raised, and a 2D label with strategy, intent and health drawn over the 3D view.</summary>
sealed class SoldierView
{
    const float StandH = 1.5f, CrouchH = 0.85f;
    readonly Node3D _root, _body, _torso, _look, _gun;
    readonly MeshInstance3D _torsoMesh;
    readonly Label _label;
    readonly ColorRect _hpBg, _hpFill;
    readonly IBrain? _brain;
    readonly string _name;
    readonly double _maxHp;
    readonly StandardMaterial3D _deadMat;
    float _fall;

    public SoldierView(int id, IBrain? brain, double maxHp, StandardMaterial3D team, StandardMaterial3D dark, Node3D parent, Control overlay)
    {
        _brain = brain; _maxHp = maxHp;
        _name = $"{id} {brain?.Name ?? ""}";
        _deadMat = new StandardMaterial3D { AlbedoColor = dark.AlbedoColor.Darkened(0.35f), Roughness = 1 };

        _root = new Node3D(); parent.AddChild(_root);
        _body = new Node3D(); _root.AddChild(_body);
        _torso = new Node3D(); _body.AddChild(_torso);
        _torsoMesh = new MeshInstance3D { Mesh = new CapsuleMesh { Radius = 0.24f, Height = 1, RadialSegments = 12, Rings = 4 }, MaterialOverride = team, Position = new Vector3(0, 0.5f, 0) };
        _torso.AddChild(_torsoMesh);

        _look = new Node3D(); _body.AddChild(_look);
        _look.AddChild(new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.15f, Height = 0.3f, RadialSegments = 12, Rings = 6 }, MaterialOverride = dark });
        // visor: shows where he looks
        _look.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.2f, 0.07f, 0.08f) }, MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color("e8e4d0") }, Position = new Vector3(0, 0.02f, 0.13f) });

        _gun = new Node3D(); _body.AddChild(_gun);
        _gun.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.07f, 0.1f, 0.9f) }, MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color("2b2b2b"), Roughness = 0.6f }, Position = new Vector3(0, 0, 0.35f) });

        _label = Main.MakeLabel(12);
        _label.AddThemeColorOverride("font_color", team.AlbedoColor.Lightened(0.45f));
        _hpBg = new ColorRect { Color = new Color(0, 0, 0, 0.6f), Size = new Vector2(34, 4), MouseFilter = Control.MouseFilterEnum.Ignore };
        _hpFill = new ColorRect { Size = new Vector2(34, 4), MouseFilter = Control.MouseFilterEnum.Ignore };
        overlay.AddChild(_hpBg); overlay.AddChild(_hpFill); overlay.AddChild(_label);
    }

    public void Free()
    {
        _label.QueueFree(); _hpBg.QueueFree(); _hpFill.QueueFree();
        // 3D nodes go away with the world root
    }

    public void Update(in SoldierTruth a, in SoldierTruth b, float t, float dt, Camera3D cam, bool labels)
    {
        var pos = a.Pos + (b.Pos - a.Pos) * t;
        float yaw = Mathf.LerpAngle((float)a.Yaw, (float)b.Yaw, t);
        float aimYaw = Mathf.LerpAngle((float)a.AimYaw, (float)b.AimYaw, t);
        float crouch = Mathf.Lerp((float)a.Crouch, (float)b.Crouch, t);
        float raise = Mathf.Lerp((float)a.Raise, (float)b.Raise, t);
        float pitch = Mathf.Lerp((float)a.AimPitch, (float)b.AimPitch, t);

        _root.Position = new Vector3((float)pos.X, 0, (float)pos.Y);
        _root.Rotation = new Vector3(0, yaw, 0);
        float h = Mathf.Lerp(StandH, CrouchH, Math.Clamp(crouch, 0, 1));
        _torso.Scale = new Vector3(1, h, 1);
        _look.Position = new Vector3(0, h + 0.13f, 0);
        _look.Rotation = new Vector3(0, aimYaw - yaw, 0);
        _gun.Position = new Vector3(-0.2f, h - 0.17f, 0.05f);
        // lowered rifle points down and ahead; raised it follows the aim
        _gun.Rotation = new Vector3(Mathf.Lerp(0.75f, -pitch, Math.Clamp(raise, 0, 1)), aimYaw - yaw, 0);

        if (!b.Alive)
        {
            if (_fall == 0) { _torsoMesh.MaterialOverride = _deadMat; _gun.Visible = false; }
            _fall = Math.Min(1, _fall + dt * 3);
            _body.Rotation = new Vector3(Mathf.Pi / 2 * _fall * _fall, 0, 0);
            _body.Position = new Vector3(0, 0.15f * _fall, 0);
            _label.Visible = _hpBg.Visible = _hpFill.Visible = false;
            return;
        }
        if (_fall != 0) { _fall = 0; _body.Rotation = Vector3.Zero; _body.Position = Vector3.Zero; _gun.Visible = true; }

        var head = _root.Position + new Vector3(0, h + 0.5f, 0);
        bool show = labels && !cam.IsPositionBehind(head);
        _label.Visible = _hpBg.Visible = _hpFill.Visible = show;
        if (!show) return;
        var sp = cam.UnprojectPosition(head);
        string intent = _brain?.Intent ?? "";
        _label.Text = $"{_name} {intent}{(b.Reloading ? " ⟳" : "")}";
        _label.Position = sp + new Vector2(-_label.Size.X / 2, -22);
        _hpBg.Position = sp + new Vector2(-17, -4);
        float frac = (float)Math.Clamp(b.Hp / _maxHp, 0, 1);
        _hpFill.Position = _hpBg.Position;
        _hpFill.Size = new Vector2(34 * frac, 4);
        _hpFill.Color = frac > 0.5f ? new Color("7cd65a") : frac > 0.25f ? new Color("e8c547") : new Color("e04a3a");
    }
}
