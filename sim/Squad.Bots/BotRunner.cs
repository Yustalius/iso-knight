using Squad.Sim;

namespace Squad.Bots;

/// <summary>A rule-based squad leader (level 1 of docs/bots.md: the "strategy" layer is a script). Every two seconds it
/// sets each member's order: the common objective, regrouping when the squad is strung out, and bounding overwatch for
/// riflemen in pairs (one moves while the other covers). Reads only its members' views.</summary>
public sealed class SquadTactic
{
    public double Interval = 2, SpreadLimit = 18, BoundTime = 3, StallTime = 20;
    double _next, _bound, _lastAction;
    bool _flip;
    readonly Dictionary<int, (double hp, int mag)> _last = new();

    public void Update(Match m, int team, IBrain?[] brains, Order[] orders)
    {
        if (m.Time < _next) return;
        _next = m.Time + Interval;
        int a = m.TeamStart(team), b = m.TeamEnd(team);

        // shared picture: the freshest uncleared contacts of all members
        Vec2 sum = Vec2.Zero, centroid = Vec2.Zero; int n = 0, alive = 0;
        bool anyVisible = false;
        AgentView? any = null;
        for (int i = a; i < b; i++)
        {
            if (!m.IsAlive(i)) continue;
            var v = m.View(i);
            any ??= v;
            centroid += v.Self.Pos; alive++;
            foreach (ref readonly var c in v.Contacts)
            {
                if (c.Cleared || c.Age > 12) continue;
                sum += c.Pos; n++;
                anyVisible |= c.Visible;
            }
        }
        if (alive == 0 || any is null) return;
        centroid /= alive;
        // stalemate guard: nobody on the team has fired or been hit for a while → everyone pushes.
        // Glimpses do not count, or two holders that see each other for a second would sit forever.
        bool fighting = false;
        for (int i = a; i < b; i++)
        {
            if (!m.IsAlive(i)) continue;
            var self = m.View(i).Self;
            if (_last.TryGetValue(i, out var prev) && (self.Hp < prev.hp || (self.Mag < prev.mag && !self.Reloading))) fighting = true;
            _last[i] = (self.Hp, self.Mag);
        }
        if (fighting) _lastAction = m.Time;
        bool push = m.Time - _lastAction > StallTime;
        Vec2 objective = any.Map.HasObjective && any.Map.Attacker ? any.Map.Objective : n > 0 ? sum / n : any.Map.EnemySpawn;

        double spread = 0;
        for (int i = a; i < b; i++) if (m.IsAlive(i)) spread = Math.Max(spread, Vec2.Distance(m.View(i).Self.Pos, centroid));
        bool regroup = !anyVisible && spread > Math.Max(SpreadLimit, any.Map.Width * 0.45);

        if (m.Time >= _bound) { _bound = m.Time + BoundTime; _flip = !_flip; }
        int rifle = 0;
        for (int i = a; i < b; i++)
        {
            ref var o = ref orders[i];
            o.HasArea = true; o.Area = objective; o.Radius = 6;
            o.Mode = OrderMode.Advance; o.MayMove = true; o.Push = push;
            if (regroup && m.IsAlive(i) && Vec2.Distance(m.View(i).Self.Pos, centroid) > SpreadLimit * 0.6) { o.Mode = OrderMode.Regroup; o.Area = centroid; o.Radius = 5; }
            if (brains[i] is RiflemanBrain) { o.MayMove = ((rifle & 1) == 0) == _flip || alive < 2; rifle++; }
        }
    }
}

/// <summary>Runs a match with bots: asks each brain for an action when its soldier's view is rebuilt, holds it between
/// decisions (presses of Single/Burst only on the decision tick), runs the squad tactics, steps the match.</summary>
public sealed class BotRunner
{
    public readonly Match Match;
    public readonly IBrain?[] Brains;
    public readonly Order[] Orders;
    public readonly AgentAction[] Actions;
    readonly AgentAction[] _held;
    readonly Rng[] _rng;
    readonly int[] _thinkEvery, _views;
    readonly SquadTactic?[] _tactics;

    public BotRunner(Match match, IBrain?[] brains, bool tactics = true, BotSkill?[]? skills = null)
    {
        Match = match;
        Brains = brains;
        int n = match.Count;
        Orders = new Order[n];
        Actions = new AgentAction[n];
        _held = new AgentAction[n];
        _rng = new Rng[n];
        _thinkEvery = new int[n];
        _views = new int[n];
        for (int i = 0; i < n; i++)
        {
            _rng[i] = new Rng(match.Config.Seed ^ 0xB0757UL, (ulong)(1000 + i));
            _thinkEvery[i] = Math.Max(1, skills?[i]?.ThinkEvery ?? (brains[i] as BrainBase)?.Skill.ThinkEvery ?? 1);
            Orders[i].MayMove = true;
        }
        _tactics = tactics ? new[] { new SquadTactic(), new SquadTactic() } : new SquadTactic?[2];
    }

    /// <summary>Set the action of a soldier controlled from outside (human or network); it holds until changed.</summary>
    public void SetExternal(int i, in AgentAction a) => _held[i] = a;

    public void Step()
    {
        var m = Match;
        for (int t = 0; t < 2; t++) _tactics[t]?.Update(m, t, Brains, Orders);
        for (int i = 0; i < m.Count; i++)
        {
            var brain = Brains[i];
            bool decide = brain != null && m.IsAlive(i) && m.ViewUpdated(i) && (_views[i]++ % _thinkEvery[i] == 0);
            if (decide)
            {
                brain!.Think(m.View(i), Orders[i], ref _held[i], ref _rng[i]);
                Actions[i] = _held[i];
            }
            else
            {
                Actions[i] = _held[i];
                // a press is a single tick; held Auto keeps firing
                if (brain is not ExternalBrain && brain != null && Actions[i].Trigger is Trigger.Single or Trigger.Burst) Actions[i].Trigger = Trigger.None;
                Actions[i].Reload = false;
            }
        }
        m.Step(Actions);
    }

    public void RunToEnd(int maxTicks = int.MaxValue)
    {
        for (int k = 0; k < maxTicks && !Match.Over; k++) Step();
    }
}
