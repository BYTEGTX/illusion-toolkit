using System.Numerics;
using Illusion.Assets.Frames;
using Illusion.Assets.Sds;
using Illusion.Formats.Archive;
using Illusion.Formats.City;
using Illusion.Formats.CityAreas;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Frames.Resources;
using Illusion.Formats.Hashing;

namespace Illusion.Assets.World;

/// <summary>
/// Carries the locations a DLC's own copy of <c>city_univers.sds</c> adds into the base game's copy.
/// <para>
/// A DLC such as Joe's Adventures ships a <c>city_univers</c> of its own: a different scene, with load zones for
/// the districts it brings (<c>dlcdamroad</c>, <c>dlcyacht</c>...) and volumes for its interiors, and its own
/// <c>cityareas.bin</c> and <c>cityshops.bin</c>. Free ride reads the BASE copy, so none of it is there. What is
/// carried here is taken over as the DLC has it - every plane of a volume (most of these are cut by slanted
/// planes, which a box made with <see cref="LoadZones.Create"/> would lose), its frame flags, the byte of its
/// district line, the rows of the interiors - rather than made again.
/// </para>
/// <para>
/// By default a zone is carried when its line names a district the base table has never named, and an interior
/// when the base table has no row of that name. A district or an interior whose archive is in neither the base
/// game nor the DLC is not carried: the game would be asked for a file it does not have.
/// </para>
/// </summary>
public sealed class LocationImport
{
    /// <summary>A load zone of the DLC's copy and what happens to it; <paramref name="Skipped"/> says why it is not carried.</summary>
    public sealed record ZoneItem(string Name, string? District1, string? District2, byte Flag, Vector3 Min, Vector3 Max, int Planes, string? Skipped);

    /// <summary>An interior of the DLC's table and what happens to it: the volumes and area rows it brings.</summary>
    public sealed record InteriorItem(string Name, string Archive, IReadOnlyList<string> Volumes, int AreaRows, int Places, string? Skipped);

    private readonly FileInfo _archive;
    private readonly string _extracted;
    private readonly FrameResource _scene;
    private readonly string _areasFile;
    private readonly string _shopsFile;
    private readonly CityAreasTable _areas;
    private readonly CityShopsTable _shops;
    private readonly List<string> _notes = [];

    public IReadOnlyList<ZoneItem> Zones { get; private set; } = [];
    public IReadOnlyList<InteriorItem> Interiors { get; private set; } = [];

    /// <summary>What the carry changes that is worth knowing, beyond the lists.</summary>
    public IReadOnlyList<string> Notes => _notes;

    /// <summary>Whether anything is carried at all.</summary>
    public bool Changes => Zones.Any(z => z.Skipped == null) || Interiors.Any(i => i.Skipped == null);

    private LocationImport(FileInfo archive, string extracted, FrameResource scene, string areasFile, string shopsFile)
    {
        _archive = archive;
        _extracted = extracted;
        _scene = scene;
        _areasFile = areasFile;
        _shopsFile = shopsFile;
        _areas = CityAreasTable.Parse(File.ReadAllBytes(areasFile));
        _shops = CityShopsTable.Parse(File.ReadAllBytes(shopsFile));
    }

    /// <summary>
    /// Works out, IN MEMORY, what the DLC's copy brings and carries it into the base copy read from its working
    /// copy. <see cref="Save"/> writes it.
    /// </summary>
    /// <param name="copy">The DLC's <c>city_univers.sds</c> (one of <see cref="LoadZones.Copies"/>).</param>
    /// <param name="zones">The zones to carry by name; null for every zone that names a district the base lacks.</param>
    /// <param name="interiors">The interiors to carry by name; null for every interior the base table has no row for.</param>
    public static LocationImport Plan(Func<FileInfo, string> ensureExtracted, FileInfo copy, IReadOnlyCollection<string>? zones,
        IReadOnlyCollection<string>? interiors)
    {
        ArgumentNullException.ThrowIfNull(ensureExtracted);
        ArgumentNullException.ThrowIfNull(copy);
        var archive = new FileInfo(MafiaEnvironment.CityUniversSds);
        if (string.Equals(copy.FullName, archive.FullName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("the copy to carry from is the base copy itself");

        string extracted = ensureExtracted(archive), from = ensureExtracted(copy);
        var import = new LocationImport(archive, extracted, Scene(extracted), Table(extracted, "cityareas.bin"), Table(extracted, "cityshops.bin"));
        FrameResource theirs = Scene(from);
        var theirAreas = CityAreasTable.Parse(File.ReadAllBytes(Table(from, "cityareas.bin")));
        var theirShops = CityShopsTable.Parse(File.ReadAllBytes(Table(from, "cityshops.bin")));

        // the DLC's archives stand under its own sds folder (dlcs\<name>\sds\city, ...\shops), the game's under pc\sds
        string dlcSds = copy.Directory?.Parent?.FullName ?? throw new InvalidOperationException("the copy does not stand in a DLC's sds folder");
        var districts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string folder in new[] { MafiaEnvironment.CityFolder, Path.Combine(dlcSds, "city") }.Where(Directory.Exists))
        {
            foreach (string file in Directory.EnumerateFiles(folder, "*.sds")) districts.Add(Path.GetFileNameWithoutExtension(file));
        }
        bool HasInterior(string name) =>
            File.Exists(Path.Combine(MafiaEnvironment.PcFolder, "sds", "shops", name + ".sds")) || File.Exists(Path.Combine(dlcSds, "shops", name + ".sds"));

        import.CarryZones(theirs, theirAreas, zones, districts);
        import.CarryInteriors(theirs, theirShops, interiors, HasInterior);
        return import;
    }

    private void CarryZones(FrameResource theirs, CityAreasTable theirAreas, IReadOnlyCollection<string>? asked, HashSet<string> districts)
    {
        var named = new HashSet<string>(_areas.Districts, StringComparer.OrdinalIgnoreCase);
        var items = new List<ZoneItem>();
        foreach (CityAreasTable.Entry line in theirAreas.Entries.OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            bool wanted = asked == null
                ? new[] { line.Target1, line.Target2 }.Any(d => !string.IsNullOrEmpty(d) && !named.Contains(d))
                : asked.Contains(line.Name, StringComparer.OrdinalIgnoreCase);
            if (!wanted || _areas.Find(line.Name) != null) continue;
            FrameObjectArea? volume = Volume(theirs, line.Name);
            (Vector3 lo, Vector3 hi) = volume == null ? (Vector3.Zero, Vector3.Zero) : LoadZones.WorldBox(volume);
            string? missing = new[] { line.Target1, line.Target2 }
                .FirstOrDefault(d => !string.IsNullOrEmpty(d) && DistrictNames.Resolve(d, districts) == null);
            string? skipped = volume == null ? "the DLC's scene has no volume of that name"
                : missing != null ? $"district '{missing}' has no archive in the game or the DLC"
                : Volume(_scene, line.Name) != null ? "the base scene already has a volume of that name"
                : CopyVolume(theirs, volume);
            if (skipped == null && _areas.Add(line.Name, line.Target1!, line.Target2, line.Flag) is { } refused) skipped = refused;
            items.Add(new ZoneItem(line.Name, line.Target1, line.Target2, line.Flag, lo, hi, volume?.Planes?.Length ?? 0, skipped));
        }
        if (asked != null)
        {
            foreach (string name in asked.Where(n => !items.Any(i => string.Equals(i.Name, n, StringComparison.OrdinalIgnoreCase))))
            {
                items.Add(new ZoneItem(name, null, null, 0, Vector3.Zero, Vector3.Zero, 0,
                    _areas.Find(name) != null ? "the base table already has a line for it" : "the DLC's cityareas.bin has no line for it"));
            }
        }
        Zones = items;
    }

    private void CarryInteriors(FrameResource theirs, CityShopsTable theirShops, IReadOnlyCollection<string>? asked, Func<string, bool> hasArchive)
    {
        var items = new List<InteriorItem>();
        bool padsLost = false;
        foreach (CityShopsTable.Shop row in theirShops.Shops)
        {
            bool wanted = asked == null || asked.Contains(row.Name, StringComparer.OrdinalIgnoreCase);
            if (!wanted || _shops.Shops.Any(s => Same(s.Name, row.Name))) continue;
            List<CityShopsTable.Area> rows = [.. theirShops.Areas.Where(a => Same(a.Archive, row.Name))];
            string archive = rows.FirstOrDefault()?.Archive ?? row.Name.ToLowerInvariant();
            List<string> volumes = [.. rows.SelectMany(a => new[] { a.LoadZone, a.UnloadZone }).Distinct(StringComparer.OrdinalIgnoreCase)];
            string? skipped = !hasArchive(archive) ? $"shops\\{archive}.sds is in neither the game nor the DLC"
                : rows.Count == 0 ? "no area row of the DLC's table loads it"
                : volumes.FirstOrDefault(v => Volume(theirs, v) == null && Volume(_scene, v) == null) is { } absent ? $"neither scene has a volume {absent}"
                : null;
            // the volumes first: they are what can refuse; a volume the base scene already has is used as it is
            foreach (string name in skipped == null ? volumes : [])
            {
                if (Volume(_scene, name) == null && CopyVolume(theirs, Volume(theirs, name)!) is { } refused)
                {
                    skipped = refused;
                    break;
                }
            }
            if (skipped == null)
            {
                foreach (CityShopsTable.Area area in rows.Where(a => !_shops.Areas.Any(b => Same(b.LoadZone, a.LoadZone) && Same(b.UnloadZone, a.UnloadZone))))
                {
                    _shops.Areas.Add(new CityShopsTable.Area { LoadZone = area.LoadZone, UnloadZone = area.UnloadZone, Archive = area.Archive, Pad = area.Pad });
                    padsLost |= area.Pad != 0;
                }
                var shop = new CityShopsTable.Shop
                {
                    Name = row.Name, Holder = row.Holder, ActorFile = row.ActorFile, Description = row.Description,
                    Unk1 = row.Unk1, Unk2 = row.Unk2, Unk3 = row.Unk3,
                };
                shop.Entities.AddRange(row.Entities);
                foreach (CityShopsTable.Place place in row.Places)
                {
                    shop.Places.Add(new CityShopsTable.Place
                    {
                        Marker = place.Marker, MapX = place.MapX, MapY = place.MapY, Icon = place.Icon, TextId = place.TextId,
                        Marks = [.. place.Marks], Pad = place.Pad,
                    });
                    padsLost |= place.Pad != 0;
                }
                _shops.Shops.Add(shop);
            }
            items.Add(new InteriorItem(row.Name, archive, volumes, rows.Count, row.Places.Count, skipped));
        }
        if (asked != null)
        {
            foreach (string name in asked.Where(n => !items.Any(i => Same(i.Name, n))))
            {
                items.Add(new InteriorItem(name, name.ToLowerInvariant(), [], 0, 0,
                    _shops.Shops.Any(s => Same(s.Name, name)) ? "the base table already has a row for it" : "the DLC's cityshops.bin has no row for it"));
            }
        }
        if (padsLost && _shops.Version != 9)
        {
            _notes.Add($"the base cityshops.bin is version {_shops.Version}: the extra byte version 9 gives a row was not 0 on some carried rows, and has no place in it");
        }
        Interiors = items;
    }

    // A volume taken over whole: planes, box, flags and place, filed in the base scene's folder of the same name.
    // The copy is made from a volume of the BASE scene filed there - what ties a frame to the scene that owns it
    // comes from that one - and given everything else of the DLC's.
    private string? CopyVolume(FrameResource theirs, FrameObjectArea source)
    {
        if (source.Parent != null || source.Root != null) return $"{source.Name} hangs on a frame, not on a scene folder";
        if (FrameDuplicator.ResolveRef(theirs, source, FrameEntryRefTypes.Parent2) is not FrameHeaderScene folder)
            return $"{source.Name} is not filed in a scene folder";
        FrameHeaderScene? mine = _scene.FrameScenes.Values.FirstOrDefault(s => s.Name.Hash == folder.Name.Hash);
        if (mine == null) return $"the base scene has no folder {folder.Name}";
        FrameObjectArea? like = _scene.FrameObjects.Values.OfType<FrameObjectArea>()
            .FirstOrDefault(a => a.Parent == null && a.Root == null && ReferenceEquals(FrameDuplicator.ResolveRef(_scene, a, FrameEntryRefTypes.Parent2), mine));
        if (like == null) return $"the base scene has no volume in {folder.Name} to file it like";

        var volume = new FrameObjectArea(like)
        {
            Name = new HashName(source.Name),
            SecondaryFlags = source.SecondaryFlags,
            Unk3 = source.Unk3,
            FrameNameTableFlags = source.FrameNameTableFlags,
            IsOnFrameTable = source.IsOnFrameTable,
            DataSize = source.DataSize,
            Data = [.. source.Data ?? []],
            Unk01 = source.Unk01,
            Planes = [.. source.Planes ?? []],
            PlaneSize = source.PlaneSize,
            Bounds = source.Bounds,
        };
        _scene.FrameObjects.Add(volume.RefID, volume);
        FrameDuplicator.LinkParents(volume, null, mine);
        volume.LocalTransform = source.LocalTransform;
        return null;
    }

    /// <summary>
    /// Writes the scene, its frame name table, <c>cityareas.bin</c> and <c>cityshops.bin</c> into the working copy
    /// of the base <c>city_univers.sds</c> - all of them, or, when one cannot be written, none. A Build of that
    /// archive takes it into the game. Returns the files written.
    /// </summary>
    /// <exception cref="IOException">A file could not be written; the message says what was left changed, if anything.</exception>
    public IReadOnlyList<string> Save()
    {
        if (!Changes) return [];
        List<string> files =
        [
            .. SdsManifest.Load(_extracted).GetFiles("FrameResource").Take(1), .. SdsManifest.Load(_extracted).GetFiles("FrameNameTable").Take(1),
            _areasFile, _shopsFile,
        ];
        var before = files.Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(f => f, f => File.Exists(f) ? File.ReadAllBytes(f) : null,
            StringComparer.OrdinalIgnoreCase);
        var written = new List<string>();
        try
        {
            // the tables first: they are the ones that can refuse (names past the reach of their offsets)
            byte[] areas = _areas.ToBytes(), shops = _shops.ToBytes();
            written.Add(SdsWriter.SaveFrameResource(_scene, _archive));
            if (SdsWriter.SaveFrameNameTable(_scene, _archive) is { } names) written.Add(names);
            AtomicFile.WriteAllBytes(_areasFile, areas);
            written.Add(_areasFile);
            AtomicFile.WriteAllBytes(_shopsFile, shops);
            written.Add(_shopsFile);
            return written;
        }
        catch (Exception ex)
        {
            var left = new List<string>();
            foreach (string file in written)
            {
                try
                {
                    if (before.TryGetValue(file, out byte[]? bytes) && bytes != null) AtomicFile.WriteAllBytes(file, bytes);
                    else left.Add(Path.GetFileName(file));
                }
                catch (Exception again) when (again is IOException or UnauthorizedAccessException)
                {
                    left.Add(Path.GetFileName(file));
                }
            }
            if (left.Count == 0) throw;
            throw new IOException($"{ex.Message} - and the working copy could not be put back as it was ({string.Join(", ", left)}): unpack city_univers again", ex);
        }
    }

    private static FrameResource Scene(string extracted) =>
        ExtractedSds.Load(extracted).FrameResource ?? throw new InvalidDataException("city_univers.sds has no scene");

    private static string Table(string extracted, string name) =>
        Directory.EnumerateFiles(extracted, name, SearchOption.AllDirectories).FirstOrDefault()
            ?? throw new FileNotFoundException($"the working copy of city_univers has no {name}", Path.Combine(extracted, name));

    private static FrameObjectArea? Volume(FrameResource scene, string name) =>
        scene.FrameObjects.Values.OfType<FrameObjectArea>().FirstOrDefault(a => string.Equals(a.Name?.ToString(), name, StringComparison.OrdinalIgnoreCase));

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
