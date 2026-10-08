using Squad.Sim;
using Xunit;

namespace Squad.Tests;

public class WorldTests
{
    static readonly Vec2 Eye = new(10, 3), Behind = new(10, 10.6);
    static readonly BodyBalance Body = new();

    static double Frac(World w, double[] pts)
    {
        var s = new WorldScratch(w);
        double f = 0;
        foreach (var h in pts) f += w.Visibility(Eye, Body.StandEye, Behind, h, s);
        return f / pts.Length;
    }

    [Fact]
    public void LowWallHidesCrouchedAndShowsHeadOfStanding()
    {
        var w = new World(TestUtil.Yard(ObstacleKind.LowWall));
        Assert.Equal(2.0 / 3, Frac(w, Body.StandPoints), 6);
        Assert.Equal(0, Frac(w, Body.CrouchPoints));
    }

    [Fact]
    public void HighWallHidesEverything()
    {
        var w = new World(TestUtil.Yard(ObstacleKind.HighWall));
        Assert.Equal(0, Frac(w, Body.StandPoints));
    }

    [Fact]
    public void BushConcealsButBulletsPass()
    {
        var w = new World(TestUtil.Yard(ObstacleKind.Bush));
        double f = Frac(w, Body.StandPoints);
        Assert.InRange(f, 0.0001, 0.5);
        var s = new WorldScratch(w);
        Assert.Equal(-1, w.TraceBullet(Eye, 1.45, Behind - Eye, 0, s, out _));
        Assert.False(w.SolidBetween(Eye, 1.65, Behind, 1.0, s));
    }

    [Fact]
    public void OpenGroundIsFullyVisible()
    {
        var w = new World(TestUtil.Yard(null));
        Assert.Equal(1, Frac(w, Body.StandPoints));
    }

    [Fact]
    public void ThinWallStopsBulletsAtAnySpeed()
    {
        var w = new World(TestUtil.Yard(ObstacleKind.HighWall));
        var s = new WorldScratch(w);
        // a 30 m step crossing the 0.24 m thick wall
        int hit = w.TraceBullet(new Vec2(10, 1), 1.45, new Vec2(0.3, 30), 0, s, out double t);
        Assert.Equal(0, hit);
        Assert.InRange(t * 30, 8.7, 8.9);
    }

    [Fact]
    public void CollisionPushesOutOfWalls()
    {
        var w = new World(TestUtil.Yard(ObstacleKind.HighWall));
        var s = new WorldScratch(w);
        var p = w.Collide(new Vec2(10, 10.1), 0.28, s, out _);
        Assert.True(Geometry.DistanceToSegment(p, new Vec2(5, 10), new Vec2(15, 10)) >= 0.4 - 1e-9);
    }

    [Fact]
    public void PathGoesAroundTheWall()
    {
        var w = new World(TestUtil.Yard(ObstacleKind.HighWall, 0, 16));
        var s = new WorldScratch(w);
        Span<Vec2> path = stackalloc Vec2[48];
        var none = new NoExtraCost();
        int n = w.Nav.FindPath(new Vec2(5, 5), new Vec2(5, 15), s.Nav, path, ref none);
        Assert.True(n >= 2);
        Assert.Equal(new Vec2(5, 15), path[n - 1]);
        // every leg is walkable and the route passes the open east end
        Vec2 prev = new(5, 5);
        bool east = false;
        for (int k = 0; k < n; k++) { Assert.True(w.Nav.Walkable(prev, path[k]) || k == 0); east |= path[k].X > 16; prev = path[k]; }
        Assert.True(east);
    }

    [Fact]
    public void CoverPointsLineBothFaces()
    {
        var w = new World(TestUtil.Yard(ObstacleKind.LowWall));
        Assert.Contains(w.Cover, c => c.Pos.Y < 10 && c.Normal.Y > 0.9);
        Assert.Contains(w.Cover, c => c.Pos.Y > 10 && c.Normal.Y < -0.9);
        Span<int> idx = stackalloc int[8]; Span<double> d2 = stackalloc double[8];
        int n = w.NearestCover(new Vec2(10, 9), 5, idx, d2);
        Assert.Equal(8, n);
        for (int k = 1; k < n; k++) Assert.True(d2[k] >= d2[k - 1]);
    }
}
