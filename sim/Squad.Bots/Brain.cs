using Squad.Sim;

namespace Squad.Bots;

public enum OrderMode : byte { Free, Advance, Hold, Regroup }

/// <summary>An order from above (the squad tactic now, a commander network or the player later): where and how.
/// It is part of a bot's input, so the same fighter works under any commander.</summary>
public struct Order
{
    public OrderMode Mode;
    public Vec2 Area;
    public double Radius;
    /// <summary>Bounding overwatch: false while the partner moves and this one covers him.</summary>
    public bool MayMove;
    public bool HasArea;
    /// <summary>The fight has stalled: everyone, defenders included, goes looking for the enemy.</summary>
    public bool Push;
}

/// <summary>A rule bot. It sees only the AgentView (what its soldier knows) and the order, and writes an action.
/// Think is called when the view is rebuilt (10 Hz); the action holds until the next call.</summary>
public interface IBrain
{
    string Name { get; }
    /// <summary>Short state name for debug overlays and logs.</summary>
    string Intent { get; }
    void Think(AgentView view, in Order order, ref AgentAction action, ref Rng rng);
}

/// <summary>Difficulty: the soldier-level knobs (reaction, hands, eyes) plus how picky the bot is about shots.
/// Weapon spread is physics and is the same for everyone.</summary>
public sealed class BotSkill
{
    public string Name = "normal";
    public double Reaction = 0.2, TurnRate = 6, Noise = 1, LeadError = 0.25;
    /// <summary>Minimum estimated hit chance to pull the trigger at medium range.</summary>
    public double MinChance = 0.2;
    /// <summary>Think on every Nth view update (1 = 10 Hz, 2 = 5 Hz).</summary>
    public int ThinkEvery = 1;

    public static BotSkill Get(string name) => name.ToLowerInvariant() switch
    {
        "easy" => new BotSkill { Name = "easy", Reaction = 0.3, TurnRate = 3.5, Noise = 1.6, LeadError = 0.5, MinChance = 0.35, ThinkEvery = 2 },
        "hard" => new BotSkill { Name = "hard", Reaction = 0.15, TurnRate = 9, Noise = 0.7, LeadError = 0.1, MinChance = 0.12, ThinkEvery = 1 },
        "normal" => new BotSkill(),
        _ => throw new ArgumentException($"Unknown skill '{name}' (easy, normal, hard).")
    };

    public void ApplyTo(AgentSetup s)
    {
        s.ReactionDelay = Reaction; s.TurnRate = TurnRate; s.NoiseMul = Noise; s.LeadError = LeadError;
    }
}
