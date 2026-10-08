using Godot;
using Squad.Sim;

/// <summary>--showcase NAME: no match, two soldiers driven by scripted truth through the same view and rig code, placed
/// like the concept's shots.cjs plans (the player at the origin facing 200°, the OPFOR soldier at (−0.6, −2.4) facing
/// him), so close-ups can be put side by side with the concept's frames. Scenes: stand, aim, crouch, walk, run, sneak,
/// reload, tactical, fire, turn. Soldier 0 is M81, soldier 1 is OPFOR.</summary>
public partial class Main
{
    string? _show;
    double _showT;
    SoldierTruth[] _showPrev = new SoldierTruth[2], _showCur = new SoldierTruth[2];

    static readonly Vec2 EnemyAt = new(-0.6, -2.4);

    void StartShowcase()
    {
        _title = "showcase " + _show;
        var map = new MapData { Name = "showcase", Width = 12, Height = 12 };
        _worldRoot?.QueueFree();
        _worldRoot = new Node3D { Name = "World" };
        AddChild(_worldRoot);
        var groundMat = Mat(new Color("55703a"), 1);
        // --calib: flat magenta ground, so a script can mask the soldiers when fitting the light to the concept's frames
        if (OS.GetCmdlineUserArgs().Contains("--calib")) { groundMat.AlbedoColor = new Color(1, 0, 1); groundMat.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded; }
        var ground = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(60, 60) }, MaterialOverride = groundMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        _worldRoot.AddChild(ground);
        foreach (var s in _soldiers) s.Free();
        _soldiers = new[]
        {
            new SoldierView(0, 0, null, 100, _worldRoot, _overlay),
            new SoldierView(1, 1, null, 100, _worldRoot, _overlay),
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
        // shots in the scripted fire scene: one every 0.25 s once the rifle is up
        if (_show == "fire" && Math.Floor((_showT - 0.6) / 0.25) > Math.Floor((_showT - dt - 0.6) / 0.25) && _showT > 0.6) _soldiers[0].Shot = true;
        if (_show is "reload" or "tactical") _soldiers[0].ReloadEmpty = _show == "reload";
        if (_follow >= 0) _target = new Vector3((float)_showCur[_follow].Pos.X, 0.9f, (float)_showCur[_follow].Pos.Y);
        UpdateCamera(1);
        for (int i = 0; i < 2; i++) _soldiers[i].Update(_showPrev[i], _showCur[i], 1, dt, _cam, false);
        if (_shots.Count > 0 && _showT >= _shots[0]) { _shots.RemoveAt(0); _shotPending = 2; }
        _hud.Text = $"{_title}   t = {_showT:0.00} s";
    }

    SoldierTruth ShowTruth(int who, double t)
    {
        var s = new SoldierTruth { Id = who, Team = who, Alive = true, Hp = 100, Chamber = true, Mag = 30 };
        double deg = Math.PI / 180;
        if (who == 1)
        {
            s.Pos = EnemyAt; s.Yaw = s.AimYaw = (Vec2.Zero - EnemyAt).Yaw;
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
            case "fire": AimAtEnemy(0.2); break;
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
