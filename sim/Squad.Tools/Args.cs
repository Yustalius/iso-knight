using System.Globalization;

namespace Squad.Tools;

/// <summary>--key value / --flag command-line options.</summary>
sealed class Args
{
    readonly Dictionary<string, string> _kv = new();
    public readonly List<string> Positional = new();

    public static Args Parse(IEnumerable<string> args)
    {
        var a = new Args();
        var list = args.ToList();
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].StartsWith("--"))
            {
                string k = list[i][2..];
                if (i + 1 < list.Count && !list[i + 1].StartsWith("--")) a._kv[k] = list[++i];
                else a._kv[k] = "true";
            }
            else a.Positional.Add(list[i]);
        }
        return a;
    }

    public bool Has(string k) => _kv.ContainsKey(k);
    public bool Flag(string k) => _kv.TryGetValue(k, out var v) && v != "false";
    public string Get(string k, string def) => _kv.TryGetValue(k, out var v) ? v : def;
    public string Req(string k) => _kv.TryGetValue(k, out var v) ? v : throw new ArgumentException($"--{k} is required");
    public double Num(string k, double def) => _kv.TryGetValue(k, out var v) ? double.Parse(v, CultureInfo.InvariantCulture) : def;
    public ulong U64(string k, ulong def) => _kv.TryGetValue(k, out var v) ? ulong.Parse(v, CultureInfo.InvariantCulture) : def;

    /// <summary>"7v5" → (7, 5).</summary>
    public static (int, int) Sizes(string s)
    {
        var p = s.ToLowerInvariant().Split('v', 'x', '×');
        if (p.Length != 2) throw new ArgumentException($"Team sizes look like 7v5, got '{s}'.");
        int a = int.Parse(p[0]), b = int.Parse(p[1]);
        if (a < 1 || b < 1 || a > 10 || b > 10) throw new ArgumentException("Team sizes are 1..10.");
        return (a, b);
    }
}
