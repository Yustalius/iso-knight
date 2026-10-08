using Squad.Bots;
using Squad.Sim;
using Xunit;

namespace Squad.Tests;

public class BotTests
{
    static string Root => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));

    static double Score(string brainA, string brainB, int size, int matches)
    {
        var sc = Scenario.Parse($$"""
            { "name": "t", "map": { "gen": { "symmetric": true } }, "rules": { "roundTime": 150 },
              "teams": [ { "size": {{size}}, "brain": "{{brainA}}" }, { "size": {{size}}, "brain": "{{brainB}}" } ] }
            """);
        double s = 0;
        for (int k = 0; k < matches; k++)
        {
            bool swap = (k & 1) == 1;
            var r = sc.Build((ulong)(k + 1), swap: swap).CreateRunner();
            r.RunToEnd();
            int w = r.Match.Result.Winner;
            if (w < 0) s += 0.5; else if ((w == 0) != swap) s += 1;
        }
        return s / matches;
    }

    [Fact]
    public void RusherAlwaysBeatsDummies() => Assert.Equal(1.0, Score("rusher", "dummy", 2, 10));

    [Fact]
    public void HoldersBeatRushersOnMapsWithCover() => Assert.True(Score("holder", "rusher", 3, 30) > 0.5);

    [Fact]
    public void EveryScenarioBuildsAndRuns()
    {
        var files = Directory.GetFiles(Path.Combine(Root, "scenarios"), "*.json");
        Assert.True(files.Length >= 11);
        foreach (var f in files)
        {
            var sc = Scenario.Load(f);
            var r = sc.Build(3).CreateRunner();
            r.RunToEnd(30 * 30);
            Assert.True(r.Match.Tick > 0, f);
        }
    }

    /// <summary>No bot keeps a far-away destination for 10 s without moving (stuck on geometry or in a doorway crowd).</summary>
    [Fact]
    public void BotsDoNotGetStuck()
    {
        var sc = Scenario.Load(Path.Combine(Root, "scenarios", "big-10v10.json"));
        int stuck = 0, windows = 0;
        for (ulong seed = 1; seed <= 6; seed++)
        {
            var r = sc.Build(seed).CreateRunner();
            var m = r.Match;
            var since = new int[m.Count];
            var start = new Vec2[m.Count];
            while (!m.Over && m.Tick < 30 * 120)
            {
                r.Step();
                for (int i = 0; i < m.Count; i++)
                {
                    var a = m.LastAction(i);
                    var p = m.Truth(i).Pos;
                    bool going = m.IsAlive(i) && a.Move == MoveKind.To && Vec2.Distance(p, a.Target) > 2;
                    if (!going) { since[i] = 0; continue; }
                    if (since[i] == 0) start[i] = p;
                    if (++since[i] == 300)
                    {
                        windows++;
                        if (Vec2.Distance(p, start[i]) < 0.5) stuck++;
                        since[i] = 0;
                    }
                }
            }
        }
        Assert.True(windows >= 30, $"windows {windows}");
        Assert.True(stuck <= windows / 30, $"stuck {stuck} of {windows}");
    }

    [Fact]
    public void ReachingTheZoneWinsTheRound()
    {
        var map = TestUtil.Yard(null);
        map.Zones.Add(new Zone { Name = "goal", Center = new Vec2(3, 18), Radius = 1.5 });
        var cfg = TestUtil.Config(map);
        cfg.Rules.ReachZone = "goal"; cfg.Rules.ReachTeam = 0; cfg.Rules.TimeoutWinner = 1;
        var m = Match.Create(cfg);
        m.Place(1, new Vec2(18, 2), 0);   // facing away
        var acts = new AgentAction[2];
        acts[0] = new AgentAction { Move = MoveKind.To, Target = new Vec2(3, 18), Mode = MoveMode.Run };
        TestUtil.Run(m, acts, 20);
        Assert.True(m.Over);
        Assert.Equal(EndReason.Objective, m.Result.Reason);
        Assert.Equal(0, m.Result.Winner);
    }

    [Fact]
    public void BotMatchesAreDeterministicAndReplayable()
    {
        var sc = Scenario.Load(Path.Combine(Root, "scenarios", "team-4v4-mixed.json"));
        var built = sc.Build(9);
        var r = built.CreateRunner();
        var rp = new Replay { Header = SimJson.WriteConfig(built.Config) };
        while (!r.Match.Over) { r.Step(); rp.Record(r.Match, r.Actions); }
        rp.Finish(r.Match);
        var again = sc.Build(9).CreateRunner();
        again.RunToEnd();
        Assert.Equal(r.Match.Hash(), again.Match.Hash());
        Assert.Equal(-1, rp.Verify(Match.Create(SimJson.ReadConfig(rp.Header))));
    }
}
