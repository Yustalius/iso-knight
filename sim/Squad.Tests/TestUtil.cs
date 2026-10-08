using Squad.Sim;

namespace Squad.Tests;

static class TestUtil
{
    /// <summary>A 20×20 m test yard with one obstacle across the middle (y = 10) and spawn zones at both ends.</summary>
    public static MapData Yard(ObstacleKind? kind, double x0 = 5, double x1 = 15)
    {
        var m = new MapData { Name = "yard", Width = 20, Height = 20 };
        if (kind is { } k) m.Obstacles.Add(Obstacle.Make(k, new Vec2(x0, 10), new Vec2(x1, 10)));
        m.Spawns.Add(new SpawnZone { Min = new Vec2(9, 2), Max = new Vec2(11, 3) });
        m.Spawns.Add(new SpawnZone { Min = new Vec2(9, 16), Max = new Vec2(11, 17) });
        return m;
    }

    public static MatchConfig Config(MapData map, int a = 1, int b = 1, ulong seed = 7)
    {
        var c = new MatchConfig { Seed = seed, Map = map };
        c.Teams.Add(new TeamSetup()); c.Teams.Add(new TeamSetup());
        for (int i = 0; i < a; i++) c.Teams[0].Members.Add(new AgentSetup { Name = "a" + i });
        for (int i = 0; i < b; i++) c.Teams[1].Members.Add(new AgentSetup { Name = "b" + i });
        return c;
    }

    public static void Run(Match m, AgentAction[] acts, double seconds)
    {
        int n = (int)Math.Round(seconds * Match.TickRate);
        for (int k = 0; k < n && !m.Over; k++) m.Step(acts);
    }

    public static AgentAction AimAt(Vec2 p, double h, Trigger t = Trigger.None) =>
        new() { Aim = AimKind.Point, AimPoint = p, AimHeight = h, Trigger = t };
}
