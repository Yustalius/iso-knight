using Godot;

/// <summary>The rifle sling of soldier-rig.js: a free verlet strap between the front and stock swivels, simulated in
/// world space (so it swings with the body's motion and still hangs from a dropped rifle), drawn as a ribbon that keeps
/// its flat side toward the camera.</summary>
sealed class Sling
{
    const int N = 12;
    const float Length = 0.92f, HalfWidth = 0.016f;
    readonly Vector3[] _p = new Vector3[N], _q = new Vector3[N];
    bool _init;
    readonly ImmediateMesh _mesh = new();
    public readonly MeshInstance3D Node;

    static StandardMaterial3D? _mat0, _mat1;

    public Sling(Node3D parent, int team)
    {
        ref var mat = ref (team == 0 ? ref _mat0 : ref _mat1);
        mat ??= new StandardMaterial3D
        {
            AlbedoTexture = Art.Texture(team == 0 ? "tex/soldier_m81_sling.png" : "tex/soldier_opfor_sling.png"),
            Roughness = 0.96f, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
        };
        Node = new MeshInstance3D { Mesh = _mesh, MaterialOverride = mat };
        parent.AddChild(Node);
    }

    public static void ClearMaterials() { _mat0 = _mat1 = null; }

    /// <summary>a: front swivel, b: stock swivel (world); view: camera direction.</summary>
    public void Step(float dt, Vector3 a, Vector3 b, Vector3 view)
    {
        if (!_init)
        {
            for (int i = 0; i < N; i++) { _p[i] = a.Lerp(b, i / (N - 1f)); _p[i].Y -= MathF.Sin(MathF.PI * i / (N - 1)) * 0.18f; _q[i] = _p[i]; }
            _init = true;
        }
        if (dt > 0)
        {
            float seg = Length / (N - 1);
            const int sub = 3; float h = MathF.Min(dt, 1 / 30f) / sub;
            for (int k = 0; k < sub; k++)
            {
                for (int i = 0; i < N; i++)
                {
                    var v = (_p[i] - _q[i]) * 0.985f;
                    _q[i] = _p[i];
                    _p[i] += v + new Vector3(0, -9.8f * h * h, 0);
                    if (_p[i].Y < 0.015f) _p[i].Y = 0.015f;
                }
                _p[0] = a; _p[N - 1] = b;
                for (int it = 0; it < 10; it++)
                    for (int i = 0; i < N - 1; i++)
                    {
                        var d = _p[i + 1] - _p[i]; float L = d.Length();
                        if (L <= seg || L < 1e-6f) continue;              // a strap only resists stretching
                        var c = d * ((L - seg) / L);
                        bool pinP = i == 0, pinQ = i + 1 == N - 1;
                        if (pinP) _p[i + 1] -= c; else if (pinQ) _p[i] += c; else { _p[i] += c * 0.5f; _p[i + 1] -= c * 0.5f; }
                    }
            }
        }
        else { _p[0] = a; _p[N - 1] = b; }

        _mesh.ClearSurfaces();
        _mesh.SurfaceBegin(Mesh.PrimitiveType.TriangleStrip);
        for (int i = 0; i < N; i++)
        {
            var t = _p[Math.Min(N - 1, i + 1)] - _p[Math.Max(0, i - 1)];
            var w = t.Cross(view).Normalized() * HalfWidth;
            var n = w.Cross(t).Normalized();
            if (n.Dot(view) > 0) n = -n;
            float v = i / (float)N * 4;
            _mesh.SurfaceSetNormal(n); _mesh.SurfaceSetUV(new Vector2(0, v)); _mesh.SurfaceAddVertex(_p[i] - w);
            _mesh.SurfaceSetNormal(n); _mesh.SurfaceSetUV(new Vector2(0.3f, v)); _mesh.SurfaceAddVertex(_p[i] + w);
        }
        _mesh.SurfaceEnd();
    }
}
