using System.Text.Json.Nodes;
using Squad.Sim;

namespace Squad.Bots;

/// <summary>A training or test situation as data: map (file or generator), rules, balance, and per team the size,
/// the mix of bot strategies, the skill and perception options. Build(seed) gives a ready match with its bots,
/// the same for the same seed.</summary>
public sealed class Scenario
{
    public string Name = "scenario";
    public string? MapFile;
    public JsonObject? MapGen;
    public JsonObject? Rules, Balance;
    public double RandomizeBalance;
    public bool Tactics = true;
    public readonly TeamSpec[] Teams = { new(), new() };
    /// <summary>When set, each match draws both team sizes from [min, max].</summary>
    public int RandomSizeMin, RandomSizeMax;
    /// <summary>Per-strategy field overrides, e.g. { "holder": { "awareness": 0 } }.</summary>
    public JsonObject? Params;
    /// <summary>Reward weights for training ("reward" section); the rules and bots ignore it.</summary>
    public JsonObject? Reward;
    public string BaseDir = ".";

    public sealed class TeamSpec
    {
        public int Size = 1;
        public List<(string brain, double weight)> Mix = new();
        public string Skill = "normal";
        public bool Omniscient;
        public JsonObject? Params;
    }

    public static readonly string[] BrainNames = { "dummy", "turret", "rusher", "holder", "rifleman", "flanker", "hunter", "marksman", "kiter", "random", "external" };

    public static IBrain MakeBrain(string name) => name.ToLowerInvariant() switch
    {
        "dummy" => new DummyBrain(),
        "turret" => new TurretBrain(),
        "rusher" => new RusherBrain(),
        "holder" => new HolderBrain(),
        "rifleman" => new RiflemanBrain(),
        "flanker" => new FlankerBrain(),
        "hunter" => new HunterBrain(),
        "marksman" => new MarksmanBrain(),
        "kiter" => new KiterBrain(),
        "random" => new RandomBrain(),
        "external" => new ExternalBrain(),
        _ => throw new ArgumentException($"Unknown brain '{name}'. Known: {string.Join(", ", BrainNames)}.")
    };

    public static Scenario Load(string path)
    {
        var s = Parse(File.ReadAllText(path));
        s.BaseDir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        return s;
    }

    public static Scenario Parse(string json)
    {
        var o = JsonNode.Parse(json)!.AsObject();
        var s = new Scenario { Name = (string?)o["name"] ?? "scenario" };
        if (o["map"] is JsonObject map)
        {
            s.MapFile = (string?)map["file"];
            s.MapGen = map["gen"] as JsonObject;
        }
        s.Rules = o["rules"] as JsonObject;
        s.Balance = o["balance"] as JsonObject;
        s.RandomizeBalance = SimJson.Num(o, "randomizeBalance", 0);
        s.Tactics = (bool?)o["tactics"] ?? true;
        s.Params = o["params"] as JsonObject;
        s.Reward = o["reward"] as JsonObject;
        if (o["randomSizes"] is JsonArray rs) { s.RandomSizeMin = (int)SimJson.D(rs[0]); s.RandomSizeMax = (int)SimJson.D(rs[1]); }
        var teams = o["teams"]!.AsArray();
        for (int t = 0; t < 2; t++)
        {
            var to = teams[t]!.AsObject();
            var spec = s.Teams[t];
            spec.Size = (int)SimJson.Num(to, "size", 1);
            spec.Skill = (string?)to["skill"] ?? "normal";
            spec.Omniscient = (bool?)to["omniscient"] ?? false;
            spec.Params = to["params"] as JsonObject;
            if (to["mix"] is JsonObject mix) foreach (var (k, v) in mix) spec.Mix.Add((k, SimJson.D(v)));
            else if (to["brain"] is JsonNode b) spec.Mix.Add(((string)b!, 1));
            else spec.Mix.Add(("rifleman", 1));
        }
        return s;
    }

    public sealed class Built
    {
        public required MatchConfig Config;
        public required IBrain?[] Brains;
        public required BotSkill[] Skills;
        public required bool Tactics;
        public Match CreateMatch(World? world = null) => Match.Create(Config, world);
        public BotRunner CreateRunner(World? world = null) => new(CreateMatch(world), Brains, Tactics, Skills);
    }

    /// <summary>Match config and bots for one seed. Team sizes, the strategy of each member and generated maps all follow from the seed.</summary>
    /// <summary>Team sizes a build with this seed will have (before swapping).</summary>
    public (int a, int b) SizesFor(ulong seed, int sizeA = 0, int sizeB = 0)
    {
        var r = new Rng(seed, 0x5CE);
        return DrawSizes(ref r, sizeA, sizeB);
    }

    (int a, int b) DrawSizes(ref Rng r, int sizeA, int sizeB)
    {
        int a = Teams[0].Size, b = Teams[1].Size;
        if (RandomSizeMax > 0) { a = r.Range(RandomSizeMin, RandomSizeMax + 1); b = r.Range(RandomSizeMin, RandomSizeMax + 1); }
        if (sizeA > 0) a = sizeA;
        if (sizeB > 0) b = sizeB;
        return (a, b);
    }

    /// <summary>The map of this scenario: the file, or a generated one for these team sizes.</summary>
    public MapData MapFor(ulong mapSeed, int sizeA, int sizeB)
    {
        if (MapFile != null) return SimJson.ReadMap(File.ReadAllText(Path.Combine(BaseDir, MapFile)));
        var opt = new MapGenOptions { TeamA = sizeA, TeamB = sizeB };
        SimJson.Apply(opt, MapGen);
        return Sim.MapGen.Generate(mapSeed * 7919 + 13, opt);
    }

    /// <param name="swap">Team spec 0 plays on side 1 and the other way round (for fair arena runs on asymmetric maps).</param>
    /// <param name="map">Use this map instead of loading or generating one (map pools in training).</param>
    public Built Build(ulong seed, int sizeA = 0, int sizeB = 0, bool swap = false, MapData? map = null)
    {
        var r = new Rng(seed, 0x5CE);
        var (a0, b0) = DrawSizes(ref r, sizeA, sizeB);
        int[] sizes = { a0, b0 };
        var specs = swap ? new[] { Teams[1], Teams[0] } : Teams;
        if (swap) (sizes[0], sizes[1]) = (sizes[1], sizes[0]);

        var cfg = new MatchConfig { Seed = seed, RandomizeBalance = RandomizeBalance };
        SimJson.Apply(cfg.Rules, Rules);
        SimJson.Apply(cfg.Balance, Balance);
        cfg.Map = map ?? MapFor(seed, sizes[0], sizes[1]);

        var brains = new List<IBrain?>();
        var skills = new List<BotSkill>();
        for (int t = 0; t < 2; t++)
        {
            var spec = specs[t];
            var team = new TeamSetup();
            cfg.Teams.Add(team);
            var names = Compose(spec, sizes[t], ref r);
            var skill = BotSkill.Get(spec.Skill);
            for (int k = 0; k < names.Count; k++)
            {
                var brain = MakeBrain(names[k]);
                if (brain is BrainBase bb)
                {
                    bb.Skill = skill;
                    if (Params?[names[k]] is JsonObject p) SimJson.Apply(bb, p);
                    if (spec.Params?[names[k]] is JsonObject tp) SimJson.Apply(bb, tp);
                }
                var setup = new AgentSetup { Name = $"{names[k]}{t}.{k}", Omniscient = spec.Omniscient };
                skill.ApplyTo(setup);
                team.Members.Add(setup);
                brains.Add(brain);
                skills.Add(skill);
            }
        }
        return new Built { Config = cfg, Brains = brains.ToArray(), Skills = skills.ToArray(), Tactics = Tactics };
    }

    /// <summary>Strategy per member: exact counts when the mix adds up to the size, otherwise drawn by weight.</summary>
    static List<string> Compose(TeamSpec spec, int size, ref Rng r)
    {
        var list = new List<string>();
        double total = spec.Mix.Sum(m => m.weight);
        bool exact = spec.Mix.All(m => m.weight == Math.Floor(m.weight)) && (int)total == size;
        if (exact)
        {
            foreach (var (b, w) in spec.Mix) for (int k = 0; k < (int)w; k++) list.Add(b);
            return list;
        }
        // proportional share first, the remainder drawn by weight
        foreach (var (b, w) in spec.Mix) for (int k = 0; k < (int)Math.Floor(w / total * size); k++) list.Add(b);
        while (list.Count < size)
        {
            double x = r.NextDouble() * total;
            foreach (var (b, w) in spec.Mix) { x -= w; if (x < 0) { list.Add(b); break; } }
            if (x >= 0) list.Add(spec.Mix[^1].brain);
        }
        return list;
    }
}
