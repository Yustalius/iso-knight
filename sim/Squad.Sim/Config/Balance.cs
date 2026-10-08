namespace Squad.Sim;

/// <summary>Every tunable number of the rules. Defaults come from the soldier prototype (soldier-main.js,
/// soldier-rig.js, enemy.js) and docs/bots.md. balance.json and scenarios override any field by name.
/// Units: metres, seconds, radians (half-angles for spread), hit points.</summary>
public sealed class Balance
{
    public MoveBalance Move = new();
    public WeaponBalance Weapon = new();
    public DamageBalance Damage = new();
    public BodyBalance Body = new();
    public PerceptionBalance Perception = new();

    public Balance Clone()
    {
        var b = (Balance)MemberwiseClone();
        b.Move = (MoveBalance)Move.Clone(); b.Weapon = (WeaponBalance)Weapon.Clone(); b.Damage = (DamageBalance)Damage.Clone();
        b.Body = (BodyBalance)Body.Clone(); b.Perception = (PerceptionBalance)Perception.Clone();
        b.Damage.Head = (double[])Damage.Head.Clone(); b.Damage.Torso = (double[])Damage.Torso.Clone(); b.Damage.Legs = (double[])Damage.Legs.Clone();
        return b;
    }

    /// <summary>Scale the numbers that matter for fights by a random factor in [1 − r, 1 + r], so a bot trained on
    /// many matches does not depend on exact values and small balance patches need no retraining.</summary>
    public void Randomize(ref Rng rng, double r)
    {
        if (r <= 0) return;
        double F(ref Rng g) => 1 + r * (2 * g.NextDouble() - 1);
        Move.Walk *= F(ref rng); Move.Run *= F(ref rng); Move.Aim *= F(ref rng); Move.Crouch *= F(ref rng);
        Weapon.Rpm *= F(ref rng); Weapon.Base *= F(ref rng); Weapon.Raise *= F(ref rng); Weapon.Move *= F(ref rng);
        Weapon.Turn *= F(ref rng); Weapon.Bloom *= F(ref rng); Weapon.RaiseTime *= F(ref rng);
        double d = F(ref rng); for (int i = 0; i < 2; i++) { Damage.Torso[i] *= d; Damage.Legs[i] *= d; }
        Perception.DetectRate *= F(ref rng); Perception.NoiseAngle *= F(ref rng);
    }
}

public sealed class MoveBalance
{
    public double Walk = 1.45, Run = 3.0, Sneak = 0.9, Aim = 0.95, Crouch = 0.85, CrouchAim = 0.6;
    public double Reload = 1.16;            // walk × 0.8 while changing magazines
    public double Accel = 7, Decel = 10;    // exponential approach rates of the velocity
    public double Radius = 0.28;            // body radius for collisions
    public double CrouchTime = 0.3;         // stand ↔ crouch
    public double BushSlow = 0.75;          // speed factor inside a bush
    public double BodyTurn = 12;            // body yaw approach rate when not aiming
    public double StepNoiseInterval = 0.5;  // a footstep sound every this many seconds of movement
    public object Clone() => MemberwiseClone();
}

public sealed class WeaponBalance
{
    // M16A2 from soldier-main.js: RIFLE = { cap: 30, rpm: 800, base: .0055, raise: .075, move: .04, turn: .03, bloom: .014 }
    public int Cap = 30, ReserveMags = 6, BurstCount = 3;
    public double Rpm = 800;
    public double Base = 0.0055, Raise = 0.075, Move = 0.04, Turn = 0.03, Bloom = 0.014;
    public double BloomDecay = 4.5, CrouchMul = 0.75, FocusLossPerShot = 0.12;
    public double MoveRef = 1.2, TurnRef = 2.5;  // speed (m/s) and turn rate (rad/s) at which their spread terms saturate
    public double RaiseTime = 0.3, LowerTime = 0.2;
    public double Range = 120, BulletSpeed = 900;
    public double Suppress = 0.02, SuppressRadius = 1.5, SuppressDecay = 2.5;   // spread added by a bullet passing close
    // magazine change from soldier-rig.js RELOAD (seconds)
    public double ReloadMagOut = 0.17, ReloadSeat = 1.02, ReloadBolt = 1.37, ReloadFull = 1.72, ReloadTactical = 1.38;
    public object Clone() => MemberwiseClone();
}

public sealed class DamageBalance
{
    public double Hp = 100;
    // enemy.js DAMAGE (arms are folded into the torso volume)
    public double[] Head = { 100, 120 }, Torso = { 30, 42 }, Legs = { 18, 26 };
    public object Clone() => MemberwiseClone();
}

/// <summary>Body geometry for both stances. Visibility samples three points; bullets hit three vertical volumes.
/// A low wall (1.1 m) hides a crouched soldier completely and blocks his muzzle, and leaves head and chest of a
/// standing one exposed: the peek–shoot–hide loop of docs/bots.md §4.1.</summary>
public sealed class BodyBalance
{
    public double[] StandPoints = { 0.5, 1.15, 1.65 }, CrouchPoints = { 0.3, 0.65, 0.95 };
    public double StandEye = 1.65, CrouchEye = 0.95;
    public double StandMuzzle = 1.45, CrouchMuzzle = 0.9;
    // hit volumes: [top of legs, top of torso, top of head]
    public double[] StandZones = { 0.9, 1.5, 1.8 }, CrouchZones = { 0.4, 0.85, 1.05 };
    public double LegsRadius = 0.17, TorsoRadius = 0.21, HeadRadius = 0.12, CrouchLegsRadius = 0.24;
    public object Clone() => MemberwiseClone();
}

public sealed class PerceptionBalance
{
    public double Hz = 10;                  // perception and decisions per second (ticks are 30 Hz)
    public double ViewRange = 32;           // no further than a player's screen shows
    public double ConeHalf = 60 * DMath.Deg;
    public double ConeEdgeFactor = 0.5;     // detection speed at the edge of the cone relative to its centre
    public double DetectRate = 6;           // detection per second for a fully visible walking target at SizeRef
    public double SizeRef = 8, SizeMax = 1.5, SizeMin = 0.12;
    public double MotionRun = 3, MotionWalk = 1, MotionStill = 0.3;
    public double DetectDecay = 0.6;        // per second while not visible
    public double SuspectLevel = 0.35;      // a contact appears ("something is there") at this level, seen at 1
    public double BushDepth = 0.5;          // concealment: visibility × e^(−depth/BushDepth) through foliage
    public double NoiseAngle = 1.5 * DMath.Deg, NoiseRange = 0.06, NoiseTau = 1.5;  // drifting estimate error
    public double FocusNoise = 0.5;         // error factor after watching a target for FocusTime
    public double FocusTime = 1.0;
    public double ForgetTime = 25, ClearRadius = 3, RadioDelay = 0.5;
    public double LostSpeed = 1.5, MaxUncertainty = 15;
    public double HearShot = 40, HearRun = 10, HearWalk = 4, HearQuiet = 1.5, HearReload = 6;
    public double HearError = 0.15;         // heard position error per metre of distance
    public double WallMuffle = 0.5;         // hearing range factor when a solid wall is in the way
    public int CoverQueries = 8;            // nearest cover points described in the view
    public double CoverRadius = 15;
    public object Clone() => MemberwiseClone();
}
