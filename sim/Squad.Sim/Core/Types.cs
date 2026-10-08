namespace Squad.Sim;

public enum MoveKind : byte { None, Dir, To }
public enum MoveMode : byte { Walk, Run, Sneak }
public enum Stance : byte { Stand, Crouch }
public enum AimKind : byte { None, Contact, Point }
public enum Trigger : byte { None, Single, Burst, Auto }

/// <summary>What one soldier wants this tick, in world terms. Keyboard and mouse, the rule bots and a neural network all
/// produce this same struct; the match turns it into motion with the same limits for everyone.
/// Single and Burst fire on the press (a change from None); Auto fires while held.</summary>
public struct AgentAction
{
    public MoveKind Move;
    /// <summary>Dir: direction with length ≤ 1 (scales speed). To: destination; the match finds the path.</summary>
    public Vec2 Target;
    public MoveMode Mode;
    public Stance Stance;
    /// <summary>Paths prefer cells the soldier's known threats cannot see (slow; for flanking).</summary>
    public bool AvoidThreats;
    public AimKind Aim;
    /// <summary>Aim at this enemy's contact (agent id): the barrel follows the soldier's own, noisy estimate of him.</summary>
    public int AimAt;
    public Vec2 AimPoint;
    public double AimHeight;
    public Trigger Trigger;
    public bool Reload;

    public static AgentAction Idle => default;
}

public enum HitZone : byte { None, Legs, Torso, Head }
public enum Surface : byte { None, Soldier, HighWall, LowWall, Crate, Tree, Ground, Air }

public enum EventType : byte
{
    Shot,        // Agent fired from Pos (height H) toward Pos2 (height H2: where the bullet ended)
    Hit,         // Agent hit Other in Zone for Value damage at Pos
    Kill,        // Agent killed Other
    ReloadStart, // Agent; Value = 1 when the chamber was empty
    ReloadDone,
    DryFire,
    Sound,       // Agent made a noise of radius Value at Pos (footsteps, shots, reloads)
    NearMiss,    // a bullet of Agent passed close to Other
    RoundEnd     // Agent = winner (−1 draw)
}

/// <summary>Something that happened this tick. Renderers and sound read these; the rules do not.</summary>
public struct GameEvent
{
    public EventType Type;
    public int Tick, Agent, Other;
    public Vec2 Pos, Pos2;
    public double H, H2, Value;
    public HitZone Zone;
    public Surface Surface;
}

public enum EndReason : byte { None, Elimination, Timeout, Objective }

public struct MatchResult
{
    public bool Over;
    /// <summary>Winning team, −1 for a draw.</summary>
    public int Winner;
    public int EndTick;
    public EndReason Reason;
}

public struct AgentStats
{
    public int Kills, Deaths, TeamKills, Shots, Hits;
    public double DamageDealt, DamageTaken;
    public int DeathTick;
}

/// <summary>The exact state of a soldier, for rendering, tools and drills. Bots must not read this; they get an <see cref="AgentView"/>.</summary>
public struct SoldierTruth
{
    public int Id, Team;
    public bool Alive;
    public double Hp;
    public Vec2 Pos, Vel;
    public double Yaw, AimYaw, AimPitch;
    public double Crouch, Raise;
    public bool Aiming, Crouched, InBush;
    public int Mag, Reserve;
    public bool Chamber, Reloading;
    public double ReloadT, Spread, Focus;
    public double EyeHeight;
}
