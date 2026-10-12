using System.Globalization;
using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Illusion.Mcp;
using Illusion.Viewport;

namespace Illusion.Views;

/// <summary>
/// Tools → Interiors…: the interiors of <c>shops\</c>, placed the way a prop is - dragged from the list onto
/// the map, standing where they are dropped. The jobs are the ones behind <c>shop_places</c>,
/// <c>shop_place_add</c>, <c>shop_place_delete</c>, <c>shop_create</c> and <c>shop_delete</c>, run through the
/// same session so they refuse the same things.
/// </summary>
public sealed partial class InteriorsWindow : Window
{
    /// <summary>An interior as the list shows it.</summary>
    public sealed record ShopRow(ShopInfo Shop, string Name, string CountText);

    /// <summary>A place of the picked interior as the list shows it.</summary>
    public sealed record PlaceRow(ShopPlaceInfo Place, string Marker, string AtText, string NoteText);

    private const string DragFormat = "Illusion.Interior";

    private const float LoadHalf = InteriorStandIns.LoadHalf, UnloadHalf = InteriorStandIns.UnloadHalf, HalfHeight = InteriorStandIns.HalfHeight;

    private readonly D3DImageHost _host;
    private readonly AppEditorSession _session = new();
    private readonly InteriorStandIns _standIns;
    private List<ShopRow> _all = [];
    private string? _keepMarker;
    private ShopRow? _pressed;
    private Point _pressedAt;
    // Where the interior being dragged would land, while it is over the map.
    private Vector3? _dragPoint;

    /// <param name="layer">The map's Interiors layer: what draws an interior where it stands.</param>
    /// <param name="showLayer">Switches that layer on - somebody placing interiors wants to see them.</param>
    internal InteriorsWindow(D3DImageHost host, InteriorStandIns layer, Action showLayer)
    {
        ArgumentNullException.ThrowIfNull(host);
        InitializeComponent();
        _host = host;
        _standIns = layer;
        showLayer();
        // The layer and the list show one pick: an interior clicked on the map is the row picked here, and a
        // place the gizmo moved is read again.
        Action picked = FollowMap;
        Action moved = () => Load(PickedShop?.Name, _standIns.PickedPlace?.Marker);
        _standIns.PickedChanged += picked;
        _standIns.PlacesChanged += moved;
        Closed += (_, _) =>
        {
            _standIns.PickedChanged -= picked;
            _standIns.PlacesChanged -= moved;
        };
        // The map takes the drop. The editor's own handler turns away what is not a prop and marks the event
        // handled, so these are asked for handled events too.
        var over = new DragEventHandler(Map_DragOver);
        var leave = new DragEventHandler(Map_DragLeave);
        var drop = new DragEventHandler(Map_Drop);
        _host.AddHandler(DragDrop.DragOverEvent, over, handledEventsToo: true);
        _host.AddHandler(DragDrop.DragLeaveEvent, leave, handledEventsToo: true);
        _host.AddHandler(DragDrop.DropEvent, drop, handledEventsToo: true);
        Closed += (_, _) =>
        {
            _host.RemoveHandler(DragDrop.DragOverEvent, over);
            _host.RemoveHandler(DragDrop.DragLeaveEvent, leave);
            _host.RemoveHandler(DragDrop.DropEvent, drop);
            _standIns.ShowMarks(null);
        };
        Load(null, null);
    }

    private ShopRow? PickedShop => ShopList.SelectedItem as ShopRow;

    private PlaceRow? PickedPlace => PlaceList.SelectedItem as PlaceRow;

    private static string Number(float value) => value.ToString("0.#", CultureInfo.InvariantCulture);

    // The name typed for a copy of its own: null when none is typed, "" when what is typed cannot be one.
    private string? NewName
    {
        get
        {
            string name = NewNameBox.Text.Trim();
            if (name.Length == 0) return null;
            return name.Length is >= 3 and <= 31 && char.IsAsciiLetterLower(name[0])
                && name.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_') ? name : "";
        }
    }

    // ── reading ──

    // Reads the table. `keepShop` and `keepMarker` pick the same interior and place again afterwards.
    private void Load(string? keepShop, string? keepMarker)
    {
        (string? refused, IReadOnlyList<ShopInfo> shops) = Busy(() => (_session.ShopPlaces(null, out IReadOnlyList<ShopInfo> read), read));
        _all = [.. shops.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Select(s => new ShopRow(s, s.Name, s.Archive == null ? "no archive" : Dlc(s.Archive) is { } dlc ? $"{Short(dlc)} · {Count(s)}" : Count(s)))];
        if (refused != null) StatusText.Text = ToolText.ForPeople(refused);
        ShowShops(keepShop, keepMarker);
    }

    private static string Count(ShopInfo shop) => shop.Places.Count == 1 ? "1 place" : $"{shop.Places.Count} places";

    // The DLC an interior's archive is in, when it is not the base game's.
    private static string? Dlc(string archive) => Assets.World.ShopPlaces.DlcOf(Assets.World.ShopPlaces.ArchiveFile(archive));

    // "cnt_joes_adventures" as people call it.
    private static string Short(string dlc) => dlc.StartsWith("cnt_", StringComparison.OrdinalIgnoreCase) ? dlc[4..].Replace('_', ' ') : dlc;

    private void ShowShops(string? keepShop, string? keepMarker)
    {
        string filter = FilterBox.Text.Trim();
        List<ShopRow> rows = [.. _all.Where(r => filter.Length == 0 || r.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))];
        _keepMarker = keepMarker;
        ShopList.ItemsSource = rows;
        ShopList.SelectedItem = keepShop == null ? null : rows.FirstOrDefault(r => string.Equals(r.Name, keepShop, StringComparison.OrdinalIgnoreCase));
        if (ShopList.SelectedItem is { } picked) ShopList.ScrollIntoView(picked);
        else ShowPlaces(null);
    }

    private void Filter_Changed(object sender, TextChangedEventArgs e)
    {
        if (IsInitialized) ShowShops(PickedShop?.Name, PickedPlace?.Marker);
    }

    private void ShopList_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowPlaces(PickedShop);

    // The places of one interior. Its archive is read for them - that is where a marker's point and turn are.
    private void ShowPlaces(ShopRow? row)
    {
        List<PlaceRow> rows = [];
        string? refused = null;
        if (row != null)
        {
            (refused, IReadOnlyList<ShopInfo> read) = Busy(() => (_session.ShopPlaces(row.Name, out IReadOnlyList<ShopInfo> shops), shops));
            ShopInfo shop = read.FirstOrDefault(s => string.Equals(s.Name, row.Name, StringComparison.OrdinalIgnoreCase)) ?? row.Shop;
            rows = [.. shop.Places.Select(p => new PlaceRow(p, p.Marker,
                p.At is { Length: 3 } at ? $"{Number(at[0])}, {Number(at[1])}, {Number(at[2])}" : "", p.Added ? "yours" : ""))];
        }
        PlaceList.ItemsSource = rows;
        PlacesLabel.Text = row == null ? "Where it stands" : $"Where {row.Name} stands";
        PlaceEmpty.Text = row == null ? "Pick an interior to see where it stands."
            : refused != null ? ToolText.ForPeople(refused)
            : row.Shop.Archive == null ? "The table names it, but shops\\ has no archive of it."
            : "It stands nowhere yet.";
        PlaceEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PlaceList.SelectedItem = _keepMarker == null ? null : rows.FirstOrDefault(r => string.Equals(r.Marker, _keepMarker, StringComparison.OrdinalIgnoreCase));
        _keepMarker = null;
        // An interior that was made here stands at added places only; one of the game's cannot be deleted.
        DeleteShopBtn.IsEnabled = rows.Count > 0 && rows.All(r => r.Place.Added);
        PlaceChoiceChanged();
    }

    private void PlaceList_SelectionChanged(object sender, SelectionChangedEventArgs e) => PlaceChoiceChanged();

    private void PlaceChoiceChanged()
    {
        PlaceRow? place = PickedPlace;
        bool known = place?.Place.At is { Length: 3 };
        GoBtn.IsEnabled = known;
        RemovePlaceBtn.IsEnabled = place is { Place.Added: true };
        if (!_following) _standIns.PickPlace(place?.Marker);
        if (place is { Place.Added: true }) StatusText.Text = "Move and turn it on the map with the gizmo of the tool shelf, like any object. Ctrl+Z takes a move back.";
    }

    private bool _following;

    // An interior was clicked on the map (or the click went elsewhere): the same row here.
    private void FollowMap()
    {
        if (_following) return;
        _following = true;
        try
        {
            if (_standIns.PickedPlace is not var (shop, marker))
            {
                PlaceList.SelectedItem = null;
                return;
            }
            if (!string.Equals(PickedShop?.Name, shop, StringComparison.OrdinalIgnoreCase))
            {
                FilterBox.Text = "";
                ShowShops(shop, marker);
            }
            else
            {
                PlaceList.SelectedItem = (PlaceList.ItemsSource as IEnumerable<PlaceRow>)?
                    .FirstOrDefault(r => string.Equals(r.Marker, marker, StringComparison.OrdinalIgnoreCase));
            }
            if (PlaceList.SelectedItem is { } row) PlaceList.ScrollIntoView(row);
        }
        finally
        {
            _following = false;
        }
    }

    private void PlaceList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (GoBtn.IsEnabled) Go_Click(sender, e);
    }

    private void Go_Click(object sender, RoutedEventArgs e)
    {
        if (PickedPlace?.Place.At is not { Length: 3 } at) return;
        _host.LookAt(new Vector3(at[0], at[1], at[2]), 45f, null);
    }

    private void Name_Changed(object sender, TextChangedEventArgs e)
    {
        if (!IsInitialized) return;
        StatusText.Text = NewName switch
        {
            null => "",
            "" => "A name is 3 to 31 lower-case letters, digits and '_', starting with a letter.",
            { } name => $"The next drop makes {name}: a copy of the interior you drag, as an archive of its own.",
        };
    }

    // ── the view ──

    // ── dragging an interior onto the map ──

    private void ShopList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _pressed = (e.OriginalSource as FrameworkElement)?.DataContext as ShopRow;
        _pressedAt = e.GetPosition(this);
        // The table names interiors of DLCs that are not installed: there is nothing of them to stand anywhere.
        if (_pressed is { Shop.Archive: null })
        {
            StatusText.Text = $"{_pressed.Name} cannot be placed: this installation has no archive of it - neither pc\\sds\\shops nor an "
                + "installed DLC. The game's table only names it.";
            _pressed = null;
        }
    }

    // A drag starts once the press has travelled the system's drag distance - a click stays a click.
    private void ShopList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressed == null || e.LeftButton != MouseButtonState.Pressed) return;
        System.Windows.Vector moved = e.GetPosition(this) - _pressedAt;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }
        ShopRow dragged = _pressed;
        _pressed = null;
        ShopList.SelectedItem = dragged;
        DragDrop.DoDragDrop(ShopList, new DataObject(DragFormat, dragged.Name), DragDropEffects.Copy);
        _dragPoint = null;
        _standIns.ShowMarks(null);
    }

    private void Map_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DragFormat)) return;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
        // Only over something: an interior stands on the ground or in a building, not in the sky.
        _dragPoint = _host.PickSceneryPoint(e.GetPosition(_host));
        _standIns.ShowMarks(_dragPoint);
    }

    private void Map_DragLeave(object sender, DragEventArgs e)
    {
        if (_dragPoint == null) return;
        _dragPoint = null;
        _standIns.ShowMarks(null);
    }

    private void Map_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DragFormat) is not string name) return;
        e.Handled = true;
        Vector3? under = _host.PickSceneryPoint(e.GetPosition(_host));
        _dragPoint = null;
        if (under is not { } ground)
        {
            _standIns.ShowMarks(null);
            Say("Not placed: there is nothing under the cursor there. Drop it on the ground or on a building.", isError: true);
            return;
        }
        // The point given is where the interior's marker goes, and its rooms hang below that: raised by as
        // much, the interior stands ON the spot it was dropped at instead of sunk into it.
        ShopRow? row = _all.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
        string? marker = row?.Shop.Places.FirstOrDefault()?.Marker;
        float depth = row?.Shop.Archive is { } archive && marker != null
            ? Busy(() => _standIns.DepthUnderMarker(archive, InteriorStandIns.HolderOf(marker)))
            : 0f;
        Stand(name, ground + (Vector3.UnitZ * depth), 0f);
    }

    // The drop: one more place for the interior - or, with a name typed, a new interior like it standing there.
    private void Stand(string shop, Vector3 at, float turn)
    {
        string? copy = NewName;
        if (copy == "")
        {
            Say("Not placed: the name for the copy is not one the game can take.", isError: true);
            return;
        }
        IReadOnlyList<string> archives = [];
        string? marker = null;
        string? refused;
        // Asked before the drop is written: afterwards the interior is the base game's too.
        string? dlc = _all.FirstOrDefault(r => string.Equals(r.Name, shop, StringComparison.OrdinalIgnoreCase))?.Shop.Archive is { } inArchive
            ? Dlc(inArchive)
            : null;
        if (copy == null)
        {
            // One more place for the interior: a step of the history, so Ctrl+Z takes it out again.
            refused = _standIns.Stand(shop, at, turn, out InteriorStandIns.Stood? stood);
            marker = stood?.Marker;
            archives = stood?.Archives ?? [];
        }
        else
        {
            // A new interior is an archive made on disk: not something Ctrl+Z unmakes. Delete interior does.
            ShopPlaceInfo? made = null;
            refused = Guarded(() => _session.ShopCreate(copy, shop, [at.X, at.Y, at.Z], turn, LoadHalf, UnloadHalf, HalfHeight,
                apply: true, out made, out archives, out _));
            marker = made?.Marker;
            if (refused == null) Busy(() => { _standIns.Refresh(); return 0; });
        }
        if (refused != null || marker == null)
        {
            _standIns.ShowMarks(null);
            Say("Not placed: " + ToolText.ForPeople(refused ?? "nothing was written"), isError: true);
            return;
        }
        if (copy != null) NewNameBox.Text = "";
        Load(copy ?? shop, marker);
        Say($"{copy ?? shop} stands here now. Move and turn it with the gizmo of the tool shelf; Ctrl+Z takes "
            + (copy == null ? "it" : "a move") + $" back. Build packs {Files(archives)}."
            + (dlc == null || copy != null ? "" : $" It is {Short(dlc)}'s interior: its archive was copied into pc\\sds\\shops so the base game "
                + "has it, and its materials are that DLC's - Materials of an archive saves them as a library for a server."), isError: false);
    }

    private static string Files(IReadOnlyList<string> archives) =>
        archives.Count == 0 ? "it" : string.Join(", ", archives.Select(a => Path.GetFileName(a)));

    // Said twice: in the window, and over the map where the eyes are after a drop.
    private void Say(string text, bool isError)
    {
        StatusText.Text = text;
        _host.RaiseNotice(text, isError);
    }

    private static T Busy<T>(Func<T> job)
    {
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            return job();
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    // A job that writes: what it throws on a file it cannot take is its refusal, not the end of the program.
    private static string? Guarded(Func<string?> job)
    {
        try
        {
            return Busy(job);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
        {
            return ex.Message;
        }
    }

    // ── the picked place ──

    // No question asked: it takes out what a drop put in, and a drop puts it back.
    private void RemovePlace_Click(object sender, RoutedEventArgs e)
    {
        if (PickedShop is not { } shop || PickedPlace is not { Place.At: { Length: 3 } at } row) return;
        if (_standIns.Remove(shop.Name, row.Marker, new Vector3(at[0], at[1], at[2]), row.Place.Turn ?? 0f) is { } refused)
        {
            Say("Not removed: " + ToolText.ForPeople(refused), isError: true);
            return;
        }
        Load(shop.Name, null);
        StatusText.Text = $"{row.Marker} is removed. Ctrl+Z stands it again.";
    }

    private void DeleteShop_Click(object sender, RoutedEventArgs e)
    {
        if (PickedShop is not { } shop) return;
        bool sure = AppDialog.Show(this, new DialogOptions
        {
            Title = Title,
            Icon = DialogIcon.Question,
            Heading = $"Delete the interior {shop.Name}?",
            Text = "Its row and its places are taken out of the table. Its archive under shops\\ and its working copy stay where they are.",
            Buttons = DialogButtons.YesCancel,
            ConfirmText = "Delete",
        }).Confirmed;
        if (!sure) return;
        if (Guarded(() => _session.ShopDelete(shop.Name, apply: true, out _, out _)) is { } refused)
        {
            Say("Not deleted: " + ToolText.ForPeople(refused), isError: true);
            return;
        }
        Load(null, null);
        _standIns.Refresh();
        StatusText.Text = $"{shop.Name} is out of the table.";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
