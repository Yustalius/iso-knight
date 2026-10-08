using Godot;
using Squad.Sim;

/// <summary>--showcase NAME: no match, two soldiers driven by scripted truth through the same view and rig code, placed
/// like the concept's shots.cjs plans (the player at the origin facing 200°, the OPFOR soldier at (−0.6, −2.4) facing
/// him), so close-ups can be put side by side with the concept's frames. Scenes: stand, aim, crouch, walk, run, sneak,
/// reload, tactical, fire, kill, turn. Soldier 0 is M81, soldier 1 is OPFOR.</summary>
public partial class Main
{
    string? _show;
    double _showT;
    SoldierTruth[] _showPrev = new SoldierTruth[2], _showCur = new SoldierTruth[2];

    static readonly Vec2 EnemyAt = new(-0.6, -2.4);
    double KillT => _show == "kill" ? 1.1 : 1e9;

    void StartShowcase()
    {
        _title = "showcase " + _show;
        _worldRoot?.QueueFree();
        _worldRoot = new Node3D { Name = "World" };
        AddChild(_worldRoot);
        // --calib: flat magenta ground, so a script can mask the soldiers when fitting the light to the concept's frames;
        // otherwise the map's grass on an empty 16 m field centred on the origin
        if (OS.GetCmdlineUserArgs().Contains("--calib"))
        {
            var groundMat = Mat(new Color(1, 0, 1), 1); groundMat.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
            _worldRoot.AddChild(new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(60, 60) }, MaterialOverride = groundMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        }
        else
        {
            var field = new Node3D { Position = new Vector3(-8, 0, -8) };
            _worldRoot.AddChild(field);
            var map = new MapData { Name = "showcase", Width = 16, Height = 16 };
            if (_show == "yard") Yard(map.Obstacles);
            _map = new MapView(new Squad.Sim.World(map), field, 1);
            _mapOffset = field.Position;
            _map.ApplyCut(_cut);
        }
        foreach (var s in _soldiers) s.Free();
        _soldiers = new[]
        {
            new SoldierView(0, 0, null, 100, _worldRoot, _overlay, _fx, _walls),
            new SoldierView(1, 1, null, 100, _worldRoot, _overlay, _fx, _walls),
        };
        _showT = 0;
        for (int i = 0; i < 2; i++) _showPrev[i] = _showCur[i] = ShowTruth(i, 0);
        _target = new Vector3(0, 0.9f, 0);
        _zoom = _zoomArg ?? 3.2f;
        _camYaw = _camYawArg ?? Mathf.Pi / 4;
    }

    void ProcessShowcase(double delta)
    {
        _showT += delta * _speed;
        for (int i = 0; i < 2; i++) { _showPrev[i] = _showCur[i]; _showCur[i] = ShowTruth(i, _showT); }
        float dt = (float)(delta * _speed);
        // shots in the scripted fire and kill scenes: one every 0.25 s once the rifle is up; the third one kills in "kill"
        bool shot = _show is "fire" or "kill" && _showT > 0.6 && _showT - dt <= KillT && Math.Floor((_showT - 0.6) / 0.25) > Math.Floor((_showT - dt - 0.6) / 0.25);
        if (shot) _soldiers[0].Shot = true;
        if (_show is "reload" or "tactical") _soldiers[0].ReloadEmpty = _show == "reload";
        if (_follow >= 0) _target = new Vector3((float)_showCur[_follow].Pos.X, 0.9f, (float)_showCur[_follow].Pos.Y);
        UpdateCamera(1);
        for (int i = 0; i < 2; i++) _soldiers[i].Update(_showPrev[i], _showCur[i], 1, dt, _cam, false);
        if (shot)
        {
            // the bullet goes into the enemy's chest, as a Shot + Hit (+ Kill) from the simulation would say
            var at = new Vector3((float)EnemyAt.X, 1.25f, (float)EnemyAt.Y) + new Vector3(0.05f, 0, 0) * (float)Math.Sin(_showT * 7);
            var from = _soldiers[0].MuzzleW; var dir = (at - from).Normalized();
            _fx.Tracer(from, at, 0, () => _fx.Blood(at, dir, 1));
            _soldiers[1].Flinch = (dir, at, HitZone.Torso);
            if (_showT >= KillT - 0.01) _soldiers[1].Killed = (at, dir, HitZone.Torso);
        }
        _fx.Update(dt);
        UpdateMap(dt);
        if (_shots.Count > 0 && _showT >= _shots[0]) { _shots.RemoveAt(0); _shotPending = 2; }
        _hud.Text = $"{_title}   t = {_showT:0.00} s";
    }

    /// <summary>yard: a house around the OPFOR soldier (door toward the camera, a window), sandbags, a concrete block,
    /// crates, a bush and a tree, in map coordinates (the showcase field starts at (−8, −8)).</summary>
    static void Yard(List<Obstacle> o)
    {
        Vec2 P(double x, double z) => new(x + 8, z + 8);
        void Wall(double x0, double z0, double x1, double z1) => o.Add(Obstacle.Make(ObstacleKind.HighWall, P(x0, z0), P(x1, z1)));
        // house x −2.6…1.6, z −4.8…−0.8: a door on the z = −0.8 side, a window on the x = 1.6 side
        Wall(-2.6, -4.8, 1.6, -4.8); Wall(-2.6, -0.8, -2.6, -4.8);
        Wall(-2.6, -0.8, -1.4, -0.8); Wall(0.1, -0.8, 1.6, -0.8);
        Wall(1.6, -0.8, 1.6, -2.1); o.Add(Obstacle.Make(ObstacleKind.LowWall, P(1.6, -2.1), P(1.6, -3.5), 0.12)); Wall(1.6, -3.5, 1.6, -4.8);
        o.Add(Obstacle.Make(ObstacleKind.LowWall, P(2.6, 1.4), P(5.2, 1.4)));
        o.Add(Obstacle.Make(ObstacleKind.LowWall, P(-3.2, 2.2), P(-3.2, 4.4)));
        o.Add(Obstacle.Make(ObstacleKind.Crate, P(3.4, -1.6), P(3.4, -1.6))); o.Add(Obstacle.Make(ObstacleKind.Crate, P(3.5, -2.5), P(3.5, -2.5)));
        o.Add(Obstacle.Make(ObstacleKind.Bush, P(-4.6, 0.2), P(-4.0, 1.4), 0.8));
        o.Add(Obstacle.Make(ObstacleKind.Tree, P(4.4, -5.0), P(4.4, -5.0), 0.28));
    }

    SoldierTruth ShowTruth(int who, double t)
    {
        var s = new SoldierTruth { Id = who, Team = who, Alive = true, Hp = 100, Chamber = true, Mag = 30 };
        double deg = Math.PI / 180;
        if (who == 1)
        {
            s.Pos = EnemyAt; s.Yaw = s.AimYaw = (Vec2.Zero - EnemyAt).Yaw;
            s.Alive = !(_show == "kill" && t >= KillT);
            return s;
        }
        s.Yaw = s.AimYaw = 200 * deg;
        double Ramp(double t0, double dur) => Math.Clamp((t - t0) / dur, 0, 1);
        void AimAtEnemy(double from)
        {
            s.Aiming = t >= from;
            s.Raise = Ramp(from, 0.3);
            var to = EnemyAt - s.Pos;
            if (s.Aiming) { s.Yaw = s.AimYaw = to.Yaw; s.AimPitch = Math.Atan2(1.3 - (s.Crouch > 0.5 ? 0.9 : 1.45), to.Length); }
        }
        void Walk(double speed, double yaw)
        {
            // from rest, reaching full speed in 0.4 s; the position is the integral of that velocity
            var dir = Vec2.FromYaw(yaw);
            double dist = speed * (t < 0.4 ? t * t / 0.8 : t - 0.2);
            s.Pos = dir * (dist - 2); s.Vel = dir * (speed * Math.Min(1, t / 0.4));
            s.Yaw = s.AimYaw = yaw;
        }
        switch (_show)
        {
            case "aim": AimAtEnemy(0.2); break;
            case "crouch": s.Crouch = Ramp(0.1, 0.3); AimAtEnemy(0.4); break;
            case "walk": Walk(1.45, 135 * deg); break;
            case "run": Walk(3.0, 135 * deg); break;
            case "sneak": s.Crouch = 1; Walk(0.85, 135 * deg); break;
            case "reload":
            case "tactical":
            {
                double dur = _show == "reload" ? 1.72 : 1.38;
                if (t >= 0.5 && t < 0.5 + dur) { s.Reloading = true; s.ReloadT = t - 0.5; s.Mag = s.ReloadT < 0.17 ? 30 : 0; }
                break;
            }
            case "fire": case "kill": AimAtEnemy(0.2); break;
            case "turn":
            {
                // aim sweeps left and right of the enemy: the rifle leads, the legs follow
                s.Aiming = true; s.Raise = Ramp(0, 0.3);
                s.Yaw = s.AimYaw = (EnemyAt - s.Pos).Yaw + Math.Sin(t * 1.6) * 1.2;
                break;
            }
        }
        return s;
    }
}
