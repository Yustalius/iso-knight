using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Squad.Bots;
using Squad.Sim;

namespace Squad.Train;

/// <summary>Reward weights, the "reward" section of a training scenario. The outcome is the real goal;
/// the hints (damage, kills, deaths) are multiplied by the shaping factor that the trainer anneals to zero.</summary>
public sealed class RewardSpec
{
    public double Win = 1, Loss = -1, Draw = 0;
    public double DamageDealt = 0.5, DamageTaken = 0.5;   // per 100 HP
    public double Kill = 0.3, Death = 0.3;
    public double TimePenalty;                            // per second, not shaped
}

/// <summary>Settings of a batch of training matches (JSON, see train/configs).</summary>
public sealed class EnvConfig
{
    public List<(string file, double weight)> Scenarios = new();
    public int Envs = 16;
    public ulong Seed = 1;
    /// <summary>"team0": the learner plays team 0, bots the rest. "all": every soldier (self-play).</summary>
    public string Learner = "team0";
    public int DecisionTicks = 3;
    public int Threads;
    public double Shaping = 1;
    /// <summary>Generated maps are drawn from this many seeds per scenario and size, so worlds can be cached.</summary>
    public int MapPool = 128;

    public static EnvConfig Parse(string json, string baseDir)
    {
        var o = JsonNode.Parse(json)!.AsObject();
        var c = new EnvConfig();
        foreach (var n in o["scenarios"]!.AsArray())
        {
            if (n is JsonValue) { c.Scenarios.Add((Resolve((string)n!, baseDir), 1)); continue; }
            var so = n!.AsObject();
            c.Scenarios.Add((Resolve((string)so["file"]!, baseDir), SimJson.Num(so, "weight", 1)));
        }
        c.Envs = (int)SimJson.Num(o, "envs", c.Envs);
        c.Seed = (ulong)SimJson.Num(o, "seed", c.Seed);
        c.Learner = (string?)o["learner"] ?? c.Learner;
        c.DecisionTicks = (int)SimJson.Num(o, "decisionTicks", c.DecisionTicks);
        c.Threads = (int)SimJson.Num(o, "threads", 0);
        c.Shaping = SimJson.Num(o, "shaping", 1);
        c.MapPool = (int)SimJson.Num(o, "mapPool", c.MapPool);
        if (c.Scenarios.Count == 0) throw new FormatException("No scenarios.");
        if (c.Learner is not ("team0" or "all")) throw new FormatException("learner is team0 or all.");
        return c;
    }

    static string Resolve(string p, string baseDir) => Path.IsPathRooted(p) ? p : Path.GetFullPath(Path.Combine(baseDir, p));
}

/// <summary>Many matches stepped together for training. The learner's soldiers take actions from outside
/// (multi-discrete, see <see cref="Act"/>); everyone else is a rule bot run here, so opponents cost the trainer nothing.
/// One step = one decision = DecisionTicks ticks (10 Hz). Finished matches restart at once (the observation after a
/// done is the first of the next episode). Matches run in parallel on all cores; results do not depend on that.</summary>
public sealed unsafe class VecEnv
{
    public const int InfoSize = 8;   // ended, outcome (+1/0/−1), seconds, kills, deaths, damage dealt, damage taken, scenario
    public readonly EnvConfig Config;
    public readonly int Envs, MaxLearners;
    public int Slots => Envs * MaxLearners;
    public double Shaping;

    readonly Scenario[] _sc;
    readonly RewardSpec[] _rw;
    readonly double _weightSum;
    readonly Env[] _env;
    readonly ConcurrentDictionary<(int, ulong, int, int), Lazy<(MapData map, World world)>> _maps = new();
    static readonly object ScenarioLock = new();

    sealed class Env
    {
        public int Index, Episode, Scenario;
        public Rng Rng;
        public BotRunner Runner = null!;
        public Match M => Runner.Match;
        public readonly int[] Learners;
        public readonly bool[] Alive;
        public readonly AgentStats[] Prev;
        public readonly AgentAction[] Held;
        public Replay? Rec;
        public string? RecPath, PendingRecord;
        public Env(int n) { Learners = new int[n]; Alive = new bool[n]; Prev = new AgentStats[n]; Held = new AgentAction[n]; }
    }

    public VecEnv(EnvConfig cfg)
    {
        Config = cfg;
        Shaping = cfg.Shaping;
        Envs = Math.Max(1, cfg.Envs);
        _sc = cfg.Scenarios.Select(s => Scenario.Load(s.file)).ToArray();
        _rw = _sc.Select(s => { var r = new RewardSpec(); SimJson.Apply(r, s.Reward); return r; }).ToArray();
        _weightSum = cfg.Scenarios.Sum(s => s.weight);
        int max = 1;
        foreach (var s in _sc)
        {
            int a = s.RandomSizeMax > 0 ? s.RandomSizeMax : s.Teams[0].Size, b = s.RandomSizeMax > 0 ? s.RandomSizeMax : s.Teams[1].Size;
            max = Math.Max(max, cfg.Learner == "all" ? a + b : a);
        }
        MaxLearners = max;
        _env = new Env[Envs];
        var root = new Rng(cfg.Seed, 0x7EA1);
        for (int e = 0; e < Envs; e++) _env[e] = new Env(MaxLearners) { Index = e, Rng = root.Fork((ulong)e) };
    }

    ParallelOptions Par => new() { MaxDegreeOfParallelism = Config.Threads > 0 ? Config.Threads : Environment.ProcessorCount };

    /// <summary>Record the next episode of env e to a replay file (written when it ends).</summary>
    public void RecordNext(int e, string path) => _env[e].PendingRecord = path;

    // ---------- reset ----------

    public void Reset(float* obs, byte* masks, byte* alive)
    {
        nint o = (nint)obs, m = (nint)masks, a = (nint)alive;
        Parallel.For(0, Envs, Par, e =>
        {
            NewEpisode(_env[e]);
            Write(_env[e], (float*)o, (byte*)m, (byte*)a);
        });
    }

    void NewEpisode(Env env)
    {
        env.Episode++;
        ulong seed = (ulong)env.Rng.NextU32() << 32 | env.Rng.NextU32();
        double x = env.Rng.NextDouble() * _weightSum;
        int si = 0;
        for (; si < _sc.Length - 1; si++) { x -= Config.Scenarios[si].weight; if (x < 0) break; }
        env.Scenario = si;
        var sc = _sc[si];

        Scenario.Built built;
        World world;
        lock (ScenarioLock)
        {
            var (a, b) = sc.SizesFor(seed);
            ulong mapSeed = sc.MapFile != null ? 0 : seed % (ulong)Math.Max(1, Config.MapPool) + 1;
            var cached = _maps.GetOrAdd((si, mapSeed, a, b), k => new Lazy<(MapData, World)>(() =>
            {
                var map = sc.MapFor(mapSeed == 0 ? seed : mapSeed, k.Item3, k.Item4);
                return (map, new World(map));
            })).Value;
            built = sc.Build(seed, map: cached.map);
            world = cached.world;
        }
        var brains = (IBrain?[])built.Brains.Clone();
        var match = Match.Create(built.Config, world);
        Array.Fill(env.Learners, -1);
        int n = 0;
        int from = 0, to = Config.Learner == "all" ? match.Count : match.TeamEnd(0);
        for (int i = from; i < to && n < MaxLearners; i++) { brains[i] = new ExternalBrain(); env.Learners[n++] = i; }
        env.Runner = new BotRunner(match, brains, built.Tactics, built.Skills);
        for (int k = 0; k < MaxLearners; k++) { env.Alive[k] = env.Learners[k] >= 0; env.Prev[k] = default; env.Held[k] = default; }
        if (env.PendingRecord != null)
        {
            env.Rec = new Replay { Header = SimJson.WriteConfig(built.Config) };
            env.RecPath = env.PendingRecord;
            env.PendingRecord = null;
        }
    }

    // ---------- step ----------

    public void Step(int* actions, float* obs, byte* masks, float* rewards, byte* dones, byte* alive, float* info)
    {
        nint ac = (nint)actions, o = (nint)obs, m = (nint)masks, r = (nint)rewards, d = (nint)dones, a = (nint)alive, inf = (nint)info;
        Parallel.For(0, Envs, Par, e => StepEnv(_env[e], (int*)ac, (float*)o, (byte*)m, (float*)r, (byte*)d, (byte*)a, (float*)inf));
    }

    void StepEnv(Env env, int* actions, float* obs, byte* masks, float* rewards, byte* dones, byte* alive, float* info)
    {
        var m = env.M;
        var runner = env.Runner;
        int baseSlot = env.Index * MaxLearners;
        new Span<float>(info + env.Index * InfoSize, InfoSize).Clear();

        for (int k = 0; k < MaxLearners; k++)
        {
            int id = env.Learners[k];
            if (id < 0 || !env.Alive[k]) continue;
            var heads = new ReadOnlySpan<int>(actions + (baseSlot + k) * Act.NumHeads, Act.NumHeads);
            env.Held[k] = Codec.Decode(m.View(id), runner.Orders[id], heads);
            runner.SetExternal(id, env.Held[k]);
        }
        for (int t = 0; t < Config.DecisionTicks && !m.Over; t++)
        {
            runner.Step();
            env.Rec?.Record(m, runner.Actions);
            if (t == 0)
                for (int k = 0; k < MaxLearners; k++)
                {
                    int id = env.Learners[k];
                    if (id < 0) continue;
                    // a press lasts one tick, a reload request too; held Auto keeps firing
                    if (env.Held[k].Trigger is Trigger.Single or Trigger.Burst) env.Held[k].Trigger = Trigger.None;
                    env.Held[k].Reload = false;
                    runner.SetExternal(id, env.Held[k]);
                }
        }

        // rewards: shaped hints from stat deltas, the outcome at the end
        var w = _rw[env.Scenario];
        double dt = Config.DecisionTicks * Match.Dt;
        for (int k = 0; k < MaxLearners; k++)
        {
            int slot = baseSlot + k, id = env.Learners[k];
            rewards[slot] = 0; dones[slot] = 0;
            if (id < 0 || !env.Alive[k]) continue;
            var st = m.Stats(id);
            var pv = env.Prev[k];
            double rew = Shaping * ((st.DamageDealt - pv.DamageDealt) / 100 * w.DamageDealt - (st.DamageTaken - pv.DamageTaken) / 100 * w.DamageTaken
                                    + (st.Kills - pv.Kills) * w.Kill - (st.Deaths - pv.Deaths) * w.Death)
                         - w.TimePenalty * dt;
            env.Prev[k] = st;
            bool died = !m.IsAlive(id);
            if (m.Over)
            {
                int win = m.Result.Winner;
                rew += win < 0 ? w.Draw : win == m.TeamOf(id) ? w.Win : w.Loss;
                dones[slot] = 1;
            }
            else if (died) dones[slot] = 1;
            if (died) env.Alive[k] = false;
            rewards[slot] = (float)rew;
        }

        if (m.Over)
        {
            float* inf = info + env.Index * InfoSize;
            int team = env.Learners[0] >= 0 ? m.TeamOf(env.Learners[0]) : 0;
            inf[0] = 1;
            inf[1] = m.Result.Winner < 0 ? 0 : m.Result.Winner == team ? 1 : -1;
            inf[2] = (float)m.Time;
            for (int k = 0; k < MaxLearners; k++)
            {
                int id = env.Learners[k];
                if (id < 0) continue;
                var st = m.Stats(id);
                inf[3] += st.Kills; inf[4] += st.Deaths; inf[5] += (float)st.DamageDealt; inf[6] += (float)st.DamageTaken;
            }
            inf[7] = env.Scenario;
            if (env.Rec != null)
            {
                env.Rec.Finish(m);
                env.Rec.Save(env.RecPath!);
                env.Rec = null; env.RecPath = null;
            }
            NewEpisode(env);
        }
        Write(env, obs, masks, alive);
    }

    void Write(Env env, float* obs, byte* masks, byte* alive)
    {
        var m = env.M;
        for (int k = 0; k < MaxLearners; k++)
        {
            int slot = env.Index * MaxLearners + k, id = env.Learners[k];
            var o = new Span<float>(obs + (long)slot * Obs.Size, Obs.Size);
            var mk = new Span<byte>(masks + (long)slot * Act.MaskSize, Act.MaskSize);
            bool live = id >= 0 && env.Alive[k] && m.IsAlive(id);
            alive[slot] = (byte)(live ? 1 : 0);
            if (!live) { o.Clear(); Codec.DeadMask(mk); continue; }
            Codec.Encode(m.View(id), env.Runner.Orders[id], m.Rules.RoundTime, o, mk);
        }
    }

    // ---------- managed convenience (tests, tools) ----------

    public void Reset(float[] obs, byte[] masks, byte[] alive)
    {
        fixed (float* o = obs) fixed (byte* m = masks) fixed (byte* a = alive) Reset(o, m, a);
    }

    public void Step(int[] actions, float[] obs, byte[] masks, float[] rewards, byte[] dones, byte[] alive, float[] info)
    {
        fixed (int* ac = actions) fixed (float* o = obs) fixed (byte* m = masks) fixed (float* r = rewards)
        fixed (byte* d = dones) fixed (byte* a = alive) fixed (float* inf = info)
            Step(ac, o, m, r, d, a, inf);
    }

    /// <summary>The match of env e (tests and debugging).</summary>
    public Match MatchOf(int e) => _env[e].M;
    public int LearnerId(int e, int k) => _env[e].Learners[k];
}
