using System.Diagnostics;
using Squad.Bots;
using Squad.Sim;

namespace Squad.Tools;

/// <summary>Speed of the simulation alone (actions replayed) and with rule bots deciding, per team size, on one core;
/// plus bytes allocated per tick (must be 0 for the simulation).</summary>
static class Bench
{
    public static void Run(Args o)
    {
        int matches = (int)o.Num("matches", 6);
        Console.WriteLine("size    ticks   sim µs/tick  bots+sim µs/tick  sim ticks/s   alloc B/tick (sim)");
        foreach (var (a, b) in new[] { (1, 1), (4, 4), (7, 5), (10, 10) })
        {
            var sc = Scenario.Parse($$"""
                { "name": "bench", "map": { "gen": { "symmetric": true } }, "rules": { "roundTime": 120 }, "tactics": true,
                  "teams": [ { "size": {{a}}, "mix": { "rifleman": 2, "flanker": 1, "holder": 1, "hunter": 1 } },
                             { "size": {{b}}, "mix": { "rifleman": 2, "rusher": 1, "holder": 1, "marksman": 1 } } ] }
                """);
            long ticks = 0; double simSec = 0, allSec = 0; long alloc = 0;
            for (int k = 0; k < matches + 1; k++)
            {
                var built = sc.Build((ulong)(k + 1));
                var world = new World(built.Config.Map);
                var runner = built.CreateRunner(world);
                var log = new List<AgentAction[]>();
                var sw = Stopwatch.StartNew();
                while (!runner.Match.Over) { runner.Step(); log.Add((AgentAction[])runner.Actions.Clone()); }
                double all = sw.Elapsed.TotalSeconds;

                var m = Match.Create(built.Config, world);
                long before = GC.GetAllocatedBytesForCurrentThread();
                sw.Restart();
                foreach (var acts in log) m.Step(acts);
                double sim = sw.Elapsed.TotalSeconds;
                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                if (k == 0) continue;   // warm-up (JIT)
                ticks += log.Count; simSec += sim; allSec += all; alloc += allocated;
            }
            Console.WriteLine($"{a}v{b,-4} {ticks,7} {simSec / ticks * 1e6,12:0.0} {allSec / ticks * 1e6,17:0.0} {ticks / simSec,12:0} {(double)alloc / ticks,15:0.0}");
        }
    }
}
