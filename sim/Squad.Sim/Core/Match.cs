namespace Squad.Sim;

internal struct Soldier
{
    public int Team;
    public bool Alive;
    public double Hp;
    public Vec2 Pos, Vel;
    public double Yaw, AimYaw, AimRate, AimPitch;
    public double Crouch, Raise;
    public bool Aiming, InBush;
    public double Speed;
    // weapon
    public int Mag, Reserve, Latched, Pending;
    public bool Chamber, DryDone;
    public Trigger PrevTrigger;
    public double Cool, Focus, Bloom, Suppress;
    public bool Reloading, ReloadEmpty, ReloadOut, ReloadSeated, ReloadBolted;
    public double ReloadT;
    public double StepT;
    public MoveMode Mode;
}

internal struct Motor
{
    public Vec2 Goal;
    public bool HasGoal, Avoid;
    public int PathLen, PathIdx;
    public double RepathT, StuckT;
    public Vec2 StuckRef;
}

internal struct Bullet
{
    public bool Active;
    public int Owner, Team;
    public Vec2 Origin, Pos, Dir;
    public double OriginH, H, Slope, Traveled;
}

/// <summary>A round between two teams. Pure rules: no graphics, fixed 30 Hz tick, every random number from generators
/// stored in the match, so the same config and actions always give the same result on any machine.</summary>
public sealed partial class Match
{
    public const int TickRate = 30;
    public const double Dt = 1.0 / TickRate;
    const int MaxPath = 48;

    public readonly MatchConfig Config;
    public readonly World World;
    public readonly Balance B;
    public readonly Rules Rules;
    public readonly int Count;
    public int Tick { get; private set; }
    public double Time => Tick * Dt;
    public MatchResult Result;
    public bool Over => Result.Over;

    readonly WorldScratch _ws;
    internal readonly Soldier[] S;
    internal readonly Motor[] M;
    readonly Vec2[] _paths;
    internal readonly AgentSetup[] Setup;
    readonly int[] _teamStart = new int[3];
    internal Rng WorldRng;
    internal readonly Rng[] PercRng;
    readonly AgentStats[] _stats;
    readonly Bullet[] _bullets;
    GameEvent[] _events = new GameEvent[256];
    int _eventCount;
    readonly AgentView[] _views;
    readonly bool[] _viewFresh;
    readonly int _percEvery;
    readonly AgentAction[] _last;

    /// <summary>Build a match. The world can be shared between matches on the same map (it is immutable).</summary>
    public static Match Create(MatchConfig config, World? world = null) => new(config, world);

    Match(MatchConfig config, World? world)
    {
        if (config.Teams.Count != 2) throw new ArgumentException("A match has exactly two teams.");
        Config = config;
        Rules = config.Rules;
        WorldRng = new Rng(config.Seed, 1);
        B = config.Balance.Clone();
        B.Randomize(ref WorldRng, config.RandomizeBalance);
        World = world ?? new World(config.Map, B.Move.Radius);
        _ws = new WorldScratch(World);

        Count = config.Teams[0].Members.Count + config.Teams[1].Members.Count;
        _teamStart[0] = 0; _teamStart[1] = config.Teams[0].Members.Count; _teamStart[2] = Count;
        S = new Soldier[Count];
        M = new Motor[Count];
        _paths = new Vec2[Count * MaxPath];
        Setup = new AgentSetup[Count];
        PercRng = new Rng[Count];
        _stats = new AgentStats[Count];
        _bullets = new Bullet[Math.Max(16, Count * 8)];
        _views = new AgentView[Count];
        _viewFresh = new bool[Count];
        _last = new AgentAction[Count];
        _percEvery = Math.Max(1, (int)DMath.Round(TickRate / B.Perception.Hz));
        Contacts = new Contact[Count * Count];
        InitBoards();

        for (int t = 0; t < 2; t++)
        {
            var members = config.Teams[t].Members;
            for (int k = 0; k < members.Count; k++)
            {
                int i = _teamStart[t] + k;
                Setup[i] = members[k];
                PercRng[i] = new Rng(config.Seed ^ 0x9E3779B97F4A7C15UL, (ulong)(100 + i));
                _views[i] = new AgentView { Id = i, Team = t };
            }
        }
        Spawn();
        for (int i = 0; i < Count; i++) { UpdatePerception(i); BuildView(i); _viewFresh[i] = true; }
    }

    void Spawn()
    {
        var spawns = Config.Map.Spawns;
        var center = new Vec2(World.Width / 2, World.Height / 2);
        for (int t = 0; t < 2; t++)
        {
            var z = spawns.Count > t ? spawns[t] : new SpawnZone { Min = new Vec2(1, t == 0 ? 1 : World.Height - 3), Max = new Vec2(World.Width - 1, t == 0 ? 3 : World.Height - 1) };
            for (int i = _teamStart[t]; i < _teamStart[t + 1]; i++)
            {
                Vec2 p = z.Center;
                for (int attempt = 0; attempt < 80; attempt++)
                {
                    var q = new Vec2(WorldRng.Range(z.Min.X, z.Max.X), WorldRng.Range(z.Min.Y, z.Max.Y));
                    if (!World.Nav.FreeAt(q) || World.Overlaps(q, B.Move.Radius, _ws)) continue;
                    double minD = attempt < 60 ? 1.2 : 0.6;
                    bool close = false;
                    for (int j = _teamStart[t]; j < i; j++) if (Vec2.DistanceSq(S[j].Pos, q) < minD * minD) { close = true; break; }
                    if (close) continue;
                    p = q; break;
                }
                ref var s = ref S[i];
                s.Team = t; s.Alive = true; s.Hp = B.Damage.Hp;
                s.Pos = p; s.Yaw = s.AimYaw = (center - p).Yaw;
                s.Mag = B.Weapon.Cap; s.Chamber = true; s.Reserve = B.Weapon.ReserveMags;
                s.StepT = WorldRng.NextDouble() * B.Move.StepNoiseInterval;
                M[i].RepathT = WorldRng.NextDouble();
            }
        }
    }

    public int TeamOf(int i) => S[i].Team;
    public int TeamStart(int team) => _teamStart[team];
    public int TeamEnd(int team) => _teamStart[team + 1];
    public int TeamSize(int team) => _teamStart[team + 1] - _teamStart[team];
    public bool IsAlive(int i) => S[i].Alive;

    public int AliveCount(int team)
    {
        int n = 0;
        for (int i = _teamStart[team]; i < _teamStart[team + 1]; i++) if (S[i].Alive) n++;
        return n;
    }

    /// <summary>What soldier i knows. Rebuilt every few ticks; <see cref="ViewUpdated"/> tells when.</summary>
    public AgentView View(int i) => _views[i];
    /// <summary>The view of soldier i was rebuilt during the last Step: the moment for his bot to decide.</summary>
    public bool ViewUpdated(int i) => _viewFresh[i];
    public ReadOnlySpan<GameEvent> Events => _events.AsSpan(0, _eventCount);
    public AgentStats Stats(int i) => _stats[i];
    public AgentAction LastAction(int i) => _last[i];

    public SoldierTruth Truth(int i)
    {
        ref var s = ref S[i];
        bool cr = s.Crouch > 0.5;
        return new SoldierTruth
        {
            Id = i, Team = s.Team, Alive = s.Alive, Hp = s.Hp, Pos = s.Pos, Vel = s.Vel, Yaw = s.Yaw, AimYaw = s.AimYaw, AimPitch = s.AimPitch,
            Crouch = s.Crouch, Raise = s.Raise, Aiming = s.Aiming, Crouched = cr, InBush = s.InBush, Mag = s.Mag, Reserve = s.Reserve,
            Chamber = s.Chamber, Reloading = s.Reloading, ReloadT = s.ReloadT, Spread = Spread(i), Focus = s.Focus,
            EyeHeight = cr ? B.Body.CrouchEye : B.Body.StandEye
        };
    }

    /// <summary>Put a soldier somewhere (tests and drills set-up only: replays do not record it).</summary>
    internal void Place(int i, Vec2 pos, double yaw, Stance stance = Stance.Stand)
    {
        ref var s = ref S[i];
        s.Pos = pos; s.Vel = Vec2.Zero; s.Speed = 0; s.Yaw = s.AimYaw = yaw;
        s.Crouch = stance == Stance.Crouch ? 1 : 0;
        M[i] = default;
    }

    /// <summary>Waypoints of soldier i's current path (for debug drawing).</summary>
    public ReadOnlySpan<Vec2> Path(int i) => M[i].HasGoal ? _paths.AsSpan(i * MaxPath + M[i].PathIdx, Math.Max(0, M[i].PathLen - M[i].PathIdx)) : default;

    /// <summary>Advance one tick (1/30 s). actions[i] belongs to soldier i; dead soldiers' actions are ignored.</summary>
    public void Step(ReadOnlySpan<AgentAction> actions)
    {
        if (actions.Length < Count) throw new ArgumentException("One action per soldier.");
        _eventCount = 0;
        Array.Clear(_viewFresh);
        if (Result.Over) return;

        for (int i = 0; i < Count; i++)
        {
            _last[i] = actions[i];
            if (S[i].Alive) Act(i, actions[i]);
        }
        Separate();
        for (int i = 0; i < Count; i++) if (S[i].Alive) Weapon(i, actions[i]);
        UpdateBullets();
        for (int i = 0; i < Count; i++) if (S[i].Alive) Footsteps(i);

        Tick++;
        for (int i = 0; i < Count; i++)
        {
            if ((Tick + i) % _percEvery != 0) continue;
            if (!S[i].Alive && !_views[i].Alive) continue;
            UpdatePerception(i);
            BuildView(i);
            _viewFresh[i] = true;
        }
        CheckEnd();
    }

    void CheckEnd()
    {
        int a0 = AliveCount(0), a1 = AliveCount(1);
        if (a0 == 0 || a1 == 0)
        {
            End(a0 == 0 && a1 == 0 ? -1 : a0 == 0 ? 1 : 0, EndReason.Elimination);
            return;
        }
        if (Rules.ReachZone != null && Config.Map.TryZone(Rules.ReachZone, out var z))
        {
            for (int i = _teamStart[Rules.ReachTeam]; i < _teamStart[Rules.ReachTeam + 1]; i++)
                if (S[i].Alive && z.Contains(S[i].Pos)) { End(Rules.ReachTeam, EndReason.Objective); return; }
        }
        if (Time >= Rules.RoundTime) End(Rules.TimeoutWinner, EndReason.Timeout);
    }

    void End(int winner, EndReason reason)
    {
        Result = new MatchResult { Over = true, Winner = winner, EndTick = Tick, Reason = reason };
        Emit(new GameEvent { Type = EventType.RoundEnd, Agent = winner, Value = (int)reason });
    }

    internal void Emit(in GameEvent e)
    {
        if (_eventCount == _events.Length) Array.Resize(ref _events, _events.Length * 2);
        _events[_eventCount] = e;
        _events[_eventCount].Tick = Tick;
        _eventCount++;
    }

    /// <summary>Hash of the whole simulation state: equal hashes mean equal matches (used by replays).</summary>
    public ulong Hash()
    {
        var h = new Fnv();
        h.Add(Tick); h.Add(WorldRng.State);
        for (int i = 0; i < Count; i++)
        {
            ref var s = ref S[i];
            h.Add(s.Alive); h.Add(s.Hp); h.Add(s.Pos); h.Add(s.Vel); h.Add(s.Yaw); h.Add(s.AimYaw); h.Add(s.AimPitch);
            h.Add(s.Crouch); h.Add(s.Raise); h.Add(s.Mag); h.Add(s.Chamber); h.Add(s.Cool); h.Add(s.Focus); h.Add(s.Bloom); h.Add(s.ReloadT);
            h.Add(PercRng[i].State);
        }
        foreach (ref readonly var c in Contacts.AsSpan()) { h.Add(c.Known); h.Add(c.Pos); h.Add(c.Detect); }
        foreach (ref readonly var b in _bullets.AsSpan()) if (b.Active) { h.Add(b.Pos); h.Add(b.H); }
        return h.Value;
    }

    double MuzzleH(int i) => S[i].Crouch > 0.5 ? B.Body.CrouchMuzzle : B.Body.StandMuzzle;
    double EyeH(int i) => S[i].Crouch > 0.5 ? B.Body.CrouchEye : B.Body.StandEye;
    double[] Points(int i) => S[i].Crouch > 0.5 ? B.Body.CrouchPoints : B.Body.StandPoints;
    static bool Enemies(in Soldier a, in Soldier b) => a.Team != b.Team;
}
