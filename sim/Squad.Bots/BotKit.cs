using Squad.Sim;

namespace Squad.Bots;

/// <summary>Building blocks shared by the rule bots: choosing a target, when and how to shoot, where to take cover.</summary>
public static class BotKit
{
    /// <summary>Index into view.Contacts of the best enemy to shoot at, or −1. Keeps the current target unless another
    /// is clearly better, so the barrel does not flick between enemies.</summary>
    public static int BestTarget(AgentView v, int currentId, bool visibleOnly = true)
    {
        var cs = v.Contacts;
        int best = -1; double bestScore = double.NegativeInfinity, curScore = double.NegativeInfinity; int cur = -1;
        for (int i = 0; i < cs.Length; i++)
        {
            ref readonly var c = ref cs[i];
            if (visibleOnly && !c.Visible) continue;
            if (c.Cleared) continue;
            double s = -c.Dist * 0.08 + c.HitChance * 2 + (c.AimingAtMe ? 1.2 : c.Aiming ? 0.4 : 0) + (c.Visible ? 2 : 0) - c.Age * 0.3;
            if (c.Id == currentId) { curScore = s; cur = i; }
            if (s > bestScore) { bestScore = s; best = i; }
        }
        if (cur >= 0 && curScore + 0.6 >= bestScore) return cur;
        return best;
    }

    public static int CountVisible(AgentView v)
    {
        int n = 0;
        foreach (ref readonly var c in v.Contacts) if (c.Visible) n++;
        return n;
    }

    /// <summary>Nearest contact not marked as cleared (any source), or −1.</summary>
    public static int NearestKnown(AgentView v, double maxAge = 20)
    {
        var cs = v.Contacts;
        int best = -1; double bd = double.PositiveInfinity;
        for (int i = 0; i < cs.Length; i++)
        {
            if (cs[i].Cleared || cs[i].Age > maxAge) continue;
            if (cs[i].Dist < bd) { bd = cs[i].Dist; best = i; }
        }
        return best;
    }

    /// <summary>Most recently updated contact not cleared, or −1.</summary>
    public static int Freshest(AgentView v)
    {
        var cs = v.Contacts;
        int best = -1; double ba = double.PositiveInfinity;
        for (int i = 0; i < cs.Length; i++)
        {
            if (cs[i].Cleared) continue;
            if (cs[i].Age < ba) { ba = cs[i].Age; best = i; }
        }
        return best;
    }

    /// <summary>Mean position of known, uncleared enemies; the enemy spawn when nothing is known.</summary>
    public static Vec2 ThreatCenter(AgentView v, double maxAge = 15)
    {
        Vec2 sum = Vec2.Zero; int n = 0;
        foreach (ref readonly var c in v.Contacts)
            if (!c.Cleared && c.Age <= maxAge) { sum += c.Pos; n++; }
        return n > 0 ? sum / n : v.Map.EnemySpawn;
    }

    public static bool AnyThreatWithin(AgentView v, double dist, double maxAge = 8)
    {
        foreach (ref readonly var c in v.Contacts) if (!c.Cleared && c.Age <= maxAge && c.Dist < dist) return true;
        return false;
    }

    /// <summary>Fire mode by range: full auto up close, bursts at medium range, single aimed shots far away.</summary>
    public static Trigger FireMode(double dist) => dist < 7 ? Trigger.Auto : dist < 22 ? Trigger.Burst : Trigger.Single;

    /// <summary>Aim at a visible contact and pull the trigger once the rifle is up and the shot is worth it.</summary>
    public static void Engage(AgentView v, in ContactInfo c, ref AgentAction a, double minChance)
    {
        a.Aim = AimKind.Contact; a.AimAt = c.Id;
        var mode = FireMode(c.Dist);
        double need = mode == Trigger.Auto ? minChance * 0.5 : mode == Trigger.Single ? minChance * 1.3 : minChance;
        bool ready = v.Self.Raise >= 1 && !v.Self.Reloading;
        a.Trigger = ready && c.Visible && c.HitChance >= need && (v.Self.Chamber || v.Self.Mag > 0) ? mode : Trigger.None;
    }

    /// <summary>Keep the rifle on a point (holding an angle): the barrel settles there before anyone appears.</summary>
    public static void Watch(ref AgentAction a, Vec2 p, double h = 1.3)
    {
        a.Aim = AimKind.Point; a.AimPoint = p; a.AimHeight = h; a.Trigger = Trigger.None;
    }

    /// <summary>Reload when empty, or topping up when nobody is in sight.</summary>
    public static bool WantsReload(AgentView v, int topUpBelow = 12)
    {
        var s = v.Self;
        if (s.Reloading || s.Reserve <= 0) return false;
        if (s.Mag == 0 && !s.Chamber) return true;
        if (s.Mag == 0) return CountVisible(v) == 0;
        return s.Mag < topUpBelow && CountVisible(v) == 0 && !AnyThreatWithin(v, 12, 3);
    }

    public static void MoveTo(ref AgentAction a, Vec2 p, MoveMode mode, bool avoid = false)
    {
        a.Move = MoveKind.To; a.Target = p; a.Mode = mode; a.AvoidThreats = avoid;
    }

    public static void Stop(ref AgentAction a) { a.Move = MoveKind.None; a.AvoidThreats = false; }

    /// <summary>Best cover in the view for fighting toward 'threat': protected crouching, able to see standing,
    /// near, in the wanted direction. Returns an index into view.Cover or −1.</summary>
    public static int PickCover(AgentView v, Vec2 threat, Vec2 toward, double maxDist, double towardWeight = 0.5, bool lowOnly = false, double minProgress = double.NegativeInfinity)
    {
        var cov = v.Cover;
        Vec2 me = v.Self.Pos;
        double myGoalDist = Vec2.Distance(me, toward);
        int best = -1; double bs = double.NegativeInfinity;
        for (int i = 0; i < cov.Length; i++)
        {
            ref readonly var c = ref cov[i];
            if (c.Occupied || c.Dist > maxDist) continue;
            if (lowOnly && c.Kind != CoverKind.Low) continue;
            double progress = myGoalDist - Vec2.Distance(c.Pos, toward);
            if (progress < minProgress) continue;
            Vec2 toThreat = (threat - c.Pos).Normalized();
            double facing = c.Kind == CoverKind.Bush ? 0.3 : c.Normal.Dot(toThreat);   // obstacle between point and threat
            double s = facing * 2 + progress * towardWeight - c.Dist * 0.15
                - c.ThreatsCrouch * 2.5 - (c.ThreatsStand == 0 ? 0.8 : 0)                // want cover, but one that lets us shoot
                + (c.Kind == CoverKind.Low ? 0.6 : c.Kind == CoverKind.Bush ? -0.3 : c.Corner ? 0.4 : 0);
            if (s > bs) { bs = s; best = i; }
        }
        return best;
    }

    public static Vec2 Toward(Vec2 from, Vec2 to, double dist)
    {
        Vec2 d = to - from;
        double l = d.Length;
        return l <= dist ? to : from + d / l * dist;
    }
}
