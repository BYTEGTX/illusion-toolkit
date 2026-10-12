using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Threading;
using Illusion.Assets;
using Illusion.Assets.Sds;
using Illusion.Domain;
using Illusion.Mcp;
using Illusion.Rendering.Gizmos;
using Illusion.Rendering.Gpu;
using Illusion.Scene;
using Illusion.Viewport;

namespace Illusion.Views;

/// <summary>
/// The Interiors layer of the map editor: every interior that stands inside the loaded part of the map, drawn
/// where it stands. The game keeps an interior's rooms in an archive of their own under <c>shops\</c>, hung on
/// a marker that carries them to each place; a district's scene has none of it, so without this a place on
/// the map is a point with nothing at it. Switched like the Crash objects layer.
/// <para>
/// What is drawn is a stand-in - not part of the scene, never saved. A place that was ADDED is picked by a
/// click on it and then moved and turned with the tool shelf's gizmo like any object; letting go writes the
/// place where it now stands. A place the game ships with is not moved: it is not ours to move. Standing a
/// place, moving it and taking it out are steps of the editor's history, undone and redone like any other.
/// </para>
/// </summary>
internal sealed class InteriorStandIns : IGizmoStandIn, IDisposable
{
    /// <summary>The most places drawn at once, the nearest to the camera first: an interior is a district's
    /// worth of triangles, and the whole map has some hundred and fifty places.</summary>
    public const int MostPlaces = 24;

    /// <summary>The boxes a place gets: what the tools write when they are not told otherwise. Half the side
    /// of the box inside which the game loads the interior, of the one outside which it lets it go, and half
    /// the height of both.</summary>
    public const float LoadHalf = 35f, UnloadHalf = 60f, HalfHeight = 23f;

    // How near loaded scenery a place has to lie to be drawn with it.
    private const float Margin = 25f;

    // A mesh wider than this is a sheet of ground or water, not something an interior stands in or beside.
    private const float WidestNeighbour = 150f;

    // The widest a room is taken to be. The widest of the game's interiors is under 60 m; the "outside" an
    // interior fakes behind its windows is a sheet of streets hundreds of metres across.
    private const float WidestRoom = 100f;

    // Words in the names of what an interior carries beside its rooms: far-off shells and low-detail doubles
    // (proxy, lod), the view faked outside the windows (venek, venku, okoli - Czech for outside and
    // surroundings), and the glow of lamps and sun through glass.
    private static readonly string[] NotRooms = ["proxy", "venek", "venku", "okoli", "glow", "flare", "corona", "sunray", "godray"];

    private static readonly Vector4 Picked = new(1f, 0.62f, 0.22f, 1f);
    private static readonly Vector4 Fresh = new(0.35f, 1f, 0.55f, 1f);

    // An interior as read: its meshes where the archive has them, where the marker that carries it stands, and
    // how far its lowest point lies under that marker.
    private sealed record Loaded(IReadOnlyList<MeshData> Meshes, Vector3 Holder, float Depth);

    // One place on the map: whose it is, where its marker stands and how it is turned.
    private sealed record Standing(string Key, string Shop, string Archive, string Marker, bool Added, Vector3 At, float Turn);

    private readonly D3DImageHost _host;
    private readonly AppEditorSession _session = new();
    private readonly Dictionary<string, Loaded?> _read = new(StringComparer.OrdinalIgnoreCase);
    // What is drawn, place by place: each mesh with the matrix it is drawn at when nothing drags it.
    private readonly Dictionary<string, List<(GpuMesh Mesh, Matrix4x4 World)>> _drawn = [];
    private readonly DispatcherTimer _settle = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly Func<Point, bool> _clicks;
    private List<Standing> _standing = [];
    private Standing? _picked;
    private object? _scene;
    private bool _shown;
    // A drag of the gizmo in progress: what it does, and where it has the place by now.
    private GizmoMode _dragMode;
    private (Vector3 At, float Turn)? _dragged;

    public InteriorStandIns(D3DImageHost host)
    {
        _host = host;
        _clicks = Clicked;
        _host.SceneChanged += SceneChanged;
        _host.SelectionChanged += SelectionChanged;
        _host.StandInClickTaker = _clicks;
        _settle.Tick += (_, _) => Refresh();
    }

    public void Dispose()
    {
        _host.SceneChanged -= SceneChanged;
        _host.SelectionChanged -= SelectionChanged;
        if (_host.StandInClickTaker == _clicks) _host.StandInClickTaker = null;
        _settle.Stop();
        Pick(null);
        HideAll();
    }

    /// <summary>Raised when another place is picked, or none.</summary>
    public event Action? PickedChanged;

    /// <summary>Raised when the gizmo has written a place somewhere else: the table is not what it was.</summary>
    public event Action? PlacesChanged;

    /// <summary>The interior and the marker of the picked place.</summary>
    public (string Shop, string Marker)? PickedPlace => _picked == null ? null : (_picked.Shop, _picked.Marker);

    /// <summary>Whether the layer is on. Switching it on reads the table and draws; off takes everything away.</summary>
    public bool Shown
    {
        get => _shown;
        set
        {
            if (_shown == value) return;
            _shown = value;
            Refresh();
        }
    }

    // Edits raise this too, and they change nothing here: only another scene - a district loaded, the area
    // changed - is a reason to look again, once it has finished loading.
    private void SceneChanged()
    {
        if (_shown && !ReferenceEquals(_host.Roots.FirstOrDefault(), _scene)) _settle.Start();
    }

    // Something of the scene was selected: the gizmo is that object's now.
    private void SelectionChanged()
    {
        if (_picked != null && _host.SelectedNodes.Count > 0) Pick(null);
    }

    /// <summary>The marker that carries an interior, named from one of its place markers: the same name
    /// numbered 00.</summary>
    public static string HolderOf(string marker)
    {
        int cut = marker.LastIndexOf('_');
        return cut > 0 && marker[(cut + 1)..].All(char.IsAsciiDigit) ? marker[..(cut + 1)] + "00" : marker;
    }

    /// <summary>
    /// Whether a mesh of an interior's archive is part of its ROOMS - what somebody placing the interior on the
    /// map wants to see. An archive also carries what the game shows from afar or through the windows, and
    /// light it draws as glow; drawn on the map those are a slab over half a district and black pillars.
    /// </summary>
    internal static bool IsRoom(MeshData mesh)
    {
        if (mesh.Lod != 0 || mesh.Instances != null || mesh.Positions.Length == 0) return false;
        string name = mesh.Name ?? "";
        if (NotRooms.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase))) return false;
        // "..._lod", "..._lod1", "lod_...": a low-detail double of something the archive has in full.
        string[] words = name.Split('_', ' ', '-', '.');
        if (words.Any(w => w.StartsWith("lod", StringComparison.OrdinalIgnoreCase) && w.Skip(3).All(char.IsAsciiDigit))) return false;
        // Drawn with nothing but glow: a shaft of light, which this viewport would draw as a solid.
        if (mesh.Parts.Length > 0 && mesh.Parts.All(part => (MafiaMaterials.GetMaterialName(part.MaterialHash) ?? "") is { } material
                && (material.Contains("glow", StringComparison.OrdinalIgnoreCase) || material.Contains("flare", StringComparison.OrdinalIgnoreCase))))
        {
            return false;
        }
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (Vector3 corner in mesh.Positions)
        {
            Vector3 at = Vector3.Transform(corner, mesh.World);
            min = Vector3.Min(min, at);
            max = Vector3.Max(max, at);
        }
        return max.X - min.X <= WidestRoom && max.Y - min.Y <= WidestRoom;
    }

    private static SdsFrameNode? Find(IEnumerable<SdsFrameNode> nodes, string name)
    {
        foreach (SdsFrameNode node in nodes)
        {
            if (string.Equals(node.Name, name, StringComparison.OrdinalIgnoreCase)) return node;
            if (Find(node.Children, name) is { } below) return below;
        }
        return null;
    }

    private Loaded? Read(string archive, string holder)
    {
        if (_read.TryGetValue(archive, out Loaded? known)) return known;
        Loaded? loaded = null;
        // Wherever the game has it: its own shops folder, or a DLC's.
        FileInfo file = Assets.World.ShopPlaces.ArchiveFile(archive);
        if (file.Exists)
        {
            try
            {
                (List<SdsFrameNode> roots, List<MeshData> meshes, _) = SdsMeshLoader.LoadHierarchy(file);
                // Its textures are its own: the district's folders do not have them.
                _host.Rnd?.Textures.AddFolder(SdsMeshLoader.EnsureExtracted(file));
                Vector3 at = Find(roots, holder)?.Source is IFrameNode frame ? frame.WorldTransform.Translation : Vector3.Zero;
                List<MeshData> drawn = [.. meshes.Where(IsRoom)];
                float lowest = at.Z;
                foreach (MeshData mesh in drawn)
                {
                    foreach (Vector3 corner in mesh.Positions) lowest = MathF.Min(lowest, Vector3.Transform(corner, mesh.World).Z);
                }
                loaded = new Loaded(drawn, at, at.Z - lowest);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException
                or NotSupportedException)
            {
                loaded = null;      // an interior that cannot be read is a place without a picture, not an error
            }
        }
        return _read[archive] = loaded;
    }

    /// <summary>How far an interior's lowest point lies under the marker that carries it: what a point on the
    /// ground is raised by for the interior to stand ON it. 0 when the interior cannot be read.</summary>
    public float DepthUnderMarker(string archive, string holder) => Read(archive, holder)?.Depth ?? 0f;

    private static IEnumerable<SceneNode> Walk(IEnumerable<SceneNode> roots)
    {
        var stack = new Stack<SceneNode>(roots);
        while (stack.Count > 0)
        {
            SceneNode node = stack.Pop();
            yield return node;
            foreach (SceneNode child in node.Children) stack.Push(child);
        }
    }

    // The loaded scenery on the map: the footprint of each of its meshes, grown a little. A district is not a
    // rectangle - its box covers water and the corners of its neighbours - so a place is held against the
    // buildings and streets themselves: one with nothing loaded round it would hang in the air.
    private List<(Vector2 Min, Vector2 Max)> LoadedPart()
    {
        var footprints = new List<(Vector2, Vector2)>();
        foreach (SceneNode node in Walk(_host.Roots))
        {
            // An instanced mesh spans every copy of it across the map: it says nothing of where the district is.
            if (node.Mesh is not { Instanced: false } mesh) continue;
            var min = new Vector2(mesh.BoundsMin.X, mesh.BoundsMin.Y);
            var max = new Vector2(mesh.BoundsMax.X, mesh.BoundsMax.Y);
            if (max.X - min.X > WidestNeighbour || max.Y - min.Y > WidestNeighbour) continue;
            footprints.Add((min - new Vector2(Margin), max + new Vector2(Margin)));
        }
        return footprints;
    }

    // The places inside the loaded part of the map, the nearest to the camera first. The table has every
    // place's spot on the map; an interior's archive is read only when one of its places lies inside.
    private List<Standing> Wanted(out int skipped)
    {
        skipped = 0;
        var wanted = new List<Standing>();
        List<(Vector2 Min, Vector2 Max)> loaded = LoadedPart();
        if (loaded.Count == 0) return wanted;
        bool Inside(float x, float y)
        {
            foreach ((Vector2 min, Vector2 max) in loaded)
            {
                if (x >= min.X && x <= max.X && y >= min.Y && y <= max.Y) return true;
            }
            return false;
        }
        if (_session.ShopPlaces(null, out IReadOnlyList<ShopInfo> shops) != null) return wanted;
        foreach (ShopInfo shop in shops)
        {
            if (shop.Archive == null || !shop.Places.Any(p => p.Map is { Length: 2 } map && Inside(map[0], map[1]))) continue;
            if (_session.ShopPlaces(shop.Name, out IReadOnlyList<ShopInfo> read) != null) continue;
            foreach (ShopPlaceInfo place in read.FirstOrDefault(s => string.Equals(s.Name, shop.Name, StringComparison.OrdinalIgnoreCase))?.Places ?? [])
            {
                if (place.At is not { Length: 3 } at || !Inside(at[0], at[1])) continue;
                var stands = new Vector3(at[0], at[1], at[2]);
                float turn = place.Turn ?? 0f;
                wanted.Add(new Standing($"{shop.Archive}|{stands.X:R}|{stands.Y:R}|{stands.Z:R}|{turn:R}", shop.Name, shop.Archive,
                    place.Marker, place.Added, stands, turn));
            }
        }
        Vector3 eye = _host.CameraPose.Position;
        wanted = [.. wanted.OrderBy(w => Vector3.DistanceSquared(w.At, eye))];
        if (wanted.Count > MostPlaces)
        {
            skipped = wanted.Count - MostPlaces;
            wanted.RemoveRange(MostPlaces, skipped);
        }
        return wanted;
    }

    // Where an interior's meshes go for its marker to stand at a point, turned: they stand against the marker
    // that carries them in the archive.
    private static Matrix4x4 There(Loaded interior, Vector3 at, float turnDegrees) =>
        Matrix4x4.CreateTranslation(-interior.Holder) * Matrix4x4.CreateRotationZ(turnDegrees * MathF.PI / 180f) * Matrix4x4.CreateTranslation(at);

    /// <summary>
    /// Draws what stands inside the loaded part of the map now, and takes away what no longer does: called when
    /// the layer is switched, when another scene has loaded, and after a place was added, moved or removed.
    /// An interior already drawn where it stands is left as it is.
    /// </summary>
    public void Refresh()
    {
        _settle.Stop();
        if (!_shown || _host.Rnd is not { } renderer)
        {
            Pick(null);
            HideAll();
            return;
        }
        if (_host.IsLoading)
        {
            _settle.Start();        // looked at again once the district is in
            return;
        }
        _scene = _host.Roots.FirstOrDefault();

        // A scene reset - the same area opened again, the whole map switched - clears the renderer, and these
        // meshes with it: what it no longer draws is not drawn, whatever the list here says.
        foreach (string cleared in _drawn.Where(d => d.Value.Count > 0 && !renderer.Draws(d.Value[0].Mesh)).Select(d => d.Key).ToList())
        {
            _drawn.Remove(cleared);
        }

        _standing = Wanted(out int skipped);
        var keep = _standing.Select(w => w.Key).ToHashSet();
        foreach (string gone in _drawn.Keys.Where(k => !keep.Contains(k)).ToList())
        {
            renderer.RemoveMeshes(_drawn[gone].Select(d => d.Mesh));
            _drawn.Remove(gone);
        }
        foreach (Standing place in _standing)
        {
            if (_drawn.ContainsKey(place.Key) || Read(place.Archive, HolderOf(place.Marker)) is not { } interior) continue;
            Matrix4x4 there = There(interior, place.At, place.Turn);
            var meshes = new List<(GpuMesh, Matrix4x4)>(interior.Meshes.Count);
            foreach (MeshData data in interior.Meshes)
            {
                GpuMesh mesh = renderer.CreateMeshGpu(data);
                Matrix4x4 world = data.World * there;
                mesh.SetWorld(world);
                renderer.AttachMesh(mesh);
                meshes.Add((mesh, world));
            }
            _drawn[place.Key] = meshes;
        }
        // The picked place is picked still when it is still there - by its marker, which is what it is known by.
        if (_picked != null) Pick(_standing.FirstOrDefault(s => string.Equals(s.Marker, _picked.Marker, StringComparison.OrdinalIgnoreCase)));
        ShowMarks(null);
        if (skipped > 0)
        {
            _host.RaiseNotice($"Interiors: the {MostPlaces} places nearest the camera are drawn, {skipped} more are not.", isError: false);
        }
    }

    private void HideAll()
    {
        foreach (List<(GpuMesh Mesh, Matrix4x4 World)> meshes in _drawn.Values) _host.Rnd?.RemoveMeshes(meshes.Select(d => d.Mesh));
        _drawn.Clear();
        _standing = [];
        _scene = null;
        _host.ShowToolMarks([], []);
    }

    // ── writing: every change of a place is a step of the editor's history ──

    // A place as the history knows it. The table names a place by its marker, and a place that is taken out and
    // stood again - an undone Remove - comes back under another marker: every step about one place holds the
    // same handle, so that an older step still finds the place a newer one has renamed. (A move keeps the marker.)
    private sealed class Handle
    {
        public string? Marker;      // null while the place stands nowhere (taken out, or not made again yet)
    }

    private readonly Dictionary<string, Handle> _handles = new(StringComparer.OrdinalIgnoreCase);

    private Handle HandleOf(string marker)
    {
        if (!_handles.TryGetValue(marker, out Handle? handle)) _handles[marker] = handle = new Handle { Marker = marker };
        return handle;
    }

    // Takes a place from where it stands to `to`: a place that stands and goes on standing is MOVED - marker,
    // volumes and all, as they are; one that stands nowhere is stood, and `to` null takes it out. Each is one
    // write that either happens or changes nothing. Null on success.
    private string? Put(string shop, Handle handle, (Vector3 At, float Turn)? to, out IReadOnlyList<string> archives, out IReadOnlyList<string> notes)
    {
        archives = [];
        notes = [];
        System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
        try
        {
            if (handle.Marker is not { } standing) return to is { } fresh ? Add(shop, handle, fresh, out archives, out notes) : null;
            if (to is { } there)
            {
                return _session.ShopPlaceMove(standing, [there.At.X, there.At.Y, there.At.Z], there.Turn, apply: true, out _, out archives);
            }
            if (_session.ShopPlaceDelete(standing, apply: true, out _, out archives) is { } refused) return refused;
            _handles.Remove(standing);
            handle.Marker = null;
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
        {
            return ex.Message;
        }
        finally
        {
            System.Windows.Input.Mouse.OverrideCursor = null;
        }
    }

    // A place stood again by an undone Remove gets boxes of the usual size: what its volumes were given by hand
    // before it was taken out is not kept.
    private string? Add(string shop, Handle handle, (Vector3 At, float Turn) there, out IReadOnlyList<string> archives, out IReadOnlyList<string> notes)
    {
        string? refused = _session.ShopPlaceAdd(shop, [there.At.X, there.At.Y, there.At.Z], there.Turn, LoadHalf, UnloadHalf, HalfHeight,
            apply: true, out ShopPlaceInfo? made, out archives, out notes);
        if (refused != null || made == null) return refused ?? "nothing was written";
        handle.Marker = made.Marker;
        _handles[made.Marker] = handle;
        return null;
    }

    // What follows a write: the place drawn where the table has it now, picked when it was, and whoever lists
    // the places told to read them again.
    private void Written(Handle handle, bool pick)
    {
        Standing? was = _picked;
        _picked = null;
        _host.PickedStandIn = null;
        Refresh();
        Standing? now = handle.Marker == null ? null
            : _standing.FirstOrDefault(s => string.Equals(s.Marker, handle.Marker, StringComparison.OrdinalIgnoreCase));
        Pick(pick ? now : was == null ? null : _standing.FirstOrDefault(s => string.Equals(s.Marker, was.Marker, StringComparison.OrdinalIgnoreCase)));
        PlacesChanged?.Invoke();
    }

    // One step: a place goes from where it stood to where it stands - either may be nowhere, which makes the
    // step a place stood on the map or one taken out.
    private sealed class PlaceEdit(InteriorStandIns owner, string shop, Handle handle, (Vector3 At, float Turn)? before,
        (Vector3 At, float Turn)? after) : IEditAction
    {
        public void Undo() => Go(before, "put back");

        public void Redo() => Go(after, "done again");

        private void Go((Vector3 At, float Turn)? to, string what)
        {
            string? refused = owner.Put(shop, handle, to, out _, out _);
            owner.Written(handle, pick: to != null);
            if (refused != null) throw new EditRefusedException($"{shop} was not {what}: {ToolText.ForPeople(refused)}");
        }
    }

    /// <summary>The marker of the place just stood: what <see cref="Stand"/> answers with.</summary>
    public sealed record Stood(string Marker, IReadOnlyList<string> Archives, IReadOnlyList<string> Notes);

    /// <summary>Stands an interior at one more place. One step of the history: Ctrl+Z takes it out again.</summary>
    public string? Stand(string shop, Vector3 at, float turn, out Stood? stood)
    {
        stood = null;
        var handle = new Handle();
        if (Put(shop, handle, (at, turn), out IReadOnlyList<string> archives, out IReadOnlyList<string> notes) is { } refused) return refused;
        _host.History.Push(new PlaceEdit(this, shop, handle, null, (at, turn)));
        stood = new Stood(handle.Marker!, archives, notes);
        Written(handle, pick: true);
        return null;
    }

    /// <summary>Takes a place that was added out. One step of the history: Ctrl+Z stands it again.</summary>
    public string? Remove(string shop, string marker, Vector3 at, float turn)
    {
        Handle handle = HandleOf(marker);
        if (Put(shop, handle, null, out _, out _) is { } refused) return refused;
        _host.History.Push(new PlaceEdit(this, shop, handle, (at, turn), null));
        Written(handle, pick: false);
        return null;
    }

    // ── picking ──

    private void Pick(Standing? place)
    {
        bool changed = !string.Equals(place?.Marker, _picked?.Marker, StringComparison.OrdinalIgnoreCase);
        _picked = place;
        // Only a place that was added goes under the gizmo: the game's own places are not ours to move.
        _host.PickedStandIn = place is { Added: true } ? this : null;
        ShowMarks(null);
        if (changed) PickedChanged?.Invoke();
    }

    /// <summary>Picks a place by its marker - the row picked in the Interiors window - or none.</summary>
    public void PickPlace(string? marker)
    {
        Standing? place = marker == null ? null : _standing.FirstOrDefault(s => string.Equals(s.Marker, marker, StringComparison.OrdinalIgnoreCase));
        if (place != null && _host.SelectedNodes.Count > 0) _host.Select(null);
        Pick(place);
    }

    // A click on the scene, offered here before it selects anything: on an interior, it picks it.
    private bool Clicked(Point pixel)
    {
        Standing? place = null;
        if (_shown && _host.MeshAt(pixel) is { } hit)
        {
            foreach ((string key, List<(GpuMesh Mesh, Matrix4x4 World)> meshes) in _drawn)
            {
                if (!meshes.Any(d => ReferenceEquals(d.Mesh, hit))) continue;
                place = _standing.FirstOrDefault(s => s.Key == key);
                break;
            }
        }
        if (place == null)
        {
            if (_picked != null) Pick(null);
            return false;       // the scene's own, or nothing: an ordinary click
        }
        _host.Select(null);
        Pick(place);
        if (!place.Added)
        {
            _host.RaiseNotice($"{place.Shop} stands here in the game itself: this place is not moved. Drag {place.Shop} from the "
                + "Interiors list to stand it somewhere else.", isError: false);
        }
        return true;
    }

    // ── marks ──

    private static void Line(List<Vector3> lines, List<Vector4> colours, Vector3 from, Vector3 to, Vector4 colour)
    {
        lines.Add(from);
        lines.Add(to);
        colours.Add(colour);
        colours.Add(colour);
    }

    // A place and the way it is turned: a cross on the ground, a mast, and an arrow that turns with it.
    private static void Pin(List<Vector3> lines, List<Vector4> colours, Vector3 at, float turnDegrees, Vector4 colour)
    {
        const float Arm = 1.5f, Mast = 6f, Arrow = 4f;
        Line(lines, colours, at - (Vector3.UnitX * Arm), at + (Vector3.UnitX * Arm), colour);
        Line(lines, colours, at - (Vector3.UnitY * Arm), at + (Vector3.UnitY * Arm), colour);
        Line(lines, colours, at, at + (Vector3.UnitZ * Mast), colour);
        float turn = turnDegrees * MathF.PI / 180f;
        var ahead = new Vector3(-MathF.Sin(turn), MathF.Cos(turn), 0f);
        var side = new Vector3(ahead.Y, -ahead.X, 0f);
        Vector3 top = at + (Vector3.UnitZ * Mast), tip = top + (ahead * Arrow);
        Line(lines, colours, top, tip, colour);
        Line(lines, colours, tip, tip - ahead + (side * 0.6f), colour);
        Line(lines, colours, tip, tip - ahead - (side * 0.6f), colour);
    }

    /// <summary>
    /// Marks the picked place - where its marker stands and which way it is turned - and, while an interior is
    /// being dragged from the list over the map, where it would land with the square inside which the game
    /// would load it.
    /// </summary>
    public void ShowMarks(Vector3? landing)
    {
        var lines = new List<Vector3>();
        var colours = new List<Vector4>();
        if (_picked != null)
        {
            (Vector3 at, float turn) = _dragged ?? (_picked.At, _picked.Turn);
            Pin(lines, colours, at, turn, Picked);
        }
        if (landing is { } point)
        {
            Pin(lines, colours, point, 0f, Fresh);
            Vector3 Corner(int i) => point + new Vector3(i is 0 or 3 ? -LoadHalf : LoadHalf, i < 2 ? -LoadHalf : LoadHalf, 0.3f);
            for (int i = 0; i < 4; i++) Line(lines, colours, Corner(i), Corner((i + 1) % 4), Fresh);
        }
        _host.ShowToolMarks(lines, colours);
    }

    // ── the gizmo (IGizmoStandIn): the picked place, moved and turned like an object ──

    // Between a scene reset and the next look at the map the meshes here are disposed ones: not to be touched.
    private bool StillDrawn(List<(GpuMesh Mesh, Matrix4x4 World)> meshes) =>
        meshes.Count > 0 && _host.Rnd is { } renderer && renderer.Draws(meshes[0].Mesh);

    Vector3 IGizmoStandIn.Pivot => _dragged?.At ?? _picked?.At ?? Vector3.Zero;

    void IGizmoStandIn.Begin(GizmoMode mode)
    {
        _dragMode = mode;
        _dragged = null;
    }

    // A place has a point and a turn about the vertical, and nothing else: of whatever the gizmo asks, that
    // much is taken - a tilt or a scale is not something an interior can be stood with.
    void IGizmoStandIn.Apply(Matrix4x4 totalWorldDelta)
    {
        if (_picked is not { } place || _dragMode == GizmoMode.Scale
            || !_drawn.TryGetValue(place.Key, out List<(GpuMesh Mesh, Matrix4x4 World)>? meshes) || !StillDrawn(meshes))
        {
            return;
        }
        Vector3 at = Vector3.Transform(place.At, totalWorldDelta);
        Vector3 ahead = Vector3.TransformNormal(Vector3.UnitX, totalWorldDelta);
        float yaw = MathF.Abs(ahead.X) + MathF.Abs(ahead.Y) < 1e-5f ? 0f : MathF.Atan2(ahead.Y, ahead.X);
        Matrix4x4 taken = Matrix4x4.CreateTranslation(-place.At) * Matrix4x4.CreateRotationZ(yaw) * Matrix4x4.CreateTranslation(at);
        foreach ((GpuMesh mesh, Matrix4x4 world) in meshes) mesh.SetWorld(world * taken);
        _dragged = (at, MathF.IEEERemainder(place.Turn + (yaw * 180f / MathF.PI), 360f));
        ShowMarks(null);
    }

    void IGizmoStandIn.Cancel()
    {
        if (_picked != null && _drawn.TryGetValue(_picked.Key, out List<(GpuMesh Mesh, Matrix4x4 World)>? meshes) && StillDrawn(meshes))
        {
            foreach ((GpuMesh mesh, Matrix4x4 world) in meshes) mesh.SetWorld(world);
        }
        _dragged = null;
        ShowMarks(null);
    }

    // Letting go writes it, as one step of the history: the place is moved as it is - its marker, its volumes
    // and its row keep their names and sizes.
    void IGizmoStandIn.End()
    {
        (Vector3 At, float Turn)? left = _dragged;
        _dragged = null;
        if (_picked is not { } place || left is not { } now) return;
        if (Vector3.DistanceSquared(now.At, place.At) < 1e-6f && MathF.Abs(now.Turn - place.Turn) < 1e-3f) return;

        Handle handle = HandleOf(place.Marker);
        (Vector3 At, float Turn) was = (place.At, place.Turn);
        string? refused = Put(place.Shop, handle, now, out _, out _);
        // One step of the history, like a move of any object: Ctrl+Z stands it where it was.
        if (refused == null) _host.History.Push(new PlaceEdit(this, place.Shop, handle, was, now));
        // Drawn again from what the table says now - where it was left, or where it was when that was refused.
        if (_drawn.Remove(place.Key, out List<(GpuMesh Mesh, Matrix4x4 World)>? old)) _host.Rnd?.RemoveMeshes(old.Select(d => d.Mesh));
        Written(handle, pick: true);
        if (refused != null) _host.RaiseNotice("Not moved: " + ToolText.ForPeople(refused), isError: true);
    }
}
