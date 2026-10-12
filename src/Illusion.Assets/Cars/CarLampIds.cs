using System.Globalization;
using Illusion.Assets.Bridge;
using Illusion.Assets.Sds;
using Illusion.Formats.Archive;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Geometry;
using Illusion.Formats.Hashing;
using Illusion.Formats.Prefab;

namespace Illusion.Assets.Cars;

/// <summary>
/// Gives the glass of a car's lamps the LAMP ID the game lights it by.
///
/// <para>
/// Every vertex of a car's body carries a damage group, and its low byte says which of the car's lights the
/// vertex glows with. Measured on the 84 shipped cars (<c>--probe-car-lamp-census</c>), by the kind and side
/// of the PREFAB light whose bone the glass rides, with no exception worth the name: head lamp 0x30, left
/// indicator 0x31, right indicator 0x32, brake lamp 0x33 (kinds 8 and 2) or 0x34 left / 0x35 right (kind 7:
/// the lamp that is brake and turn signal in one), reversing lamp 0x36 (kind 5, "back"), taxi sign 0x37; the
/// number plate is 0x38 and its six digits 0x39..0x3E.
/// </para>
/// <para>
/// The Blender bridge does not show that channel, and a NEW vertex takes it from the nearest vertex of the
/// source mesh. So new lamp glass glowed with whatever lay nearest: on the first generated body the red brake
/// lamps and the amber indicators carried the reversing lamp's id, and the small white lamps beside the plate
/// carried the PLATE's - they were lit whenever the lights were. The glass follows its id, not the bone it
/// rides or that bone's light entry.
/// </para>
/// <para>
/// This sets the id on every vertex whose heaviest bone is a light's bone - by the light's kind and side - and
/// on the vertices of any bone named explicitly (glass kept on a bone that has no light entry: a donor's own
/// lamp bone, when the lamp material's remap pool has no room for a new one). Only the low byte changes; the
/// rest of the damage group stays what the vertex had. Writes the working copy's vertex pools.
/// </para>
/// </summary>
public static class CarLampIds
{
    public sealed record Lamp(string Bone, string Why, int Id, int Lod, int Vertices, int Changed, IReadOnlyList<string> Was);

    public sealed record Report(string Car, IReadOnlyList<Lamp> Lamps, int Changed, int PoolFilesWritten, IReadOnlyList<string> Notes);

    /// <summary>The id a light's glass carries, by the light's kind and side; null for a kind that lights no glass
    /// of its own (the beacon's casing carries none).</summary>
    public static int? IdOf(uint kind, bool left) => kind switch
    {
        0 => 0x30,
        3 => left ? 0x31 : 0x32,
        2 or 8 => 0x33,
        7 => left ? 0x34 : 0x35,
        5 => 0x36,
        9 => 0x37,
        _ => null,
    };

    /// <param name="explicitIds">Bone name -> id, for glass on a bone that has no light entry (or to overrule one).</param>
    /// <param name="write">False: report what would change, write nothing.</param>
    public static string? Apply(FileInfo car, IReadOnlyDictionary<string, int>? explicitIds, bool write, out Report? report)
    {
        report = null;
        ArgumentNullException.ThrowIfNull(car);
        if (!car.Exists) return $"no such archive: {car.FullName}";
        return ApplyIn(SdsMeshLoader.EnsureExtracted(car), car.Name, explicitIds, write, out report);
    }

    /// <inheritdoc cref="Apply(FileInfo, IReadOnlyDictionary{string, int}?, bool, out Report?)"/>
    /// <param name="extracted">The car's extracted folder (a working copy, or a scratch copy of one).</param>
    /// <param name="label">What the car is called in what is said about it.</param>
    public static string? ApplyIn(string extracted, string label, IReadOnlyDictionary<string, int>? explicitIds, bool write, out Report? report)
    {
        report = null;
        ArgumentException.ThrowIfNullOrEmpty(extracted);
        if (SdsMeshLoader.OpenScene(extracted).FrameResource is not { FrameObjects: not null } frame) return $"{label} has no scene";
        FrameObjectModel? model = frame.FrameObjects.Values.OfType<FrameObjectModel>()
            .OrderByDescending(m => m.GetSkeletonObject().BoneNames?.Length ?? 0).FirstOrDefault();
        if (model == null) return $"{label} has no skinned model - a car's body is one";
        IReadOnlyList<string> prefabs = SdsManifest.Load(extracted).GetFiles("PREFAB");
        if (prefabs.Count == 0 || PrefabFile.Load(Path.Combine(extracted, prefabs[0])).Car is not { } prefab) return $"{label} has no car PREFAB";
        string[] names = [.. (model.GetSkeletonObject().BoneNames ?? []).Select(n => n.ToString() ?? "")];

        // which id each bone's glass takes, and why
        var wanted = new Dictionary<string, (int Id, string Why)>(StringComparer.Ordinal);
        var notes = new List<string>();
        foreach (CarPrefab.Light light in prefab.Lights)
        {
            string? bone = names.FirstOrDefault(n => n.Length > 0 && Fnv64.Hash(n) == light.Frame);
            if (bone == null) { notes.Add($"a light names a bone the rig has not (0x{light.Frame:X16})"); continue; }
            if (IdOf(light.Unk3, light.Unk4 == 8) is { } id) wanted[bone] = (id, $"light of kind {light.Unk3}, {(light.Unk4 == 8 ? "left" : "right")}");
        }
        foreach ((string bone, int id) in explicitIds ?? new Dictionary<string, int>())
        {
            if (!names.Contains(bone, StringComparer.Ordinal)) return $"the rig has no bone '{bone}'";
            if (id is < 0 or > 0xFF) return $"an id is one byte: {id} for '{bone}'";
            wanted[bone] = (id, "named");
        }

        var lamps = new List<Lamp>();
        var dirty = new HashSet<ulong>();
        int total = 0;
        for (int lod = 0; lod < model.Geometry.LOD.Length; lod++)
        {
            DecodedMesh? mesh = SdsMeshLoader.DecodeLod(model, lod);
            if (mesh == null || mesh.RawVertexData == null) continue;
            // (a level that cannot take ids is SAID: the far level of a car is the one seen at night from a distance)
            if (!mesh.Declaration.HasFlag(VertexFlags.DamageGroup)) { notes.Add($"LOD {lod} carries no damage group: its lamps keep what they glow by"); continue; }
            byte[]? bones = SdsMeshLoader.GlobalBoneIds(model, lod);
            if (bones == null || mesh.BoneWeights == null) { notes.Add($"LOD {lod} is not skinned to the rig's bones: its lamps are left as they are"); continue; }
            Vertex[] verts = VertexTranslator.DecompressBuffer(
                mesh.RawVertexData, mesh.NumVerts, mesh.Declaration, mesh.DecompressionOffset, mesh.DecompressionFactor);
            var perBone = new Dictionary<string, (int Vertices, int Changed, SortedDictionary<int, int> Was)>(StringComparer.Ordinal);
            var changed = new List<int>();
            for (int v = 0; v < verts.Length; v++)
            {
                int best = 0;
                for (int k = 1; k < 4; k++) if (mesh.BoneWeights[(v * 4) + k] > mesh.BoneWeights[(v * 4) + best]) best = k;
                if (mesh.BoneWeights[(v * 4) + best] <= 0f) continue;
                int index = bones[(v * 4) + best];
                if (index >= names.Length || !wanted.TryGetValue(names[index], out (int Id, string Why) want)) continue;
                int was = verts[v].DamageGroup & 0xFF;
                (int Vertices, int Changed, SortedDictionary<int, int> Was) row = perBone.GetValueOrDefault(names[index], (0, 0, []));
                row.Was[was] = row.Was.GetValueOrDefault(was) + 1;
                row.Vertices++;
                if (was != want.Id)
                {
                    verts[v].DamageGroup = (verts[v].DamageGroup & ~0xFF) | want.Id;
                    changed.Add(v);
                    row.Changed++;
                }
                perBone[names[index]] = row;
            }
            foreach ((string bone, (int Vertices, int Changed, SortedDictionary<int, int> Was) row) in perBone.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                lamps.Add(new Lamp(bone, wanted[bone].Why, wanted[bone].Id, lod, row.Vertices, row.Changed,
                    [.. row.Was.Select(p => string.Create(CultureInfo.InvariantCulture, $"0x{p.Key:X2} x{p.Value}"))]));
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
        foreach (string bone in wanted.Keys.Where(b => lamps.All(l => l.Bone != b)).OrderBy(b => b, StringComparer.Ordinal))
        {
            notes.Add($"no vertex rides '{bone}' ({wanted[bone].Why}): its glass is on another bone, or it has none");
        }

        int files = write && dirty.Count > 0 ? SdsGeometrySaver.SaveDirtyPools(frame, dirty, []) : 0;
        report = new Report(label, lamps, total, files, notes);
        return null;
    }
}
