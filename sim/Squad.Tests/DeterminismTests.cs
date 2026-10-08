using Squad.Sim;
using Xunit;

namespace Squad.Tests;

public class DeterminismTests
{
    /// <summary>Random but reproducible actions for every soldier.</summary>
    static void RandomActions(ref Rng r, Match m, AgentAction[] acts)
    {
        for (int i = 0; i < m.Count; i++)
        {
            if (m.Tick % 6 != i % 6) continue;
            acts[i] = new AgentAction
            {
                Move = (MoveKind)r.Int(3), Target = r.Chance(0.5) ? Vec2.FromYaw(r.Range(-3, 3)) : new Vec2(r.Range(1, 19), r.Range(1, 19)),
                Mode = (MoveMode)r.Int(3), Stance = (Stance)r.Int(2),
                Aim = (AimKind)r.Int(3), AimAt = r.Int(m.Count), AimPoint = new Vec2(r.Range(0, 20), r.Range(0, 20)), AimHeight = 1.2,
                Trigger = (Trigger)r.Int(4), Reload = r.Chance(0.05)
            };
        }
    }

    static MatchConfig Cfg()
    {
        var map = TestUtil.Yard(ObstacleKind.LowWall, 3, 9);
        map.Obstacles.Add(Obstacle.Make(ObstacleKind.Bush, new Vec2(14, 8), new Vec2(16, 12)));
        map.Obstacles.Add(Obstacle.Make(ObstacleKind.HighWall, new Vec2(12, 14), new Vec2(17, 14)));
        var c = TestUtil.Config(map, 3, 3, seed: 99);
        c.Rules.RoundTime = 60;
        c.RandomizeBalance = 0.1;
        return c;
    }

    [Fact]
    public void SameSeedAndActionsGiveTheSameMatch()
    {
        ulong Run()
        {
            var m = Match.Create(Cfg());
            var acts = new AgentAction[m.Count];
            var r = new Rng(5);
            for (int k = 0; k < 1500 && !m.Over; k++) { RandomActions(ref r, m, acts); m.Step(acts); }
            return m.Hash();
        }
        Assert.Equal(Run(), Run());
    }

    [Fact]
    public void ReplayVerifiesAndDetectsTampering()
    {
        var m = Match.Create(Cfg());
        var acts = new AgentAction[m.Count];
        var r = new Rng(6);
        var rp = new Replay { Header = "{}" };
        for (int k = 0; k < 1200 && !m.Over; k++) { RandomActions(ref r, m, acts); m.Step(acts); rp.Record(m, acts); }
        rp.Finish(m);
        string path = Path.Combine(Path.GetTempPath(), $"squad-test-{Environment.ProcessId}.rpl");
        rp.Save(path);
        var loaded = Replay.Load(path);
        File.Delete(path);
        Assert.Equal(-1, loaded.Verify(Match.Create(Cfg())));
        loaded.Ticks[100][0].Target = new Vec2(1, 1);
        loaded.Ticks[100][0].Move = MoveKind.To;
        Assert.NotEqual(-1, loaded.Verify(Match.Create(Cfg())));
    }

    [Fact]
    public void ParallelMatchesDoNotInterfere()
    {
        var world = new World(Cfg().Map);
        ulong[] hashes = new ulong[8];
        Parallel.For(0, 8, k =>
        {
            var m = Match.Create(Cfg(), world);
            var acts = new AgentAction[m.Count];
            var r = new Rng(7);
            for (int t = 0; t < 600 && !m.Over; t++) { RandomActions(ref r, m, acts); m.Step(acts); }
            hashes[k] = m.Hash();
        });
        Assert.All(hashes, h => Assert.Equal(hashes[0], h));
    }

    [Fact]
    public void StepAllocatesNothing()
    {
        var m = Match.Create(Cfg());
        var acts = new AgentAction[m.Count];
        var r = new Rng(8);
        for (int k = 0; k < 300; k++) { RandomActions(ref r, m, acts); m.Step(acts); }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int k = 0; k < 600 && !m.Over; k++) { RandomActions(ref r, m, acts); m.Step(acts); }
        long after = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0, after - before);
    }
}
