using Godot;
using Squad.Bots;
using Squad.Sim;

/// <summary>3D view of a bot match. The match runs in Squad.Sim at 30 Hz exactly as in the arena;
/// this node only draws Truth and Events, interpolating soldiers between ticks.
/// Sim (x, y) is Godot (x, height, y); yaw turns +Z toward +X, the same as Node3D.Rotation.Y.</summary>
public partial class Main : Node3D
{
    const double Dt = 1.0 / 30;

    internal static readonly Color[] TeamCol = { new("4fa8ff"), new("ff6a55") };

    // what to play
    string _simDir = "";
    string[] _scenarios = Array.Empty<string>();
    int _scenarioIdx;
    ulong _seed = 1;
    string _error = "";

    // the match
    BotRunner? _runner;
    Match? _m;                 // read on this thread only for what never changes: world, balance, team sizes
    SimFeed? _feed;            // the match itself runs ahead on a worker thread
    SimFeed.Frame _now = new(); // the newest tick shown
    string _title = "";
    SoldierTruth[] _prev = Array.Empty<SoldierTruth>(), _cur = Array.Empty<SoldierTruth>();
    double _acc, _speed = 1, _overFor;
    bool _paused, _debug, _labels = true, _cones = true, _auto = true, _cut;
    int[] _shotsSeen = Array.Empty<int>();
    MapView? _map;
    Fx _fx = null!;
    Ragdoll.Collider[] _walls = Array.Empty<Ragdoll.Collider>();
    readonly Dictionary<int, Queue<double>> _fired = new();   // per shooter: times of shots whose bullet is still flying
    readonly Dictionary<int, (Vector3 point, Vector3 dir, HitZone zone)> _lastHit = new();

    // scene
    Node3D? _worldRoot;
    SoldierView[] _soldiers = Array.Empty<SoldierView>();
    MeshInstance3D _coneMesh = null!;
    ImmediateMesh _coneIm = null!;
    StandardMaterial3D _coneMat = null!;
    Camera3D _cam = null!;
    Godot.Environment _env = null!;
    DirectionalLight3D _sun = null!;
    internal static readonly Color Bg = new("20251f");
    DirectionalLight3D _skyLight = null!, _rim = null!;
    // ambient (ground colour), sky light from above, sun, rim, exposure; --light a,k,s,r,e overrides them for fitting
    static readonly float[] Light = { 2.03f, 0.32f, 0.73f, 0.43f, 1.03f };
    float[]? _lightArg;

    void ApplyLight(float[] l)
    {
        _env.AmbientLightEnergy = l[0]; _skyLight.LightEnergy = l[1]; _sun.LightEnergy = l[2]; _rim.LightEnergy = l[3];
        _env.TonemapExposure = l[4];
    }
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
    bool _shotsMode;
    // --bench [seconds]: play the match in real time without vsync, then print frames per second (mean and 1 % low)
    bool _bench;
    double _benchFor = 60, _benchT;
    readonly List<double> _frameMs = new();
    ulong _lastTick;
    readonly System.Diagnostics.Stopwatch _sw = new();
    double _tSol, _tFx, _tMap, _sumSol, _sumFx, _sumMap;
    float? _camYawArg;

    public override void _ExitTree()
    {
        _feed?.Dispose();
        if (SoldierView.ProbeFeet) GD.Print($"feet: planted-foot travel {SoldierView.SlipSum:0.000} m over {SoldierView.BodySum:0.0} m of body travel ({100 * SoldierView.SlipSum / Math.Max(1e-9, SoldierView.BodySum):0.00}%)");
        Art.Clear(); Sling.ClearMaterials(); SoldierView.ClearMaterials(); MapView.ClearMaterials();
    }

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
                case "--showcase": _show = next; i++; break;
                case "--cam-yaw": _camYawArg = Mathf.DegToRad(float.Parse(next, System.Globalization.CultureInfo.InvariantCulture)); i++; break;
                case "--probe-feet": SoldierView.ProbeFeet = true; break;
                case "--bench": _bench = true; if (double.TryParse(next, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var bs)) { _benchFor = bs; i++; } break;
                case "--light": _lightArg = next.Split(',').Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray(); i++; break;
                case "--shots":
                    _shotsMode = true;
                    foreach (var s in next.Split(',')) _shots.Add(double.Parse(s, System.Globalization.CultureInfo.InvariantCulture));
                    i++; break;
            }
        }

        string scenDir = Path.Combine(_simDir, "scenarios");
        _scenarios = Directory.Exists(scenDir) ? Directory.GetFiles(scenDir, "*.json").OrderBy(p => p).ToArray() : Array.Empty<string>();
        string want = scenarioArg ?? "team-4v4-mixed";
        _scenarioIdx = Math.Max(0, Array.FindIndex(_scenarios, p => Path.GetFileNameWithoutExtension(p) == want || Path.GetFullPath(p) == Path.GetFullPath(want)));

        BuildStatic();
        if (_show != null) StartShowcase(); else StartMatch();
    }

    // ───────────────────────── match lifecycle ─────────────────────────

    void StartMatch()
    {
        _error = "";
        _fx.Clear(); _fired.Clear(); _lastHit.Clear();
        _acc = 0; _overFor = 0;
        if (_scenarios.Length == 0) { _error = "no scenarios in " + _simDir; return; }
        try
        {
            var sc = Scenario.Load(_scenarios[_scenarioIdx]);
            _feed?.Dispose(); _feed = null;
            _runner = sc.Build(_seed).CreateRunner();
            _m = _runner.Match;
            _title = $"{sc.Name}  seed {_seed}";
        }
        catch (Exception e)
        {
            _runner = null; _m = null; _feed?.Dispose(); _feed = null;
            _error = e.Message;
            GD.PushError(e.ToString());
            return;
        }
        _prev = new SoldierTruth[_m.Count];
        _shotsSeen = new int[_m.Count];
        _cur = new SoldierTruth[_m.Count];
        _feed = new SimFeed(_runner);
        _now = _feed.First;
        for (int i = 0; i < _m.Count; i++) _prev[i] = _cur[i] = _now.Truth[i];
        BuildWorld(_m.World);
        ApplyCut();
        BuildSoldiers(_m);
        _target = new Vector3((float)_m.World.Width / 2, 0, (float)_m.World.Height / 2);
        _zoom = _zoomArg ?? (float)Math.Max(_m.World.Width, _m.World.Height) * 0.7f;
        if (_follow >= _m.Count) _follow = -1;
    }

    /// <summary>Take the next tick from the worker; false when it is not ready yet.</summary>
    bool StepSim(bool wait = false)
    {
        var m = _m!;
        if (_feed!.Error is { } err) { _error = err.Message; GD.PushError(err.ToString()); }
        var f = _feed.Next(wait || _shotsMode);
        if (f == null) return false;
        _now = f;
        (_prev, _cur) = (_cur, _prev);
        for (int i = 0; i < m.Count; i++) _cur[i] = f.Truth[i];
        // a shot leaves the muzzle in the tick the shooter's counter goes up; its Shot event comes when the bullet stops
        for (int i = 0; i < m.Count; i++)
        {
            int n = f.Shots[i];
            if (n > _shotsSeen[i])
            {
                _soldiers[i].Shot = true;
                if (!_fired.TryGetValue(i, out var q)) _fired[i] = q = new Queue<double>();
                q.Enqueue(f.Time);
            }
            _shotsSeen[i] = n;
        }
        foreach (var e in f.Events)
        {
            switch (e.Type)
            {
                case EventType.Shot: OnShot(e, f.Time); break;
                case EventType.Hit:
                {
                    var dir = new Vector3((float)e.Pos2.X, 0, (float)e.Pos2.Y);
                    var at = W(e.Pos, e.H);
                    _soldiers[e.Other].Flinch = (dir, at, e.Zone);
                    _lastHit[e.Other] = (at, dir, e.Zone);
                    break;
                }
                case EventType.Kill:
                    if (_lastHit.TryGetValue(e.Other, out var h)) _soldiers[e.Other].Killed = h;
                    break;
                case EventType.ReloadStart:
                    _soldiers[e.Agent].ReloadEmpty = e.Value > 0;
                    break;
            }
        }
        return true;
    }

    /// <summary>A bullet stopped: a tracer from the drawn muzzle to that point, and what it hit reacts when it arrives.</summary>
    void OnShot(in GameEvent e, double now)
    {
        double fired = _fired.TryGetValue(e.Agent, out var q) && q.Count > 0 ? q.Dequeue() : now;
        var shooter = _soldiers[e.Agent];
        var from = shooter.Dead || shooter.MuzzleW == Vector3.Zero ? W(e.Pos, e.H) : shooter.MuzzleW;
        var end = W(e.Pos2, e.H2);
        var dir = (end - from).Normalized();
        var surf = e.Surface;
        if (surf == Surface.Air) { _fx.Tracer(from, end, (float)(now - fired), null); return; }
        var (kind, normal) = ImpactAt(e.Pos2, e.H2, surf, dir);
        _fx.Tracer(from, end, (float)(now - fired), () =>
        {
            if (surf == Surface.Soldier) _fx.Blood(end, dir, 1);
            else _fx.Impact(kind, end, normal, dir);
        });
    }

    /// <summary>What a bullet hit and the surface normal there: the nearest obstacle of that kind (they are capsules).</summary>
    (string kind, Vector3 normal) ImpactAt(Vec2 p, double h, Surface s, Vector3 dir)
    {
        if (s is Surface.Ground or Surface.Soldier or Surface.None or Surface.Air) return ("dirt", Vector3.Up);
        var want = s switch { Surface.HighWall => ObstacleKind.HighWall, Surface.LowWall => ObstacleKind.LowWall, Surface.Crate => ObstacleKind.Crate, _ => ObstacleKind.Tree };
        Obstacle best = default; double bd = double.MaxValue; Vec2 bq = p; int bi = -1;
        var obs = _m!.World.Obstacles;
        for (int i = 0; i < obs.Length; i++)
        {
            var o = obs[i];
            if (o.Kind != want) continue;
            var ab = o.B - o.A; double L2 = ab.LengthSq;
            double t = L2 > 0 ? Math.Clamp((p - o.A).Dot(ab) / L2, 0, 1) : 0;
            var c = o.A + ab * t; double d = Vec2.Distance(p, c) - o.R;
            if (Math.Abs(d) < bd) { bd = Math.Abs(d); best = o; bq = c; bi = i; }
        }
        string kind = bi >= 0 && _map?.Kinds[bi] is { } k ? k : "concrete";
        if (h >= best.H - 0.03) return (kind, Vector3.Up);
        var n = new Vector3((float)(p.X - bq.X), 0, (float)(p.Y - bq.Y));
        return (kind, n.LengthSquared() > 1e-8f ? n.Normalized() : new Vector3(-dir.X, 0, -dir.Z).Normalized());
    }

    public override void _Process(double delta)
    {
        if (_shotPending >= 0) { SaveShot(); return; }
        if (_bench) BenchFrame();
        // frames for --shots advance by a fixed 1/60 s, so they do not depend on how fast this machine renders
        if (_shotsMode) delta = 1.0 / 60;
        HandleCameraKeys((float)delta);
        if (_show != null) { ProcessShowcase(delta); return; }

        if (_m != null)
        {
            if (!_paused && !_now.Over)
            {
                _acc += delta * _speed;
                int steps = 0;
                while (_acc >= Dt && !_now.Over && steps++ < 400)
                {
                    bool got = StepSim();
                    if (!got) { _acc = Math.Min(_acc, Dt); break; }   // the worker is behind: hold the last tick
                    _acc -= Dt;
                    if (_shots.Count > 0 && _now.Time >= _shots[0]) { _shots.RemoveAt(0); _shotPending = 2; _acc = 0; break; }
                }
            }
            if (_now.Over)
            {
                _overFor += delta;
                if (_shots.Count > 0) { _shots.Clear(); _shotPending = 2; }
                else if (_auto && !_paused && _overFor > 5) { _seed++; StartMatch(); }
            }
        }

        float alpha = _m == null || _now.Over || _paused ? 1 : (float)Math.Clamp(_acc / Dt, 0, 1);
        UpdateCamera(alpha);
        if (_m != null)
        {
            float simDt = _paused || _now.Over ? 0 : (float)(delta * _speed);
            _sw.Restart();
            for (int i = 0; i < _soldiers.Length; i++) _soldiers[i].Update(_prev[i], _cur[i], alpha, simDt, _cam, _debug && _labels);
            _tSol = _sw.Elapsed.TotalMilliseconds; _sw.Restart();
            _fx.Update(simDt);
            _tFx = _sw.Elapsed.TotalMilliseconds; _sw.Restart();
            UpdateMap(simDt);
            _tMap = _sw.Elapsed.TotalMilliseconds;
            DrawCones(alpha);
        }
        UpdateHud();
    }

    void BenchFrame()
    {
        ulong now = Time.GetTicksUsec();
        if (_lastTick == 0) { DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled); _lastTick = now; return; }
        double ms = (now - _lastTick) / 1000.0; _lastTick = now;
        if (_benchT > 2) { _sumSol += _tSol; _sumFx += _tFx; _sumMap += _tMap; }
        _benchT += ms / 1000;
        if (_benchT > 2) _frameMs.Add(ms);   // the first two seconds warm up shaders and caches
        if (_benchT < _benchFor + 2 && !_now.Over) return;
        var sorted = _frameMs.OrderByDescending(x => x).ToList();
        int n1 = Math.Max(1, sorted.Count / 100);
        double mean = 1000 * sorted.Count / sorted.Sum(), low = 1000 * n1 / sorted.Take(n1).Sum();
        var vp = GetViewport().GetVisibleRect().Size;
        GD.Print($"bench {_title}: {sorted.Count} frames over {sorted.Sum() / 1000:0.0} s at {vp.X}x{vp.Y}, " +
                 $"mean {mean:0.0} fps, 1% low {low:0.0} fps, worst frame {sorted[0]:0.0} ms, grass {_map?.GrassCount}, " +
                 $"{RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalDrawCallsInFrame)} draw calls, " +
                 $"{RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalPrimitivesInFrame) / 1000} k primitives; " +
                 $"CPU per frame: soldiers {_sumSol / sorted.Count:0.00} ms, effects {_sumFx / sorted.Count:0.00} ms, map {_sumMap / sorted.Count:0.00} ms (the match runs on its own thread)");
        GetTree().Quit();
    }

    void SaveShot()
    {
        // wait a couple of frames so the frame at the requested time has actually been drawn
        if (--_shotPending > 0) return;
        _shotPending = -1;
        string dir = string.IsNullOrEmpty(_shotDir) ? Path.Combine(_simDir, "out", "godot") : Path.GetFullPath(_shotDir, Path.Combine(_simDir, ".."));
        Directory.CreateDirectory(dir);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string path = Path.Combine(dir, _show != null
            ? $"show-{_show}-t{_showT.ToString("0.00", inv)}.png"
            : $"{Path.GetFileNameWithoutExtension(_scenarios[_scenarioIdx])}-{_seed}-t{_now.Time.ToString("000.0", inv)}.png");
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
                case Key.Period: if (_m != null && !_now.Over) { StepSim(true); _acc = 0; _paused = true; } break;
                case Key.Equal or Key.KpAdd: _speed = Math.Min(16, _speed * 2); break;
                case Key.Minus or Key.KpSubtract: _speed = Math.Max(0.125, _speed / 2); break;
                case Key.R: StartMatch(); break;
                case Key.N: _seed++; StartMatch(); break;
                case Key.Bracketright: _scenarioIdx = (_scenarioIdx + 1) % Math.Max(1, _scenarios.Length); StartMatch(); break;
                case Key.Bracketleft: _scenarioIdx = (_scenarioIdx + _scenarios.Length - 1) % Math.Max(1, _scenarios.Length); StartMatch(); break;
                case Key.Tab: _debug = !_debug; break;
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
            _target = _target.Lerp(W(p, 0.9), _shotsMode ? 1 : 0.25f);
        }
        var off = new Vector3(Mathf.Sin(_camYaw) * Mathf.Cos(_camPitch), Mathf.Sin(_camPitch), Mathf.Cos(_camYaw) * Mathf.Cos(_camPitch));
        _cam.Size = _zoom;
        // as close as the bottom edge of the view allows (an ortho ray there meets the ground 1.73·size/2 nearer),
        // so the single directional shadow split covers as little depth as possible
        float dist = 20 + _zoom * 1.2f;
        _cam.Far = dist * 2 + 40;
        _sun.DirectionalShadowMaxDistance = dist + _zoom * 1.2f;
        _cam.LookAtFromPosition(_target + off * dist, _target, Vector3.Up);
    }

    // ───────────────────────── static scene ─────────────────────────

    void BuildStatic()
    {
        // Light as on the concept's firing range (soldier-main.js): a hemisphere light (sky dfe7d5 over ground 4d4639),
        // a warm sun with soft shadows from (−6, 10, 3), a cool rim light from (4, 5, −6), ACES filmic. The compatibility
        // renderer has no hemisphere light and ignores the energy of sky ambient, so the hemisphere is the ground colour
        // as ambient plus a shadowless sky-coloured light from straight above. The energies are fitted to the concept's
        // frames of the same soldier (Light below), not converted from three.js units: the two engines differ there.
        _env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = Bg,
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color("4d4639"),
            ReflectedLightSource = Godot.Environment.ReflectionSource.Disabled,
            TonemapMode = Godot.Environment.ToneMapper.Aces,
        };
        AddChild(new WorldEnvironment { Environment = _env });
        _skyLight = new DirectionalLight3D { LightColor = new Color("dfe7d5"), LightSpecular = 0.3f };
        AddChild(_skyLight);
        _skyLight.LookAtFromPosition(new Vector3(0, 1, 0.001f), Vector3.Zero, Vector3.Up);
        _sun = new DirectionalLight3D
        {
            LightColor = new Color("ffe8bf"),
            ShadowEnabled = true,
            ShadowBias = 0.02f,
            ShadowNormalBias = 1.0f,
            ShadowBlur = 1.5f,
            DirectionalShadowMode = DirectionalLight3D.ShadowMode.Orthogonal,
        };
        AddChild(_sun);
        _sun.LookAtFromPosition(new Vector3(-6, 10, 3), Vector3.Zero, Vector3.Up);
        _rim = new DirectionalLight3D { LightColor = new Color("acc3ca") };
        AddChild(_rim);
        _rim.LookAtFromPosition(new Vector3(4, 5, -6), Vector3.Zero, Vector3.Up);
        ApplyLight(_lightArg ?? Light);

        _cam = new Camera3D { Projection = Camera3D.ProjectionType.Orthogonal, Near = 1, Far = 500, Current = true };
        AddChild(_cam);

        _fx = new Fx(this);
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
        _walls = w.Obstacles.Where(o => o.Solid).Select(o => new Ragdoll.Collider
        {
            Ax = (float)o.A.X, Az = (float)o.A.Y, Bx = (float)o.B.X, Bz = (float)o.B.Y, R = (float)o.R, H = (float)o.H,
        }).ToArray();
        _map = new MapView(w, _worldRoot, _seed);
        // objective zones of the rules: a faint painted ring
        foreach (var z in w.Map.Zones)
        {
            var mat = Mat(new Color(0.95f, 0.8f, 0.25f, 0.22f)); mat.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
            _worldRoot.AddChild(new MeshInstance3D
            {
                Mesh = new CylinderMesh { TopRadius = (float)z.Radius, BottomRadius = (float)z.Radius, Height = 0.02f, RadialSegments = 48 },
                MaterialOverride = mat, Position = W(z.Center, 0.012), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            });
        }
    }

    float _time;
    Vector3 _mapOffset;
    readonly List<Vector3> _alive = new();
    void UpdateMap(float dt)
    {
        if (_map == null) return;
        _time += dt;
        _alive.Clear();
        foreach (var s in _soldiers) if (!s.Dead) _alive.Add(s.Root.Position - _mapOffset);
        _map.Update(_time, dt, _camYaw, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_alive), _cut);
    }

    void ApplyCut() => _map?.ApplyCut(_cut);   // cosmetic only: the simulation still treats them as 2.5 m walls

    void BuildSoldiers(Match m)
    {
        foreach (var s in _soldiers) s.Free();
        _soldiers = new SoldierView[m.Count];
        for (int i = 0; i < m.Count; i++)
        {
            var t = m.Truth(i);
            _soldiers[i] = new SoldierView(i, t.Team, _runner!.Brains[i], m.B.Damage.Hp, _worldRoot!, _overlay, _fx, _walls);
        }
    }

    // ───────────────────────── per-frame effects ─────────────────────────

    void DrawCones(float alpha)
    {
        _coneIm.ClearSurfaces();
        if (!_cones || !_debug) return;
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
        var m = _m; var f = _now;
        string state = _paused ? "  PAUSED" : "";
        _hud.Text =
            $"{_title}    [{_scenarioIdx + 1}/{_scenarios.Length}]\n" +
            $"t = {f.Time:0.0} s    blue {f.Alive[0]}/{m.TeamSize(0)}    red {f.Alive[1]}/{m.TeamSize(1)}    speed ×{_speed:0.###}{state}" +
            (_debug ? "\n" : "    Tab debug") + (!_debug ? "" :
            "Tab debug off · Space pause · . step · +/- speed · R restart · N next seed · [ ] scenario · L labels · C cones · H cut walls · Shift+A autoplay " + (_auto ? "on" : "off") + "\n" +
            "WASD / RMB drag pan · Q/E rotate · wheel zoom · F / Shift+F follow soldier · G free camera") + (_follow >= 0 ? $"    following {_follow}" : "");
        _banner.Text = f.Over
            ? (f.Result.Winner < 0 ? "DRAW" : f.Result.Winner == 0 ? "BLUE WINS" : "RED WINS") + $"  ({f.Result.Reason}, {f.Time:0.0} s)"
            : "";
        _banner.AddThemeColorOverride("font_color", f.Over && f.Result.Winner >= 0 ? TeamCol[f.Result.Winner].Lightened(0.3f) : Colors.White);
    }
}
