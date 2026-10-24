using System.Text.RegularExpressions;
using Illusion.Assets.Cars;
using Illusion.Assets.Sds;
using Illusion.Formats.Archive;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Hashing;

namespace Illusion.Assets.People;

/// <summary>
/// Makes a new PERSON out of an existing one: a copy of a character archive (traffic, hchar, player, script…)
/// under a name of its own, so that it can live BESIDE the person it was made from instead of replacing them -
/// the way a cloned car lives beside its source.
///
/// <para>
/// What ties a person together, read off the shipped archives: the game loads <c>/sds/&lt;folder&gt;/&lt;file&gt;.sds</c>
/// and finds each person in it by the name of a ROOT frame (a <c>Frame</c> on the frame name table:
/// <c>CHLID1</c> in traffic\chlid1, <c>CORPORAL</c> and <c>WILLIAMS</c> in script\m01_vojaci3). Every root takes a
/// new name here (one person: the clone's name in capitals; several: the clone's name and the old one), the
/// name table is rebuilt from them, and - as for a car - every vertex and index buffer gets a name of its own:
/// the engine keeps buffers by name across all the archives it has loaded, and a copy keeping the source's names
/// would draw whichever of the two streamed first, on both people.
/// </para>
/// <para>
/// Materials and textures stay shared with the source, so the clone looks like it until they are made its own
/// (material_variant, archive_texture). Nothing registers the person anywhere: unlike a car the game has no
/// table of people to add it to - who uses it (a mission script, a multiplayer server) names its archive and
/// root frame.
/// </para>
/// </summary>
public static partial class PersonCloner
{
    public const int MaxNameLength = 31;

    [GeneratedRegex("^[a-z][a-z0-9_]*$")]
    private static partial Regex NamePattern();

    /// <summary>Null when <paramref name="name"/> can name an archive, else why not.</summary>
    public static string? Refuse(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength)
        {
            return $"a person's archive name is 1 to {MaxNameLength} characters";
        }
        if (!NamePattern().IsMatch(name)) return $"'{name}' - an archive name is a lower-case letter, then a-z, 0-9 and '_'";
        return null;
    }

    /// <summary>
    /// Clones the character archive <paramref name="source"/> (a path under pc\sds without the extension, such
    /// as <c>traffic/chlid1</c>) into <c>&lt;same folder&gt;/&lt;name&gt;.sds</c> and builds it. The copy is
    /// made from the source's WORKING COPY, so changes made to the source and not yet reverted travel with it.
    /// <paramref name="roots"/> may give the new name of each person (old root name -> new); missing ones are
    /// named by the rule in the class summary.
    /// </summary>
    public static PersonCloneOutcome? Clone(string source, string name, IReadOnlyDictionary<string, string>? roots, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(source);
        refusal = Refuse(name);
        if (refusal != null) return null;
        if (!MafiaEnvironment.IsInitialized)
        {
            refusal = "the game folder is not set";
            return null;
        }
        string relative = source.Replace('\\', '/').Trim('/');
        if (relative.EndsWith(".sds", StringComparison.OrdinalIgnoreCase)) relative = relative[..^4];
        string sds = Path.Combine(MafiaEnvironment.PcFolder, "sds");
        var from = new FileInfo(Path.Combine(sds, relative.Replace('/', Path.DirectorySeparatorChar) + ".sds"));
        if (!from.Exists)
        {
            refusal = $"there is no archive {relative}.sds under pc\\sds";
            return null;
        }
        var to = new FileInfo(Path.Combine(from.DirectoryName!, name + ".sds"));
        if (to.Exists)
        {
            refusal = $"{to.Name} already exists in {from.Directory!.Name} - pick another name";
            return null;
        }
        string fromFolder = SdsMeshLoader.EnsureExtracted(from);
        string toFolder = MafiaEnvironment.ExtractedDir(to);
        if (Directory.Exists(toFolder))
        {
            refusal = $"there is already a working copy at {toFolder}, though {to.Name} is not built - it may hold work; "
                + "remove or rename that folder, or pick another name";
            return null;
        }

        var journal = new GameWriteJournal();
        journal.WillCreateFolder(toFolder);
        journal.WillCreateFile(to.FullName);
        try
        {
            SdsWriter.EnsureMemoryRequirements(from);
            CarCloner.CopyTree(fromFolder, toFolder);
            Dictionary<string, string> renamed = RenameInside(toFolder, relative, name, roots);
            SdsWriter.PackResult made = SdsWriter.PackSds(to, createBackup: false);
            journal.Commit();
            var notes = new List<string>
            {
                "materials and textures are still the source's - give the clone its own with material_variant / archive_texture",
            };
            if (made.Dropped.Count > 0)
            {
                notes.Add($"{made.Dropped.Count} manifest entr(ies) named a file missing from the working copy and were left out");
            }
            notes.Add(GameFileIndex.Reset()
                ? "the game's file list (vfs.bin) was reset - the next start rebuilds it with the new archive"
                : $"the game's file list was not reset - remove {GameFileIndex.Path} before starting the game");
            return new PersonCloneOutcome(made.Archive, renamed, notes);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            List<string> problems = journal.Undo();
            refusal = $"the clone failed and was taken back: {ex.Message}"
                + (problems.Count == 0 ? "" : " - but NOT everything could be put back: " + string.Join("; ", problems));
            return null;
        }
    }

    // Renames every person's root frame (and so the name table) and gives the buffers names of their own.
    internal static Dictionary<string, string> RenameInside(
        string folder, string source, string name, IReadOnlyDictionary<string, string>? roots)
    {
        SdsManifest manifest = SdsManifest.Load(folder);
        string label = Path.GetFileName(folder);
        IReadOnlyList<string> frames = manifest.GetFiles("FrameResource");
        if (frames.Count == 0 || ExtractedSds.Load(folder).FrameResource is not { } frame)
        {
            throw new InvalidDataException($"{label} has no frame resource - it is not a person's archive");
        }

        // The people: named root Frames (no parent) - what the game looks a person up by, and what the archive's
        // name table lists (CHLID1; CORPORAL and WILLIAMS).
        List<FrameObjectFrame> people = [.. frame.FrameObjects.Values.OfType<FrameObjectFrame>()
            .Where(f => f.ParentIndex1.Index < 0 && f.ParentIndex2.Index < 0 && !string.IsNullOrEmpty(f.Name.String))];
        // Given names name the people: an archive of a scripted scene may hold other named roots (a camera, a
        // prop) that are not people and keep their names.
        if (roots is { Count: > 0 })
        {
            foreach (string wanted in roots.Keys)
            {
                if (!people.Any(p => string.Equals(p.Name.String, wanted, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidDataException($"{label} has no root frame named {wanted}");
                }
            }
            people = [.. people.Where(p => roots.Keys.Any(k => string.Equals(k, p.Name.String, StringComparison.OrdinalIgnoreCase)))];
        }
        if (people.Count == 0) throw new InvalidDataException($"{label} has no named root frame - no person to rename");
        // the roots that keep their names: a new name may not be one of them (two roots of one name, one person found)
        HashSet<string> kept = new(frame.FrameObjects.Values.OfType<FrameObjectFrame>()
            .Where(f => f.ParentIndex1.Index < 0 && f.ParentIndex2.Index < 0 && !string.IsNullOrEmpty(f.Name.String) && !people.Contains(f))
            .Select(f => f.Name.String!), StringComparer.OrdinalIgnoreCase);

        string upper = name.ToUpperInvariant();
        var renamed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (FrameObjectFrame person in people)
        {
            string old = person.Name.String!;
            string? given = roots?.FirstOrDefault(k => string.Equals(k.Key, old, StringComparison.OrdinalIgnoreCase)).Value;
            string fresh = !string.IsNullOrWhiteSpace(given)
                ? given
                : people.Count == 1 ? upper : $"{upper}_{old}";
            if (renamed.ContainsValue(fresh) || kept.Contains(fresh)) throw new InvalidDataException($"two roots would both be named {fresh}");
            renamed[old] = fresh;
        }

        int buffers = CarCloner.GiveOwnBuffers(manifest, frame, people[0].Name.String!, upper);
        if (buffers == 0) throw new InvalidDataException($"{label} has no geometry buffers to give the new name to");
        foreach (FrameObjectFrame person in people)
        {
            string old = person.Name.String!;
            person.Name = new HashName(renamed[old]);
            if (string.Equals(person.ActorHash?.String, old, StringComparison.OrdinalIgnoreCase))
            {
                person.ActorHash = new HashName(renamed[old]);
            }
        }

        AtomicFile.WriteAllBytes(frames[0], frame.WriteToStream());
        IReadOnlyList<string> tables = manifest.GetFiles("FrameNameTable");
        if (tables.Count > 0)
        {
            var table = new FrameNameTable();
            table.BuildDataFromResource(frame);
            using var ms = new MemoryStream();
            using (var writer = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true)) table.WriteToFile(writer);
            AtomicFile.WriteAllBytes(tables[0], ms.ToArray());
        }
        File.WriteAllText(Path.Combine(folder, CarCloner.ProvenanceFile), System.Text.Json.JsonSerializer.Serialize(
            new Dictionary<string, object> { ["source"] = source, ["archive"] = name, ["people"] = renamed }));
        return renamed;
    }
}

/// <summary>What a person clone made: the built archive and each person's new root name.</summary>
public sealed record PersonCloneOutcome(string Archive, IReadOnlyDictionary<string, string> People, IReadOnlyList<string> Notes);
