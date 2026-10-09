using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;

namespace Squad.Sim;

/// <summary>JSON for maps, balance and match configs. Hand-written readers keep the files short and the errors clear.</summary>
public static class SimJson
{
    // ---------- maps ----------

    /// <summary>{ "name", "width", "height", "obstacles": [{ "kind": "highwall", "a": [x, y], "b": [x, y], "r"?, "h"? }],
    /// "spawns": [[x0, y0, x1, y1], …], "zones": [{ "name", "at": [x, y], "r" }] }. A missing "b" makes a round prop at "a".</summary>
    public static MapData ReadMap(string json) => ReadMap(JsonNode.Parse(json)!.AsObject());

    public static MapData ReadMap(JsonObject o)
    {
        var m = new MapData
        {
            Name = (string?)o["name"] ?? "map",
            Width = Num(o, "width", 30),
            Height = Num(o, "height", 30)
        };
        foreach (var n in o["obstacles"]?.AsArray() ?? new JsonArray())
        {
            var ob = n!.AsObject();
            var kind = ParseKind((string?)ob["kind"] ?? "crate");
            Vec2 a = V(ob["a"]), b = ob["b"] is null ? a : V(ob["b"]);
            m.Obstacles.Add(Obstacle.Make(kind, a, b, Num(ob, "r", -1), Num(ob, "h", -1)));
        }
        foreach (var n in o["spawns"]?.AsArray() ?? new JsonArray())
        {
            var r = n!.AsArray();
            m.Spawns.Add(new SpawnZone { Min = new Vec2(D(r[0]), D(r[1])), Max = new Vec2(D(r[2]), D(r[3])) });
        }
        foreach (var n in o["zones"]?.AsArray() ?? new JsonArray())
        {
            var z = n!.AsObject();
            m.Zones.Add(new Zone { Name = (string?)z["name"] ?? "zone", Center = V(z["at"]), Radius = Num(z, "r", 2) });
        }
        return m;
    }

    /// <summary>Map as JSON. Rounded to millimetres for hand-editing; exact (round = false) inside replay headers.</summary>
    public static string WriteMap(MapData m, bool round = true) => MapNode(m, round).ToJsonString();

    static JsonObject MapNode(MapData m, bool round)
    {
        double R(double x) => round ? Math.Round(x, 3) : x;
        JsonArray Arr(Vec2 v) => new(R(v.X), R(v.Y));
        var o = new JsonObject { ["name"] = m.Name, ["width"] = R(m.Width), ["height"] = R(m.Height) };
        var obs = new JsonArray();
        foreach (var x in m.Obstacles)
        {
            var (dr, dh) = Obstacle.Defaults(x.Kind);
            var j = new JsonObject { ["kind"] = x.Kind.ToString().ToLowerInvariant(), ["a"] = Arr(x.A) };
            if (x.B != x.A) j["b"] = Arr(x.B);
            if (Math.Abs(x.R - dr) > 1e-9) j["r"] = R(x.R);
            if (Math.Abs(x.H - dh) > 1e-9) j["h"] = R(x.H);
            obs.Add((JsonNode)j);
        }
        o["obstacles"] = obs;
        var sp = new JsonArray();
        foreach (var s in m.Spawns) sp.Add((JsonNode)new JsonArray(R(s.Min.X), R(s.Min.Y), R(s.Max.X), R(s.Max.Y)));
        o["spawns"] = sp;
        var zs = new JsonArray();
        foreach (var z in m.Zones) zs.Add((JsonNode)new JsonObject { ["name"] = z.Name, ["at"] = Arr(z.Center), ["r"] = R(z.Radius) });
        o["zones"] = zs;
        return o;
    }

    // ---------- whole match configs (replay headers) ----------

    public static string WriteConfig(MatchConfig c)
    {
        var o = new JsonObject
        {
            ["seed"] = c.Seed.ToString(CultureInfo.InvariantCulture),
            ["randomizeBalance"] = c.RandomizeBalance,
            ["rules"] = Fields(c.Rules),
            ["balance"] = Fields(c.Balance),
            ["map"] = MapNode(c.Map, false)
        };
        var teams = new JsonArray();
        foreach (var t in c.Teams)
        {
            var ms = new JsonArray();
            foreach (var a in t.Members) ms.Add((JsonNode)Fields(a));
            teams.Add((JsonNode)ms);
        }
        o["teams"] = teams;
        return o.ToJsonString();
    }

    public static MatchConfig ReadConfig(string json)
    {
        var o = JsonNode.Parse(json)!.AsObject();
        var c = new MatchConfig
        {
            Seed = ulong.Parse((string)o["seed"]!, CultureInfo.InvariantCulture),
            RandomizeBalance = D(o["randomizeBalance"]),
            Map = ReadMap(o["map"]!.AsObject())
        };
        Apply(c.Rules, o["rules"] as JsonObject);
        Apply(c.Balance, o["balance"] as JsonObject);
        foreach (var t in o["teams"]!.AsArray())
        {
            var team = new TeamSetup();
            foreach (var a in t!.AsArray()) { var s = new AgentSetup(); Apply(s, a as JsonObject); team.Members.Add(s); }
            c.Teams.Add(team);
        }
        return c;
    }

    /// <summary>Public fields of an object as JSON (numbers exact), recursing into nested classes.</summary>
    static JsonObject Fields(object x)
    {
        var o = new JsonObject();
        foreach (var f in x.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            var v = f.GetValue(x);
            o[f.Name] = v switch
            {
                null => null,
                double d => d,
                int i => i,
                ulong u => u,
                bool b => b,
                string s => s,
                double[] arr => new JsonArray(arr.Select(z => (JsonNode)z).ToArray()),
                _ when f.FieldType.IsClass => Fields(v),
                _ => throw new NotSupportedException($"{x.GetType().Name}.{f.Name}")
            };
        }
        return o;
    }

    public static ObstacleKind ParseKind(string s) => s.ToLowerInvariant() switch
    {
        "highwall" or "wall" => ObstacleKind.HighWall,
        "lowwall" or "sandbags" => ObstacleKind.LowWall,
        "crate" => ObstacleKind.Crate,
        "bush" => ObstacleKind.Bush,
        "tree" or "pillar" => ObstacleKind.Tree,
        _ => throw new FormatException($"Unknown obstacle kind '{s}'.")
    };

    // ---------- overrides ----------

    /// <summary>Copy JSON values onto an object's public fields by name (case-insensitive), recursing into nested objects.
    /// Used for balance.json and the "balance"/"rules"/"setup" sections of scenarios.</summary>
    public static void Apply(object target, JsonObject? json)
    {
        if (json is null) return;
        var t = target.GetType();
        foreach (var (key, val) in json)
        {
            var f = t.GetFields(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(x => string.Equals(x.Name, key, StringComparison.OrdinalIgnoreCase))
                ?? throw new FormatException($"{t.Name} has no field '{key}'.");
            var ft = f.FieldType;
            if (val is null) { f.SetValue(target, null); continue; }
            if (ft == typeof(double)) f.SetValue(target, D(val));
            else if (ft == typeof(int)) f.SetValue(target, (int)D(val));
            else if (ft == typeof(ulong)) f.SetValue(target, (ulong)D(val));
            else if (ft == typeof(bool)) f.SetValue(target, (bool)val);
            else if (ft == typeof(string)) f.SetValue(target, (string?)val);
            else if (ft == typeof(double[])) f.SetValue(target, val.AsArray().Select(D).ToArray());
            else if (ft.IsClass && val is JsonObject jo) Apply(f.GetValue(target) ?? throw new FormatException($"{key} is null"), jo);
            else throw new FormatException($"Cannot set {t.Name}.{f.Name} from JSON.");
        }
    }

    // ---------- helpers ----------

    public static double D(JsonNode? n) => n is null ? 0 : n.GetValueKind() == System.Text.Json.JsonValueKind.String
        ? double.Parse((string)n!, CultureInfo.InvariantCulture) : (double)n;
    public static double Num(JsonObject o, string key, double def) => o[key] is { } n ? D(n) : def;
    public static Vec2 V(JsonNode? n) { var a = n!.AsArray(); return new Vec2(D(a[0]), D(a[1])); }
}
