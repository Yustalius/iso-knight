namespace Squad.Sim;

// Rifle, bullets and damage. The weapon logic follows soldier-main.js (focus, bloom, fire modes, phased reload),
// with the "rifle is up" moment owned by the rules instead of the animation.
public sealed partial class Match
{
    void Weapon(int i, in AgentAction a)
    {
        ref var s = ref S[i];
        var w = B.Weapon;

        // focus settles while aiming still, is lost by moving, turning and shooting
        bool ready = s.Aiming && s.Raise >= 1 && !s.Reloading;
        if (s.Aiming)
        {
            double still = 1 - DMath.Clamp01(s.Speed / w.MoveRef);
            double rate = (ready ? 0.6 + 1.2 * still : 3) * (1 - DMath.Clamp(Math.Abs(s.AimRate) / 4, 0, 0.8));
            s.Focus = DMath.Damp(s.Focus, ready ? 1 : 0, rate, Dt);
        }
        else { s.Focus = DMath.Damp(s.Focus, 0, 6, Dt); s.Latched = 0; s.Pending = 0; }
        s.Bloom *= DMath.Exp(-w.BloomDecay * Dt);
        s.Suppress *= DMath.Exp(-w.SuppressDecay * Dt);
        s.Cool = Math.Max(-Dt, s.Cool - Dt);

        // reload: requested, or automatically when empty and the trigger is pressed
        if (a.Reload && !s.Reloading && s.Mag < w.Cap && s.Reserve > 0) StartReload(i);
        if (s.Reloading) Reload(i);

        // trigger: Single/Burst register on the press and wait until the rifle is up; Auto fires while held
        if (a.Trigger != Trigger.None && s.PrevTrigger == Trigger.None)
        {
            if (a.Trigger == Trigger.Single) s.Latched = 1;
            else if (a.Trigger == Trigger.Burst) s.Latched = w.BurstCount;
            s.DryDone = false;
        }
        s.PrevTrigger = a.Trigger;
        if (!s.Aiming) s.Latched = 0;
        if (ready && s.Latched > 0) { s.Pending = s.Latched; s.Latched = 0; }
        bool auto = a.Trigger == Trigger.Auto;
        if (ready && s.Cool <= 0 && (s.Pending > 0 || auto))
        {
            if (s.Chamber)
            {
                Fire(i);
                s.Cool += 60 / w.Rpm;   // keeps the fraction of a tick, so 800 rpm holds at 30 Hz
                s.Pending = Math.Max(0, s.Pending - 1);
            }
            else
            {
                if (!s.DryDone) { Emit(new GameEvent { Type = EventType.DryFire, Agent = i, Pos = s.Pos }); s.DryDone = true; }
                s.Pending = 0;
            }
        }
    }

    void StartReload(int i)
    {
        ref var s = ref S[i];
        s.Reloading = true; s.ReloadT = 0; s.ReloadEmpty = !s.Chamber;
        s.ReloadOut = s.ReloadSeated = s.ReloadBolted = false;
        s.Pending = s.Latched = 0;
        Emit(new GameEvent { Type = EventType.ReloadStart, Agent = i, Pos = s.Pos, Value = s.ReloadEmpty ? 1 : 0 });
        MakeSound(i, s.Pos, B.Perception.HearReload);
    }

    void Reload(int i)
    {
        ref var s = ref S[i];
        var w = B.Weapon;
        s.ReloadT += Dt;
        if (!s.ReloadOut && s.ReloadT >= w.ReloadMagOut) { s.ReloadOut = true; s.Mag = 0; }
        if (!s.ReloadSeated && s.ReloadT >= w.ReloadSeat) { s.ReloadSeated = true; s.Mag = w.Cap; s.Reserve--; }
        if (s.ReloadEmpty && !s.ReloadBolted && s.ReloadT >= w.ReloadBolt) { s.ReloadBolted = true; s.Chamber = true; s.Mag--; }
        if (s.ReloadT >= (s.ReloadEmpty ? w.ReloadFull : w.ReloadTactical))
        {
            s.Reloading = false;
            Emit(new GameEvent { Type = EventType.ReloadDone, Agent = i, Pos = s.Pos });
        }
    }

    void Fire(int i)
    {
        ref var s = ref S[i];
        var w = B.Weapon;
        s.Chamber = false;
        if (s.Mag > 0) { s.Mag--; s.Chamber = true; }

        // a direction uniformly inside the spread cone (coneDir of the prototype)
        double half = Spread(i);
        DMath.SinCos(half, out double sh, out double ch);
        Vec2 off = WorldRng.InDisk() * (sh / ch);
        double yaw = s.AimYaw + off.X, pitch = s.AimPitch + off.Y;
        DMath.SinCos(pitch, out double sp, out double cp);

        int slot = FreeBullet();
        ref var b = ref _bullets[slot];
        b.Active = true; b.Owner = i; b.Team = s.Team;
        b.Origin = b.Pos = s.Pos; b.OriginH = b.H = MuzzleH(i);
        b.Dir = Vec2.FromYaw(yaw); b.Slope = sp / cp; b.Traveled = 0;

        s.Bloom += w.Bloom;
        s.Focus = Math.Max(0, s.Focus - w.FocusLossPerShot);
        _stats[i].Shots++;
        MakeSound(i, s.Pos, B.Perception.HearShot);
    }

    int FreeBullet()
    {
        int oldest = 0; double far = -1;
        for (int k = 0; k < _bullets.Length; k++)
        {
            if (!_bullets[k].Active) return k;
            if (_bullets[k].Traveled > far) { far = _bullets[k].Traveled; oldest = k; }
        }
        return oldest;
    }

    void UpdateBullets()
    {
        var w = B.Weapon;
        for (int k = 0; k < _bullets.Length; k++)
        {
            ref var b = ref _bullets[k];
            if (!b.Active) continue;
            double len = Math.Min(w.BulletSpeed * Dt, w.Range - b.Traveled);
            Vec2 d = b.Dir * len;
            double dh = b.Slope * len;

            // nearest of: obstacle, soldier, ground
            double best = 1.0001; Surface surf = Surface.None; int victim = -1; HitZone zone = HitZone.None;
            int oi = World.TraceBullet(b.Pos, b.H, d, dh, _ws, out double to);
            if (oi >= 0 && to < best) { best = to; surf = World.Obstacles[oi].Kind switch { ObstacleKind.HighWall => Surface.HighWall, ObstacleKind.LowWall => Surface.LowWall, ObstacleKind.Crate => Surface.Crate, _ => Surface.Tree }; }
            if (dh < 0 && b.H + dh < 0) { double tg = b.H / -dh; if (tg < best) { best = tg; surf = Surface.Ground; } }
            for (int j = 0; j < Count; j++)
            {
                if (j == b.Owner || !S[j].Alive) continue;
                bool ally = S[j].Team == b.Team;
                if (ally && !Rules.AlliesBlockBullets && !Rules.FriendlyFire) continue;
                if (HitBody(j, b.Pos, b.H, d, dh, best, out double tb, out HitZone z)) { best = tb; surf = Surface.Soldier; victim = j; zone = z; }
            }

            if (surf == Surface.None)
            {
                NearMisses(ref b, d, dh, 1, -1);
                b.Pos += d; b.H += dh; b.Traveled += len;
                if (b.Traveled >= w.Range - 1e-9) { EndBullet(ref b, Surface.Air); }
                continue;
            }
            NearMisses(ref b, d, dh, best, victim);
            b.Pos += d * best; b.H += dh * best;
            if (victim >= 0) Damage(b.Owner, victim, zone, b.Pos, b.H, b.Dir);
            EndBullet(ref b, surf);
        }
    }

    void EndBullet(ref Bullet b, Surface surf)
    {
        b.Active = false;
        Emit(new GameEvent { Type = EventType.Shot, Agent = b.Owner, Pos = b.Origin, H = b.OriginH, Pos2 = b.Pos, H2 = b.H, Surface = surf });
    }

    /// <summary>Entry of the bullet segment into soldier j's hit volumes (three stacked vertical cylinders).</summary>
    bool HitBody(int j, Vec2 p, double h0, Vec2 d, double dh, double before, out double t, out HitZone zone)
    {
        t = before; zone = HitZone.None;
        ref var s = ref S[j];
        var bb = B.Body;
        bool cr = s.Crouch > 0.5;
        double[] z = cr ? bb.CrouchZones : bb.StandZones;
        // quick reject: the segment passes too far from him
        double tc = Geometry.ClosestT(p, d, s.Pos);
        if (Vec2.DistanceSq(p + d * tc, s.Pos) > 0.3 * 0.3) return false;
        for (int k = 0; k < 3; k++)
        {
            double r = k == 0 ? (cr ? bb.CrouchLegsRadius : bb.LegsRadius) : k == 1 ? bb.TorsoRadius : bb.HeadRadius;
            double lo = k == 0 ? 0 : z[k - 1], hi = z[k];
            if (!Geometry.SegmentDisc(p, d, s.Pos, r, out double c0, out double c1)) continue;
            double a0, a1;
            if (Math.Abs(dh) < 1e-12) { if (h0 < lo || h0 > hi) continue; a0 = c0; a1 = c1; }
            else
            {
                double ta = (lo - h0) / dh, tb = (hi - h0) / dh;
                if (ta > tb) (ta, tb) = (tb, ta);
                a0 = Math.Max(c0, ta); a1 = Math.Min(c1, tb);
                if (a0 > a1) continue;
            }
            if (a0 < t) { t = a0; zone = (HitZone)(k + 1); }
        }
        return zone != HitZone.None;
    }

    /// <summary>Bullets passing close make enemies flinch (suppression) and tell them where the shot came from.</summary>
    void NearMisses(ref Bullet b, Vec2 d, double dh, double upto, int victim)
    {
        var w = B.Weapon;
        Vec2 seg = d * upto;
        for (int j = 0; j < Count; j++)
        {
            if (j == victim || !S[j].Alive || S[j].Team == b.Team) continue;
            double tc = Geometry.ClosestT(b.Pos, seg, S[j].Pos);
            double dist = (b.Pos + seg * tc - S[j].Pos).Length;
            if (dist > w.SuppressRadius) continue;
            double hAt = b.H + dh * upto * tc;
            if (hAt < 0 || hAt > 2.5) continue;
            S[j].Suppress += w.Suppress * (1 - dist / w.SuppressRadius);
            Emit(new GameEvent { Type = EventType.NearMiss, Agent = b.Owner, Other = j, Pos = b.Pos + seg * tc, Value = dist });
            HearFrom(j, b.Owner, b.Origin);
        }
    }

    void Damage(int shooter, int victim, HitZone zone, Vec2 at, double h, Vec2 dir)
    {
        ref var v = ref S[victim];
        bool ally = S[shooter].Team == v.Team;
        if (ally && !Rules.FriendlyFire) return;
        var db = B.Damage;
        double[] range = zone == HitZone.Head ? db.Head : zone == HitZone.Torso ? db.Torso : db.Legs;
        double dmg = DMath.Round(range[0] + WorldRng.NextDouble() * (range[1] - range[0]));
        dmg = Math.Min(dmg, v.Hp);
        v.Hp -= dmg;
        // being hit throws the aim off
        v.Focus = Math.Max(0, v.Focus - 0.35); v.Bloom += 0.02;
        _stats[shooter].Hits++; _stats[shooter].DamageDealt += dmg; _stats[victim].DamageTaken += dmg;
        Emit(new GameEvent { Type = EventType.Hit, Agent = shooter, Other = victim, Pos = at, H = h, Pos2 = dir, Value = dmg, Zone = zone });
        HearFrom(victim, shooter, S[shooter].Pos);
        if (v.Hp <= 0)
        {
            v.Alive = false; v.Vel = Vec2.Zero; v.Aiming = false; v.Reloading = false;
            _stats[victim].Deaths++; _stats[victim].DeathTick = Tick;
            if (ally) _stats[shooter].TeamKills++; else _stats[shooter].Kills++;
            Emit(new GameEvent { Type = EventType.Kill, Agent = shooter, Other = victim, Pos = v.Pos, Zone = zone, Pos2 = dir });
        }
    }
}
