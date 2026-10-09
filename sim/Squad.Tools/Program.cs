using System.Diagnostics;
using System.Globalization;
using SkiaSharp;
using Squad.Bots;
using Squad.Sim;
using Squad.Tools;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

if (args.Length == 0) { Usage(); return 1; }
var opts = Args.Parse(args.Skip(1));
try
{
    switch (args[0])
    {
        case "mapgen": MapGenCmd(opts); break;
        case "render": RenderCmd(opts); break;
        case "arena": Arena.Run(opts); break;
        case "crosstab": Arena.Crosstab(opts); break;
        case "bench": Bench.Run(opts); break;
        case "replay": ReplayCmd(args.Length > 1 ? args[1] : "", Args.Parse(args.Skip(2))); break;
        default: Usage(); return 1;
    }
}
catch (Exception e) when (e is ArgumentException or FileNotFoundException or FormatException or InvalidDataException)
{
    Console.Error.WriteLine("error: " + e.Message);
    return 2;
}
return 0;

static void Usage() => Console.WriteLine("""
    Squad.Tools — headless simulation tools
      mapgen   [--players 7v5] [--seed 1] [--asym] [--out out/map.png] [--json out/map.json]
      render   --scenario F [--seed 1] [--from 0] [--to 60] [--every 5] [--cols 4] [--scale 12] [--focus 0|1|-1] [--out out/x.png]
      render   --replay F.rpl [--every 3] ...      (frames of a recorded match, e.g. a trained network's)
      arena    --scenario F [--matches 200] [--threads N] [--seed 1] [--swap] [--sizes 4v6]
      crosstab --brains rifleman,holder,rusher [--size 3] [--matches 40] [--threads N] [--skill normal]
      bench    [--matches 6]
      replay record --scenario F [--seed 1] --out F.rpl
      replay verify F.rpl
    """);

static void MapGenCmd(Args o)
{
    var (a, b) = Args.Sizes(o.Get("players", "4v4"));
    ulong seed = o.U64("seed", 1);
    var map = MapGen.Generate(seed, new MapGenOptions { TeamA = a, TeamB = b, Symmetric = !o.Flag("asym") });
    var w = new World(map);
    Console.WriteLine($"{map.Name}: {map.Width}×{map.Height} m, {map.Obstacles.Count} obstacles, {w.Cover.Length} cover points");
    if (o.Has("json")) { File.WriteAllText(o.Get("json", ""), SimJson.WriteMap(map)); }
    var cfg = new MatchConfig { Map = map, Seed = seed };
    cfg.Teams.Add(new TeamSetup()); cfg.Teams.Add(new TeamSetup());
    for (int i = 0; i < a; i++) cfg.Teams[0].Members.Add(new AgentSetup());
    for (int i = 0; i < b; i++) cfg.Teams[1].Members.Add(new AgentSetup());
    var m = Match.Create(cfg, w);
    var r = new Renderer(w, o.Num("scale", Math.Clamp(900 / Math.Max(map.Width, map.Height), 8, 24))) { CoverPoints = true, Cones = false };
    using var bmp = r.Frame(m, null, Array.Empty<GameEvent>(), $"{map.Name}  {a}v{b}");
    string outPath = o.Get("out", "out/map.png");
    Renderer.SavePng(bmp, outPath);
    Console.WriteLine("wrote " + outPath);
}

static void RenderCmd(Args o)
{
    if (o.Has("replay")) { RenderReplay(o); return; }
    var sc = Scenario.Load(o.Req("scenario"));
    ulong seed = o.U64("seed", 1);
    var (sa, sb) = o.Has("sizes") ? Args.Sizes(o.Get("sizes", "")) : (0, 0);
    var built = sc.Build(seed, sa, sb);
    var runner = built.CreateRunner();
    var m = runner.Match;
    double from = o.Num("from", 0), to = o.Num("to", 60), every = o.Num("every", 5);
    int cols = (int)o.Num("cols", 4);
    double scale = o.Num("scale", Math.Clamp(520 / Math.Max(m.World.Width, m.World.Height), 6, 20));
    var r = new Renderer(m.World, scale) { FocusTeam = (int)o.Num("focus", 0) };
    var frames = new List<SKBitmap>();
    var recent = new List<GameEvent>();
    string? framesDir = o.Has("frames") ? o.Get("frames", "") : null;  // one numbered PNG per frame, for ffmpeg
    int frameCount = 0;
    void Shoot()
    {
        var f = r.Frame(m, runner, recent, $"{sc.Name}  seed {seed}");
        if (framesDir == null) { frames.Add(f); }
        else { Renderer.SavePng(f, Path.Combine(framesDir, $"{frameCount:00000}.png")); f.Dispose(); }
        frameCount++;
    }
    double next = from;
    while (true)
    {
        if (m.Time + 1e-9 >= next)
        {
            Shoot();
            next += every;
            if (next > to + 1e-9) break;
        }
        if (m.Over) { Shoot(); break; }
        runner.Step();
        recent.RemoveAll(e => m.Tick - e.Tick > 10);
        foreach (var e in m.Events) if (e.Type is EventType.Shot or EventType.Hit) recent.Add(e);
    }
    string outPath = framesDir ?? o.Get("out", $"out/{sc.Name}-{seed}.png");
    if (framesDir == null)
    {
        using var sheet = Renderer.Sheet(frames, Math.Min(cols, frames.Count));
        Renderer.SavePng(sheet, outPath);
    }
    foreach (var f in frames) f.Dispose();
    Console.WriteLine($"{frameCount} frames → {outPath}; result: {(m.Over ? $"winner {m.Result.Winner} ({m.Result.Reason}) at {m.Time:0.0}s" : "running")}");
}

/// <summary>Frames of a recorded match (e.g. a trained network's episode from train/eval.py --record).</summary>
static void RenderReplay(Args o)
{
    string path = o.Req("replay");
    var rp = Replay.Load(path);
    var m = Match.Create(SimJson.ReadConfig(rp.Header));
    double from = o.Num("from", 0), to = o.Num("to", 1e9), every = o.Num("every", 3);
    int cols = (int)o.Num("cols", 4);
    double scale = o.Num("scale", Math.Clamp(520 / Math.Max(m.World.Width, m.World.Height), 6, 20));
    var r = new Renderer(m.World, scale) { FocusTeam = (int)o.Num("focus", 0) };
    var frames = new List<SKBitmap>();
    var recent = new List<GameEvent>();
    string title = Path.GetFileName(path) + "  (team 0: network)";
    double next = from;
    foreach (var acts in rp.Ticks.Prepend(null))
    {
        if (acts != null)
        {
            m.Step(acts);
            recent.RemoveAll(e => m.Tick - e.Tick > 10);
            foreach (var e in m.Events) if (e.Type is EventType.Shot or EventType.Hit) recent.Add(e);
        }
        if ((m.Time + 1e-9 >= next && next <= to) || (m.Over && acts == rp.Ticks[^1]))
        {
            frames.Add(r.Frame(m, null, recent, title));
            next += every;
        }
    }
    using var sheet = Renderer.Sheet(frames, Math.Min(cols, frames.Count));
    string outPath = o.Get("out", Path.ChangeExtension(path, ".png"));
    Renderer.SavePng(sheet, outPath);
    foreach (var f in frames) f.Dispose();
    Console.WriteLine($"{frames.Count} frames → {outPath}; result: winner {m.Result.Winner} ({m.Result.Reason}) at {m.Time:0.0}s");
}

static void ReplayCmd(string sub, Args o)
{
    if (sub == "record")
    {
        var sc = Scenario.Load(o.Req("scenario"));
        ulong seed = o.U64("seed", 1);
        var built = sc.Build(seed);
        var runner = built.CreateRunner();
        var rp = new Replay { Header = SimJson.WriteConfig(built.Config) };
        while (!runner.Match.Over) { runner.Step(); rp.Record(runner.Match, runner.Actions); }
        rp.Finish(runner.Match);
        string path = o.Req("out");
        rp.Save(path);
        Console.WriteLine($"recorded {rp.Ticks.Count} ticks, winner {runner.Match.Result.Winner}, hash {rp.FinalHash:x16} → {path}");
    }
    else if (sub == "verify")
    {
        string path = o.Positional.FirstOrDefault() ?? o.Req("file");
        var rp = Replay.Load(path);
        var m = Match.Create(SimJson.ReadConfig(rp.Header));
        var sw = Stopwatch.StartNew();
        int bad = rp.Verify(m);
        Console.WriteLine(bad < 0 ? $"OK: {rp.Ticks.Count} ticks reproduced exactly ({sw.ElapsedMilliseconds} ms), hash {m.Hash():x16}" : $"DIVERGED at tick {bad}");
        if (bad >= 0) Environment.Exit(3);
    }
    else throw new ArgumentException("replay record|verify");
}
