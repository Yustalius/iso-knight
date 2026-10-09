namespace Squad.Sim;

internal struct Contact
{
    public bool Known, Visible, Cleared;
    public ContactSource Src;
    public Vec2 Pos, Vel;
    public double AimH, SeenT, UncSeen, Unc, Detect, VisFrac, WatchT;
    public double ErrA, ErrR;
    public bool Crouched, Aiming, AimingAtMe;
}

internal struct BoardEntry
{
    public int Tick;
    public Vec2 Pos, Vel;
    public double AimH;
    public bool Crouched;
}

// Perception (docs/bots.md §4): sight with the 2.5D line test and a vision cone, detection that accumulates,
// estimates with a slowly drifting error, hearing, contact memory, the team radio with a delay.
// A soldier — and his bot — only ever knows what this produces.
public sealed partial class Match
{
    internal Contact[] Contacts = Array.Empty<Contact>();
    const int BoardDepth = 8;
    BoardEntry[] _board = Array.Empty<BoardEntry>();   // [team, enemy, depth] ring of sightings
    int[] _boardHead = Array.Empty<int>();

    void InitBoards()
    {
        _board = new BoardEntry[2 * Count * BoardDepth];
        _boardHead = new int[2 * Count];
        for (int k = 0; k < _board.Length; k++) _board[k].Tick = int.MinValue;
    }

    double PerceptionDt => _percEvery * Dt;

    void UpdatePerception(int i)
    {
        ref var me = ref S[i];
        var pb = B.Perception;
        double dtp = PerceptionDt, now = Time;
        Vec2 eye = me.Pos;
        double eyeH = EyeH(i);
        bool omni = Setup[i].Omniscient;
        int delayTicks = (int)DMath.Round(pb.RadioDelay / Dt);
        double decayA = DMath.Exp(-dtp / pb.NoiseTau);
        double driveA = DMath.Sqrt(1 - decayA * decayA);

        for (int j = 0; j < Count; j++)
        {
            if (S[j].Team == me.Team) continue;
            ref var c = ref Contacts[i * Count + j];
            ref var e = ref S[j];
            if (!e.Alive || !me.Alive) { c = default; continue; }

            Vec2 to = e.Pos - eye;
            double d = Math.Max(0.1, to.Length), bearing = to.Yaw;
            double frac = 0, centre = 1, aimH = 0;
            double[] pts = Points(j);
            if (omni) { frac = 1; aimH = pts[1]; }
            else if (d <= pb.ViewRange)
            {
                double ang = Math.Abs(DMath.WrapAngle(bearing - me.AimYaw));
                if (ang <= pb.ConeHalf)
                {
                    centre = DMath.Lerp(1, pb.ConeEdgeFactor, ang / pb.ConeHalf);
                    double hsum = 0;
                    for (int k = 0; k < pts.Length; k++)
                    {
                        double v = World.Visibility(eye, eyeH, e.Pos, pts[k], _ws, pb.BushDepth);
                        frac += v; hsum += v * pts[k];
                    }
                    if (frac > 0) aimH = hsum / frac;
                    frac /= pts.Length;
                }
            }

            bool inSight = frac > 0.02;
            if (omni) c.Detect = 1;
            else if (inSight)
            {
                double size = DMath.Clamp(pb.SizeRef / d, pb.SizeMin, pb.SizeMax);
                double motion = e.Speed > 2.2 ? pb.MotionRun : e.Speed > 0.2 ? pb.MotionWalk : pb.MotionStill;
                c.Detect = Math.Min(1.5, c.Detect + pb.DetectRate * frac * size * motion * centre * dtp);
            }
            else c.Detect = Math.Max(0, c.Detect - pb.DetectDecay * dtp);

            // drifting estimate error (Ornstein–Uhlenbeck): worse for running, half-hidden or foliage-covered targets,
            // better after watching the target for a while
            double sigA = pb.NoiseAngle * Setup[i].NoiseMul * (1 + (1 - Math.Min(1, frac * 1.5))) * (e.Speed > 2.2 ? 2 : 1) * (e.InBush ? 1.5 : 1)
                * (c.WatchT >= pb.FocusTime ? pb.FocusNoise : 1);
            double sigR = pb.NoiseRange * Setup[i].NoiseMul;
            if (omni) { c.ErrA = 0; c.ErrR = 0; }
            else
            {
                c.ErrA = c.ErrA * decayA + sigA * driveA * PercRng[i].Gauss();
                c.ErrR = c.ErrR * decayA + sigR * driveA * PercRng[i].Gauss();
            }

            if (inSight && c.Detect >= pb.SuspectLevel)
            {
                Vec2 est = eye + Vec2.FromYaw(bearing + c.ErrA) * (d * (1 + c.ErrR));
                if (c.Detect >= 1)
                {
                    if (c.Known && c.Visible && now - c.SeenT > 1e-9) c.Vel = c.Vel + ((est - c.Pos) / (now - c.SeenT) - c.Vel) * 0.5;
                    else c.Vel = Vec2.Zero;
                    c.Known = true; c.Visible = true; c.Cleared = false; c.Src = ContactSource.See;
                    c.Pos = est; c.SeenT = now;
                    c.UncSeen = c.Unc = d * (Math.Abs(c.ErrA) + Math.Abs(c.ErrR)) + 0.2;
                    c.AimH = aimH; c.VisFrac = frac; c.Crouched = e.Crouch > 0.5;
                    c.Aiming = e.Aiming && e.Raise > 0.5;
                    c.AimingAtMe = c.Aiming && Math.Abs(DMath.WrapAngle(e.AimYaw - (eye - e.Pos).Yaw)) < 8 * DMath.Deg;
                    PostBoard(me.Team, j, c);
                }
                else
                {
                    // "something is there": a rough position with a wide uncertainty
                    if (!c.Known || c.Src != ContactSource.See || now - c.SeenT > 1)
                    {
                        Vec2 jitter = PercRng[i].InDisk() * (d * 0.1);
                        c.Pos = est + jitter; c.Vel = Vec2.Zero;
                        c.UncSeen = c.Unc = Math.Max(2, d * 0.15);
                        c.Src = ContactSource.See; c.SeenT = now; c.AimH = aimH;
                    }
                    c.Known = true; c.Visible = false; c.Cleared = false; c.VisFrac = frac;
                }
            }
            else c.Visible = false;

            bool watching = c.Visible && me.Aiming && Math.Abs(DMath.WrapAngle(me.AimYaw - bearing)) < 5 * DMath.Deg;
            c.WatchT = watching ? c.WatchT + dtp : 0;

            if (!c.Known) { MergeBoard(i, j, ref c, delayTicks); continue; }
            if (!c.Visible)
            {
                MergeBoard(i, j, ref c, delayTicks);
                double age = now - c.SeenT;
                c.Unc = Math.Min(pb.MaxUncertainty, c.UncSeen + Math.Max(c.Vel.Length, pb.LostSpeed) * age);
                c.VisFrac = 0;
                // went to look and found nobody: he is elsewhere now
                if (!c.Cleared && Vec2.DistanceSq(eye, c.Pos) < pb.ClearRadius * pb.ClearRadius
                    && World.Visibility(eye, eyeH, c.Pos, 1.0, _ws, pb.BushDepth) > 0.5)
                {
                    c.Cleared = true;
                    c.UncSeen = Math.Max(c.UncSeen, 8); c.Unc = Math.Max(c.Unc, 8);
                }
                if (age > pb.ForgetTime) c = default;
            }
        }
    }

    void PostBoard(int team, int enemy, in Contact c)
    {
        int slot = team * Count + enemy;
        int head = _boardHead[slot];
        int baseIdx = slot * BoardDepth;
        if (_board[baseIdx + head].Tick != Tick)
        {
            head = (head + 1) % BoardDepth;
            _boardHead[slot] = head;
        }
        _board[baseIdx + head] = new BoardEntry { Tick = Tick, Pos = c.Pos, Vel = c.Vel, AimH = c.AimH, Crouched = c.Crouched };
    }

    /// <summary>Take a teammate's sighting over the radio if it is at least RadioDelay old and newer than what I know.</summary>
    void MergeBoard(int i, int enemy, ref Contact c, int delayTicks)
    {
        int slot = S[i].Team * Count + enemy, baseIdx = slot * BoardDepth, head = _boardHead[slot];
        for (int k = 0; k < BoardDepth; k++)
        {
            ref var e = ref _board[baseIdx + (head - k + BoardDepth) % BoardDepth];
            if (e.Tick == int.MinValue) return;
            if (e.Tick > Tick - delayTicks) continue;
            double t = e.Tick * Dt;
            if (c.Known && t <= c.SeenT + 1e-9) return;
            c.Known = true; c.Visible = false; c.Cleared = false; c.Src = ContactSource.Team;
            c.Pos = e.Pos; c.Vel = e.Vel; c.AimH = e.AimH; c.Crouched = e.Crouched;
            c.SeenT = t; c.UncSeen = c.Unc = 1.0;
            return;
        }
    }

    /// <summary>A noise at p made by soldier src, audible within radius (halved through solid walls).</summary>
    void MakeSound(int src, Vec2 p, double radius)
    {
        Emit(new GameEvent { Type = EventType.Sound, Agent = src, Pos = p, Value = radius });
        for (int j = 0; j < Count; j++)
        {
            if (!S[j].Alive || S[j].Team == S[src].Team) continue;
            double d2 = Vec2.DistanceSq(S[j].Pos, p);
            if (d2 > radius * radius) continue;
            double r = radius;
            if (World.SolidBetween(S[j].Pos, 1.6, p, 1.6, _ws)) r *= B.Perception.WallMuffle;
            if (d2 > r * r) continue;
            HearFrom(j, src, p);
        }
    }

    /// <summary>Listener j learns roughly where src is (a sound, a bullet cracking past, a hit).</summary>
    void HearFrom(int j, int src, Vec2 p)
    {
        if (S[j].Team == S[src].Team || !S[j].Alive) return;
        ref var c = ref Contacts[j * Count + src];
        if (c.Visible) return;
        double now = Time;
        if (c.Known && c.Src == ContactSource.See && now - c.SeenT < 0.5) return;
        double d = Vec2.Distance(S[j].Pos, p);
        double err = B.Perception.HearError * d + 0.5;
        c.Known = true; c.Cleared = false; c.Src = ContactSource.Hear;
        c.Pos = World.ClampToMap(p + PercRng[j].InDisk() * err, 0.3);
        c.Vel = Vec2.Zero; c.SeenT = now; c.UncSeen = c.Unc = err;
        c.AimH = 1.15;
    }

    // ---------- views ----------

    void BuildView(int i)
    {
        var v = _views[i];
        ref var me = ref S[i];
        var pb = B.Perception;
        v.Alive = me.Alive;
        v.Time = Time;
        v.TimeLeft = Math.Max(0, Rules.RoundTime - Time);
        v.AlliesAlive = AliveCount(me.Team) - (me.Alive ? 1 : 0);
        v.EnemiesAlive = AliveCount(1 - me.Team);
        v.Self = new SelfInfo
        {
            Pos = me.Pos, Vel = me.Vel, Yaw = me.Yaw, AimYaw = me.AimYaw, Hp = me.Hp, Crouched = me.Crouch > 0.5, Aiming = me.Aiming,
            Reloading = me.Reloading, InBush = me.InBush, Raise = me.Raise, Spread = Spread(i), Focus = me.Focus, Suppress = me.Suppress,
            Mag = me.Mag, Reserve = me.Reserve, Chamber = me.Chamber
        };
        BuildMapInfo(v, me.Team);

        // allies: exact, nearest first
        int na = 0;
        for (int j = 0; j < Count; j++)
        {
            if (j == i || !S[j].Alive || S[j].Team != me.Team) continue;
            double d = Vec2.Distance(me.Pos, S[j].Pos);
            var info = new AllyInfo { Id = j, Pos = S[j].Pos, Vel = S[j].Vel, Yaw = S[j].Yaw, Hp = S[j].Hp, Dist = d, Crouched = S[j].Crouch > 0.5, Aiming = S[j].Aiming };
            InsertSorted(v.AllyBuf, ref na, info, d, static a => a.Dist);
        }
        v.AllyCount = na;

        // contacts into the delay ring
        v.RingDelay = AgentView.DelaySteps(Setup[i].ReactionDelay, pb.Hz);
        v.RingHead = (v.RingHead + 1) % v.ContactRing.Length;
        var ring = v.ContactRing[v.RingHead];
        int nc = 0;
        double spread = Spread(i);
        for (int j = 0; j < Count; j++)
        {
            ref var c = ref Contacts[i * Count + j];
            if (!c.Known) continue;
            Vec2 to = c.Pos - me.Pos;
            double d = Math.Max(0.1, to.Length), bearing = to.Yaw;
            // chance to hit: Gaussian aim error σ from the spread cone and the estimate's uncertainty, target ±θ wide
            double sigma = DMath.Sqrt(spread * spread * 0.25 + (c.Unc / d) * (c.Unc / d) * 0.25);
            double theta = 0.22 * DMath.Sqrt(Math.Max(c.VisFrac, c.Visible ? 0.3 : 0.15)) / d;
            double aimErr = Math.Abs(DMath.WrapAngle(me.AimYaw - bearing));
            var info = new ContactInfo
            {
                Id = j, Pos = c.Pos, Vel = c.Vel, AimHeight = c.AimH, Uncertainty = c.Unc, Age = Time - c.SeenT, Dist = d, Bearing = bearing,
                Source = c.Src, Visible = c.Visible, VisibleFraction = c.VisFrac, Detection = c.Detect, Cleared = c.Cleared,
                Crouched = c.Crouched, Aiming = c.Aiming, AimingAtMe = c.AimingAtMe,
                HitChance = c.Visible ? DMath.HitChance(theta, aimErr, sigma) : 0
            };
            InsertSorted(ring, ref nc, info, Priority(info), static x => Priority(x));
        }
        v.ContactRingCount[v.RingHead] = nc;

        Exposure(i, v);
        CoverView(i, v);
    }

    static double Priority(in ContactInfo c) => c.Dist + (c.Visible ? 0 : 20) + (c.Cleared ? 40 : 0) + c.Age * 2;

    static void InsertSorted<T>(T[] buf, ref int n, T item, double key, Func<T, double> keyOf)
    {
        int cap = buf.Length;
        if (n == cap && key >= keyOf(buf[n - 1])) return;
        int j = n < cap ? n++ : n - 1;
        while (j > 0 && keyOf(buf[j - 1]) > key) { buf[j] = buf[j - 1]; j--; }
        buf[j] = item;
    }

    void BuildMapInfo(AgentView v, int team)
    {
        var sp = Config.Map.Spawns;
        Vec2 own = sp.Count > team ? sp[team].Center : new Vec2(World.Width / 2, team == 0 ? 2 : World.Height - 2);
        Vec2 enemy = sp.Count > 1 - team ? sp[1 - team].Center : new Vec2(World.Width / 2, team == 0 ? World.Height - 2 : 2);
        v.Map = new MapInfo { Width = World.Width, Height = World.Height, OwnSpawn = own, EnemySpawn = enemy };
        if (Rules.ReachZone != null && Config.Map.TryZone(Rules.ReachZone, out var z))
        {
            v.Map.HasObjective = true; v.Map.Objective = z.Center; v.Map.ObjectiveRadius = z.Radius; v.Map.Attacker = Rules.ReachTeam == team;
        }
    }

    /// <summary>Up to three most pressing known threats of soldier i (nearest visible first).</summary>
    int Threats(int i, Span<Vec2> pos)
    {
        int n = 0;
        Span<double> key = stackalloc double[3];
        for (int j = 0; j < Count; j++)
        {
            ref var c = ref Contacts[i * Count + j];
            if (!c.Known || c.Cleared || Time - c.SeenT > 10) continue;
            double k = Vec2.Distance(c.Pos, S[i].Pos) + (c.Visible ? 0 : 15);
            if (n == 3 && k >= key[2]) continue;
            int m = n < 3 ? n++ : 2;
            while (m > 0 && key[m - 1] > k) { key[m] = key[m - 1]; pos[m] = pos[m - 1]; m--; }
            key[m] = k; pos[m] = c.Pos;
        }
        return n;
    }

    void Exposure(int i, AgentView v)
    {
        Span<Vec2> t = stackalloc Vec2[3];
        int n = Threats(i, t);
        int es = 0, ec = 0;
        var bb = B.Body;
        for (int k = 0; k < n; k++)
        {
            if (!World.SolidBetween(t[k], bb.StandEye, S[i].Pos, bb.StandPoints[2], _ws)) es++;
            if (!World.SolidBetween(t[k], bb.StandEye, S[i].Pos, bb.CrouchPoints[2], _ws)) ec++;
        }
        v.ExposedStand = es; v.ExposedCrouch = ec;
    }

    void CoverView(int i, AgentView v)
    {
        var pb = B.Perception;
        Span<int> idx = stackalloc int[AgentView.MaxCover];
        Span<double> d2 = stackalloc double[AgentView.MaxCover];
        int n = World.NearestCover(S[i].Pos, pb.CoverRadius, idx.Slice(0, Math.Min(pb.CoverQueries, AgentView.MaxCover)), d2);
        Span<Vec2> t = stackalloc Vec2[3];
        int nt = Threats(i, t);
        var bb = B.Body;
        for (int k = 0; k < n; k++)
        {
            ref readonly var cp = ref World.Cover[idx[k]];
            int ts = 0, tc = 0;
            for (int q = 0; q < nt; q++)
            {
                if (World.Visibility(t[q], bb.StandEye, cp.Pos, bb.StandPoints[2], _ws, pb.BushDepth) > 0.3) ts++;
                if (World.Visibility(t[q], bb.StandEye, cp.Pos, bb.CrouchPoints[2], _ws, pb.BushDepth) > 0.3) tc++;
            }
            bool occupied = false;
            for (int j = 0; j < Count; j++)
                if (j != i && S[j].Alive && S[j].Team == S[i].Team && Vec2.DistanceSq(S[j].Pos, cp.Pos) < 0.7 * 0.7) { occupied = true; break; }
            v.CoverBuf[k] = new CoverInfo
            {
                Index = idx[k], Pos = cp.Pos, Normal = cp.Normal, Kind = cp.Kind, Corner = cp.Corner, Occupied = occupied,
                Dist = Math.Sqrt(d2[k]), ThreatsStand = ts, ThreatsCrouch = tc
            };
        }
        v.CoverCount = n;
    }
}
