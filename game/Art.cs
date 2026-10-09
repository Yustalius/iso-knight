using Godot;

/// <summary>Art exported from the three.js concept (tools/export-art.cjs → game/art/): soldier models as .glb and painted
/// textures as PNG. Loaded at run time with GltfDocument, so the project needs no editor import step (the folder has a
/// .gdignore) and runs the same under Xvfb in the cloud. Templates are loaded once and duplicated per soldier.</summary>
static class Art
{
    static readonly Dictionary<string, Node3D> _models = new();
    static readonly Dictionary<string, Texture2D> _textures = new();

    public static void Clear()
    {
        foreach (var n in _models.Values) n.Free();
        _models.Clear(); _textures.Clear(); _fixed.Clear();
    }

    public static string Dir => ProjectSettings.GlobalizePath("res://art");

    /// <summary>A fresh copy of a .glb scene; materials and meshes are shared with the template.</summary>
    public static Node3D Model(string file)
    {
        if (!_models.TryGetValue(file, out var tpl))
        {
            var doc = new GltfDocument();
            var state = new GltfState();
            var err = doc.AppendFromFile(Path.Combine(Dir, file), state);
            if (err != Error.Ok) throw new InvalidOperationException($"cannot load {file}: {err}");
            tpl = (Node3D)doc.GenerateScene(state);
            foreach (var mi in Meshes(tpl)) FixMaterials(mi);
            _models[file] = tpl;
        }
        return (Node3D)tpl.Duplicate();
    }

    public static Texture2D Texture(string file, bool mipmaps = true)
    {
        if (_textures.TryGetValue(file, out var t)) return t;
        var img = Image.LoadFromFile(Path.Combine(Dir, file));
        if (mipmaps) img.GenerateMipmaps();
        return _textures[file] = ImageTexture.CreateFromImage(img);
    }

    public static IEnumerable<MeshInstance3D> Meshes(Node n)
    {
        if (n is MeshInstance3D mi) yield return mi;
        foreach (var c in n.GetChildren())
            foreach (var m in Meshes(c)) yield return m;
    }

    public static Skeleton3D? FindSkeleton(Node n)
    {
        if (n is Skeleton3D s) return s;
        foreach (var c in n.GetChildren()) if (FindSkeleton(c) is { } f) return f;
        return null;
    }

    // The concept draws trilinear + anisotropic, smooth shading, painted 256 px maps that tile where three repeated them.
    static readonly HashSet<string> Tiled = new() { "BDU", "PASGT vest", "Rolled sleeve", "Helmet cover" };
    static readonly HashSet<Material> _fixed = new();

    static void FixMaterials(MeshInstance3D mi)
    {
        var mesh = mi.Mesh;
        for (int s = 0; s < mesh.GetSurfaceCount(); s++)
        {
            if (mesh.SurfaceGetMaterial(s) is not StandardMaterial3D m || !_fixed.Add(m)) continue;
            m.TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic;
            m.TextureRepeat = Tiled.Contains(m.ResourceName);
            if (m.AlbedoTexture is ImageTexture tex && tex.GetImage() is { } img && !img.HasMipmaps())
            {
                img.GenerateMipmaps();
                m.AlbedoTexture = ImageTexture.CreateFromImage(img);
            }
        }
    }
}
