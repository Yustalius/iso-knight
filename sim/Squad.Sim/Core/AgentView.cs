namespace Squad.Sim;

public enum ContactSource : byte { None, See, Hear, Team }

public struct SelfInfo
{
    public Vec2 Pos, Vel;
    public double Yaw, AimYaw, Hp;
    public bool Crouched, Aiming, Reloading, InBush;
    public double Raise, Spread, Focus, Suppress;
    public int Mag, Reserve;
    public bool Chamber;
}

public struct AllyInfo
{
    public int Id;
    public Vec2 Pos, Vel;
    public double Yaw, Hp, Dist;
    public bool Crouched, Aiming;
}

/// <summary>What a soldier believes about one enemy. Position is an estimate with an error that drifts slowly;
/// Uncertainty is the radius the enemy may be in now. Contacts exist only for enemies he has seen, heard or been told about.</summary>
public struct ContactInfo
{
    public int Id;
    public Vec2 Pos, Vel;
    /// <summary>Height to aim at: the middle of what was visible of him.</summary>
    public double AimHeight;
    public double Uncertainty, Age, Dist, Bearing;
    public ContactSource Source;
    /// <summary>Seen right now (detection complete and in sight).</summary>
    public bool Visible;
    public double VisibleFraction, Detection;
    /// <summary>Someone went to the spot and found nothing: he is somewhere else.</summary>
    public bool Cleared;
    public bool Crouched, Aiming, AimingAtMe;
    /// <summary>Estimated chance that a shot fired now hits him (own spread, perception error, visible size).</summary>
    public double HitChance;
}

public struct CoverInfo
{
    public int Index;
    public Vec2 Pos, Normal;
    public CoverKind Kind;
    public bool Corner, Occupied;
    public double Dist;
    /// <summary>Known threats (up to three nearest) that would see a soldier standing / crouching here.</summary>
    public int ThreatsStand, ThreatsCrouch;
}

public struct MapInfo
{
    public double Width, Height;
    public Vec2 OwnSpawn, EnemySpawn;
    public bool HasObjective, Attacker;
    public Vec2 Objective;
    public double ObjectiveRadius;
}

/// <summary>Everything one soldier knows, rebuilt at the perception rate (10 Hz). This is the whole input of a bot
/// (rules or neural network). Information about enemies arrives with the soldier's reaction delay.
/// Version changes whenever the layout changes: trained networks depend on it.</summary>
public sealed class AgentView
{
    public const int Version = 1;
    public const int MaxAllies = 9, MaxContacts = 10, MaxCover = 8;
    const int Ring = 4;

    public int Id, Team;
    public bool Alive;
    public double Time, TimeLeft;
    public SelfInfo Self;
    public MapInfo Map;
    public int AlliesAlive, EnemiesAlive;
    /// <summary>Known threats that can see me standing / crouching here (judged from my own contacts, not the truth).</summary>
    public int ExposedStand, ExposedCrouch;

    internal readonly AllyInfo[] AllyBuf = new AllyInfo[MaxAllies];
    internal int AllyCount;
    internal readonly CoverInfo[] CoverBuf = new CoverInfo[MaxCover];
    internal int CoverCount;
    internal readonly ContactInfo[][] ContactRing;
    internal readonly int[] ContactRingCount = new int[Ring];
    internal int RingHead, RingDelay;

    public AgentView()
    {
        ContactRing = new ContactInfo[Ring][];
        for (int i = 0; i < Ring; i++) ContactRing[i] = new ContactInfo[MaxContacts];
    }

    public ReadOnlySpan<AllyInfo> Allies => AllyBuf.AsSpan(0, AllyCount);
    public ReadOnlySpan<CoverInfo> Cover => CoverBuf.AsSpan(0, CoverCount);

    public ReadOnlySpan<ContactInfo> Contacts
    {
        get
        {
            int k = ((RingHead - RingDelay) % Ring + Ring) % Ring;
            return ContactRing[k].AsSpan(0, ContactRingCount[k]);
        }
    }

    /// <summary>Index of the contact with this enemy id, or −1.</summary>
    public int FindContact(int enemyId)
    {
        var c = Contacts;
        for (int i = 0; i < c.Length; i++) if (c[i].Id == enemyId) return i;
        return -1;
    }

    internal static int DelaySteps(double reaction, double hz) => Math.Clamp((int)DMath.Round(reaction * hz), 0, Ring - 1);
}
