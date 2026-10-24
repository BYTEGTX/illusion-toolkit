using System.IO;
using System.Text;
using Illusion.Assets;
using Illusion.Assets.People;
using Illusion.Formats.Archive;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Frames.Resources;
using Illusion.Formats.Geometry;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// Cloning a person, on scratch extractions of SHIPPED archives (nothing of the game is written): the root
/// frames take the new names, only the listed ones when names are given, every buffer the geometry draws from
/// is renamed and still found in the pools, the name table lists the new names, and a bad name is refused.
/// </summary>
internal static class PersonCloneProbes
{
    // Output: %TEMP%\illusion_person_clone.txt
    internal static void RunPersonCloneProbe()
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_person_clone.txt");
        string scratch = Path.Combine(Path.GetTempPath(), "illusion_person_clone");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " - " + detail)}");
        }

        try
        {
            if (!ProbeAssert.InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            Check("refuses an upper-case name", PersonCloner.Refuse("M2rp") != null);
            Check("refuses a name with a dash", PersonCloner.Refuse("m2rp-medic") != null);
            Check("takes a plain name", PersonCloner.Refuse("m2rp_medic") == null);

            string sds = Path.Combine(MafiaEnvironment.PcFolder, "sds");
            foreach ((string archive, Dictionary<string, string>? roots) in new (string, Dictionary<string, string>?)[]
            {
                ("traffic/chlid2", null),
                ("hchar/m14csguard", new Dictionary<string, string> { ["PETE"] = "PROBE_PETE" }),
            })
            {
                var file = new FileInfo(Path.Combine(sds, archive.Replace('/', Path.DirectorySeparatorChar) + ".sds"));
                if (!file.Exists) { sb.AppendLine($"SKIP {archive}: not installed"); continue; }
                string folder = Path.Combine(scratch, archive.Replace('/', '_'));
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
                SdsArchive.Open(file.FullName).Extract(folder);

                FrameResource before = ExtractedSds.Load(folder).FrameResource!;
                List<string> rootsBefore = [.. before.FrameObjects.Values.OfType<FrameObjectFrame>()
                    .Where(f => f.ParentIndex1.Index < 0 && f.ParentIndex2.Index < 0 && !string.IsNullOrEmpty(f.Name.String))
                    .Select(f => f.Name.String!)];
                HashSet<ulong> buffersBefore = [.. before.FrameGeometries.Values.SelectMany(g => g.LOD ?? [])
                    .SelectMany(l => new[] { l.VertexBufferRef.Hash, l.IndexBufferRef.Hash })];

                Dictionary<string, string> renamed = PersonCloner.RenameInside(folder, archive, "probe_person", roots);
                FrameResource after = ExtractedSds.Load(folder).FrameResource!;
                List<string> rootsAfter = [.. after.FrameObjects.Values.OfType<FrameObjectFrame>()
                    .Where(f => f.ParentIndex1.Index < 0 && f.ParentIndex2.Index < 0 && !string.IsNullOrEmpty(f.Name.String))
                    .Select(f => f.Name.String!)];

                Check($"{archive}: the people named as asked", renamed.All(r => rootsAfter.Contains(r.Value)) && renamed.Keys.All(k => !rootsAfter.Contains(k)),
                    string.Join(", ", renamed.Select(r => $"{r.Key}->{r.Value}")));
                List<string> untouched = [.. rootsBefore.Where(r => !renamed.ContainsKey(r))];
                Check($"{archive}: other roots keep their names", untouched.All(rootsAfter.Contains), string.Join(", ", untouched));
                if (roots == null)
                {
                    Check($"{archive}: one person takes the name in capitals", renamed.Count != 1 || renamed.Values.Single() == "PROBE_PERSON");
                }

                List<FrameLOD> lods = [.. after.FrameGeometries.Values.SelectMany(g => g.LOD ?? [])];
                Check($"{archive}: no buffer keeps the source's name", lods.All(l => !buffersBefore.Contains(l.VertexBufferRef.Hash) && !buffersBefore.Contains(l.IndexBufferRef.Hash)),
                    $"{lods.Count} level(s)");
                var manifest = SdsManifest.Load(folder);
                HashSet<ulong> vbs = [.. manifest.GetFiles("VertexBufferPool").SelectMany(p =>
                    new VertexBufferPool(new MemoryStream(File.ReadAllBytes(p), writable: false)).Buffers.Keys)];
                HashSet<ulong> ibs = [.. manifest.GetFiles("IndexBufferPool").SelectMany(p =>
                    new IndexBufferPool(new MemoryStream(File.ReadAllBytes(p), writable: false)).Buffers.Keys)];
                Check($"{archive}: every renamed buffer is in the pools", lods.All(l => vbs.Contains(l.VertexBufferRef.Hash) && ibs.Contains(l.IndexBufferRef.Hash)));

                string table = manifest.GetFiles("FrameNameTable").FirstOrDefault() ?? "";
                string text = File.Exists(table) ? Encoding.ASCII.GetString(File.ReadAllBytes(table)) : "";
                Check($"{archive}: the name table lists the new names", renamed.Values.All(text.Contains));
                Check($"{archive}: provenance written", File.Exists(Path.Combine(folder, Assets.Cars.CarCloner.ProvenanceFile)));
            }
        }
        catch (Exception ex)
        {
            fail++;
            sb.AppendLine("EXCEPTION: " + ex);
        }
        finally
        {
            sb.Insert(0, $"{pass} passed, {fail} failed{Environment.NewLine}");
            File.WriteAllText(outFile, sb.ToString());
        }
    }
}
