using Squad.Sim;

namespace Squad.Bots;

/// <summary>Common state of the rule bots: skill, current target, a timer for the current state, hysteresis.
/// Public fields are tunable from scenarios ("params": { "holder": { "awareness": 0 } }).</summary>
public abstract class BrainBase : IBrain
{
    public abstract string Name { get; }
    public string Intent { get; protected set; } = "";
    public BotSkill Skill = new();
    protected int TargetId = -1;
    protected double StateT, LastTime;
    protected double Dt;

    public void Think(AgentView v, in Order order, ref AgentAction a, ref Rng rng)
    {
        Dt = LastTime > 0 ? v.Time - LastTime : 0;
        LastTime = v.Time;
        StateT += Dt;
        if (!v.Alive) { a = default; Intent = "dead"; return; }
        a.Trigger = Trigger.None;
        a.Reload = false;
        Decide(v, order, ref a, ref rng);
        if (BotKit.WantsReload(v) && a.Trigger == Trigger.None) a.Reload = true;
    }

    protected abstract void Decide(AgentView v, in Order o, ref AgentAction a, ref Rng rng);

    protected void Enter(string intent) { if (Intent != intent) { Intent = intent; StateT = 0; } }

    /// <summary>The visible target to fight, if any (index into view.Contacts).</summary>
    protected int Fight(AgentView v)
    {
        int t = BotKit.BestTarget(v, TargetId);
        TargetId = t >= 0 ? v.Contacts[t].Id : -1;
        return t;
    }

    Vec2 _search; bool _hasSearch; double _searchT;

    /// <summary>Go after the freshest uncleared contact; with nothing known, sweep random points of the enemy half.</summary>
    protected void Search(AgentView v, ref AgentAction a, ref Rng rng)
    {
        a.Stance = Stance.Stand;
        int k = BotKit.Freshest(v);
        if (k >= 0)
        {
            var c = v.Contacts[k];
            bool close = c.Dist < 25 && c.Age < 8;
            BotKit.MoveTo(ref a, c.Pos, close ? MoveMode.Walk : MoveMode.Run);
            if (close) BotKit.Watch(ref a, c.Pos, c.AimHeight); else a.Aim = AimKind.None;
            Enter("investigate");
            return;
        }
        _searchT -= Dt;
        if (!_hasSearch || Vec2.Distance(v.Self.Pos, _search) < 2 || _searchT <= 0)
        {
            Vec2 c0 = Vec2.Lerp(v.Map.OwnSpawn, v.Map.EnemySpawn, rng.Range(0.4, 0.95));
            _search = new Vec2(DMath.Clamp(c0.X + rng.Range(-0.4, 0.4) * v.Map.Width, 2, v.Map.Width - 2), DMath.Clamp(c0.Y, 2, v.Map.Height - 2));
            _hasSearch = true; _searchT = 15;
        }
        BotKit.MoveTo(ref a, _search, MoveMode.Walk);
        a.Aim = AimKind.None;
        Enter("search");
    }

    protected static Vec2 Objective(AgentView v, in Order o) =>
        o.HasArea ? o.Area : v.Map.HasObjective && v.Map.Attacker ? v.Map.Objective : BotKit.ThreatCenter(v);
}

/// <summary>Target practice: stands or walks a short beat, never shoots.</summary>
public sealed class DummyBrain : BrainBase
{
    public override string Name => "dummy";
    public bool Patrol = true;
    public double Beat = 4;
    Vec2 _a, _b; bool _init, _toB;

    protected override void Decide(AgentView v, in Order o, ref AgentAction a, ref Rng rng)
    {
        a.Aim = AimKind.None;
        if (!Patrol) { BotKit.Stop(ref a); Enter("idle"); return; }
        if (!_init) { _init = true; Vec2 side = (v.Map.EnemySpawn - v.Map.OwnSpawn).Normalized().Perp; _a = v.Self.Pos - side * Beat / 2; _b = v.Self.Pos + side * Beat / 2; }
        Vec2 goal = _toB ? _b : _a;
        if (Vec2.Distance(v.Self.Pos, goal) < 0.4) _toB = !_toB;
        BotKit.MoveTo(ref a, goal, MoveMode.Walk);
        Enter("patrol");
    }
}

/// <summary>Stationary gun: shoots whatever it sees, otherwise keeps the rifle on the last known enemy or the approach.</summary>
public sealed class TurretBrain : BrainBase
{
    public override string Name => "turret";
    public bool Crouch;

    protected override void Decide(AgentView v, in Order o, ref AgentAction a, ref Rng rng)
    {
        BotKit.Stop(ref a);
        a.Stance = Crouch ? Stance.Crouch : Stance.Stand;
        int t = Fight(v);
        if (t >= 0) { BotKit.Engage(v, v.Contacts[t], ref a, Skill.MinChance * 0.75); Enter("fire"); return; }
        int k = BotKit.Freshest(v);
        BotKit.Watch(ref a, k >= 0 ? v.Contacts[k].Pos : v.Map.EnemySpawn);
        Enter("watch");
    }
}

/// <summary>Aggression: runs at the nearest known enemy (or the enemy spawn) and shoots on the move. Never crouches.</summary>
public sealed class RusherBrain : BrainBase
{
    public override string Name => "rusher";
    public double EngageRange = 28;

    protected override void Decide(AgentView v, in Order o, ref AgentAction a, ref Rng rng)
    {
        a.Stance = Stance.Stand;
        int t = Fight(v);
        int k = BotKit.NearestKnown(v);
        Vec2 goal = k >= 0 ? v.Contacts[k].Pos : Objective(v, o);
        if (t >= 0 && v.Contacts[t].Dist < EngageRange)
        {
            var c = v.Contacts[t];
            BotKit.Engage(v, c, ref a, Skill.MinChance * 0.8);
            // shoot on the move while it still hits; otherwise stop for a moment so the aim settles
            bool moveOn = c.Dist > 4 && (a.Trigger != Trigger.None || v.Self.Raise < 1 || c.Dist < 9);
            if (moveOn) { BotKit.MoveTo(ref a, c.Pos, MoveMode.Walk); Enter("assault"); }
            else { BotKit.Stop(ref a); Enter("stop-shoot"); }
            return;
        }
        a.Aim = AimKind.None;
        BotKit.MoveTo(ref a, goal, MoveMode.Run);
        Enter(k >= 0 ? "charge" : "advance");
    }
}

/// <summary>Defence: takes a post in cover facing the enemy, crouches and holds an angle on the last known enemy.
/// Stands up to shoot over low cover, ducks after a burst or when exposed. Awareness is how often it checks flanks
/// and rear (0 = only ever looks forward: the flawed defender of the flanking drill).</summary>
public sealed class HolderBrain : BrainBase
{
    public override string Name => "holder";
    public double Awareness = 0.15;
    public double PostDepth = 0.35;    // where to set up, from own spawn toward the enemy (0..1)
    public double PeekTime = 1.6, DuckTime = 1.2;
    Vec2 _post, _look; bool _hasPost; double _scanT; Vec2 _scanDir; int _repost;

    protected override void Decide(AgentView v, in Order o, ref AgentAction a, ref Rng rng)
    {
        Vec2 own = v.Map.OwnSpawn, foe = v.Map.EnemySpawn;
        if (!_hasPost || (o.HasArea && o.Mode == OrderMode.Hold && Vec2.Distance(o.Area, _post) > o.Radius + 3))
        {
            Vec2 area = o.HasArea && o.Mode == OrderMode.Hold ? o.Area
                : v.Map.HasObjective && !v.Map.Attacker ? v.Map.Objective : Vec2.Lerp(own, foe, PostDepth) + (foe - own).Normalized().Perp * rng.Range(-4, 4);
            int ci = BotKit.PickCover(v, foe, area, 15, 0.8);
            _post = ci >= 0 ? v.Cover[ci].Pos : area;
            _hasPost = true;
        }
        int t = Fight(v);
        bool atPost = Vec2.Distance(v.Self.Pos, _post) < 0.6;
        if (v.Self.Hp < 40 && _repost == 0 && t >= 0)
        {
            // fall back once: a cover further from the threat
            int ci = BotKit.PickCover(v, v.Contacts[t].Pos, own, 12, 0.6);
            if (ci >= 0) { _post = v.Cover[ci].Pos; _repost = 1; }
        }
        if (!atPost && o.Push)
        {
            if (t < 0) { Search(v, ref a, ref rng); return; }
            BotKit.Stop(ref a);
            a.Stance = Stance.Stand;
            BotKit.Engage(v, v.Contacts[t], ref a, Skill.MinChance);
            Enter("engage");
            return;
        }
        if (!atPost && (t < 0 || v.Contacts[t].Dist > 10))
        {
            BotKit.MoveTo(ref a, _post, t >= 0 || BotKit.AnyThreatWithin(v, 20) ? MoveMode.Walk : MoveMode.Run);
            a.Stance = Stance.Stand;
            if (t >= 0) BotKit.Engage(v, v.Contacts[t], ref a, Skill.MinChance); else a.Aim = AimKind.None;
            Enter("to-post");
            return;
        }
        BotKit.Stop(ref a);
        if (t >= 0)
        {
            var c = v.Contacts[t];
            // peek–shoot–duck behind low cover; up close just fight
            bool duck = Intent == "duck" && StateT < DuckTime;
            if (!duck && Intent == "peek" && (StateT > PeekTime || v.ExposedStand >= 2) && c.Dist > 6) { Enter("duck"); duck = true; }
            if (duck)
            {
                a.Stance = Stance.Crouch;
                BotKit.Watch(ref a, c.Pos, c.AimHeight);
                return;
            }
            Enter("peek");
            a.Stance = Stance.Stand;   // a crouched muzzle is below a low wall's top
            BotKit.Engage(v, c, ref a, Skill.MinChance);
            return;
        }
        if (o.Push) { Search(v, ref a, ref rng); return; }
        // hold: crouched, rifle on the last known enemy or down the approach; sometimes glance around
        a.Stance = Stance.Crouch;
        int k = BotKit.Freshest(v);
        _look = k >= 0 && v.Contacts[k].Age < 12 ? v.Contacts[k].Pos : foe;
        _scanT -= Dt;
        if (_scanT <= 0 && k < 0 && rng.Chance(Awareness * 0.3))
        {
            _scanT = 1.5;
            Vec2 fwd = (_look - v.Self.Pos).Normalized();
            _scanDir = rng.Chance(0.5) ? fwd.Perp * rng.Sign() : -fwd;
        }
        if (_scanT > 0) { BotKit.Watch(ref a, v.Self.Pos + _scanDir * 10); Enter("scan"); }
        else { BotKit.Watch(ref a, _look); Enter("hold"); }
    }
}

/// <summary>The basic soldier: moves from cover to cover toward the objective, peeks, shoots, ducks, advances again.
/// Respects bounding overwatch (MayMove) from the squad tactic.</summary>
public sealed class RiflemanBrain : BrainBase
{
    public override string Name => "rifleman";
    public double Step = 9, PeekTime = 1.5, DuckTime = 1.0;
    Vec2 _cover; bool _hasCover; double _quiet;

    protected override void Decide(AgentView v, in Order o, ref AgentAction a, ref Rng rng)
    {
        int t = Fight(v);
        Vec2 goal = Objective(v, o);
        bool threats = BotKit.AnyThreatWithin(v, 30, 6);
        if (t >= 0) _quiet = 0; else _quiet += Dt;

        if (v.Self.Hp < 35 && t >= 0 && Intent != "fall-back")
        {
            int ci = BotKit.PickCover(v, v.Contacts[t].Pos, v.Map.OwnSpawn, 12, 0.6);
            if (ci >= 0) { _cover = v.Cover[ci].Pos; _hasCover = true; Enter("fall-back"); }
        }
        bool inCover = _hasCover && Vec2.Distance(v.Self.Pos, _cover) < 0.6;

        if (t >= 0)
        {
            var c = v.Contacts[t];
            // caught in the open: stop and shoot, or reach the nearest cover first
            if (!inCover && Intent != "fall-back")
            {
                if (!_hasCover || Vec2.Distance(v.Self.Pos, _cover) > 4)
                {
                    int ci = BotKit.PickCover(v, c.Pos, goal, 5, 0.1);
                    if (ci >= 0) { _cover = v.Cover[ci].Pos; _hasCover = true; }
                }
                if (_hasCover && Vec2.Distance(v.Self.Pos, _cover) < 4 && c.Dist > 8)
                {
                    BotKit.MoveTo(ref a, _cover, MoveMode.Walk);
                    a.Stance = Stance.Stand;
                    BotKit.Engage(v, c, ref a, Skill.MinChance * 1.2);
                    Enter("to-cover");
                    return;
                }
                BotKit.Stop(ref a);
                a.Stance = c.Dist > 12 ? Stance.Crouch : Stance.Stand;
                BotKit.Engage(v, c, ref a, Skill.MinChance);
                Enter("open-fight");
                return;
            }
            if (Intent == "fall-back" && !inCover)
            {
                BotKit.MoveTo(ref a, _cover, MoveMode.Run);
                a.Aim = AimKind.None;
                return;
            }
            BotKit.Stop(ref a);
            bool duck = Intent == "duck" && StateT < DuckTime;
            if (!duck && Intent == "peek" && (StateT > PeekTime || v.ExposedStand >= 2) && c.Dist > 7) { Enter("duck"); duck = true; }
            if (duck) { a.Stance = Stance.Crouch; BotKit.Watch(ref a, c.Pos, c.AimHeight); return; }
            Enter("peek");
            a.Stance = Stance.Stand;
            BotKit.Engage(v, c, ref a, Skill.MinChance);
            return;
        }

        // nobody in sight: hold for overwatch, or advance to the next cover
        if (!o.MayMove && !o.Push && o.Mode == OrderMode.Advance && inCover)
        {
            BotKit.Stop(ref a);
            a.Stance = Stance.Crouch;
            BotKit.Watch(ref a, goal);
            Enter("overwatch");
            return;
        }
        if (o.Mode == OrderMode.Regroup && o.HasArea && Vec2.Distance(v.Self.Pos, o.Area) > o.Radius)
        {
            BotKit.MoveTo(ref a, o.Area, MoveMode.Run);
            a.Aim = AimKind.None; a.Stance = Stance.Stand;
            Enter("regroup");
            return;
        }
        if (!inCover || _quiet > 1.5 || !_hasCover)
        {
            if (!_hasCover || inCover)
            {
                int ci = BotKit.PickCover(v, goal, goal, Step + 3, 0.9, minProgress: 2);
                if (ci >= 0) { _cover = v.Cover[ci].Pos; _hasCover = true; }
                else { _cover = BotKit.Toward(v.Self.Pos, goal, Step); _hasCover = true; }
            }
            BotKit.MoveTo(ref a, _cover, threats ? MoveMode.Walk : MoveMode.Run);
            a.Stance = Stance.Stand;
            int k = BotKit.Freshest(v);
            if (threats && k >= 0) BotKit.Watch(ref a, v.Contacts[k].Pos); else a.Aim = AimKind.None;
            Enter("advance");
            return;
        }
        BotKit.Stop(ref a);
        a.Stance = Stance.Crouch;
        int f = BotKit.Freshest(v);
        BotKit.Watch(ref a, f >= 0 ? v.Contacts[f].Pos : goal);
        Enter("cover");
    }
}

/// <summary>Goes wide to one side, out of sight of known enemies, then closes in from the flank.</summary>
public sealed class FlankerBrain : BrainBase
{
    public override string Name => "flanker";
    public double Width = 0.38;
    Vec2 _wp; bool _init, _turned;

    protected override void Decide(AgentView v, in Order o, ref AgentAction a, ref Rng rng)
    {
        Vec2 own = v.Map.OwnSpawn, foe = v.Map.EnemySpawn, axis = (foe - own).Normalized();
        if (!_init)
        {
            _init = true;
            double side = rng.Sign();
            Vec2 mid = Vec2.Lerp(own, foe, 0.55);
            _wp = mid + axis.Perp * (side * v.Map.Width * Width);
            _wp = new Vec2(DMath.Clamp(_wp.X, 2, v.Map.Width - 2), DMath.Clamp(_wp.Y, 2, v.Map.Height - 2));
        }
        int t = Fight(v);
        if (t >= 0 && (_turned || v.Contacts[t].Dist < 16 || v.Contacts[t].AimingAtMe))
        {
            var c = v.Contacts[t];
            BotKit.Stop(ref a);
            a.Stance = c.Dist > 14 ? Stance.Crouch : Stance.Stand;
            BotKit.Engage(v, c, ref a, Skill.MinChance);
            Enter("engage");
            return;
        }
        a.Stance = Stance.Stand;
        bool near = BotKit.AnyThreatWithin(v, 18);
        if (!_turned && Vec2.Distance(v.Self.Pos, _wp) < 2.5) _turned = true;
        if (!_turned)
        {
            BotKit.MoveTo(ref a, _wp, near ? MoveMode.Sneak : MoveMode.Run, avoid: true);
            a.Aim = AimKind.None;
            Enter("flank-out");
            return;
        }
        int k = BotKit.Freshest(v);
        Vec2 goal = k >= 0 ? v.Contacts[k].Pos : Objective(v, o);
        BotKit.MoveTo(ref a, goal, near ? MoveMode.Sneak : MoveMode.Walk, avoid: true);
        if (near && k >= 0) BotKit.Watch(ref a, v.Contacts[k].Pos); else a.Aim = AimKind.None;
        Enter("flank-in");
    }
}

/// <summary>Searches: goes to the freshest sighting or noise, clears it, moves on; sweeps the enemy half when nothing is known.</summary>
public sealed class HunterBrain : BrainBase
{
    public override string Name => "hunter";

    protected override void Decide(AgentView v, in Order o, ref AgentAction a, ref Rng rng)
    {
        int t = Fight(v);
        if (t >= 0)
        {
            var c = v.Contacts[t];
            BotKit.Engage(v, c, ref a, Skill.MinChance);
            // close in while the shots still land, otherwise stop (crouched at range) and let the aim settle
            if (c.Dist > 12 && (a.Trigger != Trigger.None || v.Self.Raise < 1)) { BotKit.MoveTo(ref a, c.Pos, MoveMode.Walk); a.Stance = Stance.Stand; }
            else { BotKit.Stop(ref a); a.Stance = c.Dist > 15 ? Stance.Crouch : Stance.Stand; }
            Enter("engage");
            return;
        }
        Search(v, ref a, ref rng);
    }
}

/// <summary>Long range: a post in its own half, single shots only when the aim has settled; relocates after a few shots.</summary>
public sealed class MarksmanBrain : BrainBase
{
    public override string Name => "marksman";
    public double PostDepth = 0.25;
    public int ShotsPerPost = 3;
    Vec2 _post; bool _hasPost; int _shots; int _lastMag = -1;

    protected override void Decide(AgentView v, in Order o, ref AgentAction a, ref Rng rng)
    {
        Vec2 own = v.Map.OwnSpawn, foe = v.Map.EnemySpawn;
        if (_lastMag >= 0 && v.Self.Mag < _lastMag) _shots += _lastMag - v.Self.Mag;
        _lastMag = v.Self.Mag;
        if (!_hasPost || _shots >= ShotsPerPost)
        {
            Vec2 area = _hasPost ? v.Self.Pos + (foe - own).Normalized().Perp * rng.Range(-8, 8) : Vec2.Lerp(own, foe, PostDepth);
            int ci = BotKit.PickCover(v, BotKit.ThreatCenter(v), area, 14, 0.4, lowOnly: true);
            _post = ci >= 0 ? v.Cover[ci].Pos : area;
            _hasPost = true; _shots = 0;
        }
        int t = Fight(v);
        if (t < 0 && o.Push) { Search(v, ref a, ref rng); return; }
        bool atPost = Vec2.Distance(v.Self.Pos, _post) < 0.6;
        if (!atPost)
        {
            BotKit.MoveTo(ref a, _post, t >= 0 ? MoveMode.Walk : MoveMode.Run);
            a.Stance = Stance.Stand;
            if (t >= 0 && v.Contacts[t].Dist < 10) BotKit.Engage(v, v.Contacts[t], ref a, Skill.MinChance); else a.Aim = AimKind.None;
            Enter("to-post");
            return;
        }
        BotKit.Stop(ref a);
        if (t >= 0)
        {
            var c = v.Contacts[t];
            a.Stance = Stance.Stand;
            a.Aim = AimKind.Contact; a.AimAt = c.Id;
            bool settled = v.Self.Focus > 0.9 && v.Self.Raise >= 1;
            a.Trigger = settled && c.HitChance >= Math.Max(0.3, Skill.MinChance * 1.5) ? Trigger.Single : c.Dist < 8 ? Trigger.Burst : Trigger.None;
            Enter("aim");
            return;
        }
        a.Stance = Stance.Crouch;
        int k = BotKit.Freshest(v);
        BotKit.Watch(ref a, k >= 0 ? v.Contacts[k].Pos : foe);
        Enter("overwatch");
    }
}

/// <summary>Keeps a distance band to the nearest threat: backs off while shooting when too close, closes in when too far.</summary>
public sealed class KiterBrain : BrainBase
{
    public override string Name => "kiter";
    public double Near = 13, Far = 24;

    protected override void Decide(AgentView v, in Order o, ref AgentAction a, ref Rng rng)
    {
        int t = Fight(v);
        int k = t >= 0 ? t : BotKit.NearestKnown(v);
        if (k < 0) { Search(v, ref a, ref rng); return; }
        var c = v.Contacts[k];
        Vec2 away = (v.Self.Pos - c.Pos).Normalized();
        if (c.Dist < Near) { a.Move = MoveKind.Dir; a.Target = away; a.Mode = MoveMode.Walk; a.Stance = Stance.Stand; Enter("back-off"); }
        else if (c.Dist > Far) { BotKit.MoveTo(ref a, c.Pos, MoveMode.Walk); a.Stance = Stance.Stand; Enter("close-in"); }
        else { BotKit.Stop(ref a); a.Stance = Stance.Crouch; Enter("hold-band"); }
        if (c.Visible) BotKit.Engage(v, c, ref a, Skill.MinChance);
        else BotKit.Watch(ref a, c.Pos, c.AimHeight);
    }
}

/// <summary>Random actions: a smoke test for the API and the dumbest opponent.</summary>
public sealed class RandomBrain : BrainBase
{
    public override string Name => "random";

    protected override void Decide(AgentView v, in Order o, ref AgentAction a, ref Rng rng)
    {
        if (rng.Chance(0.2)) { a.Move = (MoveKind)rng.Int(3); a.Target = a.Move == MoveKind.Dir ? Vec2.FromYaw(rng.Range(-3.2, 3.2)) : new Vec2(rng.Range(1, v.Map.Width - 1), rng.Range(1, v.Map.Height - 1)); }
        a.Mode = (MoveMode)rng.Int(3);
        if (rng.Chance(0.1)) a.Stance = (Stance)rng.Int(2);
        var cs = v.Contacts;
        if (cs.Length > 0 && rng.Chance(0.7)) { a.Aim = AimKind.Contact; a.AimAt = cs[rng.Int(cs.Length)].Id; a.Trigger = (Trigger)rng.Int(4); }
        else { a.Aim = AimKind.None; }
        Enter("random");
    }
}

/// <summary>No decisions: the action is set from outside (a human, a network, a test).</summary>
public sealed class ExternalBrain : IBrain
{
    public string Name => "external";
    public string Intent => "external";
    public void Think(AgentView view, in Order order, ref AgentAction action, ref Rng rng) { }
}
