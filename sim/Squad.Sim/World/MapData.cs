namespace Squad.Sim;

public enum ObstacleKind : byte { HighWall, LowWall, Crate, Bush, Tree }

/// <summary>A capsule on the ground: segment A–B swept by radius R, standing H metres tall.
/// Sight and bullets are blocked where a line passes the capsule below H; a bush only conceals.</summary>
public struct Obstacle
{
    public ObstacleKind Kind;
    public Vec2 A, B;
    public double R, H;

    public readonly bool BlocksMove => Kind != ObstacleKind.Bush;
    public readonly bool BlocksBullets => Kind != ObstacleKind.Bush;
    public readonly bool Solid => Kind != ObstacleKind.Bush;

    public static (double r, double h) Defaults(ObstacleKind k) => k switch
    {
        ObstacleKind.HighWall => (0.12, 2.5),
        ObstacleKind.LowWall => (0.2, 1.1),
        ObstacleKind.Crate => (0.45, 1.0),
        ObstacleKind.Bush => (0.8, 1.4),
        ObstacleKind.Tree => (0.25, 3.0),
        _ => (0.2, 1)
    };

    public static Obstacle Make(ObstacleKind k, Vec2 a, Vec2 b, double r = -1, double h = -1)
    {
        var (dr, dh) = Defaults(k);
        return new Obstacle { Kind = k, A = a, B = b, R = r > 0 ? r : dr, H = h > 0 ? h : dh };
    }
}

public struct Zone
{
    public string Name;
    public Vec2 Center;
    public double Radius;
    public readonly bool Contains(Vec2 p) => Vec2.DistanceSq(p, Center) <= Radius * Radius;
}

/// <summary>Rectangle where a team's soldiers appear; positions inside are random per match.</summary>
public struct SpawnZone
{
    public Vec2 Min, Max;
    public readonly Vec2 Center => (Min + Max) * 0.5;
}

/// <summary>A map as data. Hand-made maps are JSON files, the generator produces the same structure.</summary>
public sealed class MapData
{
    public string Name = "empty";
    public double Width = 30, Height = 30;
    public List<Obstacle> Obstacles = new();
    public List<SpawnZone> Spawns = new();
    public List<Zone> Zones = new();

    public bool TryZone(string name, out Zone z)
    {
        foreach (var x in Zones) if (x.Name == name) { z = x; return true; }
        z = default; return false;
    }
}
