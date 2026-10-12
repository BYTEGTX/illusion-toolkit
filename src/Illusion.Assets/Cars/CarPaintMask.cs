using System.Globalization;
using System.Numerics;
using Illusion.Assets.Bridge;
using Illusion.Assets.Sds;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Frames.Resources;
using Illusion.Formats.Geometry;

namespace Illusion.Assets.Cars;

/// <summary>
/// Which of a car's TWO PAINTS each piece of its body takes.
///
/// <para>
/// A car is painted in two colours, and which one a piece takes is not in any texture: it is the first colour of
/// the body's vertices. Measured on smith_stingray_pha (<c>--experiment-car-paint-mask</c>): every vertex of the
/// paint carries (255, 0, 0, 255) - the first paint - or (0, 255, 0, 255) - the second, and the second is exactly
/// the cabin's top: pillars and roof, down to the foot of the glass.
/// </para>
/// <para>
/// The Blender bridge does not show that channel, and a NEW vertex takes it from the nearest vertex of the source
/// mesh's paint. A new body's roof then carries the donor's roof where it happens to lie near it and the donor's
/// flank elsewhere: the second paint shows in patches the size of the new body's faces (seen in the game on the
/// first generated coupe: "покраска крыши 2ой цвет идет квадратами").
/// </para>
/// <para>
/// This writes the mask anew over the whole paint: the second colour on what lies ABOVE a line drawn along the
/// car's side (the foot of the glass), between two places along the car; the first on everything else. A triangle
/// is judged by its middle, and a vertex takes the second colour only when every painted triangle it is a corner
/// of does - so the two paints meet in a hard line wherever the mesh has its vertices doubled along the line (the
/// recipe splits them there before the push: carkit.twotone), and blend across one row of faces where it has not.
/// Run after the last save; writes the working copy's vertex pools.
/// </para>
/// </summary>
public static class CarPaintMask
{
    private static readonly byte[] First = [255, 0, 0, 255];
    private static readonly byte[] Second = [0, 255, 0, 255];

    public sealed record Level(int Lod, string Material, int FirstVertices, int SecondVertices, int OnTheLine, int Changed);

    public sealed record Report(string Car, IReadOnlyList<Level> Levels, int Changed, int PoolFilesWritten, IReadOnlyList<string> Notes);

    /// <param name="materials">The paint's materials by name (the near level's and the far level's).</param>
    /// <param name="from">Where along the car the second paint begins and ends (y, either order).</param>
    /// <param name="line">The line it lies above, as (y, z) points along the car's side.</param>
    /// <param name="write">False: report what would change, write nothing.</param>
    public static string? Apply(FileInfo car, IReadOnlyCollection<string> materials, float from, float to,
        IReadOnlyList<(float Y, float Z)> line, bool write, out Report? report)
    {
        report = null;
        ArgumentNullException.ThrowIfNull(car);
        if (!car.Exists) return $"no such archive: {car.FullName}";
        return ApplyIn(SdsMeshLoader.EnsureExtracted(car), car.Name, materials, from, to, line, write, out report);
    }

    /// <inheritdoc cref="Apply(FileInfo, IReadOnlyCollection{string}, float, float, IReadOnlyList{ValueTuple{float, float}}, bool, out Report?)"/>
    /// <param name="extracted">The car's extracted folder (a working copy, or a scratch copy of one).</param>
    /// <param name="label">What the car is called in what is said about it.</param>
    public static string? ApplyIn(string extracted, string label, IReadOnlyCollection<string> materials, float from, float to,
        IReadOnlyList<(float Y, float Z)> line, bool write, out Report? report)
    {
        report = null;
        ArgumentNullException.ThrowIfNull(materials);
        ArgumentNullException.ThrowIfNull(line);
        if (line.Count < 2) return "the line needs two points at least";
        if (materials.Count == 0) return "name the paint's materials";
        if (Open(extracted, label, out FrameObjectModel? model, out FrameResource? frame) is { } refused) return refused;
        (float low, float high) = (MathF.Min(from, to), MathF.Max(from, to));
        (float Y, float Z)[] sorted = [.. line.OrderBy(p => p.Y)];
        float LineAt(float y)
        {
            if (y <= sorted[0].Y) return sorted[0].Z;
            for (int i = 1; i < sorted.Length; i++)
            {
                if (y <= sorted[i].Y)
                {
                    float t = (y - sorted[i - 1].Y) / MathF.Max(sorted[i].Y - sorted[i - 1].Y, 1e-6f);
                    return sorted[i - 1].Z + ((sorted[i].Z - sorted[i - 1].Z) * t);
                }
            }
            return sorted[^1].Z;
        }

        var levels = new List<Level>();
        var notes = new List<string>();
        var dirty = new HashSet<ulong>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int total = 0;
        for (int lod = 0; lod < model!.Geometry.LOD.Length; lod++)
        {
            DecodedMesh? mesh = SdsMeshLoader.DecodeLod(model, lod);
            if (mesh?.RawVertexData == null) continue;
            if (!mesh.Declaration.HasFlag(VertexFlags.Color)) { notes.Add($"LOD {lod} carries no vertex colour"); continue; }
            Vertex[] verts = VertexTranslator.DecompressBuffer(
                mesh.RawVertexData, mesh.NumVerts, mesh.Declaration, mesh.DecompressionOffset, mesh.DecompressionFactor);
            var changed = new HashSet<int>();
            foreach (MaterialStruct slot in model.Material?.Materials?.ElementAtOrDefault(lod) ?? [])
            {
                string material = MafiaMaterials.GetMaterialName(slot.MaterialHash) ?? $"0x{slot.MaterialHash:X16}";
                if (!materials.Contains(material)) continue;
                seen.Add(material);
                // how many of a vertex's painted triangles lie above the line, and how many it has
                var above = new Dictionary<int, (int Above, int All)>();
                int end = Math.Min(slot.StartIndex + (slot.NumFaces * 3), mesh.Indices.Length);
                for (int t = slot.StartIndex; t + 2 < end; t += 3)
                {
                    Vector3 middle = (mesh.Positions[mesh.Indices[t]] + mesh.Positions[mesh.Indices[t + 1]] + mesh.Positions[mesh.Indices[t + 2]]) / 3f;
                    bool second = middle.Y >= low && middle.Y <= high && middle.Z > LineAt(middle.Y);
                    for (int k = 0; k < 3; k++)
                    {
                        int v = (int)mesh.Indices[t + k];
                        (int Above, int All) was = above.GetValueOrDefault(v);
                        above[v] = (was.Above + (second ? 1 : 0), was.All + 1);
                    }
                }
                int first = 0, seconds = 0, onLine = 0, slotChanged = 0;
                foreach ((int v, (int Above, int All) count) in above)
                {
                    bool second = count.Above == count.All;
                    if (count.Above > 0 && !second) onLine++;
                    if (second) seconds++; else first++;
                    byte[] want = second ? Second : First;
                    if (verts[v].Color0.AsSpan().SequenceEqual(want)) continue;
                    want.CopyTo(verts[v].Color0, 0);
                    if (changed.Add(v)) slotChanged++;
                }
                levels.Add(new Level(lod, material, first, seconds, onLine, slotChanged));
            }
            if (changed.Count == 0) continue;
            total += changed.Count;
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
        foreach (string name in materials.Where(m => !seen.Contains(m))) notes.Add($"no level is drawn with '{name}'");
        int files = write && dirty.Count > 0 ? SdsGeometrySaver.SaveDirtyPools(frame!, dirty, []) : 0;
        report = new Report(label, levels, total, files, notes);
        return null;
    }

    /// <summary>The vertices of one material of one level that carry one colour, and where they stand.</summary>
    public sealed record Patch(int Lod, string Material, string Colour, int Vertices, Vector3 Low, Vector3 High);

    /// <summary>What colours the vertices of every material carry, read from the working copy; nothing is written.
    /// <paramref name="channel"/>: 0 or 1 - the first colour of a vertex or its second.</summary>
    public static string? Survey(FileInfo car, int channel, out IReadOnlyList<Patch> patches)
    {
        patches = [];
        ArgumentNullException.ThrowIfNull(car);
        if (!car.Exists) return $"no such archive: {car.FullName}";
        return SurveyIn(SdsMeshLoader.EnsureExtracted(car), car.Name, channel, out patches);
    }

    /// <inheritdoc cref="Survey(FileInfo, int, out IReadOnlyList{Patch})"/>
    public static string? SurveyIn(string extracted, string label, int channel, out IReadOnlyList<Patch> patches)
    {
        patches = [];
        if (channel is not (0 or 1)) return "a vertex has two colours: 0 or 1";
        if (Open(extracted, label, out FrameObjectModel? model, out _) is { } refused) return refused;
        var found = new List<Patch>();
        for (int lod = 0; lod < model!.Geometry.LOD.Length; lod++)
        {
            DecodedMesh? mesh = SdsMeshLoader.DecodeLod(model, lod);
            if (mesh?.RawVertexData == null) continue;
            Vertex[] verts = VertexTranslator.DecompressBuffer(
                mesh.RawVertexData, mesh.NumVerts, mesh.Declaration, mesh.DecompressionOffset, mesh.DecompressionFactor);
            foreach (MaterialStruct slot in model.Material?.Materials?.ElementAtOrDefault(lod) ?? [])
            {
                string material = MafiaMaterials.GetMaterialName(slot.MaterialHash) ?? $"0x{slot.MaterialHash:X16}";
                var byColour = new SortedDictionary<string, HashSet<int>>(StringComparer.Ordinal);
                int end = Math.Min(slot.StartIndex + (slot.NumFaces * 3), mesh.Indices.Length);
                for (int i = slot.StartIndex; i < end; i++)
                {
                    int v = (int)mesh.Indices[i];
                    byte[] c = channel == 0 ? verts[v].Color0 : verts[v].Color1;
                    string key = $"{c[0]},{c[1]},{c[2]},{c[3]}";
                    if (!byColour.TryGetValue(key, out HashSet<int>? set)) byColour[key] = set = [];
                    set.Add(v);
                }
                foreach ((string colour, HashSet<int> set) in byColour)
                {
                    var low = new Vector3(float.MaxValue);
                    var high = new Vector3(float.MinValue);
                    foreach (int v in set) { low = Vector3.Min(low, mesh.Positions[v]); high = Vector3.Max(high, mesh.Positions[v]); }
                    found.Add(new Patch(lod, material, colour, set.Count, low, high));
                }
            }
        }
        patches = found;
        return null;
    }

    private static string? Open(string extracted, string label, out FrameObjectModel? model, out FrameResource? frame)
    {
        model = null;
        frame = null;
        ArgumentException.ThrowIfNullOrEmpty(extracted);
        if (SdsMeshLoader.OpenScene(extracted).FrameResource is not { FrameObjects: not null } resource) return $"{label} has no scene";
        frame = resource;
        model = resource.FrameObjects.Values.OfType<FrameObjectModel>()
            .OrderByDescending(m => m.GetSkeletonObject().BoneNames?.Length ?? 0).FirstOrDefault();
        if (model == null) return $"{label} has no skinned model - a car's body is one";
        MafiaMaterials.EnsureLoaded();
        return null;
    }

    internal static string Text(Vector3 v) => string.Create(CultureInfo.InvariantCulture, $"({v.X:0.00}, {v.Y:0.00}, {v.Z:0.00})");
}
