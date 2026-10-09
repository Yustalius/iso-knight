using Squad.Sim;
using Xunit;

namespace Squad.Tests;

public class CombatTests
{
    static (Match m, AgentAction[] acts) Duel(ObstacleKind? kind, Vec2 target, Stance stance, Action<MatchConfig>? tweak = null)
    {
        var cfg = TestUtil.Config(TestUtil.Yard(kind));
        tweak?.Invoke(cfg);
        var m = Match.Create(cfg);
        m.Place(0, new Vec2(10, 3), 0);
        m.Place(1, target, DMath.Pi, stance);
        return (m, new AgentAction[m.Count]);
    }

    [Fact]
    public void AimedShotsInTheOpenHit()
    {
        var (m, acts) = Duel(null, new Vec2(10, 13), Stance.Stand);
        m.B.Damage.Torso[0] = m.B.Damage.Torso[1] = 1;   // keep him alive to count
        m.B.Damage.Head[0] = m.B.Damage.Head[1] = 1; m.B.Damage.Legs[0] = m.B.Damage.Legs[1] = 1;
        acts[0] = TestUtil.AimAt(new Vec2(10, 13), 1.2);
        TestUtil.Run(m, acts, 2);
        for (int k = 0; k < 20; k++)
        {
            acts[0].Trigger = Trigger.Single; m.Step(acts);
            acts[0].Trigger = Trigger.None; TestUtil.Run(m, acts, 0.3);
        }
        var st = m.Stats(0);
        Assert.Equal(20, st.Shots);
        Assert.True(st.Hits >= 18, $"hits {st.Hits}");
    }

    [Fact]
    public void CrouchedBehindLowWallCannotBeShotNorShoot()
    {
        var (m, acts) = Duel(ObstacleKind.LowWall, new Vec2(10, 10.6), Stance.Crouch);
        acts[1] = new AgentAction { Stance = Stance.Crouch };
        acts[0] = TestUtil.AimAt(new Vec2(10, 10.6), 0.65, Trigger.Auto);
        TestUtil.Run(m, acts, 3);
        Assert.True(m.Stats(0).Shots > 20);
        Assert.Equal(0, m.Stats(0).Hits);

        // and his own muzzle (0.9 m) is under the wall top (1.1 m): his shots hit the wall
        acts[0] = new AgentAction();
        acts[1] = new AgentAction { Stance = Stance.Crouch, Aim = AimKind.Point, AimPoint = new Vec2(10, 3), AimHeight = 1.2, Trigger = Trigger.Auto };
        TestUtil.Run(m, acts, 3);
        Assert.True(m.Stats(1).Shots > 20);
        Assert.Equal(0, m.Stats(1).Hits);
    }

    [Fact]
    public void StandingBehindLowWallIsHitAboveIt()
    {
        var (m, acts) = Duel(ObstacleKind.LowWall, new Vec2(10, 10.6), Stance.Stand);
        m.B.Damage.Head[0] = m.B.Damage.Head[1] = 1; m.B.Damage.Torso[0] = m.B.Damage.Torso[1] = 1;
        acts[0] = TestUtil.AimAt(new Vec2(10, 10.6), 1.45);
        TestUtil.Run(m, acts, 2);
        int legs = 0;
        for (int k = 0; k < 15; k++)
        {
            acts[0].Trigger = Trigger.Single; m.Step(acts);
            foreach (var e in m.Events) if (e.Type == EventType.Hit && e.Zone == HitZone.Legs) legs++;
            acts[0].Trigger = Trigger.None;
            for (int q = 0; q < 9; q++) { m.Step(acts); foreach (var e in m.Events) if (e.Type == EventType.Hit && e.Zone == HitZone.Legs) legs++; }
        }
        Assert.True(m.Stats(0).Hits >= 10, $"hits {m.Stats(0).Hits}");
        Assert.Equal(0, legs);
    }

    [Fact]
    public void AlliesStopBulletsWithoutTakingDamage()
    {
        foreach (bool ff in new[] { false, true })
        {
            var cfg = TestUtil.Config(TestUtil.Yard(null), a: 2, b: 1);
            cfg.Rules.FriendlyFire = ff;
            var m = Match.Create(cfg);
            m.Place(0, new Vec2(10, 3), 0);
            m.Place(1, new Vec2(10, 7), 0);
            m.Place(2, new Vec2(10, 14), DMath.Pi);
            var acts = new AgentAction[3];
            acts[0] = TestUtil.AimAt(new Vec2(10, 14), 1.2);
            TestUtil.Run(m, acts, 1.5);
            acts[0].Trigger = Trigger.Burst; m.Step(acts); acts[0].Trigger = Trigger.None;
            TestUtil.Run(m, acts, 0.5);
            Assert.Equal(3, m.Stats(0).Shots);
            Assert.Equal(100, m.Truth(2).Hp);
            if (ff) Assert.True(m.Truth(1).Hp < 100); else Assert.Equal(100, m.Truth(1).Hp);
        }
    }

    [Fact]
    public void AutoFireRateBurstAndReloadTimings()
    {
        var (m, acts) = Duel(ObstacleKind.HighWall, new Vec2(10, 16), Stance.Stand);
        acts[0] = TestUtil.AimAt(new Vec2(10, 10), 1.2);
        TestUtil.Run(m, acts, 1);
        acts[0].Trigger = Trigger.Auto;
        TestUtil.Run(m, acts, 1);
        Assert.InRange(m.Stats(0).Shots, 13, 14);    // 800 rpm

        acts[0].Trigger = Trigger.None; TestUtil.Run(m, acts, 0.2);
        int before = m.Stats(0).Shots;
        acts[0].Trigger = Trigger.Burst; m.Step(acts); acts[0].Trigger = Trigger.None;
        TestUtil.Run(m, acts, 1);
        Assert.Equal(before + 3, m.Stats(0).Shots);

        // empty the rifle, then a full reload: 1.72 s, chamber + 29 in the magazine
        acts[0].Trigger = Trigger.Auto; TestUtil.Run(m, acts, 4);
        Assert.False(m.Truth(0).Chamber);
        acts[0] = new AgentAction { Reload = true };
        m.Step(acts);
        acts[0] = new AgentAction();
        int ticks = 1;
        while (m.Truth(0).Reloading) { m.Step(acts); ticks++; }
        Assert.InRange(ticks * Match.Dt, 1.70, 1.76);
        Assert.True(m.Truth(0).Chamber);
        Assert.Equal(29, m.Truth(0).Mag);
    }

    [Fact]
    public void SpreadSettlesToBaseAndCrouchTightens()
    {
        var (m, acts) = Duel(null, new Vec2(10, 16), Stance.Stand);
        acts[0] = TestUtil.AimAt(new Vec2(10, 16), 1.2);
        TestUtil.Run(m, acts, 0.1);
        double early = m.Truth(0).Spread;
        TestUtil.Run(m, acts, 6);
        double settled = m.Truth(0).Spread;
        Assert.True(early > 0.05);
        Assert.InRange(settled, m.B.Weapon.Base, m.B.Weapon.Base * 1.05);
        acts[0].Stance = Stance.Crouch;
        TestUtil.Run(m, acts, 6);
        Assert.InRange(m.Truth(0).Spread, m.B.Weapon.Base * 0.74, m.B.Weapon.Base * 0.8);
    }

    [Fact]
    public void HeadshotKillsAndEndsTheRound()
    {
        var (m, acts) = Duel(null, new Vec2(10, 8), Stance.Stand);
        acts[0] = TestUtil.AimAt(new Vec2(10, 8), 1.65);
        TestUtil.Run(m, acts, 4);
        for (int k = 0; k < 30 && !m.Over; k++) { acts[0].Trigger = Trigger.Single; m.Step(acts); acts[0].Trigger = Trigger.None; TestUtil.Run(m, acts, 0.2); }
        Assert.True(m.Over);
        Assert.Equal(0, m.Result.Winner);
        Assert.Equal(EndReason.Elimination, m.Result.Reason);
        Assert.False(m.Truth(1).Alive);
    }
}
