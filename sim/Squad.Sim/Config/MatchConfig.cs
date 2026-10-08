namespace Squad.Sim;

/// <summary>Everything that defines a match. Together with the actions of every tick it reproduces the match exactly.</summary>
public sealed class MatchConfig
{
    public ulong Seed = 1;
    public MapData Map = new();
    public Rules Rules = new();
    public Balance Balance = new();
    /// <summary>Random ± share applied to the balance at creation (0 = off).</summary>
    public double RandomizeBalance;
    /// <summary>Two teams; team i spawns in the map's spawn zone i.</summary>
    public List<TeamSetup> Teams = new();
}

public sealed class Rules
{
    public double RoundTime = 120;
    public bool FriendlyFire;
    /// <summary>Allies stop bullets even without friendly fire, so nobody learns to shoot through his own men.</summary>
    public bool AlliesBlockBullets = true;
    /// <summary>Team that wins when time runs out; −1 is a draw.</summary>
    public int TimeoutWinner = -1;
    /// <summary>Optional objective: team <see cref="ReachTeam"/> wins as soon as one of its soldiers stands in the map zone named here.</summary>
    public string? ReachZone;
    public int ReachTeam;
}

public sealed class TeamSetup
{
    public List<AgentSetup> Members = new();
}

/// <summary>Per-soldier knobs that live in the rules: what he perceives and how fast his hands are.
/// Humans and bots use the same knobs; bot difficulty presets only pick other values.</summary>
public sealed class AgentSetup
{
    public string Name = "";
    /// <summary>Sees every enemy exactly, ignoring sight lines. Only for drills (a turret that sees everything); never for real opponents.</summary>
    public bool Omniscient;
    public double ReactionDelay = 0.2;
    public double NoiseMul = 1;
    public double TurnRate = 6;        // barrel rad/s
    public double LeadError = 0.25;    // relative error of the lead on a moving target

    public AgentSetup Clone() => (AgentSetup)MemberwiseClone();
}
