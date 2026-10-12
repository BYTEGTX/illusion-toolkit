using System.Globalization;
using System.IO;
using System.Text;
using Illusion.Assets;
using Illusion.Assets.Sds;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Frames.Resources;
using Illusion.Formats.Geometry;
using static Illusion.Diagnostics.Probes.ProbeAssert;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// What the vertices of a car's lamps carry BESIDES position, normal and the first UV set: colours, the
/// further UV sets and the damage group - the channels the Blender bridge does not show and that new geometry
/// gets "from a neighbour". Listed per lamp bone (and for the rest of each material those lamps are drawn
/// with), for several cars side by side: a stock car says what a lamp of each kind must carry.
/// Read from the working copies; nothing is written.
/// <para>Output: %TEMP%\illusion_car_lamp_channels.txt</para>
/// </summary>
internal static class CarLampChannelProbes
{
    internal static void Run(string[] cars)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_car_lamp_channels.txt");
        var sb = new StringBuilder();
        try
        {
            if (!InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            if (cars.Length == 0) cars = ["berkley_kingfisher_pha", "smith_stingray_pha"];
            MafiaMaterials.EnsureLoaded();
            foreach (string car in cars)
            {
                var archive = new FileInfo(Path.Combine(MafiaEnvironment.PcFolder, "sds", "cars", car + ".sds"));
                if (!archive.Exists) { sb.AppendLine($"==== {car}: no such archive"); continue; }
                SdsMeshLoader.EnsureExtracted(archive);
                FrameObjectModel? model = SdsMeshLoader.OpenScene(MafiaEnvironment.ExtractedDir(archive))
                    .FrameResource?.FrameObjects?.Values.OfType<FrameObjectModel>().FirstOrDefault();
                DecodedMesh? mesh = model == null ? null : SdsMeshLoader.DecodeLod(model, 0);
                if (model == null || mesh == null) { sb.AppendLine($"==== {car}: no skinned model"); continue; }
                Vertex[] verts = VertexTranslator.DecompressBuffer(
                    mesh.RawVertexData, mesh.NumVerts, mesh.Declaration, mesh.DecompressionOffset, mesh.DecompressionFactor);
                byte[]? bones = SdsMeshLoader.GlobalBoneIds(model, 0);
                string[] names = [.. (model.GetSkeletonObject().BoneNames ?? []).Select(n => n.ToString() ?? "?")];
                sb.AppendLine($"==== {car}: {verts.Length} vertices, declaration {mesh.Declaration}");
                if (bones == null || mesh.BoneWeights == null) { sb.AppendLine("  no skin"); continue; }

                string BoneOf(int v)
                {
                    int best = 0;
                    for (int k = 1; k < 4; k++) if (mesh.BoneWeights[(v * 4) + k] > mesh.BoneWeights[(v * 4) + best]) best = k;
                    int id = bones[(v * 4) + best];
                    return id < names.Length ? names[id] : "#" + id;
                }

                static bool IsLamp(string bone) =>
                    bone.StartsWith("Dummy_L", StringComparison.OrdinalIgnoreCase)
                    || bone.Contains("light", StringComparison.OrdinalIgnoreCase)
                    || bone.Contains("reflector", StringComparison.OrdinalIgnoreCase);

                if (model.Material?.Materials is not { } levels || levels.Count == 0 || levels[0] == null) continue;
                foreach (MaterialStruct slot in levels[0])
                {
                    string material = MafiaMaterials.GetMaterialName(slot.MaterialHash) ?? $"0x{slot.MaterialHash:X16}";
                    var groups = new SortedDictionary<string, HashSet<uint>>(StringComparer.Ordinal);
                    int end = Math.Min(slot.StartIndex + (slot.NumFaces * 3), mesh.Indices.Length);
                    for (int i = slot.StartIndex; i < end; i++)
                    {
                        uint v = mesh.Indices[i];
                        string bone = BoneOf((int)v);
                        string key = IsLamp(bone) ? bone : "(the rest)";
                        if (!groups.TryGetValue(key, out HashSet<uint>? set)) groups[key] = set = [];
                        set.Add(v);
                    }
                    if (!groups.Keys.Any(k => k != "(the rest)")) continue;
                    sb.AppendLine($"  material {material}, {slot.NumFaces} faces");
                    foreach ((string bone, HashSet<uint> set) in groups)
                    {
                        sb.Append($"    {bone,-24} {set.Count,5} vertices");
                        sb.Append("  colour0 " + Distinct(set.Select(v => Hex(verts[v].Color0))));
                        sb.Append("  colour1 " + Distinct(set.Select(v => Hex(verts[v].Color1))));
                        sb.Append("  damage " + Distinct(set.Select(v => verts[v].DamageGroup.ToString(CultureInfo.InvariantCulture))));
                        for (int s = 1; s < 4; s++)
                        {
                            float loX = set.Min(v => (float)verts[v].UVs[s].X), hiX = set.Max(v => (float)verts[v].UVs[s].X);
                            float loY = set.Min(v => (float)verts[v].UVs[s].Y), hiY = set.Max(v => (float)verts[v].UVs[s].Y);
                            sb.Append(string.Create(CultureInfo.InvariantCulture, $"  uv{s} x {loX:0.###}..{hiX:0.###} y {loY:0.###}..{hiY:0.###}"));
                        }
                        sb.AppendLine();
                    }
                }
                sb.AppendLine();
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("unexpected exception — " + ex);
        }
        finally
        {
            File.WriteAllText(outFile, sb.ToString());
        }
    }

    /// <summary>
    /// Over EVERY car archive: which damage-group low byte the vertices on a light's bone carry, by the light's
    /// kind and side - the table a new lamp's vertices must follow - and which bones that are no light's carry a
    /// low byte in the lamps' range. Output: %TEMP%\illusion_car_lamp_census.txt
    /// </summary>
    internal static void Census()
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_car_lamp_census.txt");
        var sb = new StringBuilder();
        try
        {
            if (!InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            var byKind = new SortedDictionary<string, SortedDictionary<int, int>>(StringComparer.Ordinal);
            var examples = new Dictionary<string, string>();
            var others = new SortedDictionary<string, int>(StringComparer.Ordinal);
            int cars = 0;
            foreach (string file in Directory.GetFiles(Path.Combine(MafiaEnvironment.PcFolder, "sds", "cars"), "*.sds"))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (name.Contains("backup", StringComparison.OrdinalIgnoreCase) || name is "cars_universal" or "berkley_phantom" or "stern_300"
                    || name.StartsWith("shubert_ambulance", StringComparison.OrdinalIgnoreCase) || name.Contains("custom", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("zz_", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    string extracted = SdsMeshLoader.EnsureExtracted(new FileInfo(file));
                    FrameObjectModel? model = SdsMeshLoader.OpenScene(extracted).FrameResource?.FrameObjects?.Values.OfType<FrameObjectModel>()
                        .OrderByDescending(m => m.GetSkeletonObject().BoneNames?.Length ?? 0).FirstOrDefault();
                    DecodedMesh? mesh = model == null ? null : SdsMeshLoader.DecodeLod(model, 0);
                    IReadOnlyList<string> prefabs = Illusion.Formats.Archive.SdsManifest.Load(extracted).GetFiles("PREFAB");
                    if (model == null || mesh == null || prefabs.Count == 0 || !mesh.Declaration.HasFlag(VertexFlags.DamageGroup)) continue;
                    if (Illusion.Formats.Prefab.PrefabFile.Load(Path.Combine(extracted, prefabs[0])).Car is not { } prefab) continue;
                    Vertex[] verts = VertexTranslator.DecompressBuffer(
                        mesh.RawVertexData, mesh.NumVerts, mesh.Declaration, mesh.DecompressionOffset, mesh.DecompressionFactor);
                    byte[]? bones = SdsMeshLoader.GlobalBoneIds(model, 0);
                    string[] names = [.. (model.GetSkeletonObject().BoneNames ?? []).Select(n => n.ToString() ?? "?")];
                    if (bones == null || mesh.BoneWeights == null) continue;
                    cars++;
                    var kindOf = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (Illusion.Formats.Prefab.CarPrefab.Light light in prefab.Lights)
                    {
                        string? bone = names.FirstOrDefault(n => Illusion.Formats.Hashing.Fnv64.Hash(n) == light.Frame);
                        if (bone != null) kindOf[bone] = $"kind {light.Unk3} {(light.Unk4 == 8 ? "left " : light.Unk4 == 0 ? "right" : "side" + light.Unk4)}";
                    }
                    for (int v = 0; v < verts.Length; v++)
                    {
                        int best = 0;
                        for (int k = 1; k < 4; k++) if (mesh.BoneWeights[(v * 4) + k] > mesh.BoneWeights[(v * 4) + best]) best = k;
                        int id = bones[(v * 4) + best];
                        string bone = id < names.Length ? names[id] : "#" + id;
                        int low = verts[v].DamageGroup & 0xFF;
                        if (kindOf.TryGetValue(bone, out string? kind))
                        {
                            if (!byKind.TryGetValue(kind, out SortedDictionary<int, int>? row)) byKind[kind] = row = [];
                            row[low] = row.GetValueOrDefault(low) + 1;
                            examples.TryAdd($"{kind} 0x{low:X2}", $"{name}:{bone}");
                        }
                        else if (low >= 0x30)
                        {
                            string key = $"0x{low:X2} on {bone}";
                            others[key] = others.GetValueOrDefault(key) + 1;
                            examples.TryAdd(key, name);
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or InvalidOperationException)
                {
                    sb.AppendLine($"  {name}: skipped - {ex.Message}");
                }
            }
            sb.AppendLine($"LAMP IDS over {cars} cars: the low byte of the damage group on a light's bone, by the light's kind and side");
            foreach ((string kind, SortedDictionary<int, int> row) in byKind)
            {
                sb.AppendLine($"  {kind,-14} " + string.Join("  ", row.OrderByDescending(p => p.Value).Select(p => $"0x{p.Key:X2} x{p.Value} ({examples[$"{kind} 0x{p.Key:X2}"]})")));
            }
            sb.AppendLine();
            sb.AppendLine("low bytes of 0x30 and more on bones that are NO light's:");
            foreach ((string key, int count) in others.OrderByDescending(p => p.Value).Take(60)) sb.AppendLine($"  {key,-44} x{count,5}  e.g. {examples[key]}");
        }
        catch (Exception ex)
        {
            sb.AppendLine("unexpected exception — " + ex);
        }
        finally
        {
            File.WriteAllText(outFile, sb.ToString());
        }
    }

    /// <summary>
    /// The colours a car's vertices carry, material by material (Illusion.Assets.Cars.CarPaintMask) - the mask of the
    /// car's second paint is among them: cars. Reads the working copies; nothing is written (the tool car_paint_mask
    /// writes a mask). Output: %TEMP%\illusion_car_paint_mask.txt
    /// </summary>
    internal static void PaintMask(string[] a)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_car_paint_mask.txt");
        var sb = new StringBuilder();
        try
        {
            if (!InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            foreach (string name in a)
            {
                var car = new FileInfo(Path.Combine(MafiaEnvironment.PcFolder, "sds", "cars", name + ".sds"));
                for (int channel = 0; channel < 2; channel++)
                {
                    string? refused = Illusion.Assets.Cars.CarPaintMask.Survey(car, channel, out IReadOnlyList<Illusion.Assets.Cars.CarPaintMask.Patch> patches);
                    sb.AppendLine($"== {name}, colour {channel}" + (refused != null ? ": REFUSED " + refused : ""));
                    foreach (Illusion.Assets.Cars.CarPaintMask.Patch patch in patches)
                    {
                        sb.AppendLine($"  LOD {patch.Lod} {patch.Material,-24} {patch.Colour,-16} {patch.Vertices,5} vertices  {Illusion.Assets.Cars.CarPaintMask.Text(patch.Low)} .. {Illusion.Assets.Cars.CarPaintMask.Text(patch.High)}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("unexpected exception — " + ex);
        }
        finally
        {
            File.WriteAllText(outFile, sb.ToString());
        }
    }

    /// <summary>
    /// What a car's glass carries in each texture-coordinate set, pane by pane (Illusion.Assets.Cars.CarGlassUv): cars.
    /// Reads the working copies; nothing is written (the tool car_glass lays the second set anew).
    /// Output: %TEMP%\illusion_car_glass_uv.txt
    /// </summary>
    internal static void GlassUv(string[] a)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_car_glass_uv.txt");
        var sb = new StringBuilder();
        try
        {
            if (!InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            foreach (string name in a)
            {
                var car = new FileInfo(Path.Combine(MafiaEnvironment.PcFolder, "sds", "cars", name + ".sds"));
                string? refused = Illusion.Assets.Cars.CarGlassUv.Survey(car, out IReadOnlyList<Illusion.Assets.Cars.CarGlassUv.Pane> panes);
                sb.AppendLine($"== {name}" + (refused != null ? ": REFUSED " + refused : ""));
                foreach (Illusion.Assets.Cars.CarGlassUv.Pane pane in panes)
                {
                    sb.AppendLine($"  LOD {pane.Lod} {pane.Material} {pane.Bone,-16} {pane.Vertices,4} vertices  at {Illusion.Assets.Cars.CarGlassUv.Text(pane.Low)} .. {Illusion.Assets.Cars.CarGlassUv.Text(pane.High)}");
                    for (int s = 0; s < pane.Sets.Count; s++)
                    {
                        sb.AppendLine($"       set {s}: {Illusion.Assets.Cars.CarGlassUv.Text(pane.Sets[s].Low)} .. {Illusion.Assets.Cars.CarGlassUv.Text(pane.Sets[s].High)}   u per m of x, y, z {Illusion.Assets.Cars.CarGlassUv.Text(pane.Slopes[s].U)}   v {Illusion.Assets.Cars.CarGlassUv.Text(pane.Slopes[s].V)}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("unexpected exception — " + ex);
        }
        finally
        {
            File.WriteAllText(outFile, sb.ToString());
        }
    }

    /// <summary>
    /// A car's tuning tables read straight from an archive ANYWHERE - a DLC's folder too, which the resource editor
    /// does not open: the full path of the .sds, or a car's name under pc\sds\cars. One line a field:
    /// table TAB band TAB element TAB name TAB value. Output: %TEMP%\illusion_car_tuning_dump.txt
    /// </summary>
    internal static void TuningDump(string[] a)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_car_tuning_dump.txt");
        var sb = new StringBuilder();
        try
        {
            if (!InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            if (a.Length < 1) { sb.AppendLine("usage: car | path to .sds"); return; }
            var car = new FileInfo(File.Exists(a[0]) ? a[0] : Path.Combine(MafiaEnvironment.PcFolder, "sds", "cars", a[0] + ".sds"));
            if (!car.Exists) { sb.AppendLine("REFUSED: no such archive: " + car.FullName); return; }
            SdsMeshLoader.EnsureExtracted(car);
            Illusion.Assets.EntityData.CarTuning? tuning = Illusion.Assets.EntityData.CarTuning.Read(car);
            if (tuning == null) { sb.AppendLine("REFUSED: the archive has no tuning table that can be read"); return; }
            int n = 0;
            foreach (var table in tuning.Tables)
            {
                n++;
                foreach (var band in table.Bands)
                    foreach (var element in band.Elements)
                        foreach (var row in element.Rows)
                            sb.Append(n).Append('	').Append(band.Title).Append('	').Append(element.Title ?? "").Append('	').Append(row.Name).Append('	').AppendLine(row.Value);
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("unexpected exception — " + ex);
        }
        finally
        {
            File.WriteAllText(outFile, sb.ToString());
        }
    }

    private static string Hex(byte[] c) => c.Length >= 4 ? $"{c[0]:X2}{c[1]:X2}{c[2]:X2}{c[3]:X2}" : "-";

    // The values a group holds, most frequent first, at most five named.
    private static string Distinct(IEnumerable<string> values)
    {
        var counts = values.GroupBy(v => v).OrderByDescending(g => g.Count()).ToList();
        string text = string.Join(" ", counts.Take(5).Select(g => $"{g.Key}x{g.Count()}"));
        return counts.Count > 5 ? text + $" (+{counts.Count - 5} more)" : text;
    }
}
