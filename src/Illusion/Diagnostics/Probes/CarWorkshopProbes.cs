using System.IO;
using System.Text;
using Illusion.Assets;
using Illusion.Assets.Cars;
using Illusion.Assets.Materials;
using Illusion.Domain.Materials;
using static Illusion.Diagnostics.Probes.ProbeAssert;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// The car workshop's two halves, headless: <see cref="CarCheck"/> must find nothing on a car as shipped
/// (a check that cries wolf on stock data is worse than none), and a material variant must be the source in
/// everything but its name and stay its own - changing the copy's texture must not change the source's.
/// Then the writers the car tools use - beacon, lights, the paint's rows, a collision, the winter twin - are run
/// for real, on a COPY of the car's working copy in the temp folder.
/// The game's folders are not written to, beyond extracting an archive that has no working copy yet: the
/// material copy is taken out of the library again and the library is never saved, and the scratch copy is
/// deleted at the end. Output: %TEMP%\illusion_car_workshop.txt
/// </summary>
internal static class CarWorkshopProbes
{
    // The channels of a vertex that Blender does not show (lamp ids, the glass's second coordinates, the mask of the
    // second paint), the car's kind, and the small writers of the rig and the PREFAB: each run on the scratch copy.
    private static void Unseen(string car, string scratch, Action<string, bool, string> check)
    {
        void Check(string name, bool ok, string detail = "") => check(name, ok, detail);

        // lamp ids
        string? idsRefused = CarLampIds.ApplyIn(scratch, car, null, write: false, out CarLampIds.Report? ids);
        Check("a shipped car's lamp glass carries the ids its lights ask for: nothing would change", idsRefused == null && ids is { Changed: 0, Lamps.Count: > 0 },
            idsRefused ?? $"{ids?.Changed} of {ids?.Lamps.Sum(l => l.Vertices)} vertices on {ids?.Lamps.Count} lamps");
        if (ids is { Lamps.Count: > 0 })
        {
            // every vertex pool of the copy, to hold against what a change and its undoing leave
            Dictionary<string, byte[]> Pools() => Directory.GetFiles(scratch, "VertexBufferPool_*").ToDictionary(f => Path.GetFileName(f)!, File.ReadAllBytes);
            Dictionary<string, byte[]> poolsWere = Pools();
            CarLampIds.Lamp lamp = ids.Lamps[0];
            int other = lamp.Id == 0x3E ? 0x3D : 0x3E;
            var named = new Dictionary<string, int> { [lamp.Bone] = other };
            Check("an id named for a bone is written on that bone's vertices",
                CarLampIds.ApplyIn(scratch, car, named, write: true, out CarLampIds.Report? set) == null && set is { PoolFilesWritten: > 0 }
                && set.Lamps.Where(l => l.Bone == lamp.Bone).Sum(l => l.Changed) == set.Changed && set.Changed > 0, $"{set?.Changed} vertices");
            Check("…and read back from the written buffers it is what would change back, to the light's own id",
                CarLampIds.ApplyIn(scratch, car, null, write: true, out CarLampIds.Report? back) == null && back!.Changed == set!.Changed
                && CarLampIds.ApplyIn(scratch, car, null, write: false, out CarLampIds.Report? clean) == null && clean!.Changed == 0, $"{back?.Changed} back");
            Dictionary<string, byte[]> poolsNow = Pools();
            Check("…and the vertex buffers are byte for byte what they were: nothing but the id was touched",
                poolsNow.Count == poolsWere.Count && poolsWere.All(f => poolsNow.TryGetValue(f.Key, out byte[]? now) && now.AsSpan().SequenceEqual(f.Value)), $"{poolsWere.Count} pool file(s)");
            Check("a bone the rig does not have, and an id past a byte, are refused",
                CarLampIds.ApplyIn(scratch, car, new Dictionary<string, int> { ["no such bone"] = 0x30 }, write: false, out _) != null
                && CarLampIds.ApplyIn(scratch, car, new Dictionary<string, int> { [lamp.Bone] = 0x130 }, write: false, out _) != null);
        }

        // the glass's second coordinates
        string? surveyRefused = CarGlassUv.SurveyIn(scratch, car, out IReadOnlyList<CarGlassUv.Pane> panesWere);
        string? glassRefused = CarGlassUv.ApplyIn(scratch, car, write: true, out CarGlassUv.Report? glass);
        Check("the broken-glass picture is laid over the car's panes", surveyRefused == null && glassRefused == null && glass is { Panes.Count: > 0 },
            surveyRefused ?? glassRefused ?? $"{glass?.Panes.Count} panes, {glass?.Changed} vertices");
        string? rereadRefused = CarGlassUv.SurveyIn(scratch, car, out IReadOnlyList<CarGlassUv.Pane> panes);
        Check("the laid glass is read back", rereadRefused == null && panes.Count == panesWere.Count, rereadRefused ?? $"{panes.Count} / {panesWere.Count}");
        if (glassRefused == null && rereadRefused == null && surveyRefused == null && panes.Count == panesWere.Count)
        {
            // (a pane as the survey has it - a material's vertices on one bone - may hold several panes; each of them runs
            // edge to edge, so the bone's whole box does)
            bool Whole(CarGlassUv.Pane p) => p.Sets.Count >= 2 && p.Sets[1].Low.X is > 0f and < 0.03f && p.Sets[1].Low.Y is > 0f and < 0.03f
                && p.Sets[1].High.X is > 0.97f and < 1f && p.Sets[1].High.Y is > 0.97f and < 1f;
            CarGlassUv.Pane? partial = panes.FirstOrDefault(p => p.Lod == 0 && !Whole(p));
            Check("…each from edge to edge of the picture", panes.Any(p => p.Lod == 0) && partial == null,
                partial == null ? $"{panes.Count(p => p.Lod == 0)} panes" : $"{partial.Bone}: {(partial.Sets.Count < 2 ? "no second set" : CarGlassUv.Text(partial.Sets[1].Low) + " .. " + CarGlassUv.Text(partial.Sets[1].High))}");
            Check("…its places and its first coordinates (the dirt's) are what they were",
                panes.Zip(panesWere).All(p => p.First.Bone == p.Second.Bone && p.First.Vertices == p.Second.Vertices && p.First.Low == p.Second.Low && p.First.High == p.Second.High
                    && p.First.Sets[0].Low == p.Second.Sets[0].Low && p.First.Sets[0].High == p.Second.Sets[0].High));
            Check("…and laid again nothing changes", CarGlassUv.ApplyIn(scratch, car, write: true, out CarGlassUv.Report? again) == null && again!.Changed == 0, $"{again?.Changed}");
        }

        // the mask of the second paint
        CarMaterials.ReadIn(scratch, out CarMaterials.Sheet? listed);
        string[] paints = [.. (listed?.Rows ?? []).Where(r => r.Flags == 1 && r.OnTheCar).Select(r => r.Material).Distinct()];
        Check("the car's PREFAB names the paint its meshes are drawn with", paints.Length > 0, string.Join(", ", paints));
        if (paints.Length > 0 && CarPaintMask.SurveyIn(scratch, car, 0, out IReadOnlyList<CarPaintMask.Patch> before) == null)
        {
            CarPaintMask.Patch[] mine = [.. before.Where(b => paints.Contains(b.Material) && b.Lod == 0)];
            float low = mine.Min(b => b.Low.Z), high = mine.Max(b => b.High.Z), mid = (low + high) / 2f;
            (float, float)[] line = [(-100f, mid), (100f, mid)];
            string? maskRefused = CarPaintMask.ApplyIn(scratch, car, paints, -100f, 100f, line, write: true, out CarPaintMask.Report? mask);
            Check("a mask is written: the second paint above a line, the first below", maskRefused == null
                && mask!.Levels.Any(l => l.Lod == 0 && l.FirstVertices > 0 && l.SecondVertices > 0), maskRefused ?? string.Join("; ", mask!.Levels.Select(l => $"LOD {l.Lod} {l.FirstVertices}/{l.SecondVertices}")));
            if (maskRefused == null && CarPaintMask.SurveyIn(scratch, car, 0, out IReadOnlyList<CarPaintMask.Patch> after) == null)
            {
                CarPaintMask.Patch[] now = [.. after.Where(b => paints.Contains(b.Material) && b.Lod == 0)];
                Check("…read back: the paint carries the two paints' colours and no other", now.All(b => b.Colour is "255,0,0,255" or "0,255,0,255"),
                    string.Join(", ", now.Select(b => b.Colour).Distinct()));
                // (a vertex takes its paint from its triangles' middles, so it may stand a triangle's reach across the line)
                float slack = (high - low) * 0.3f;
                Check("…the second paint lies above the line and the first below it, within a triangle's reach",
                    now.Where(b => b.Colour == "0,255,0,255").All(b => b.Low.Z >= mid - slack && b.High.Z > mid)
                    && now.Where(b => b.Colour == "255,0,0,255").All(b => b.High.Z <= mid + slack && b.Low.Z < mid),
                    string.Join("; ", now.Select(b => $"{(b.Colour == "0,255,0,255" ? "second" : "first")} z {b.Low.Z:0.00}..{b.High.Z:0.00}")) + $" (line {mid:0.00})");
            }
            Check("…and written again nothing changes", CarPaintMask.ApplyIn(scratch, car, paints, -100f, 100f, line, write: true, out CarPaintMask.Report? twice) == null
                && twice!.Changed == 0, $"{twice?.Changed}");
            Check("a line of one point, no material, and a material the car is not drawn with are refused or said",
                CarPaintMask.ApplyIn(scratch, car, paints, -100f, 100f, [(0f, mid)], write: false, out _) != null
                && CarPaintMask.ApplyIn(scratch, car, [], -100f, 100f, line, write: false, out _) != null
                && CarPaintMask.ApplyIn(scratch, car, ["no_such_paint"], -100f, 100f, line, write: false, out CarPaintMask.Report? none) == null
                && none!.Notes.Count > 0 && none.Changed == 0);
        }

        // the exhaust's points, a copied point
        string? smokeRefused = CarLights.ExhaustsIn(scratch, out IReadOnlyList<string> smoke);
        Check("the car's PREFAB lists the points its exhaust smokes from", smokeRefused == null && smoke.Count > 0, smokeRefused ?? string.Join(", ", smoke));
        Check("…by names the car's frames have", smokeRefused != null || smoke.Count == 0 || !smoke[0].StartsWith("0x", StringComparison.Ordinal), smoke.FirstOrDefault() ?? "");
        if (smokeRefused == null && smoke.Count > 0 && !smoke[0].StartsWith("0x", StringComparison.Ordinal))
        {
            const string Copy = "zz_probe_smoke";
            Check("a point is copied under a new name", CarLights.CopyPointIn(scratch, smoke[0], Copy) == null);
            Check("…a second copy under that name, and a copy of a frame the car has not, are refused",
                CarLights.CopyPointIn(scratch, smoke[0], Copy) != null && CarLights.CopyPointIn(scratch, "no such frame", "zz_other") != null);
            string[] wanted = [.. smoke, Copy, Copy];
            Check("the list is written with the copy in it - once, though it was named twice",
                CarLights.SetExhaustsIn(scratch, wanted) == null && CarLights.ExhaustsIn(scratch, out IReadOnlyList<string> listedNow) == null
                && listedNow.SequenceEqual([.. smoke, Copy], StringComparer.OrdinalIgnoreCase), "");
            Check("a point the car does not have is refused, and the list stays", CarLights.SetExhaustsIn(scratch, ["no such point"]) != null
                && CarLights.ExhaustsIn(scratch, out IReadOnlyList<string> kept) == null && kept.Count == smoke.Count + 1);
        }

        // wheels
        string? wheelsRefused = CarLights.WheelsIn(scratch, null, out IReadOnlyList<CarLights.Wheel> wheels);
        Check("the car's wheels are read: a front pair and a rear pair on their brake drums", wheelsRefused == null && wheels.Count(w => w.Front) == 2 && wheels.Count(w => !w.Front) == 2,
            wheelsRefused ?? $"{wheels.Count} wheels");
        if (wheelsRefused == null && wheels.Count >= 4)
        {
            Check("the front wheels moved outwards stand that much further from the car's middle, the rear ones where they were",
                CarLights.WheelsIn(scratch, (0.05f, 0f), out IReadOnlyList<CarLights.Wheel> wider, out bool wroteWider) == null && wroteWider
                && wider.Zip(wheels).All(p => MathF.Abs(MathF.Abs(p.First.At.X) - MathF.Abs(p.Second.At.X) - (p.First.Front ? 0.05f : 0f)) < 1e-3f
                    && MathF.Abs(p.First.At.Y - p.Second.At.Y) < 1e-3f && MathF.Abs(p.First.At.Z - p.Second.At.Z) < 1e-3f),
                string.Join(", ", wider.Zip(wheels).Select(p => $"{MathF.Abs(p.First.At.X) - MathF.Abs(p.Second.At.X):0.000}")));
            Check("…and moved back they stand where they did", CarLights.WheelsIn(scratch, (-0.05f, 0f), out IReadOnlyList<CarLights.Wheel> back2) == null
                && back2.Zip(wheels).All(p => (p.First.OnDrum - p.Second.OnDrum).Length() < 1e-4f));
            Check("a move of nought writes nothing, and a move past half a metre is refused",
                CarLights.WheelsIn(scratch, (0f, 0f), out _, out bool wroteNought) == null && !wroteNought
                && CarLights.WheelsIn(scratch, (5f, 0f), out _) != null && CarLights.WheelsIn(scratch, (float.PositiveInfinity, 0f), out _) != null);
        }

        // a bone moved
        (string[] Names, byte[] Parents, System.Numerics.Vector3[] At)? Rig()
        {
            if (Illusion.Assets.Sds.SdsMeshLoader.OpenScene(scratch).FrameResource is not { FrameObjects: not null } frame) return null;
            Illusion.Formats.Frames.ObjectTypes.FrameObjectModel? model = frame.FrameObjects.Values.OfType<Illusion.Formats.Frames.ObjectTypes.FrameObjectModel>()
                .OrderByDescending(m => m.GetSkeletonObject().BoneNames?.Length ?? 0).FirstOrDefault();
            if (model == null) return null;
            string[] names = [.. (model.GetSkeletonObject().BoneNames ?? []).Select(n => n.ToString() ?? "")];
            return (names, model.GetSkeletonHierarchyObject().ParentIndices ?? [], [.. Enumerable.Range(0, names.Length).Select(i => model.GetJointWorldTransform(i).Translation)]);
        }
        var move = new System.Numerics.Vector3(0.03f, -0.02f, 0.01f);
        bool lit = CarLights.ReadIn(scratch, out CarLights.Sheet? rig) == null && rig!.Lights.Count > 0;
        Check("the car has a lamp's bone to move", lit && Rig() != null);
        if (lit && Rig() is { } rigWas)
        {
            string bone = rig!.Lights[0].Bone;
            int index = Array.IndexOf(rigWas.Names, bone);
            Check("a lamp's bone is moved by an offset in the model's space, and says where it stands",
                index >= 0 && CarLights.MoveBoneIn(scratch, bone, move, out int noHinge, out System.Numerics.Vector3 said) == null && noHinge == 0
                && Rig() is { } after && (after.At[index] - rigWas.At[index] - move).Length() < 1e-3f && (said - after.At[index]).Length() < 1e-3f, bone);
            Check("a bone the rig does not have, and a move past three metres, are refused",
                CarLights.MoveBoneIn(scratch, "no such bone", move) != null && CarLights.MoveBoneIn(scratch, bone, new System.Numerics.Vector3(0f, 30f, 0f)) != null);

            // a bone other bones hang on: it moves, they stay
            rigWas = Rig()!.Value;
            int parent = Enumerable.Range(1, rigWas.Names.Length - 1).FirstOrDefault(i => rigWas.Parents.Skip(1).Contains((byte)i) && CarLights.MoveBoneIn(scratch, rigWas.Names[i], move) == null, -1);
            if (parent > 0 && Rig() is { } hung)
            {
                int[] children = [.. Enumerable.Range(1, rigWas.Names.Length - 1).Where(c => rigWas.Parents[c] == parent)];
                Check("a bone other bones hang on is moved, and they stay where they stood", (hung.At[parent] - rigWas.At[parent] - move).Length() < 1e-3f
                    && children.All(c => (hung.At[c] - rigWas.At[c]).Length() < 1e-3f), $"{rigWas.Names[parent]}: {children.Length} bone(s) on it");
            }
            else
            {
                Check("a bone other bones hang on is moved, and they stay where they stood", false, "no such bone could be moved");
            }

            // a movable part: the PREFAB's record of its hinge moves with the bone
            string? part = new[] { "coverF", "coverB", "doorFL", "doorFR" }.FirstOrDefault(b => rigWas.Names.Contains(b));
            byte[] prefabWas = File.ReadAllBytes(Illusion.Formats.Archive.SdsManifest.Load(scratch).GetFiles("PREFAB")[0]);
            string prefabFile = Illusion.Formats.Archive.SdsManifest.Load(scratch).GetFiles("PREFAB")[0];
            int Differ() { byte[] now = File.ReadAllBytes(prefabFile); return now.Length != prefabWas.Length ? -1 : now.Zip(prefabWas).Count(p => p.First != p.Second); }
            string? partRefused = part == null ? "the car has no lid or door bone" : CarLights.MoveBoneIn(scratch, part, move, out int hinges, out _);
            int moved = Differ();
            Check("a movable part's bone is moved with the PREFAB's record of its hinge", partRefused == null && moved > 0, partRefused ?? $"{part}: {moved} byte(s) of the PREFAB differ");
            string? backRefused = part == null ? "-" : CarLights.MoveBoneIn(scratch, part, -move, out int hingesBack, out _);
            // (a float moved out and back may differ in its last bit: a handful of bytes, not a record left behind)
            int left = Differ();
            Check("…and moved back the PREFAB is what it was, to a float's last bit", backRefused == null && left >= 0 && left <= moved / 4, backRefused ?? $"{left} byte(s) differ after the way back, {moved} after the way out");
        }

        // the car's kind: on a copy of the table
        var tables = new FileInfo(Path.Combine(MafiaEnvironment.PcFolder, "sds", "tables", "tables.sds"));
        string shipped = tables.Exists ? Path.Combine(Illusion.Assets.Sds.SdsMeshLoader.EnsureExtracted(tables), "tables", "vehicles.tbl") : "";
        Check("the working copy of tables.sds has vehicles.tbl", File.Exists(shipped), shipped);
        if (File.Exists(shipped))
        {
            string table = Path.Combine(Path.GetDirectoryName(scratch)!, "vehicles_probe.tbl");
            File.Copy(shipped, table, overwrite: true);
            const string Police = "Smith_200_p_pha", Civil = "smith_200_PHA";
            Check("a car's row is read, whatever the case of its name", CarKind.Read(table, Civil, out CarKind.Row? civil) == null && civil is { Name: "Smith_200_pha" }, civil?.Name ?? "");
            Check("a police car's row differs from its civil twin's in both flag columns", CarKind.Read(table, Police, out CarKind.Row? police) == null && civil != null
                && police!.Flags != civil.Flags && police.MoreFlags != civil.MoreFlags, $"0x{police?.Flags:X} 0x{police?.MoreFlags:X} / 0x{civil?.Flags:X} 0x{civil?.MoreFlags:X}");
            Check("a car takes another's kind: both flag columns, and nothing else of its row",
                CarKind.Like(table, Civil, Police, out CarKind.Row? rowBefore, out CarKind.Row? rowAfter, out bool changed) == null && changed
                && rowAfter!.Flags == police!.Flags && rowAfter.MoreFlags == police.MoreFlags && rowAfter.Id == rowBefore!.Id && rowAfter.Name == rowBefore.Name
                && rowAfter.CopReport == rowBefore.CopReport);
            Check("…read back from the written table, and asked again nothing is written",
                CarKind.Read(table, Civil, out CarKind.Row? reread) == null && reread!.Flags == police!.Flags
                && CarKind.Like(table, Civil, Police, out _, out _, out bool again2) == null && !again2);
            Check("a car the table does not have is refused", CarKind.Read(table, "no_such_car", out _) != null && CarKind.Like(table, Civil, "no_such_car", out _, out _, out _) != null);
            // every other cell of the table: as the shipped one has it
            var mine = Illusion.Formats.ResourceFormats.GameTable.Load(table);
            var theirs = Illusion.Formats.ResourceFormats.GameTable.Load(shipped);
            int rowOfCivil = theirs.FindRow(2, "Smith_200_pha"), differ = 0;
            for (int r = 0; r < theirs.RowCount && mine.RowCount == theirs.RowCount; r++)
                for (int c = 0; c < theirs.ColumnCount; c++)
                    if (!Equals(mine.Cell(r, c), theirs.Cell(r, c)) && !(r == rowOfCivil && c is 1 or 9)) differ++;
            Check("…and no other cell of the table is changed", mine.RowCount == theirs.RowCount && mine.ColumnCount == theirs.ColumnCount && differ == 0, $"{differ} cell(s)");
            File.Delete(table);
        }
    }

    // A working copy, copied whole into a scratch folder (replacing what was there).
    private static void Mirror(string from, string to)
    {
        if (Directory.Exists(to)) Directory.Delete(to, recursive: true);
        foreach (string file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    internal static void RunProbe(string car)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_car_workshop.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string what, bool ok, string detail = "")
        {
            if (ok) pass++;
            else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {what}" + (detail.Length > 0 ? " — " + detail : ""));
        }

        try
        {
            if (!InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            var archive = new FileInfo(Path.Combine(MafiaEnvironment.PcFolder, "sds", "cars", car + ".sds"));
            if (!archive.Exists) { sb.AppendLine("no such archive: " + archive.FullName); return; }

            // ── the check, on a car as it is ──
            CarCheck.Report alone = CarCheck.Run(archive, null);
            Check("every level of the car is read", alone.Levels.Count > 0, $"{alone.Levels.Count} levels");
            Check("a car checked on its own has no finding", alone.Problems.Count == 0, string.Join("; ", alone.Problems.Take(3)));
            CarCheck.Report against = CarCheck.Run(archive, archive);
            // (No false alarm is all this can show: ranges, colours and bone sets are the same data on both sides.)
            Check("a car held against itself raises no false alarm, and every material is compared", against.Problems.Count == 0 && against.NotCompared.Count == 0,
                string.Join("; ", against.Problems.Take(3).Concat(against.NotCompared.Take(3))));
            foreach (CarCheck.Level level in alone.Levels)
            {
                sb.AppendLine($"    LOD {level.Lod}: {level.Vertices} vertices, {level.Triangles} triangles, skinned {level.Skinned}");
            }

            // ── a material variant, in memory ──
            MafiaMaterialCatalog catalog = MafiaMaterialCatalog.Instance;
            string? library = Assets.Bridge.CatalogAuthoredMaterials.TargetLibrary(catalog);
            MaterialSummary? pick = library == null ? null : catalog.GetMaterials(library)
                .FirstOrDefault(m => catalog.GetMaterial(m.Hash) is { TextureSlots.Count: >= 2 } info
                    && info.TextureSlots.All(s => !string.IsNullOrEmpty(s.TextureName)));
            Check("the library has a material with two textures to copy", pick != null);
            if (pick != null && library != null)
            {
                MaterialInfo source = catalog.GetMaterial(pick.Hash)!;
                const string Name = "illusion_probe_variant";
                ulong? copyHash = catalog.CloneMaterial(pick.Hash, Name, library);
                Check("the material is copied under a new name", copyHash != null && copyHash != pick.Hash);
                if (copyHash != null)
                {
                    try
                    {
                        MaterialInfo copy = catalog.GetMaterial(copyHash.Value)!;
                        Check("the copy has the source's shader and flags",
                            copy.ShaderId == source.ShaderId && copy.ShaderHash == source.ShaderHash && copy.Flags.SequenceEqual(source.Flags));
                        Check("…its samplers, texture for texture",
                            copy.TextureSlots.Select(s => (s.SlotId, s.TextureName)).SequenceEqual(source.TextureSlots.Select(s => (s.SlotId, s.TextureName))));
                        Check("…and its parameters, value for value",
                            copy.Parameters.Count == source.Parameters.Count
                            && copy.Parameters.Zip(source.Parameters).All(p => p.First.ParamId == p.Second.ParamId && p.First.Values.SequenceEqual(p.Second.Values)));
                        Check("a second copy under the same name is refused", catalog.CloneMaterial(pick.Hash, Name, library) == null);

                        string slot = source.TextureSlots[0].SlotId;
                        catalog.SetTexture(copyHash.Value, slot, "illusion_probe_texture.dds");
                        Check("re-pointing the copy's texture changes the copy",
                            catalog.GetTexture(copyHash.Value, slot) == "illusion_probe_texture.dds");
                        Check("…and leaves the source's alone",
                            catalog.GetTexture(pick.Hash, slot) == source.TextureSlots[0].TextureName,
                            $"the source now names '{catalog.GetTexture(pick.Hash, slot)}'");
                    }
                    finally
                    {
                        catalog.RemoveMaterial(copyHash.Value);
                    }
                    Check("the copy is gone again", catalog.GetMaterial(copyHash.Value) == null);
                }
            }
            sb.AppendLine("(the library was not saved)");

            // ---- lights: an entry changed, added and taken out again leaves the PREFAB byte for byte as it was;
            // the police cars' beacon bones go into the rig. All in memory.
            sb.AppendLine();
            string extracted = Illusion.Assets.Sds.SdsMeshLoader.EnsureExtracted(archive);
            string prefabPath = Path.Combine(extracted, Illusion.Formats.Archive.SdsManifest.Load(extracted).GetFiles("PREFAB")[0]);
            byte[] stored = File.ReadAllBytes(prefabPath);
            var prefab = Illusion.Formats.Prefab.PrefabFile.Load(prefabPath);
            Check("the car's lights are read", CarLights.Read(archive, out CarLights.Sheet? sheet) == null && sheet is { Lights.Count: > 0 },
                $"{sheet?.Lights.Count} lights, {sheet?.Bones.Count} bones, scaled by '{sheet?.ScaleBone}'");
            if (prefab.Car is { Lights.Count: > 0 } carPrefab && sheet != null)
            {
                Check("every light stands on a bone the rig has by name", sheet.Lights.All(l => sheet.Bones.Contains(l.Bone)),
                    string.Join(", ", sheet.Lights.Where(l => !sheet.Bones.Contains(l.Bone)).Select(l => l.Bone)));
                Illusion.Formats.Prefab.CarPrefab.Light first = carPrefab.Lights[0];
                int lights = carPrefab.Lights.Count;
                ulong newBone = Illusion.Formats.Hashing.Fnv64.Hash("probe light bone");
                Check("a light written as it is changes nothing", prefab.SetLight(first, out bool addedSame) == null && !addedSame
                    && prefab.ToBytes().AsSpan().SequenceEqual(stored));
                Check("a light on a bone that has none is added", prefab.SetLight(first with { Frame = newBone, Unk3 = 6, Unk12 = 2 }, out bool addedNew) == null && addedNew
                    && prefab.Car!.Lights.Count == lights + 1);
                byte[] grown = prefab.ToBytes();
                using var grownStream = new MemoryStream(grown);
                Illusion.Formats.Prefab.CarPrefab.Light? back = Illusion.Formats.Prefab.PrefabFile.Read(grownStream).Car?.Lights.FirstOrDefault(l => l.Frame == newBone);
                Check("…and is read back from the written file as it was given", back is { Unk3: 6, Unk12: 2 } && back.LightModel == first.LightModel
                    && back.CheckBones.SequenceEqual(first.CheckBones), $"{grown.Length} bytes against {stored.Length}");
                Check("taken out again, the file is byte for byte the stored one", prefab.RemoveLight(newBone) && prefab.ToBytes().AsSpan().SequenceEqual(stored));
                Check("a bone without a light has none to take out", !prefab.RemoveLight(newBone));
            }
            if (sheet?.ScaleBone is not { Length: > 0 })
            {
                Check("the car's PREFAB names the bone its rig is scaled by", false, "none - the beacon checks cannot run");
            }
            if (sheet?.ScaleBone is { Length: > 0 } scaleBone
                && Illusion.Assets.Sds.SdsMeshLoader.OpenScene(extracted).FrameResource is { FrameObjects: not null } scene)
            {
                Illusion.Formats.Frames.ObjectTypes.FrameObjectModel body = scene.FrameObjects.Values.OfType<Illusion.Formats.Frames.ObjectTypes.FrameObjectModel>()
                    .OrderByDescending(m => m.GetSkeletonObject().BoneNames.Length).First();
                int before = body.GetSkeletonObject().BoneNames.Length;
                bool had = sheet.Bones.Contains(CarLights.BeaconCasing);
                var at = new System.Numerics.Vector3(0f, 0.4f, 1.5f);
                string? casing = had ? null : Illusion.Assets.Frames.RigBones.Insert(body, CarLights.BeaconCasing, scaleBone, at, new System.Numerics.Vector3(0.12f), out _);
                string? lamp = had || casing != null ? null : Illusion.Assets.Frames.RigBones.Insert(body, CarLights.BeaconLamp, CarLights.BeaconCasing, at, new System.Numerics.Vector3(0.12f), out _);
                Check("the police cars' two beacon bones go into the rig under the bone it is scaled by", had || (casing == null && lamp == null
                    && body.GetSkeletonObject().BoneNames.Length == before + 2), had ? "the car has them already" : casing ?? lamp ?? $"{before} -> {before + 2} bones");
            }
            sb.AppendLine("(the car was not written)");

            // ---- the materials the game colours: the paint is among them; a material given the paint's rows has
            // them, taken out again the file is the stored one. In memory.
            sb.AppendLine();
            var listed = Illusion.Formats.Prefab.PrefabFile.Load(prefabPath);
            IReadOnlyList<Illusion.Formats.Prefab.CarMaterialRow> paintRows = listed.CarMaterialRows;
            ulong? paint = paintRows.Where(r => r.Flags == Illusion.Formats.Prefab.CarMaterialRow.Painted).Select(r => (ulong?)r.Material).FirstOrDefault();
            Check("the prefab names the material the game paints in the car's colour", paint != null,
                paint == null ? "" : $"{MafiaMaterials.GetMaterialName(paint.Value) ?? paint.Value.ToString("X16")}; {paintRows.Count} rows, {listed.CarDeformMaterialRows.Count} deform rows");
            if (paint != null)
            {
                const ulong Mine = 0x1234567890ABCDEFUL;
                int rowsOfPaint = paintRows.Count(r => r.Material == paint) + listed.CarDeformMaterialRows.Count(r => r.Material == paint);
                int adopted = listed.AdoptCarMaterial(paint.Value, Mine);
                Check("a material treated like the paint gets every row the paint has", adopted == rowsOfPaint
                    && listed.CarMaterialRows.Any(r => r.Material == Mine && r.Flags == Illusion.Formats.Prefab.CarMaterialRow.Painted), $"{adopted} of {rowsOfPaint} rows");
                Check("…and not twice", listed.AdoptCarMaterial(paint.Value, Mine) == 0);
                using var listedStream = new MemoryStream(listed.ToBytes());
                Check("…and they are read back from the written file", Illusion.Formats.Prefab.PrefabFile.Read(listedStream).CarMaterialRows.Count(r => r.Material == Mine)
                    == paintRows.Count(r => r.Material == paint));
                Check("taken out again, the file is byte for byte the stored one", listed.DropCarMaterial(Mine) == adopted && listed.ToBytes().AsSpan().SequenceEqual(stored));
            }

            // ---- winter: the swap read off the car's own pair, put on its summer scene, gives the winter scene's
            // materials slot for slot. In memory.
            sb.AppendLine();
            FileInfo twin = CarWinter.TwinOf(archive);
            if (!twin.Exists)
            {
                sb.AppendLine($"(no winter twin beside {archive.Name}: the winter checks are skipped)");
            }
            else
            {
                string twinDir = Illusion.Assets.Sds.SdsMeshLoader.EnsureExtracted(twin);
                Illusion.Formats.Frames.FrameResource? summerScene = Illusion.Assets.Sds.SdsMeshLoader.OpenScene(extracted).FrameResource;
                Illusion.Formats.Frames.FrameResource? winterScene = Illusion.Assets.Sds.SdsMeshLoader.OpenScene(twinDir).FrameResource;
                Dictionary<ulong, ulong>? swap = summerScene != null && winterScene != null ? CarWinter.Mapping(summerScene, winterScene, out string? whyNot) : null;
                Check("the car's summer and winter archives hold the same model, and one swap of materials tells them apart", swap != null,
                    swap == null ? "not the same model" : $"{swap.Count} material(s) swapped: " + string.Join(", ", swap.Select(p => $"{MafiaMaterials.GetMaterialName(p.Key) ?? p.Key.ToString("X16")} -> {MafiaMaterials.GetMaterialName(p.Value) ?? p.Value.ToString("X16")}")));
                if (swap != null && summerScene != null && winterScene != null)
                {
                    CarWinter.Apply(summerScene, swap);
                    Dictionary<ulong, ulong>? left = CarWinter.Mapping(summerScene, winterScene, out _);
                    Check("the swap put on the summer scene gives the winter scene's materials, slot for slot", left is { Count: 0 }, $"{left?.Count} still differ");
                }
            }

            // ---- the writers themselves, on a scratch copy of the working copy
            sb.AppendLine();
            string scratch = Path.Combine(Path.GetTempPath(), "illusion_car_workshop_scratch", car + ".sds");
            string scratchWinter = Path.Combine(Path.GetTempPath(), "illusion_car_workshop_scratch", car + "_z.sds");
            try
            {
                Mirror(extracted, scratch);
                if (sheet != null && sheet.ScaleBone is { Length: > 0 })
                {
                    string check = sheet.Bones.FirstOrDefault(b => b.StartsWith("deform_", StringComparison.OrdinalIgnoreCase)) ?? sheet.Bones[0];
                    var lamp = new System.Numerics.Vector3(0f, -0.4f, 1.6f);
                    bool hadBeacon = sheet.Bones.Contains(CarLights.BeaconCasing);
                    if (hadBeacon)
                    {
                        sb.AppendLine($"({car} has a beacon already: adding one is not tried on it)");
                    }
                    else
                    {
                        string? refusedBeacon = CarLights.AddBeaconIn(scratch, lamp, check, 0.12f, out CarLights.Beacon? beacon);
                        CarLights.ReadIn(scratch, out CarLights.Sheet? after);
                        Check("a beacon is written: two bones on disk and a beacon light on the casing", refusedBeacon == null && beacon is { BonesAdded: true, LightAdded: true }
                            && after != null && after.Bones.Count == sheet.Bones.Count + 2
                            && after.Lights.Any(l => l.Bone == CarLights.BeaconCasing && l.KindName == "beacon" && l.Model == "car_beacon" && l.CheckBones.SequenceEqual([check])),
                            refusedBeacon ?? $"{after?.Bones.Count} bones, parent '{beacon?.Parent}'");
                        Check("…asked again at the same place it changes nothing", CarLights.AddBeaconIn(scratch, lamp, check, 0.12f, out CarLights.Beacon? again) == null
                            && again is { BonesAdded: false, LightAdded: false });
                        string? moved = CarLights.AddBeaconIn(scratch, lamp + new System.Numerics.Vector3(0f, 0.5f, 0f), check, 0.12f, out _);
                        Check("…and asked for at another place it is refused", moved != null, moved ?? "accepted");
                    }

                    CarLights.Light first = sheet.Lights[0];
                    string? setPower = CarLights.SetLightIn(scratch, first.Bone, null, null, null, null, 1.5f, out bool addedLight, out CarLights.Sheet? powered);
                    CarLights.Light? now = powered?.Lights.FirstOrDefault(l => l.Bone == first.Bone);
                    Check("changing a light's power changes nothing else of it", setPower == null && !addedLight && now != null && now.Power == 1.5f
                        && now with { Power = first.Power, CheckBones = first.CheckBones } == first with { CheckBones = first.CheckBones } && now.CheckBones.SequenceEqual(first.CheckBones),
                        setPower ?? $"{now}");
                    Check("a power that is no number for the file is refused", CarLights.SetLightIn(scratch, first.Bone, null, null, null, null, float.PositiveInfinity, out _, out _) != null
                        && CarLights.SetLightIn(scratch, first.Bone, null, null, null, null, -1f, out _, out _) != null);
                    Check("a light model that is not one is refused", CarLights.SetLightIn(scratch, first.Bone, null, null, "no_such_model", null, null, out _, out _) != null);
                    Check("a check bone the rig does not have is refused - a light model's name is not a bone",
                        CarLights.SetLightIn(scratch, first.Bone, null, null, null, ["car_indicator"], null, out _, out _) != null);
                    Check("'-' takes the check bones off", CarLights.SetLightIn(scratch, first.Bone, null, null, null, [CarLights.NoCheckBones], null, out _, out CarLights.Sheet? bare) == null
                        && bare!.Lights.First(l => l.Bone == first.Bone).CheckBones.Count == 0);
                    Check("a light is taken out", CarLights.RemoveLightIn(scratch, first.Bone, out CarLights.Sheet? fewer) == null
                        && fewer!.Lights.All(l => l.Bone != first.Bone));
                }

                if (paint != null)
                {
                    string mine = "0x1234567890ABCDEF", theirs = "0x" + paint.Value.ToString("X16");
                    Check("a material is given the paint's rows on disk", CarMaterials.AdoptIn(scratch, mine, theirs, out int rowsAdded, out CarMaterials.Sheet? listed2) == null
                        && rowsAdded > 0 && listed2!.Rows.Any(r => r.Hash == mine && r.Flags == 1), $"{rowsAdded} rows");
                    Check("…and they are taken out again", CarMaterials.DropIn(scratch, mine, out int rowsGone, out CarMaterials.Sheet? listed3) == null
                        && rowsGone == rowsAdded && listed3!.Rows.All(r => r.Hash != mine));

                    // The car drawn with another material in place of its paint: the slots that named the paint
                    // name the other, no slot besides, and put back the scene's file is the one it was.
                    string sceneFile = Illusion.Formats.Archive.SdsManifest.Load(scratch).GetFiles("FrameResource")[0];
                    byte[] sceneWas = File.ReadAllBytes(sceneFile);
                    Check("the car is drawn with a material in place of its paint: every slot that named the paint",
                        CarMaterials.UseIn(scratch, mine, theirs, out int slotsMoved, out CarMaterials.Sheet? worn) == null && slotsMoved > 0
                        && worn!.Rows.Any(r => r.Hash == theirs && !r.OnTheCar) && CarMaterials.UseIn(scratch, mine, theirs, out int none, out _) == null && none == 0,
                        $"{slotsMoved} slots");
                    Check("…and back again the scene is byte for byte the one it was",
                        CarMaterials.UseIn(scratch, theirs, mine, out int slotsBack, out _) == null && slotsBack == slotsMoved
                        && File.ReadAllBytes(sceneFile).AsSpan().SequenceEqual(sceneWas), $"{slotsBack} slots back");
                    Check("a material is not put in its own place", CarMaterials.UseIn(scratch, theirs, theirs, out _, out _) != null);
                }

                if (Car.ReadFrom(scratch) is { Body: { } bodyPart } read)
                {
                    var size = new System.Numerics.Vector3(0.6f, 0.8f, 0.3f);
                    var where = new System.Numerics.Vector3(0.1f, -1.2f, 1.7f);
                    int before = Illusion.Mcp.AppCarWorkshop.Collisions(read, car).Components.Sum(c => c.Collisions.Count);
                    string? notAdded = Illusion.Mcp.AppCarWorkshop.AddCollisionTo(read, bodyPart.Name, CarCollisionRole.Body, CarCollisionShape.Box, size, where);
                    Illusion.Mcp.CarCollisionsInfo? collisions = Car.ReadFrom(scratch) is { } reread ? Illusion.Mcp.AppCarWorkshop.Collisions(reread, car) : null;
                    Illusion.Mcp.CarCollisionInfo? box = collisions?.Components.SelectMany(c => c.Collisions)
                        .FirstOrDefault(c => c.Shape == "box" && MathF.Abs(c.Position[0] - where.X) < 2e-3f && MathF.Abs(c.Position[1] - where.Y) < 2e-3f && MathF.Abs(c.Position[2] - where.Z) < 2e-3f);
                    Check("a box added at a place in the model's space is read back at that place, that size", notAdded == null && box != null
                        && MathF.Abs(box.Size[0] - size.X) < 2e-3f && MathF.Abs(box.Size[1] - size.Y) < 2e-3f && MathF.Abs(box.Size[2] - size.Z) < 2e-3f
                        && collisions!.Components.Sum(c => c.Collisions.Count) == before + 1, notAdded ?? $"{collisions?.Components.Sum(c => c.Collisions.Count)} collisions against {before}");
                }
                else
                {
                    sb.AppendLine($"({car}: the scratch copy does not read as a car with a body - the collision check is skipped)");
                }

                // ---- what Blender never sees, and the rig's and the PREFAB's smaller writers - on a fresh copy
                Mirror(extracted, scratch);
                Unseen(car, scratch, Check);

                // ---- winter, whole: the scratch summer copy over a scratch winter copy, by the car's own pair
                if (twin.Exists)
                {
                    string twinDir = Illusion.Assets.Sds.SdsMeshLoader.EnsureExtracted(twin);
                    Mirror(extracted, scratch);                    // a fresh summer copy: the checks above changed it
                    Mirror(twinDir, scratchWinter);
                    string[] Listing(string folder) => [.. Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
                        .Select(f => Path.GetRelativePath(folder, f) + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f))))
                        .Order(StringComparer.Ordinal)];
                    string[] winterWas = Listing(scratchWinter);
                    string? held;
                    using (new FileStream(Path.Combine(scratchWinter, "SDSContent.xml"), FileMode.Open, FileAccess.Read, FileShare.None))
                    {
                        held = CarWinter.SyncFolders(scratch, scratchWinter, extracted, twinDir, twin.Name, out _);
                    }
                    Check("a winter copy that cannot be replaced (a file of it is held open) is left exactly as it was", held != null
                        && Listing(scratchWinter).SequenceEqual(winterWas) && !Directory.Exists(scratchWinter + ".new"), held ?? "it was replaced");

                    string? notSynced = CarWinter.SyncFolders(scratch, scratchWinter, extracted, twinDir, twin.Name, out CarWinter.Result? synced);
                    Illusion.Formats.Frames.FrameResource? made = notSynced == null ? Illusion.Assets.Sds.SdsMeshLoader.OpenScene(scratchWinter).FrameResource : null;
                    Illusion.Formats.Frames.FrameResource? real = Illusion.Assets.Sds.SdsMeshLoader.OpenScene(twinDir).FrameResource;
                    Check("the winter twin made from the summer copy names the materials the shipped winter archive names, slot for slot", made != null && real != null
                        && CarWinter.Mapping(made, real, out _) is { Count: 0 }, notSynced ?? $"{synced?.Materials.Count} swaps");
                    string[] Textures(string folder) => [.. Illusion.Formats.Archive.SdsManifest.Load(folder).GetFiles("Texture").Select(f => Path.GetFileName(f)!).Order(StringComparer.OrdinalIgnoreCase)];
                    Check("…and carries the textures it carries", notSynced == null && Textures(scratchWinter).SequenceEqual(Textures(twinDir), StringComparer.OrdinalIgnoreCase),
                        notSynced ?? string.Join(", ", Textures(scratchWinter).Except(Textures(twinDir), StringComparer.OrdinalIgnoreCase).Concat(Textures(twinDir).Except(Textures(scratchWinter), StringComparer.OrdinalIgnoreCase)).Take(4)));
                    Check("…every one of them on disk, and nothing left beside the copy", notSynced == null
                        && Textures(scratchWinter).All(t => File.Exists(Path.Combine(scratchWinter, t)))
                        && !Directory.Exists(scratchWinter + ".new") && !Directory.Exists(scratchWinter + ".old"));
                }
            }
            finally
            {
                try
                {
                    string parent = Path.GetDirectoryName(scratch)!;
                    if (Directory.Exists(parent)) Directory.Delete(parent, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    sb.AppendLine("(the scratch copy could not be removed: " + ex.Message + ")");
                }
            }
            sb.AppendLine("(the writers ran on a copy in the temp folder; the car itself was not written)");
        }
        catch (Exception ex) { sb.AppendLine("EXCEPTION: " + ex); fail++; }
        finally
        {
            sb.Insert(0, $"CAR WORKSHOP PROBE ({car}): {pass} passed, {fail} failed\n\n");
            File.WriteAllText(outFile, sb.ToString());
        }
    }
}
