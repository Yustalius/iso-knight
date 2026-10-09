namespace Squad.Sim;

// Motor layer: turns an AgentAction into motion with the same limits for everyone — path following, speed by stance
// and state, barrel turning at a limited rate, stance changes. "What and where" comes from the action, "how" is here.
public sealed partial class Match
{
    void Act(int i, in AgentAction a)
    {
        ref var s = ref S[i];
        var mb = B.Move;

        bool wantCrouch = a.Stance == Stance.Crouch && a.Mode != MoveMode.Run;
        s.Crouch = Approach(s.Crouch, wantCrouch ? 1 : 0, Dt / mb.CrouchTime);
        s.Mode = a.Mode;
        bool aiming = a.Aim != AimKind.None && !s.Reloading;
        s.Aiming = aiming;

        Vec2 desired = Vec2.Zero;
        switch (a.Move)
        {
            case MoveKind.Dir: desired = a.Target.ClampLength(1); M[i].HasGoal = false; break;
            case MoveKind.To: desired = FollowPath(i, a.Target, a.AvoidThreats); break;
            default: M[i].HasGoal = false; break;
        }

        bool crouched = s.Crouch > 0.5;
        double spd = aiming ? (crouched ? mb.CrouchAim : mb.Aim)
            : crouched ? mb.Crouch
            : a.Mode == MoveMode.Run && !s.Reloading ? mb.Run
            : a.Mode == MoveMode.Sneak ? mb.Sneak : mb.Walk;
        if (s.Reloading) spd = Math.Min(spd, mb.Reload);
        if (s.InBush) spd *= mb.BushSlow;

        bool moving = desired.LengthSq > 1e-6;
        Vec2 want = desired * spd;
        double k = 1 - DMath.Exp(-(moving ? mb.Accel : mb.Decel) * Dt);
        Vec2 vel = s.Vel + (want - s.Vel) * k;
        Vec2 old = s.Pos;
        s.Pos = World.Collide(old + vel * Dt, mb.Radius, _ws, out s.InBush);
        s.Vel = (s.Pos - old) / Dt;
        s.Speed = s.Vel.Length;

        if (aiming && AimTarget(i, a, out double yaw, out double pitch))
        {
            double err = DMath.WrapAngle(yaw - s.AimYaw);
            double step = DMath.Clamp(err * (1 - DMath.Exp(-14 * Dt)), -Setup[i].TurnRate * Dt, Setup[i].TurnRate * Dt);
            s.AimYaw = DMath.WrapAngle(s.AimYaw + step);
            s.AimRate = step / Dt;
            s.AimPitch = pitch;
            s.Yaw = s.AimYaw;
        }
        else
        {
            double step = 0;
            if (s.Speed > 0.2 && !aiming)
            {
                double err = DMath.WrapAngle(s.Vel.Yaw - s.Yaw);
                step = DMath.Clamp(err * (1 - DMath.Exp(-mb.BodyTurn * Dt)), -10 * Dt, 10 * Dt);
                s.Yaw = DMath.WrapAngle(s.Yaw + step);
            }
            s.AimRate = step / Dt;
            s.AimYaw = s.Yaw;
            s.AimPitch = 0;
        }
        s.Raise = Approach(s.Raise, aiming ? 1 : 0, Dt / (aiming ? B.Weapon.RaiseTime : B.Weapon.LowerTime));
    }

    static double Approach(double v, double target, double step) =>
        v < target ? Math.Min(target, v + step) : Math.Max(target, v - step);

    /// <summary>Where the barrel should point: the soldier's own estimate of the contact (with lead) or a point.</summary>
    bool AimTarget(int i, in AgentAction a, out double yaw, out double pitch)
    {
        ref var s = ref S[i];
        Vec2 target; double h;
        if (a.Aim == AimKind.Contact)
        {
            if ((uint)a.AimAt >= (uint)Count || S[a.AimAt].Team == s.Team) { yaw = s.AimYaw; pitch = 0; return false; }
            ref var c = ref Contacts[i * Count + a.AimAt];
            if (!c.Known) { yaw = s.AimYaw; pitch = s.AimPitch; return false; }
            double d = Vec2.Distance(s.Pos, c.Pos);
            double lead = d / B.Weapon.BulletSpeed * (1 + Setup[i].LeadError * c.ErrR / Math.Max(1e-6, B.Perception.NoiseRange));
            target = c.Pos + c.Vel * lead;
            h = c.AimH;
        }
        else { target = a.AimPoint; h = a.AimHeight; }
        Vec2 to = target - s.Pos;
        double dist = to.Length;
        if (dist < 0.05) { yaw = s.AimYaw; pitch = 0; return true; }
        yaw = to.Yaw;
        pitch = DMath.Atan2(h - MuzzleH(i), dist);
        return true;
    }

    /// <summary>Current spread half-angle (spread() of soldier-main.js).</summary>
    internal double Spread(int i)
    {
        ref var s = ref S[i];
        var w = B.Weapon;
        double sp = w.Base + (1 - DMath.Smooth(s.Focus)) * w.Raise
            + DMath.Clamp01(s.Speed / w.MoveRef) * w.Move
            + DMath.Clamp01(Math.Abs(s.AimRate) / w.TurnRef) * w.Turn
            + s.Bloom + s.Suppress;
        return sp * (s.Crouch > 0.5 ? w.CrouchMul : 1);
    }

    // ---------- paths ----------

    Vec2 FollowPath(int i, Vec2 goal, bool avoid)
    {
        ref var s = ref S[i];
        ref var m = ref M[i];
        if (!m.HasGoal || Vec2.DistanceSq(m.Goal, goal) > 0.75 * 0.75 || m.Avoid != avoid)
        {
            m.Goal = goal; m.HasGoal = true; m.Avoid = avoid;
            Repath(i);
        }
        m.RepathT -= Dt;
        if (Vec2.DistanceSq(s.Pos, m.StuckRef) > 0.35 * 0.35) { m.StuckRef = s.Pos; m.StuckT = 0; }
        else if (m.PathIdx < m.PathLen) m.StuckT += Dt;
        if (m.RepathT <= 0 || m.StuckT > 1.2) { Repath(i); m.StuckT = 0; }

        var path = _paths.AsSpan(i * MaxPath, MaxPath);
        while (m.PathIdx < m.PathLen && Vec2.DistanceSq(s.Pos, path[m.PathIdx]) < 0.3 * 0.3) m.PathIdx++;
        Vec2 to;
        if (m.PathIdx >= m.PathLen)
        {
            to = m.Goal - s.Pos;
            if (to.LengthSq < 0.2 * 0.2 || !World.Nav.FreeAt(m.Goal)) return Vec2.Zero;
        }
        else to = path[m.PathIdx] - s.Pos;
        double d = to.Length;
        bool last = m.PathIdx >= m.PathLen - 1;
        return to / d * (last ? Math.Min(1, d / 0.5) : 1);
    }

    void Repath(int i)
    {
        ref var m = ref M[i];
        var span = _paths.AsSpan(i * MaxPath, MaxPath);
        if (m.Avoid)
        {
            var cost = new ThreatCost(this, i);
            m.PathLen = World.Nav.FindPath(S[i].Pos, m.Goal, _ws.Nav, span, ref cost);
            m.RepathT = 2.0;
        }
        else
        {
            var none = new NoExtraCost();
            m.PathLen = World.Nav.FindPath(S[i].Pos, m.Goal, _ws.Nav, span, ref none);
            m.RepathT = 1.0;
        }
        m.PathIdx = 0;
    }

    /// <summary>Path cost that makes cells seen by the soldier's known threats three times as expensive.</summary>
    struct ThreatCost : IPathCost
    {
        readonly World _w;
        readonly WorldScratch _ws;
        Vec2 _t0, _t1, _t2;
        readonly int _n;
        readonly double _penalty;

        public ThreatCost(Match m, int i)
        {
            _w = m.World; _ws = m._ws; _n = 0; _t0 = _t1 = _t2 = default;
            _penalty = 2 * NavGrid.Cell;
            int n = m.Count;
            for (int j = 0; j < n && _n < 3; j++)
            {
                ref var c = ref m.Contacts[i * n + j];
                if (!c.Known || c.Cleared) continue;
                if (_n == 0) _t0 = c.Pos; else if (_n == 1) _t1 = c.Pos; else _t2 = c.Pos;
                _n++;
            }
        }

        public double Extra(int cell, Vec2 c)
        {
            double e = 0;
            for (int k = 0; k < _n; k++)
            {
                Vec2 t = k == 0 ? _t0 : k == 1 ? _t1 : _t2;
                if (Vec2.DistanceSq(t, c) > 35 * 35) continue;
                if (!_w.SolidBetween(t, 1.65, c, 1.2, _ws)) e += _penalty;
            }
            return e;
        }
    }

    // ---------- bodies ----------

    void Separate()
    {
        double r = B.Move.Radius, min = 2 * r;
        for (int i = 0; i < Count; i++)
        {
            if (!S[i].Alive) continue;
            for (int j = i + 1; j < Count; j++)
            {
                if (!S[j].Alive) continue;
                Vec2 d = S[j].Pos - S[i].Pos;
                double l2 = d.LengthSq;
                if (l2 >= min * min) continue;
                double l = Math.Sqrt(l2);
                Vec2 n = l > 1e-6 ? d / l : new Vec2(i < j ? 1 : -1, 0);
                double push = (min - l) * 0.5;
                S[i].Pos = World.Collide(S[i].Pos - n * push, r, _ws, out S[i].InBush);
                S[j].Pos = World.Collide(S[j].Pos + n * push, r, _ws, out S[j].InBush);
            }
        }
    }

    void Footsteps(int i)
    {
        ref var s = ref S[i];
        if (s.Speed < 0.25) return;
        s.StepT += Dt;
        if (s.StepT < B.Move.StepNoiseInterval) return;
        s.StepT -= B.Move.StepNoiseInterval;
        var p = B.Perception;
        double radius = s.Speed > 2.2 ? p.HearRun : s.Crouch > 0.5 || s.Mode == MoveMode.Sneak || s.Speed < 1.0 ? p.HearQuiet : p.HearWalk;
        MakeSound(i, s.Pos, radius);
    }
}
