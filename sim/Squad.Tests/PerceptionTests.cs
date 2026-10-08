using Squad.Sim;
using Xunit;

namespace Squad.Tests;

public class PerceptionTests
{
    static ulong ViewHash(AgentView v)
    {
        var h = new Fnv();
        h.Add(v.Self.Pos); h.Add(v.Self.Hp); h.Add(v.ExposedStand); h.Add(v.ExposedCrouch);
        foreach (var c in v.Contacts) { h.Add(c.Id); h.Add(c.Pos); h.Add(c.Uncertainty); h.Add(c.Visible); h.Add(c.Detection); }
        foreach (var a in v.Allies) { h.Add(a.Id); h.Add(a.Pos); }
        foreach (var c in v.Cover) { h.Add(c.Index); h.Add(c.ThreatsStand); h.Add(c.ThreatsCrouch); }
        return h.Value;
    }

    /// <summary>An enemy hidden behind a wall must not change anything the observer gets, wherever he stands.</summary>
    [Fact]
    public void HiddenEnemyDoesNotLeakIntoTheView()
    {
        var hashes = new List<ulong>();
        foreach (double x in new[] { 3.0, 8.0, 13.0, 17.0 })
        {
            var m = Match.Create(TestUtil.Config(TestUtil.Yard(ObstacleKind.HighWall, -1, 21)));
            m.Place(0, new Vec2(10, 4), 0);
            m.Place(1, new Vec2(x, 15), DMath.Pi);
            var acts = new AgentAction[2];
            acts[0] = TestUtil.AimAt(new Vec2(10, 15), 1.2);
            TestUtil.Run(m, acts, 3);
            hashes.Add(ViewHash(m.View(0)));
            Assert.Equal(0, m.View(0).Contacts.Length);
        }
        Assert.All(hashes, h => Assert.Equal(hashes[0], h));
    }

    [Fact]
    public void SeesEnemyInFrontNotBehind()
    {
        var m = Match.Create(TestUtil.Config(TestUtil.Yard(null)));
        m.Place(0, new Vec2(10, 4), 0);          // facing +Y
        m.Place(1, new Vec2(10, 12), DMath.Pi);
        var acts = new AgentAction[2];
        TestUtil.Run(m, acts, 1);
        var v = m.View(0);
        Assert.Equal(1, v.Contacts.Length);
        Assert.True(v.Contacts[0].Visible);
        Assert.InRange(Vec2.Distance(v.Contacts[0].Pos, new Vec2(10, 12)), 0, 0.6);

        var m2 = Match.Create(TestUtil.Config(TestUtil.Yard(null)));
        m2.Place(0, new Vec2(10, 4), DMath.Pi);   // facing away
        m2.Place(1, new Vec2(10, 12), DMath.Pi);
        TestUtil.Run(m2, acts, 1);
        Assert.Equal(0, m2.View(0).Contacts.Length);
    }

    [Fact]
    public void DetectionTakesLongerForAStillCrouchedTargetInABush()
    {
        double TimeToSee(bool hidden)
        {
            var map = TestUtil.Yard(null);
            if (hidden) map.Obstacles.Add(Obstacle.Make(ObstacleKind.Bush, new Vec2(10, 20), new Vec2(10, 20), 1.2));
            var m = Match.Create(TestUtil.Config(map));
            m.Place(0, new Vec2(10, 1), 0);
            m.Place(1, new Vec2(10, 19.5), DMath.Pi, hidden ? Stance.Crouch : Stance.Stand);
            var acts = new AgentAction[2];
            if (hidden) acts[1].Stance = Stance.Crouch;
            for (int k = 0; k < 30 * 20; k++)
            {
                m.Step(acts);
                if (m.View(0).Contacts.Length > 0 && m.View(0).Contacts[0].Visible) return m.Time;
            }
            return double.PositiveInfinity;
        }
        double open = TimeToSee(false), bush = TimeToSee(true);
        Assert.True(open < 3, $"open {open}");
        Assert.True(bush > open * 3, $"bush {bush} open {open}");
    }

    [Fact]
    public void RunningIsHeardThroughAWallWalkingFarAwayIsNot()
    {
        var m = Match.Create(TestUtil.Config(TestUtil.Yard(ObstacleKind.HighWall, -1, 21)));
        m.Place(0, new Vec2(10, 8), 0);
        m.Place(1, new Vec2(7, 11.5), 0);
        var acts = new AgentAction[2];
        acts[1] = new AgentAction { Move = MoveKind.Dir, Target = new Vec2(1, 0), Mode = MoveMode.Run };
        TestUtil.Run(m, acts, 1.5);
        var v = m.View(0);
        Assert.Equal(1, v.Contacts.Length);
        Assert.Equal(ContactSource.Hear, v.Contacts[0].Source);
        Assert.False(v.Contacts[0].Visible);

        // walking 8 m away in the open, behind the observer: footsteps carry 4 m
        var m2 = Match.Create(TestUtil.Config(TestUtil.Yard(null)));
        m2.Place(0, new Vec2(10, 12), 0);
        m2.Place(1, new Vec2(6, 5), 0);
        acts[1] = new AgentAction { Move = MoveKind.Dir, Target = new Vec2(1, 0) };
        TestUtil.Run(m2, acts, 1.5);
        Assert.Equal(0, m2.View(0).Contacts.Length);
    }

    [Fact]
    public void ContactIsClearedWhenTheSpotIsEmpty()
    {
        var m = Match.Create(TestUtil.Config(TestUtil.Yard(null)));
        m.Place(0, new Vec2(10, 4), 0);
        m.Place(1, new Vec2(10, 9), DMath.Pi);
        var acts = new AgentAction[2];
        TestUtil.Run(m, acts, 1);
        Assert.True(m.View(0).Contacts[0].Visible);
        // he slips away behind the observer, who walks to where he was
        m.Place(1, new Vec2(2, 2), 0);
        acts[0] = new AgentAction { Move = MoveKind.To, Target = new Vec2(10, 8.5) };
        TestUtil.Run(m, acts, 4);
        var c = m.View(0).Contacts;
        Assert.True(c.Length == 0 || c[0].Cleared || c[0].Source == ContactSource.Hear);
    }

    [Fact]
    public void TeammateSightingsArriveOverTheRadioWithDelay()
    {
        var map = TestUtil.Yard(ObstacleKind.HighWall, 0, 12);
        var m = Match.Create(TestUtil.Config(map, a: 2, b: 1));
        m.Place(0, new Vec2(15, 4), 0);        // sees past the wall end
        m.Place(1, new Vec2(4, 4), 0);         // behind the wall
        m.Place(2, new Vec2(15, 15), DMath.Pi);
        var acts = new AgentAction[3];
        acts[2] = new AgentAction { Stance = Stance.Crouch };
        int seenTick = -1, toldTick = -1;
        for (int k = 0; k < 90; k++)
        {
            m.Step(acts);
            var v0 = m.View(0).Contacts; var v1 = m.View(1).Contacts;
            if (seenTick < 0 && v0.Length > 0 && v0[0].Visible) seenTick = m.Tick;
            if (toldTick < 0 && v1.Length > 0 && v1[0].Source == ContactSource.Team) toldTick = m.Tick;
        }
        Assert.True(seenTick > 0 && toldTick > 0);
        Assert.True(toldTick - seenTick >= 15, $"seen {seenTick} told {toldTick}");
    }
}
