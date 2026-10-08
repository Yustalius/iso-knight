using Squad.Bots;
using Squad.Sim;

namespace Squad.Train;

/// <summary>Layout of the network input (version 1). Everything comes from the AgentView only, in the soldier's own frame:
/// x to the right of the barrel, y along it. Fixed scales, no running statistics, so the input is reproducible and versioned.
/// [self | allies 9×A | contacts 10×C | cover 8×K | presence flags 9+10+8].</summary>
public static class Obs
{
    public const int Version = 1;
    public const int Self = 42, Ally = 10, Contact = 21, Cover = 12;
    public const int NAllies = AgentView.MaxAllies, NContacts = AgentView.MaxContacts, NCover = AgentView.MaxCover;
    public const int AlliesAt = Self, ContactsAt = AlliesAt + NAllies * Ally, CoverAt = ContactsAt + NContacts * Contact;
    public const int FlagsAt = CoverAt + NCover * Cover;
    public const int Size = FlagsAt + NAllies + NContacts + NCover;
}

/// <summary>The action heads (multi-discrete) and their sizes.
/// Move: 0 stop · 1–8 directions relative to the barrel (forward, then clockwise) · 9 toward the first contact ·
/// 10 toward the objective · 11–18 to cover slot k. Aim: 0 lowered · 1–10 contact slot k.</summary>
public static class Act
{
    public const int Version = 1;
    public const int MoveStop = 0, MoveDir0 = 1, MoveContact = 9, MoveObjective = 10, MoveCover0 = 11;
    public static readonly int[] Heads = { 19, 3, 2, 11, 4, 2 };   // move, mode, stance, aim, trigger, reload
    public const int NumHeads = 6, MaskSize = 19 + 3 + 2 + 11 + 4 + 2;
}

public static class Codec
{
    static double Clip(double x, double lim) => x < -lim ? -lim : x > lim ? lim : x;

    /// <summary>World vector → the soldier's frame (x right of the barrel, y along it).</summary>
    static (double x, double y) Local(Vec2 v, Vec2 fwd) => (v.X * fwd.Y - v.Y * fwd.X, v.X * fwd.X + v.Y * fwd.Y);

    /// <summary>Write the observation of one soldier and the masks of valid actions.</summary>
    public static void Encode(AgentView v, in Order o, double roundTime, Span<float> obs, Span<byte> mask)
    {
        obs.Clear();
        mask.Clear();
        var s = v.Self;
        Vec2 fwd = Vec2.FromYaw(s.AimYaw), me = s.Pos;
        int k = 0;
        void P(Span<float> b, ref int i, double x) => b[i++] = (float)x;

        // ---- self ----
        var (vx, vy) = Local(s.Vel, fwd);
        P(obs, ref k, Clip(vx / 3, 2)); P(obs, ref k, Clip(vy / 3, 2));
        DMath.SinCos(DMath.WrapAngle(s.Yaw - s.AimYaw), out double sy, out double cy);
        P(obs, ref k, sy); P(obs, ref k, cy);
        P(obs, ref k, s.Hp / 100); P(obs, ref k, s.Crouched ? 1 : 0); P(obs, ref k, s.Aiming ? 1 : 0); P(obs, ref k, s.Raise);
        P(obs, ref k, s.Reloading ? 1 : 0); P(obs, ref k, Clip(s.Spread / 0.1, 2)); P(obs, ref k, s.Focus); P(obs, ref k, Clip(s.Suppress / 0.05, 2));
        P(obs, ref k, s.Mag / 30.0); P(obs, ref k, s.Chamber ? 1 : 0); P(obs, ref k, s.Reserve / 6.0); P(obs, ref k, s.InBush ? 1 : 0);
        P(obs, ref k, v.ExposedStand / 3.0); P(obs, ref k, v.ExposedCrouch / 3.0);
        P(obs, ref k, roundTime > 0 ? DMath.Clamp01(v.TimeLeft / roundTime) : 0);
        P(obs, ref k, v.AlliesAlive / 9.0); P(obs, ref k, v.EnemiesAlive / 10.0);
        Place(obs, ref k, v.Map.OwnSpawn - me, fwd);
        Place(obs, ref k, v.Map.EnemySpawn - me, fwd);
        Place(obs, ref k, v.Map.HasObjective ? v.Map.Objective - me : Vec2.Zero, fwd);
        P(obs, ref k, v.Map.HasObjective ? 1 : 0); P(obs, ref k, v.Map.Attacker ? 1 : 0);
        for (int m = 0; m < 4; m++) P(obs, ref k, (int)o.Mode == m ? 1 : 0);
        {
            var (ax, ay) = o.HasArea ? Local(o.Area - me, fwd) : (0, 0);
            P(obs, ref k, Clip(ax / 20, 3)); P(obs, ref k, Clip(ay / 20, 3));
        }
        P(obs, ref k, o.MayMove ? 1 : 0); P(obs, ref k, o.Push ? 1 : 0);
        P(obs, ref k, v.Map.Width / 60); P(obs, ref k, v.Map.Height / 60);
        if (k != Obs.Self) throw new InvalidOperationException($"self block {k} != {Obs.Self}");

        // ---- allies ----
        var allies = v.Allies;
        for (int i = 0; i < allies.Length && i < Obs.NAllies; i++)
        {
            ref readonly var a = ref allies[i];
            int j = Obs.AlliesAt + i * Obs.Ally;
            var (lx, ly) = Local(a.Pos - me, fwd);
            var (avx, avy) = Local(a.Vel, fwd);
            DMath.SinCos(DMath.WrapAngle(a.Yaw - s.AimYaw), out double ss, out double cc);
            P(obs, ref j, Clip(lx / 20, 3)); P(obs, ref j, Clip(ly / 20, 3)); P(obs, ref j, Clip(a.Dist / 20, 3));
            P(obs, ref j, Clip(avx / 3, 2)); P(obs, ref j, Clip(avy / 3, 2)); P(obs, ref j, ss); P(obs, ref j, cc);
            P(obs, ref j, a.Hp / 100); P(obs, ref j, a.Crouched ? 1 : 0); P(obs, ref j, a.Aiming ? 1 : 0);
            obs[Obs.FlagsAt + i] = 1;
        }

        // ---- contacts ----
        var cs = v.Contacts;
        for (int i = 0; i < cs.Length && i < Obs.NContacts; i++)
        {
            ref readonly var c = ref cs[i];
            int j = Obs.ContactsAt + i * Obs.Contact;
            var (lx, ly) = Local(c.Pos - me, fwd);
            var (cvx, cvy) = Local(c.Vel, fwd);
            DMath.SinCos(DMath.WrapAngle(c.Bearing - s.AimYaw), out double ss, out double cc);
            P(obs, ref j, Clip(lx / 20, 3)); P(obs, ref j, Clip(ly / 20, 3)); P(obs, ref j, Clip(c.Dist / 20, 3));
            P(obs, ref j, ss); P(obs, ref j, cc); P(obs, ref j, Clip(cvx / 3, 2)); P(obs, ref j, Clip(cvy / 3, 2));
            P(obs, ref j, c.AimHeight / 2); P(obs, ref j, Clip(c.Uncertainty / 10, 2)); P(obs, ref j, Clip(c.Age / 10, 2));
            P(obs, ref j, c.Source == ContactSource.See ? 1 : 0); P(obs, ref j, c.Source == ContactSource.Hear ? 1 : 0); P(obs, ref j, c.Source == ContactSource.Team ? 1 : 0);
            P(obs, ref j, c.Visible ? 1 : 0); P(obs, ref j, c.VisibleFraction); P(obs, ref j, Clip(c.Detection, 1.5)); P(obs, ref j, c.Cleared ? 1 : 0);
            P(obs, ref j, c.Crouched ? 1 : 0); P(obs, ref j, c.Aiming ? 1 : 0); P(obs, ref j, c.AimingAtMe ? 1 : 0); P(obs, ref j, c.HitChance);
            obs[Obs.FlagsAt + Obs.NAllies + i] = 1;
        }

        // ---- cover ----
        var cov = v.Cover;
        for (int i = 0; i < cov.Length && i < Obs.NCover; i++)
        {
            ref readonly var c = ref cov[i];
            int j = Obs.CoverAt + i * Obs.Cover;
            var (lx, ly) = Local(c.Pos - me, fwd);
            var (nx, ny) = Local(c.Normal, fwd);
            P(obs, ref j, Clip(lx / 20, 3)); P(obs, ref j, Clip(ly / 20, 3)); P(obs, ref j, Clip(c.Dist / 20, 3));
            P(obs, ref j, nx); P(obs, ref j, ny);
            P(obs, ref j, c.Kind == CoverKind.Low ? 1 : 0); P(obs, ref j, c.Kind == CoverKind.High ? 1 : 0); P(obs, ref j, c.Kind == CoverKind.Bush ? 1 : 0);
            P(obs, ref j, c.Corner ? 1 : 0); P(obs, ref j, c.Occupied ? 1 : 0);
            P(obs, ref j, c.ThreatsStand / 3.0); P(obs, ref j, c.ThreatsCrouch / 3.0);
            obs[Obs.FlagsAt + Obs.NAllies + Obs.NContacts + i] = 1;
        }

        // ---- masks: move | mode | stance | aim | trigger | reload ----
        int nc = Math.Min(cs.Length, Obs.NContacts), nk = Math.Min(cov.Length, Obs.NCover);
        for (int m = 0; m <= 8; m++) mask[m] = 1;
        mask[Act.MoveContact] = (byte)(nc > 0 ? 1 : 0);
        mask[Act.MoveObjective] = 1;
        for (int m = 0; m < nk; m++) mask[Act.MoveCover0 + m] = 1;
        int at = 19;
        for (int m = 0; m < 3; m++) mask[at + m] = 1;
        at += 3;
        mask[at] = mask[at + 1] = 1;
        at += 2;
        mask[at] = 1;
        for (int m = 0; m < nc; m++) mask[at + 1 + m] = 1;
        at += 11;
        for (int m = 0; m < 4; m++) mask[at + m] = 1;
        at += 4;
        mask[at] = 1;
        mask[at + 1] = (byte)(s.Reserve > 0 && !s.Reloading && (s.Mag < 30 || !s.Chamber) ? 1 : 0);
    }

    /// <summary>A place relative to the soldier: x, y in his frame (/20 m) and distance (/40 m).</summary>
    static void Place(Span<float> obs, ref int k, Vec2 d, Vec2 fwd)
    {
        var (lx, ly) = Local(d, fwd);
        obs[k++] = (float)Clip(lx / 20, 3); obs[k++] = (float)Clip(ly / 20, 3); obs[k++] = (float)(d.Length / 40);
    }

    /// <summary>Mask for a soldier who is dead or absent: only the "nothing" option of each head.</summary>
    public static void DeadMask(Span<byte> mask)
    {
        mask.Clear();
        int at = 0;
        foreach (int h in Act.Heads) { mask[at] = 1; at += h; }
    }

    /// <summary>Turn the chosen options into an action, using the same view the observation was built from.</summary>
    public static AgentAction Decode(AgentView v, in Order o, ReadOnlySpan<int> a)
    {
        var s = v.Self;
        var act = new AgentAction();
        int move = a[0];
        var cs = v.Contacts;
        var cov = v.Cover;
        if (move >= Act.MoveDir0 && move < Act.MoveDir0 + 8)
        {
            act.Move = MoveKind.Dir;
            act.Target = Vec2.FromYaw(s.AimYaw + (move - Act.MoveDir0) * DMath.Pi / 4);
        }
        else if (move == Act.MoveContact && cs.Length > 0) { act.Move = MoveKind.To; act.Target = cs[0].Pos; }
        else if (move == Act.MoveObjective)
        {
            act.Move = MoveKind.To;
            act.Target = o.HasArea ? o.Area : v.Map.HasObjective && v.Map.Attacker ? v.Map.Objective : v.Map.EnemySpawn;
        }
        else if (move >= Act.MoveCover0 && move - Act.MoveCover0 < cov.Length) { act.Move = MoveKind.To; act.Target = cov[move - Act.MoveCover0].Pos; }

        act.Mode = (MoveMode)Math.Clamp(a[1], 0, 2);
        act.Stance = a[2] == 1 ? Stance.Crouch : Stance.Stand;
        int aim = a[3];
        if (aim >= 1 && aim - 1 < cs.Length) { act.Aim = AimKind.Contact; act.AimAt = cs[aim - 1].Id; }
        act.Trigger = act.Aim == AimKind.None ? Trigger.None : (Trigger)Math.Clamp(a[4], 0, 3);
        act.Reload = a[5] == 1;
        return act;
    }
}
