using System.Globalization;
using System.Numerics;
using Illusion.Assets.Bridge;
using Illusion.Assets.Sds;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Frames.Resources;
using Illusion.Formats.Geometry;

namespace Illusion.Assets.Cars;

/// <summary>
/// Lays the picture of BROKEN GLASS over each pane of a car.
///
/// <para>
/// A car's glass is drawn with two texture-coordinate sets. The first lays the pane's dirt (its place in the car's
/// dirt atlas). The SECOND lays the picture of a broken pane (glas_broken: shards round a hole), and on a shipped
/// car it runs across each pane whatever the pane's size or shape - edge to edge on the large ones (measured on
/// smith_stingray_pha: rear window, door panes, windscreen each 0..1), a part of the picture on some small ones
/// (a quarter light of shubert_38: 0..0.29 one way).
/// </para>
/// <para>
/// The Blender bridge shows only the first set; a NEW vertex takes the second from the nearest point of the source
/// mesh's glass. A pane that is larger than the donor's, or stands elsewhere, then reads the donor pane's EDGE
/// over everything that lies beyond it: once broken, its shards run as long streaks (seen in the game on the
/// first generated coupe: rear window 0..0.86 one way, a door pane a third of the picture).
/// </para>
/// <para>
/// This lays the second set anew over every pane - a pane being one connected piece of a glass material - flat
/// onto the pane's own plane, upright, edge to edge; a shipped car's own panes are laid anew like any other. A
/// vertex is laid once, by the largest pane that holds it. Run after the last save; writes the working copy's
/// vertex pools.
/// </para>
/// </summary>
public static class CarGlassUv
{
    /// <summary>The picture's edge is kept a hair inside the pane's, as the shipped cars keep it.</summary>
    private const float Margin = 0.008f;

    /// <summary>How much of a piece's area must look one way for a picture to be laid on it at all.</summary>
    private const float Flat = 0.75f;

    /// <summary>... and for the piece to be taken as one pane without looking closer (a curved windscreen: 0.95 and up).</summary>
    private const float OnePane = 0.92f;

    /// <summary>A part of a piece smaller than this share of it is where two panes meet, not a pane.</summary>
    private const float Sliver = 0.05f;

    /// <summary>Coordinates this near are the same (they are kept as halves: about 0.0005 near 1).</summary>
    private const float Same = 0.002f;

    // A material is glass when it is drawn with the picture of broken glass; one the library does not know (or that
    // has no such picture) by its name.
    private static bool IsGlass(ulong hash, string name)
    {
        if (Materials.MafiaMaterialCatalog.Instance.GetMaterial(hash) is { Resolved: true } info
            && info.TextureSlots.Any(t => (t.TextureName ?? "").StartsWith("glas_broken", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }
        return name.Contains("glass", StringComparison.OrdinalIgnoreCase);
    }

    private static float Area(DecodedMesh mesh, List<int> triangles)
    {
        float area = 0f;
        foreach (int t in triangles) area += Normal(mesh, t).Length();
        return area;
    }

    public sealed record Laid(int Lod, string Material, int Vertices, Vector3 Middle, float Across, float Up, Vector2 WasLow, Vector2 WasHigh);

    public sealed record Report(string Car, IReadOnlyList<Laid> Panes, int Changed, int PoolFilesWritten, IReadOnlyList<string> Notes);

    /// <param name="write">False: report what would change, write nothing.</param>
    public static string? Apply(FileInfo car, bool write, out Report? report)
    {
        report = null;
        ArgumentNullException.ThrowIfNull(car);
        if (!car.Exists) return $"no such archive: {car.FullName}";
        return ApplyIn(SdsMeshLoader.EnsureExtracted(car), car.Name, write, out report);
    }

    /// <inheritdoc cref="Apply(FileInfo, bool, out Report?)"/>
    /// <param name="extracted">The car's extracted folder (a working copy, or a scratch copy of one).</param>
    /// <param name="label">What the car is called in what is said about it.</param>
    public static string? ApplyIn(string extracted, string label, bool write, out Report? report)
    {
        report = null;
        ArgumentException.ThrowIfNullOrEmpty(extracted);
        if (SdsMeshLoader.OpenScene(extracted).FrameResource is not { FrameObjects: not null } frame) return $"{label} has no scene";
        FrameObjectModel? model = frame.FrameObjects.Values.OfType<FrameObjectModel>()
            .OrderByDescending(m => m.GetSkeletonObject().BoneNames?.Length ?? 0).FirstOrDefault();
        if (model == null) return $"{label} has no skinned model - a car's body is one";
        MafiaMaterials.EnsureLoaded();
        var laid = new List<Laid>();
        var notes = new List<string>();
        var dirty = new HashSet<ulong>();
        int total = 0;
        for (int lod = 0; lod < model.Geometry.LOD.Length; lod++)
        {
            DecodedMesh? mesh = SdsMeshLoader.DecodeLod(model, lod);
            if (mesh?.RawVertexData == null) continue;
            if (!mesh.Declaration.HasFlag(VertexFlags.TexCoords1)) { notes.Add($"LOD {lod} carries no second set"); continue; }
            Vertex[] verts = VertexTranslator.DecompressBuffer(
                mesh.RawVertexData, mesh.NumVerts, mesh.Declaration, mesh.DecompressionOffset, mesh.DecompressionFactor);
            // What each vertex had, to say afterwards what changed; and which vertices a pane has taken - a vertex is laid
            // ONCE, by the first pane that holds it (the larger first): two parts of one piece share the vertices along
            // their seam, and glass drawn from both sides shares all of them.
            var was = new Vector2[verts.Length];
            for (int v = 0; v < verts.Length; v++) was[v] = new Vector2((float)verts[v].UVs[1].X, (float)verts[v].UVs[1].Y);
            var taken = new HashSet<int>();
            foreach (MaterialStruct slot in model.Material?.Materials?.ElementAtOrDefault(lod) ?? [])
            {
                string material = MafiaMaterials.GetMaterialName(slot.MaterialHash) ?? $"0x{slot.MaterialHash:X16}";
                if (!IsGlass(slot.MaterialHash, material)) continue;
                int end = Math.Min(slot.StartIndex + (slot.NumFaces * 3), mesh.Indices.Length);
                var all = new List<int>();
                for (int t = slot.StartIndex; t + 2 < end; t += 3)
                {
                    if (mesh.Indices[t] >= verts.Length || mesh.Indices[t + 1] >= verts.Length || mesh.Indices[t + 2] >= verts.Length)
                        return $"LOD {lod} {material}: a triangle names a vertex the buffer does not have";
                    all.Add(t);
                }
                // A far level's glass is the whole cabin's in ONE piece: such a piece is parted by which way its
                // triangles look (ahead, behind, left, right), and each part is a pane - but for the slivers where two
                // ways meet, which are no panes of their own.
                var panes = new List<(List<int> Triangles, float Area)>();
                foreach (List<int> piece in Panes(mesh, all))
                {
                    float whole = Area(mesh, piece);
                    if (OneWay(mesh, piece) >= OnePane) { panes.Add((piece, whole)); continue; }
                    foreach (IGrouping<int, int> way in piece.GroupBy(t => Facing(mesh, t)))
                    {
                        foreach (List<int> part in Panes(mesh, [.. way]))
                        {
                            float area = Area(mesh, part);
                            if (area >= Sliver * whole) panes.Add((part, area));
                        }
                    }
                }
                foreach ((List<int> pane, float area) in panes.OrderByDescending(p => p.Area))
                {
                    // the pane's plane: its triangles' normals, each by its area
                    Vector3 normal = Vector3.Zero, middle = Vector3.Zero;
                    var own = new HashSet<int>();
                    foreach (int t in pane)
                    {
                        normal += Normal(mesh, t);
                        for (int k = 0; k < 3; k++) own.Add((int)mesh.Indices[t + k]);
                    }
                    int[] mine = [.. own.Where(v => !taken.Contains(v))];
                    if (mine.Length == 0) continue;                 // the other side of a pane that is laid already
                    if (area < 1e-8f) { notes.Add($"LOD {lod} {material}: a pane of no area ({own.Count} vertices) is left as it is"); continue; }
                    // A piece that is not ONE pane has no plane to lay a picture on: left with what it has.
                    if (normal.Length() < Flat * area)
                    {
                        notes.Add($"LOD {lod} {material}: a piece of {own.Count} vertices is not one pane (it looks {normal.Length() / area:P0} one way) - left as it is");
                        continue;
                    }
                    normal = Vector3.Normalize(normal);
                    // upright in the picture: the pane's own "up" is the world's, laid into its plane (a pane that lies
                    // flat - a roof light - has none: forwards then)
                    Vector3 up = Vector3.UnitZ - (normal * Vector3.Dot(Vector3.UnitZ, normal));
                    if (up.Length() < 0.2f) up = Vector3.UnitY - (normal * Vector3.Dot(Vector3.UnitY, normal));
                    up = Vector3.Normalize(up);
                    Vector3 across = Vector3.Normalize(Vector3.Cross(up, normal));
                    // (the shipped panes: the picture's left is the car's right on a pane across the car, its rear on a
                    // pane along it)
                    bool acrossCar = MathF.Abs(across.X) >= MathF.Abs(across.Y);
                    if ((acrossCar ? across.X : across.Y) < 0f) across = -across;
                    // the picture spans the WHOLE pane, whoever has taken some of its vertices
                    float a0 = float.MaxValue, a1 = float.MinValue, b0 = float.MaxValue, b1 = float.MinValue;
                    foreach (int v in own)
                    {
                        float a = Vector3.Dot(mesh.Positions[v], across), b = Vector3.Dot(mesh.Positions[v], up);
                        a0 = MathF.Min(a0, a); a1 = MathF.Max(a1, a); b0 = MathF.Min(b0, b); b1 = MathF.Max(b1, b);
                    }
                    if (a1 - a0 < 1e-4f || b1 - b0 < 1e-4f) { notes.Add($"LOD {lod} {material}: a pane with no width is left as it is"); continue; }
                    var wasLow = new Vector2(float.MaxValue);
                    var wasHigh = new Vector2(float.MinValue);
                    foreach (int v in mine)
                    {
                        float a = (Vector3.Dot(mesh.Positions[v], across) - a0) / (a1 - a0);
                        float b = (Vector3.Dot(mesh.Positions[v], up) - b0) / (b1 - b0);
                        float u = Margin + ((1f - (2f * Margin)) * (acrossCar ? 1f - a : a));
                        float w = Margin + ((1f - (2f * Margin)) * (1f - b));
                        verts[v].UVs[1] = new Half2(u, w);
                        taken.Add(v);
                        middle += mesh.Positions[v];
                        wasLow = Vector2.Min(wasLow, was[v]);
                        wasHigh = Vector2.Max(wasHigh, was[v]);
                    }
                    laid.Add(new Laid(lod, material, mine.Length, middle / mine.Length, a1 - a0, b1 - b0, wasLow, wasHigh));
                }
            }
            // (against what the vertex HAD, as its buffer keeps it - a half - so a second run finds nothing to do)
            int[] changed = [.. taken.Where(v => MathF.Abs((float)verts[v].UVs[1].X - was[v].X) > Same || MathF.Abs((float)verts[v].UVs[1].Y - was[v].Y) > Same)];
            if (changed.Length == 0) continue;
            total += changed.Length;
            if (!write) continue;

            // Only the vertices that changed are written: the rest of the buffer keeps its own bytes.
            byte[] packed = VertexCompressor.CompressBuffer(
                mesh.RawVertexData, verts, mesh.Declaration, mesh.DecompressionOffset, mesh.DecompressionFactor);
            VertexBuffer? buffer = model.GetVertexBuffer(lod);
            int stride = mesh.Stride;
            if (buffer?.Data == null || packed.Length < verts.Length * stride || buffer.Data.Length < verts.Length * stride)
            {
                return $"LOD {lod}: the vertex buffer is not the size its level says";
            }
            foreach (int v in changed) Array.Copy(packed, v * stride, buffer.Data, v * stride, stride);
            dirty.Add(model.Geometry.LOD[lod].VertexBufferRef.Hash);
        }
        if (laid.Count == 0) notes.Add("no glass was found: a material counts as glass when it is drawn with the broken-glass picture (glas_broken) or has 'glass' in its name");
        int files = write && dirty.Count > 0 ? SdsGeometrySaver.SaveDirtyPools(frame, dirty, []) : 0;
        report = new Report(label, laid, total, files, notes);
        return null;
    }

    // How much of a piece's area looks one way: 1 for a flat pane, little for glass that goes round a cabin.
    private static float OneWay(DecodedMesh mesh, List<int> triangles)
    {
        Vector3 sum = Vector3.Zero;
        float area = 0f;
        foreach (int t in triangles)
        {
            Vector3 n = Normal(mesh, t);
            sum += n;
            area += n.Length();
        }
        return area > 0f ? sum.Length() / area : 0f;
    }

    private static Vector3 Normal(DecodedMesh mesh, int t)
    {
        Vector3 a = mesh.Positions[mesh.Indices[t]], b = mesh.Positions[mesh.Indices[t + 1]], c = mesh.Positions[mesh.Indices[t + 2]];
        return Vector3.Cross(b - a, c - a);
    }

    // Which way a triangle looks, flat on the ground: 0 ahead, 1 behind, 2 right, 3 left.
    private static int Facing(DecodedMesh mesh, int t)
    {
        Vector3 n = Normal(mesh, t);
        return MathF.Abs(n.Y) >= MathF.Abs(n.X) ? (n.Y >= 0f ? 0 : 1) : (n.X >= 0f ? 2 : 3);
    }

    // The connected pieces of some triangles of a mesh (each named by the index of its first corner). Corners count as
    // one where they stand at one place: a pane's vertices are split wherever its normals or its first coordinates
    // break.
    private static List<List<int>> Panes(DecodedMesh mesh, List<int> triangles)
    {
        var place = new Dictionary<(int, int, int), int>();
        var parent = new List<int>();
        int Find(int i)
        {
            while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; }
            return i;
        }
        int Spot(uint v)
        {
            Vector3 p = mesh.Positions[v];
            var key = ((int)MathF.Round(p.X * 2000f), (int)MathF.Round(p.Y * 2000f), (int)MathF.Round(p.Z * 2000f));
            if (!place.TryGetValue(key, out int id)) { place[key] = id = parent.Count; parent.Add(id); }
            return id;
        }
        var first = new List<(int Triangle, int Spot)>();
        foreach (int t in triangles)
        {
            int a = Spot(mesh.Indices[t]), b = Spot(mesh.Indices[t + 1]), c = Spot(mesh.Indices[t + 2]);
            parent[Find(b)] = Find(a);
            parent[Find(c)] = Find(a);
            first.Add((t, a));
        }
        return [.. first.GroupBy(f => Find(f.Spot)).Select(g => g.Select(f => f.Triangle).ToList())];
    }

    /// <summary>One pane as a level of detail has it: the vertices of one glass material that ride one bone.</summary>
    public sealed record Pane(int Lod, string Material, string Bone, int Vertices, Vector3 Low, Vector3 High,
        IReadOnlyList<(Vector2 Low, Vector2 High)> Sets, IReadOnlyList<(Vector3 U, Vector3 V)> Slopes);

    /// <summary>What every pane of a car carries, read from the working copy; nothing is written.</summary>
    public static string? Survey(FileInfo car, out IReadOnlyList<Pane> panes)
    {
        panes = [];
        ArgumentNullException.ThrowIfNull(car);
        if (!car.Exists) return $"no such archive: {car.FullName}";
        return SurveyIn(SdsMeshLoader.EnsureExtracted(car), car.Name, out panes);
    }

    /// <inheritdoc cref="Survey(FileInfo, out IReadOnlyList{Pane})"/>
    public static string? SurveyIn(string extracted, string label, out IReadOnlyList<Pane> panes)
    {
        panes = [];
        ArgumentException.ThrowIfNullOrEmpty(extracted);
        if (SdsMeshLoader.OpenScene(extracted).FrameResource is not { FrameObjects: not null } frame) return $"{label} has no scene";
        FrameObjectModel? model = frame.FrameObjects.Values.OfType<FrameObjectModel>()
            .OrderByDescending(m => m.GetSkeletonObject().BoneNames?.Length ?? 0).FirstOrDefault();
        if (model == null) return $"{label} has no skinned model - a car's body is one";
        string[] names = [.. (model.GetSkeletonObject().BoneNames ?? []).Select(n => n.ToString() ?? "")];
        MafiaMaterials.EnsureLoaded();
        var found = new List<Pane>();
        for (int lod = 0; lod < model.Geometry.LOD.Length; lod++)
        {
            DecodedMesh? mesh = SdsMeshLoader.DecodeLod(model, lod);
            if (mesh?.RawVertexData == null || mesh.BoneWeights == null) continue;
            byte[]? bones = SdsMeshLoader.GlobalBoneIds(model, lod);
            if (bones == null) continue;
            Vertex[] verts = VertexTranslator.DecompressBuffer(
                mesh.RawVertexData, mesh.NumVerts, mesh.Declaration, mesh.DecompressionOffset, mesh.DecompressionFactor);
            int sets = 1 + (mesh.Declaration.HasFlag(VertexFlags.TexCoords1) ? 1 : 0) + (mesh.Declaration.HasFlag(VertexFlags.TexCoords2) ? 1 : 0);
            foreach (MaterialStruct slot in model.Material?.Materials?.ElementAtOrDefault(lod) ?? [])
            {
                string material = MafiaMaterials.GetMaterialName(slot.MaterialHash) ?? $"0x{slot.MaterialHash:X16}";
                if (!IsGlass(slot.MaterialHash, material)) continue;
                var byBone = new SortedDictionary<string, HashSet<int>>(StringComparer.Ordinal);
                int end = Math.Min(slot.StartIndex + (slot.NumFaces * 3), mesh.Indices.Length);
                for (int i = slot.StartIndex; i < end; i++)
                {
                    int v = (int)mesh.Indices[i];
                    string bone = BoneOf(mesh, bones, names, v);
                    if (!byBone.TryGetValue(bone, out HashSet<int>? set)) byBone[bone] = set = [];
                    set.Add(v);
                }
                foreach ((string bone, HashSet<int> set) in byBone)
                {
                    var low = new Vector3(float.MaxValue);
                    var high = new Vector3(float.MinValue);
                    foreach (int v in set) { low = Vector3.Min(low, mesh.Positions[v]); high = Vector3.Max(high, mesh.Positions[v]); }
                    var boxes = new List<(Vector2, Vector2)>();
                    var slopes = new List<(Vector3, Vector3)>();
                    for (int s = 0; s < sets; s++)
                    {
                        var a = new Vector2(float.MaxValue);
                        var b = new Vector2(float.MinValue);
                        foreach (int v in set)
                        {
                            var uv = new Vector2((float)verts[v].UVs[s].X, (float)verts[v].UVs[s].Y);
                            a = Vector2.Min(a, uv);
                            b = Vector2.Max(b, uv);
                        }
                        boxes.Add((a, b));
                        slopes.Add((Slope(mesh, verts, set, s, true), Slope(mesh, verts, set, s, false)));
                    }
                    found.Add(new Pane(lod, material, bone, set.Count, low, high, boxes, slopes));
                }
            }
        }
        panes = found;
        return null;
    }

    internal static string BoneOf(DecodedMesh mesh, byte[] bones, string[] names, int v)
    {
        int best = 0;
        for (int k = 1; k < 4; k++) if (mesh.BoneWeights![(v * 4) + k] > mesh.BoneWeights[(v * 4) + best]) best = k;
        int index = bones[(v * 4) + best];
        return mesh.BoneWeights![(v * 4) + best] > 0f && index < names.Length ? names[index] : "";
    }

    // How fast one coordinate of a set changes along x, y and z over a pane (covariance over the axis's variance:
    // rough, each axis by itself - enough to say which way the picture lies).
    private static Vector3 Slope(DecodedMesh mesh, Vertex[] verts, HashSet<int> set, int uvSet, bool u)
    {
        Vector3 mean = Vector3.Zero;
        float meanT = 0f;
        foreach (int v in set)
        {
            mean += mesh.Positions[v];
            meanT += u ? (float)verts[v].UVs[uvSet].X : (float)verts[v].UVs[uvSet].Y;
        }
        mean /= set.Count;
        meanT /= set.Count;
        Vector3 cov = Vector3.Zero, var = Vector3.Zero;
        foreach (int v in set)
        {
            Vector3 d = mesh.Positions[v] - mean;
            float t = (u ? (float)verts[v].UVs[uvSet].X : (float)verts[v].UVs[uvSet].Y) - meanT;
            cov += d * t;
            var += d * d;
        }
        return new Vector3(var.X > 1e-6f ? cov.X / var.X : 0f, var.Y > 1e-6f ? cov.Y / var.Y : 0f, var.Z > 1e-6f ? cov.Z / var.Z : 0f);
    }

    internal static string Text(Vector2 v) => string.Create(CultureInfo.InvariantCulture, $"({v.X:0.###}, {v.Y:0.###})");

    internal static string Text(Vector3 v) => string.Create(CultureInfo.InvariantCulture, $"({v.X:0.##}, {v.Y:0.##}, {v.Z:0.##})");
}
