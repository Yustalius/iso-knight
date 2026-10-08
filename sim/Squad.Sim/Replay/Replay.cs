using System.IO.Compression;
using System.Text;

namespace Squad.Sim;

/// <summary>A recorded match: the config (as JSON written by the caller), the seed, every action of every tick and a state
/// hash every second. Replaying re-simulates and checks the hashes, so a replay from the trainer on Linux can be verified
/// on a Windows PC. File = gzip("SQRP", version, header JSON, ticks…).</summary>
public sealed class Replay
{
    public const int FormatVersion = 1;
    public const int HashEvery = Match.TickRate;
    public string Header = "";
    public int Agents;
    public readonly List<AgentAction[]> Ticks = new();
    public readonly List<ulong> Hashes = new();   // after tick (k+1)·HashEvery
    public ulong FinalHash;

    /// <summary>Record the actions given to a match for one step; call right after Step.</summary>
    public void Record(Match m, ReadOnlySpan<AgentAction> actions)
    {
        Agents = m.Count;
        Ticks.Add(actions.Slice(0, m.Count).ToArray());
        if (m.Tick % HashEvery == 0) Hashes.Add(m.Hash());
    }

    public void Finish(Match m) => FinalHash = m.Hash();

    /// <summary>Re-run the actions on a fresh match and compare hashes. Returns the first tick that diverged, or −1.</summary>
    public int Verify(Match fresh)
    {
        int h = 0;
        foreach (var acts in Ticks)
        {
            fresh.Step(acts);
            if (fresh.Tick % HashEvery == 0 && h < Hashes.Count)
            {
                if (fresh.Hash() != Hashes[h]) return fresh.Tick;
                h++;
            }
        }
        return fresh.Hash() == FinalHash ? -1 : fresh.Tick;
    }

    public void Save(string path)
    {
        using var fs = File.Create(path);
        using var gz = new GZipStream(fs, CompressionLevel.Optimal);
        using var w = new BinaryWriter(gz, Encoding.UTF8);
        w.Write(Encoding.ASCII.GetBytes("SQRP"));
        w.Write(FormatVersion);
        w.Write(Header);
        w.Write(Agents);
        w.Write(Ticks.Count);
        foreach (var acts in Ticks)
            foreach (var a in acts) Write(w, a);
        w.Write(Hashes.Count);
        foreach (var h in Hashes) w.Write(h);
        w.Write(FinalHash);
    }

    public static Replay Load(string path)
    {
        using var fs = File.OpenRead(path);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var r = new BinaryReader(gz, Encoding.UTF8);
        if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "SQRP") throw new InvalidDataException("Not a replay.");
        int ver = r.ReadInt32();
        if (ver != FormatVersion) throw new InvalidDataException($"Replay version {ver}, expected {FormatVersion}.");
        var rp = new Replay { Header = r.ReadString(), Agents = r.ReadInt32() };
        int n = r.ReadInt32();
        for (int t = 0; t < n; t++)
        {
            var acts = new AgentAction[rp.Agents];
            for (int i = 0; i < rp.Agents; i++) acts[i] = Read(r);
            rp.Ticks.Add(acts);
        }
        int hc = r.ReadInt32();
        for (int k = 0; k < hc; k++) rp.Hashes.Add(r.ReadUInt64());
        rp.FinalHash = r.ReadUInt64();
        return rp;
    }

    static void Write(BinaryWriter w, in AgentAction a)
    {
        w.Write((byte)a.Move); w.Write(a.Target.X); w.Write(a.Target.Y);
        w.Write((byte)a.Mode); w.Write((byte)a.Stance); w.Write(a.AvoidThreats);
        w.Write((byte)a.Aim); w.Write(a.AimAt); w.Write(a.AimPoint.X); w.Write(a.AimPoint.Y); w.Write(a.AimHeight);
        w.Write((byte)a.Trigger); w.Write(a.Reload);
    }

    static AgentAction Read(BinaryReader r) => new()
    {
        Move = (MoveKind)r.ReadByte(), Target = new Vec2(r.ReadDouble(), r.ReadDouble()),
        Mode = (MoveMode)r.ReadByte(), Stance = (Stance)r.ReadByte(), AvoidThreats = r.ReadBoolean(),
        Aim = (AimKind)r.ReadByte(), AimAt = r.ReadInt32(), AimPoint = new Vec2(r.ReadDouble(), r.ReadDouble()), AimHeight = r.ReadDouble(),
        Trigger = (Trigger)r.ReadByte(), Reload = r.ReadBoolean()
    };
}
