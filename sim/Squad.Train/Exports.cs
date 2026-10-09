using System.Runtime.InteropServices;
using System.Text;

namespace Squad.Train;

/// <summary>C ABI of libsquad.so (Native AOT) for Python ctypes. Every buffer belongs to the caller (numpy arrays);
/// nothing is copied. Functions return 0 (or a handle &gt; 0) on success and −1 on error; squad_error gives the text.
/// No exception may cross this boundary: it would kill the Python process.</summary>
public static unsafe class Exports
{
    static readonly Dictionary<int, VecEnv> Envs = new();
    static int _next = 1;
    [ThreadStatic] static string? _error;

    static int Fail(Exception e) { _error = e.ToString(); return -1; }

    static VecEnv Get(int h)
    {
        lock (Envs) return Envs.TryGetValue(h, out var e) ? e : throw new ArgumentException($"No environment {h}.");
    }

    /// <summary>Copy the last error message (UTF-8, NUL-terminated) into buf. Returns its full length.</summary>
    [UnmanagedCallersOnly(EntryPoint = "squad_error")]
    public static int Error(byte* buf, int len)
    {
        var bytes = Encoding.UTF8.GetBytes(_error ?? "");
        int n = Math.Min(bytes.Length, Math.Max(0, len - 1));
        for (int i = 0; i < n; i++) buf[i] = bytes[i];
        if (len > 0) buf[n] = 0;
        return bytes.Length;
    }

    [UnmanagedCallersOnly(EntryPoint = "squad_version")]
    public static int Version() => Obs.Version * 100 + Act.Version;

    /// <summary>Create environments from a JSON config (UTF-8). Paths in it are relative to baseDir.
    /// sizes receives: slots, obs size, mask size, heads, head sizes (6), info size, envs, learners per env.</summary>
    [UnmanagedCallersOnly(EntryPoint = "squad_create")]
    public static int Create(byte* json, byte* baseDir, int* sizes)
    {
        try
        {
            var cfg = EnvConfig.Parse(Marshal.PtrToStringUTF8((nint)json)!, Marshal.PtrToStringUTF8((nint)baseDir) ?? ".");
            var env = new VecEnv(cfg);
            sizes[0] = env.Slots; sizes[1] = Obs.Size; sizes[2] = Act.MaskSize; sizes[3] = Act.NumHeads;
            for (int i = 0; i < Act.NumHeads; i++) sizes[4 + i] = Act.Heads[i];
            sizes[10] = VecEnv.InfoSize; sizes[11] = env.Envs; sizes[12] = env.MaxLearners;
            lock (Envs) { int h = _next++; Envs[h] = env; return h; }
        }
        catch (Exception e) { return Fail(e); }
    }

    [UnmanagedCallersOnly(EntryPoint = "squad_reset")]
    public static int Reset(int h, float* obs, byte* masks, byte* alive)
    {
        try { Get(h).Reset(obs, masks, alive); return 0; }
        catch (Exception e) { return Fail(e); }
    }

    /// <summary>actions: slots × heads int32. Writes obs, masks, rewards, dones, alive per slot and info per env.</summary>
    [UnmanagedCallersOnly(EntryPoint = "squad_step")]
    public static int Step(int h, int* actions, float* obs, byte* masks, float* rewards, byte* dones, byte* alive, float* info)
    {
        try { Get(h).Step(actions, obs, masks, rewards, dones, alive, info); return 0; }
        catch (Exception e) { return Fail(e); }
    }

    [UnmanagedCallersOnly(EntryPoint = "squad_set_shaping")]
    public static int SetShaping(int h, double shaping)
    {
        try { Get(h).Shaping = shaping; return 0; }
        catch (Exception e) { return Fail(e); }
    }

    /// <summary>Write env e's next whole episode to a replay file (view it with Squad.Tools render --replay).</summary>
    [UnmanagedCallersOnly(EntryPoint = "squad_record")]
    public static int Record(int h, int e, byte* path)
    {
        try { Get(h).RecordNext(e, Marshal.PtrToStringUTF8((nint)path)!); return 0; }
        catch (Exception e2) { return Fail(e2); }
    }

    [UnmanagedCallersOnly(EntryPoint = "squad_destroy")]
    public static int Destroy(int h)
    {
        lock (Envs) return Envs.Remove(h) ? 0 : -1;
    }
}
