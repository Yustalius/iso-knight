using System.Collections.Concurrent;
using System.Diagnostics;
using Squad.Bots;
using Squad.Sim;

namespace Squad.Tools;

/// <summary>Many headless matches in parallel and their statistics: who wins, how, how fast, which strategies kill and die.</summary>
static class Arena
{
    record struct AgentRow(string Brain, int Spec, AgentStats Stats);
    record struct MatchRow(ulong Seed, bool Swapped, int Winner, EndReason Reason, double Time, int SizeA, int SizeB, AgentRow[] Agents);

    /// <summary>Run matches; Winner is reported in scenario-spec terms (0 = first team in the file) even when sides were swapped.</summary>
    static List<MatchRow> RunMatches(Scenario sc, int n, int threads, ulong seed0, bool swap, int sizeA, int sizeB, out double seconds, out long ticks)
    {
        var rows = new ConcurrentBag<MatchRow>();
        long tickSum = 0;
        var sw = Stopwatch.StartNew();
        Parallel.For(0, n, new ParallelOptions { MaxDegreeOfParallelism = threads }, k =>
        {
            ulong seed = seed0 + (ulong)k;
            bool sw2 = swap && (k & 1) == 1;
            var built = sc.Build(seed, sw2 ? sizeB : sizeA, sw2 ? sizeA : sizeB, sw2);
            var runner = built.CreateRunner();
            runner.RunToEnd();
            var m = runner.Match;
            Interlocked.Add(ref tickSum, m.Tick);
            int specOf(int team) => sw2 ? 1 - team : team;
            var agents = new AgentRow[m.Count];
            for (int i = 0; i < m.Count; i++) agents[i] = new AgentRow(built.Brains[i]?.Name ?? "?", specOf(m.TeamOf(i)), m.Stats(i));
            int w = m.Result.Winner < 0 ? -1 : specOf(m.Result.Winner);
            int sa = m.TeamSize(sw2 ? 1 : 0), sb = m.TeamSize(sw2 ? 0 : 1);
            rows.Add(new MatchRow(seed, sw2, w, m.Result.Reason, m.Time, sa, sb, agents));
        });
        seconds = sw.Elapsed.TotalSeconds;
        ticks = tickSum;
        return rows.ToList();
    }

    /// <summary>Score of spec 0: wins + draws/2 over matches, with a 95% Wilson interval.</summary>
    static (double score, double lo, double hi) Score(IEnumerable<MatchRow> rows)
    {
        int n = 0; double s = 0;
        foreach (var r in rows) { n++; s += r.Winner == 0 ? 1 : r.Winner < 0 ? 0.5 : 0; }
        if (n == 0) return (0, 0, 0);
        double p = s / n, z = 1.96, d = 1 + z * z / n;
        double c = (p + z * z / (2 * n)) / d, h = z * Math.Sqrt(p * (1 - p) / n + z * z / (4.0 * n * n)) / d;
        return (p, Math.Max(0, c - h), Math.Min(1, c + h));
    }

    public static void Run(Args o)
    {
        var sc = Scenario.Load(o.Req("scenario"));
        int n = (int)o.Num("matches", 200), threads = (int)o.Num("threads", Environment.ProcessorCount);
        var (sa, sb) = o.Has("sizes") ? Args.Sizes(o.Get("sizes", "")) : (0, 0);
        var rows = RunMatches(sc, n, threads, o.U64("seed", 1), o.Flag("swap"), sa, sb, out double secs, out long ticks);

        int w0 = rows.Count(r => r.Winner == 0), w1 = rows.Count(r => r.Winner == 1), dr = rows.Count(r => r.Winner < 0);
        var (p, lo, hi) = Score(rows);
        string Spec(int t) => string.Join("+", sc.Teams[t].Mix.Select(m => m.brain)) + $" ({sc.Teams[t].Skill})";
        Console.WriteLine($"scenario {sc.Name}: {rows.Count} matches in {secs:0.0} s ({ticks / secs / 1000:0} k ticks/s, {ticks / 30.0 / secs:0}× real time)");
        Console.WriteLine($"  A = {Spec(0)}");
        Console.WriteLine($"  B = {Spec(1)}");
        Console.WriteLine($"  A wins {w0}, B wins {w1}, draws {dr}  →  A score {p:P0} [{lo:P0}–{hi:P0}]");
        Console.WriteLine($"  ends: elimination {rows.Count(r => r.Reason == EndReason.Elimination)}, timeout {rows.Count(r => r.Reason == EndReason.Timeout)}, objective {rows.Count(r => r.Reason == EndReason.Objective)};  mean length {rows.Average(r => r.Time):0.0} s");

        Console.WriteLine("  per strategy:            n   kills  deaths   K/D   acc   dmg/agent  teamkills");
        foreach (var g in rows.SelectMany(r => r.Agents).GroupBy(a => (a.Spec, a.Brain)).OrderBy(g => g.Key.Spec).ThenBy(g => g.Key.Brain))
        {
            int cnt = g.Count(); double k = g.Sum(a => a.Stats.Kills), d = g.Sum(a => a.Stats.Deaths), sh = g.Sum(a => a.Stats.Shots), hi2 = g.Sum(a => a.Stats.Hits);
            Console.WriteLine($"    {(g.Key.Spec == 0 ? "A" : "B")} {g.Key.Brain,-12} {cnt,6} {k / cnt,7:0.00} {d / cnt,7:0.00} {(d > 0 ? k / d : k),5:0.00} {(sh > 0 ? hi2 / sh : 0),5:P0} {g.Sum(a => a.Stats.DamageDealt) / cnt,10:0} {g.Sum(a => a.Stats.TeamKills),10}");
        }
        if (o.Flag("list"))
            foreach (var r in rows.OrderBy(r => r.Seed))
                Console.WriteLine($"    seed {r.Seed,-5} {(r.Swapped ? "swapped" : "       ")} {r.SizeA}v{r.SizeB}  {(r.Winner < 0 ? "draw" : r.Winner == 0 ? "A" : "B"),-4} {r.Reason,-11} {r.Time,6:0.0} s");
        var bySize = rows.GroupBy(r => $"{r.SizeA}v{r.SizeB}").ToList();
        if (bySize.Count > 1)
        {
            Console.WriteLine("  A score by size:");
            foreach (var g in bySize.OrderBy(g => g.First().SizeA - g.First().SizeB).ThenBy(g => g.Key))
            {
                var (sp, sl, sh) = Score(g);
                Console.WriteLine($"    {g.Key,-6} {g.Count(),4} matches  {sp,5:P0} [{sl:P0}–{sh:P0}]");
            }
        }
    }

    /// <summary>Every strategy against every other in equal teams: rows beat columns by this score.</summary>
    public static void Crosstab(Args o)
    {
        var brains = o.Get("brains", "rusher,holder,rifleman,flanker,hunter,marksman,kiter").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int size = (int)o.Num("size", 3), n = (int)o.Num("matches", 40), threads = (int)o.Num("threads", Environment.ProcessorCount);
        string skill = o.Get("skill", "normal");
        var score = new double[brains.Length, brains.Length];
        var sw = Stopwatch.StartNew();
        long ticks = 0;
        for (int i = 0; i < brains.Length; i++)
            for (int j = i + 1; j < brains.Length; j++)
            {
                var sc = Scenario.Parse($$"""
                    { "name": "{{brains[i]}}-vs-{{brains[j]}}", "map": { "gen": { "symmetric": true } }, "rules": { "roundTime": 150 }, "tactics": true,
                      "teams": [ { "size": {{size}}, "brain": "{{brains[i]}}", "skill": "{{skill}}" }, { "size": {{size}}, "brain": "{{brains[j]}}", "skill": "{{skill}}" } ] }
                    """);
                var rows = RunMatches(sc, n, threads, o.U64("seed", 1), true, 0, 0, out _, out long t);
                ticks += t;
                var (p, _, _) = Score(rows);
                score[i, j] = p; score[j, i] = 1 - p;
            }
        Console.WriteLine($"crosstab {size}v{size}, {n} matches per pair, skill {skill}: row's score against column ({sw.Elapsed.TotalSeconds:0} s, {ticks / sw.Elapsed.TotalSeconds / 1000:0} k ticks/s)");
        Console.Write("              ");
        foreach (var b in brains) Console.Write($"{b,9}");
        Console.WriteLine("      mean");
        for (int i = 0; i < brains.Length; i++)
        {
            Console.Write($"  {brains[i],-12}");
            double sum = 0;
            for (int j = 0; j < brains.Length; j++)
            {
                if (i == j) { Console.Write($"{"—",9}"); continue; }
                Console.Write($"{score[i, j],9:P0}"); sum += score[i, j];
            }
            Console.WriteLine($"{sum / (brains.Length - 1),10:P0}");
        }
    }
}
