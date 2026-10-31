using System.Globalization;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Illusion.Assets.Adapters;
using Illusion.Formats.Actors;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Rendering.Scene;
using Illusion.Scene;
using Illusion.Viewport;

namespace Illusion.Views;

/// <summary>
/// Tools → Light editor…: the LightEntity actors of what the editor has open, tuned by eye. While it is open the
/// viewport is lit by those lights instead of the sun and draws their reach; picking a light here selects it in
/// the scene (so the gizmo moves and turns it) and picking one there selects it here. A slider let go, a sector
/// picked or a box ticked is one undo step in the editor's history (<see cref="LightEdit"/>).
/// </summary>
public sealed partial class LightEditorWindow : Window
{
    private sealed record LightItem(SceneNode Node, ActorNodeAdapter Adapter, ActorsFile Pack)
    {
        public ActorEntry Actor => Adapter.Actor;
        public override string ToString() => Actor.EntityName;
    }

    private sealed record SectorItem(string Scene, string Sector)
    {
        public override string ToString() => Sector;
    }

    private sealed class ParamRow
    {
        public required Slider Slider { get; init; }
        public required TextBox Box { get; init; }
        public required Func<double, string> Format { get; init; }
    }

    private readonly D3DImageHost _host;
    private readonly List<LightItem> _items = [];
    private readonly List<SectorItem> _sectors = [];
    private readonly DispatcherTimer _commitTimer;
    private LightItem? _current;
    private ActorsFile.LightState? _before;
    private bool _filling;

    private readonly ParamRow _red, _green, _blue, _intensity, _range, _cone, _yaw, _pitch, _ambient;

    public LightEditorWindow(D3DImageHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
        InitializeComponent();

        _red = AddRow(ParamsPanel, "Red", 0, 1, 0.01, v => v.ToString("0.00", CultureInfo.InvariantCulture), OnColorChanged);
        _green = AddRow(ParamsPanel, "Green", 0, 1, 0.01, v => v.ToString("0.00", CultureInfo.InvariantCulture), OnColorChanged);
        _blue = AddRow(ParamsPanel, "Blue", 0, 1, 0.01, v => v.ToString("0.00", CultureInfo.InvariantCulture), OnColorChanged);
        _intensity = AddRow(ParamsPanel, "Brightness", 0, 6, 0.05, v => v.ToString("0.00", CultureInfo.InvariantCulture),
            v => SetFloat(ActorsFile.LightFields.Intensity, (float)v));
        _range = AddRow(ParamsPanel, "Range, m", 0.1, 20, 0.05, v => v.ToString("0.00", CultureInfo.InvariantCulture),
            v => SetFloat(ActorsFile.LightFields.Range, (float)v));
        _cone = AddRow(ParamsPanel, "Cone, °", 1, 180, 1, v => v.ToString("0", CultureInfo.InvariantCulture),
            v => SetFloat(ActorsFile.LightFields.Cone, (float)(v * Math.PI / 180)));
        _yaw = AddRow(ParamsPanel, "Turn, °", -180, 180, 1, v => v.ToString("0", CultureInfo.InvariantCulture), _ => OnDirectionChanged());
        _pitch = AddRow(ParamsPanel, "Tilt, °", -90, 90, 1, v => v.ToString("0", CultureInfo.InvariantCulture), _ => OnDirectionChanged());
        _ambient = AddRow(GlobalPanel, "Room ambient", 0, 1, 0.01, v => v.ToString("0.00", CultureInfo.InvariantCulture), _ => UpdateViewport());
        _filling = true;
        _ambient.Slider.Value = 0.25;
        _filling = false;

        // A slider dragged is many small changes; they become one undo step when the drag ends or the value
        // has rested a moment (keyboard and wheel changes have no "end").
        _commitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _commitTimer.Tick += (_, _) => Commit();

        Action selection = () => Dispatcher.BeginInvoke(FollowViewportSelection);
        Action history = () => Dispatcher.BeginInvoke(() =>
        {
            if (_before == null) ShowCurrent();
            UpdateViewport();
        });
        Action<SceneNode> applied = _ => Dispatcher.BeginInvoke(() => { ShowCurrent(); UpdateViewport(); });
        // Which lights the preview uses depends on where the camera stands (the nearest ones, when there are more
        // than the shader takes), so a camera move re-picks them; one re-pick per burst of moves is enough.
        bool cameraQueued = false;
        Action camera = () =>
        {
            if (cameraQueued) return;
            cameraQueued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                cameraQueued = false;
                UpdatePreview();
            });
        };
        _host.CameraMoved += camera;
        _host.SelectionChanged += selection;
        _host.Editing.History.Changed += history;
        LightEdit.Applied += applied;
        Activated += (_, _) => UpdateViewport();
        Closed += (_, _) =>
        {
            Commit();
            _commitTimer.Stop();
            _host.CameraMoved -= camera;
            _host.SelectionChanged -= selection;
            _host.Editing.History.Changed -= history;
            LightEdit.Applied -= applied;
            _host.ShowLightLines([], []);
            _host.SetLightPreview([], false, 0.25f);
        };

        Reload();
        FollowViewportSelection();
    }

    // ---- the list ----

    private void Reload()
    {
        _items.Clear();
        _sectors.Clear();
        foreach (SceneNode root in _host.Roots) Collect(root);
        _items.Sort((a, b) => string.CompareOrdinal(a.Actor.EntityName, b.Actor.EntityName));
        _sectors.Sort((a, b) => string.CompareOrdinal(a.Sector, b.Sector));

        LightItem? keep = _current;
        _filling = true;
        LightList.ItemsSource = null;
        LightList.ItemsSource = _items;
        SectorCombo.ItemsSource = null;
        SectorCombo.ItemsSource = _sectors;
        _filling = false;
        if (keep != null) LightList.SelectedItem = _items.FirstOrDefault(i => i.Node == keep.Node);
        StatusText.Text = _items.Count == 0
            ? "Nothing open has lights."
            : $"{_items.Count} lights, {_sectors.Count} sectors." + (_items.Count > LocalLightPreviewMax
                ? $" The viewport is lit by the {LocalLightPreviewMax} nearest the camera." : "");
        UpdateViewport();
    }

    private const int LocalLightPreviewMax = 32;

    private void Collect(SceneNode node)
    {
        if (node.Source is ActorNodeAdapter adapter && adapter.Actor.Type == EntityType.LightEntity
            && adapter.Placements.PackOf(adapter.Actor) is { } pack && pack.IsEditableLight(adapter.Actor))
        {
            _items.Add(new LightItem(node, adapter, pack));
        }
        else if (node.Source is FrameNodeAdapter { Frame: FrameObjectSector sector })
        {
            string scene = sector.ParentIndex2?.Name ?? "";
            if (!_sectors.Any(s => s.Sector == sector.Name.String)) _sectors.Add(new SectorItem(scene, sector.Name.String));
        }
        foreach (SceneNode child in node.Children) Collect(child);
    }

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        Commit();
        Reload();
    }

    private void LightList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling) return;
        Commit();
        _current = LightList.SelectedItem as LightItem;
        ShowCurrent();
        if (_current != null && _host.SelectedNode != _current.Node) _host.Select(_current.Node);
        UpdateViewport();
    }

    private void FollowViewportSelection()
    {
        if (_host.SelectedNode is not { } node) return;
        LightItem? item = _items.FirstOrDefault(i => i.Node == node);
        if (item == null || item == _current) return;
        Commit();
        _filling = true;
        LightList.SelectedItem = item;
        LightList.ScrollIntoView(item);
        _filling = false;
        _current = item;
        ShowCurrent();
        UpdateViewport();
    }

    // ---- showing the selected light ----

    private void ShowCurrent()
    {
        _filling = true;
        try
        {
            if (_current is not { } item)
            {
                EditPanel.IsEnabled = false;
                NameText.Text = "Pick a light in the list or in the viewport";
                NoteText.Text = "";
                return;
            }
            EditPanel.IsEnabled = true;
            NameText.Text = item.Actor.EntityName;
            int sharers = item.Pack.PropertiesOf(item.Actor)?.SharerCount ?? 1;
            NoteText.Text = sharers > 1
                ? $"This light shares its settings with {sharers - 1} other light(s): a change here changes them too, and it cannot be turned or given a sector of its own."
                : "";

            Vector3 color = new(Get(item, ActorsFile.LightFields.Red), Get(item, ActorsFile.LightFields.Green), Get(item, ActorsFile.LightFields.Blue));
            Put(_red, color.X);
            Put(_green, color.Y);
            Put(_blue, color.Z);
            Put(_intensity, Get(item, ActorsFile.LightFields.Intensity));
            Put(_range, Get(item, ActorsFile.LightFields.Range));
            Put(_cone, Get(item, ActorsFile.LightFields.Cone) * 180 / Math.PI);
            Vector3 dir = Direction(item);
            Put(_pitch, Math.Asin(Math.Clamp(dir.Z, -1f, 1f)) * 180 / Math.PI);
            Put(_yaw, Math.Atan2(-dir.X, dir.Y) * 180 / Math.PI);
            ShadowsCheck.IsChecked = (item.Pack.LightField(item.Actor, ActorsFile.LightFields.Shadows)?.Number ?? 0) != 0;
            Swatch.Background = new SolidColorBrush(Color.FromRgb(ToByte(color.X), ToByte(color.Y), ToByte(color.Z)));

            IReadOnlyList<LightSectorLink> links = item.Pack.GetLightSectors(item.Actor);
            SectorCombo.SelectedItem = links.Count > 0 ? _sectors.FirstOrDefault(s => s.Sector == links[0].Sector) : null;
            SectorText.Text = links.Count switch
            {
                0 => "The light names no sector.",
                1 => _sectors.Any(s => s.Sector == links[0].Sector) ? "" : $"It names {links[0].Sector}, which is not in what is open.",
                _ => "It names " + string.Join(" and ", links.Select(l => l.Sector)) + ". Picking one here leaves only that one.",
            };
        }
        finally
        {
            _filling = false;
        }
    }

    private static byte ToByte(float v) => (byte)Math.Clamp(Math.Round(v * 255), 0, 255);

    private static float Get(LightItem item, string field) => item.Pack.LightField(item.Actor, field)?.Single ?? 0f;

    private static Vector3 Direction(LightItem item) =>
        item.Pack.LightDirection(item.Actor) ?? Vector3.Normalize(Vector3.Transform(Vector3.UnitY, item.Actor.Rotation));

    private void Put(ParamRow row, double value)
    {
        row.Slider.Value = Math.Clamp(value, row.Slider.Minimum, row.Slider.Maximum);
        row.Box.Text = row.Format(value);
    }

    // ---- editing ----

    // The light's state before the first change of a gesture; the gesture becomes one undo step on Commit.
    private bool Begin()
    {
        if (_current == null) return false;
        _before ??= _current.Pack.CaptureLight(_current.Actor);
        _commitTimer.Stop();
        _commitTimer.Start();
        return _before != null;
    }

    private void Commit()
    {
        _commitTimer.Stop();
        if (_current is not { } item || _before == null) return;
        ActorsFile.LightState before = _before;
        _before = null;
        if (item.Pack.CaptureLight(item.Actor) is not { } after || after.SameAs(before)) return;
        LightEdit.Touched(_host, item.Node, item.Adapter);
        _host.Editing.History.Push(new LightEdit(_host, item.Node, item.Adapter, item.Pack, before, after));
    }

    private void SetFloat(string field, float value)
    {
        if (!Begin() || _current!.Pack.LightField(_current.Actor, field) is not { } f) return;
        f.Single = value;
        AfterChange();
    }

    private void OnColorChanged(double _)
    {
        if (!Begin()) return;
        LightItem item = _current!;
        float r = (float)_red.Slider.Value, g = (float)_green.Slider.Value, b = (float)_blue.Slider.Value;
        if (item.Pack.LightField(item.Actor, ActorsFile.LightFields.Red) is { } fr) fr.Single = r;
        if (item.Pack.LightField(item.Actor, ActorsFile.LightFields.Green) is { } fg) fg.Single = g;
        if (item.Pack.LightField(item.Actor, ActorsFile.LightFields.Blue) is { } fb) fb.Single = b;
        Swatch.Background = new SolidColorBrush(Color.FromRgb(ToByte(r), ToByte(g), ToByte(b)));
        AfterChange();
    }

    private void OnDirectionChanged()
    {
        if (!Begin()) return;
        LightItem item = _current!;
        // Tilt about X first (local +Y up toward +Z), then turn about the vertical: yaw 0 shines along +Y.
        var rotation = Quaternion.Concatenate(
            Quaternion.CreateFromAxisAngle(Vector3.UnitX, (float)(_pitch.Slider.Value * Math.PI / 180)),
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (float)(_yaw.Slider.Value * Math.PI / 180)));
        if (!item.Pack.SetLightRotation(item.Actor, rotation))
        {
            StatusText.Text = "This light shares its settings with another and cannot be turned on its own.";
            return;
        }
        AfterChange();
    }

    private void Shadows_Click(object sender, RoutedEventArgs e)
    {
        if (_filling || !Begin()) return;
        if (_current!.Pack.LightField(_current.Actor, ActorsFile.LightFields.Shadows) is { } f) f.Number = ShadowsCheck.IsChecked == true ? 1 : 0;
        AfterChange();
        Commit();
    }

    private void Sector_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_filling || SectorCombo.SelectedItem is not SectorItem sector || !Begin()) return;
        LightItem item = _current!;
        IReadOnlyList<LightSectorLink> links = item.Pack.GetLightSectors(item.Actor);
        if (links.Count == 1 && links[0].Sector == sector.Sector) return;
        string scene = sector.Scene.Length > 0 ? sector.Scene : links.FirstOrDefault().Scene ?? "";
        if (!item.Pack.SetLightSectors(item.Actor, [new LightSectorLink(scene, sector.Sector)]))
        {
            StatusText.Text = "This light shares its settings with another and cannot be given a sector of its own.";
            return;
        }
        AfterChange();
        Commit();
        ShowCurrent();
    }

    // After any change: the box follows the reach when asked, and the viewport shows the result at once.
    private void AfterChange()
    {
        if (_current is { } item && AutoBoundsCheck.IsChecked == true) FitBounds(item);
        UpdateViewport();
    }

    private static void FitBounds(LightItem item)
    {
        ActorsFile pack = item.Pack;
        ActorEntry actor = item.Actor;
        if (pack.LightField(actor, ActorsFile.LightFields.BoundsMin) is not { } min
            || pack.LightField(actor, ActorsFile.LightFields.BoundsMax) is not { } max) return;
        float range = Get(item, ActorsFile.LightFields.Range);
        float cone = Get(item, ActorsFile.LightFields.Cone);
        Vector3 dir = Direction(item);
        Quaternion toDir = RotationTo(dir);
        (Vector3 lo, Vector3 hi) = ActorsFile.LightReach(actor.Position, toDir, range, cone);
        min.Vector = lo;
        max.Vector = hi;
    }

    // A rotation taking +Y to the given direction (the reach box needs only the direction).
    private static Quaternion RotationTo(Vector3 dir)
    {
        Vector3 from = Vector3.UnitY;
        float d = Vector3.Dot(from, dir);
        if (d > 0.9999f) return Quaternion.Identity;
        if (d < -0.9999f) return Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI);
        Vector3 axis = Vector3.Normalize(Vector3.Cross(from, dir));
        return Quaternion.CreateFromAxisAngle(axis, MathF.Acos(d));
    }

    // ---- the viewport ----

    private void UpdateViewport()
    {
        UpdatePreview();
        UpdateLines();
    }

    private void UpdatePreview()
    {
        bool preview = PreviewCheck.IsChecked == true;
        Vector3 eye = _host.CameraPosition;
        var lights = new List<(float Distance, LocalLight Light)>();
        foreach (LightItem item in _items)
        {
            float intensity = Get(item, ActorsFile.LightFields.Intensity);
            var color = new Vector3(Get(item, ActorsFile.LightFields.Red), Get(item, ActorsFile.LightFields.Green), Get(item, ActorsFile.LightFields.Blue));
            var light = new LocalLight(item.Actor.Position, Get(item, ActorsFile.LightFields.Range), Direction(item),
                Get(item, ActorsFile.LightFields.Cone), color * intensity);
            lights.Add((Vector3.Distance(eye, item.Actor.Position), light));
        }
        lights.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        _host.SetLightPreview([.. lights.Take(LocalLightPreviewMax).Select(l => l.Light)], preview, (float)_ambient.Slider.Value);
    }

    private void UpdateLines()
    {
        var verts = new List<Vector3>();
        var colors = new List<Vector4>();
        int volumes = VolumesCombo.SelectedIndex;
        foreach (LightItem item in _items)
        {
            bool selected = item == _current;
            if (volumes == 2 || (volumes == 1 && !selected)) continue;
            var c = new Vector3(Get(item, ActorsFile.LightFields.Red), Get(item, ActorsFile.LightFields.Green), Get(item, ActorsFile.LightFields.Blue));
            if (c.LengthSquared() < 1e-4f) c = Vector3.One;
            c /= Math.Max(c.X, Math.Max(c.Y, c.Z));
            var color = new Vector4(c, selected ? 1f : 0.45f);
            AddVolume(verts, colors, item.Actor.Position, Direction(item), Get(item, ActorsFile.LightFields.Range),
                Get(item, ActorsFile.LightFields.Cone), color);
        }
        _host.ShowLightLines(verts, colors);
    }

    private static void AddVolume(List<Vector3> verts, List<Vector4> colors, Vector3 at, Vector3 dir, float range, float cone, Vector4 color)
    {
        const int Segments = 32;
        void Line(Vector3 a, Vector3 b)
        {
            verts.Add(a);
            verts.Add(b);
            colors.Add(color);
            colors.Add(color);
        }
        void Ring(Vector3 centre, Vector3 u, Vector3 v, float radius)
        {
            for (int i = 0; i < Segments; i++)
            {
                float a0 = i * MathF.Tau / Segments, a1 = (i + 1) * MathF.Tau / Segments;
                Line(centre + (u * MathF.Cos(a0) + v * MathF.Sin(a0)) * radius, centre + (u * MathF.Cos(a1) + v * MathF.Sin(a1)) * radius);
            }
        }
        range = Math.Max(range, 0.05f);
        Vector3 side = MathF.Abs(dir.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX;
        Vector3 u = Vector3.Normalize(Vector3.Cross(dir, side));
        Vector3 v = Vector3.Cross(dir, u);
        if (cone > 0f && cone < MathF.PI)
        {
            float half = cone / 2f;
            Vector3 rim = at + dir * (range * MathF.Cos(half));
            float radius = range * MathF.Sin(half);
            Ring(rim, u, v, radius);
            for (int k = 0; k < 4; k++)
            {
                float a = k * MathF.Tau / 4;
                Line(at, rim + (u * MathF.Cos(a) + v * MathF.Sin(a)) * radius);
            }
            Line(at, at + dir * range);
        }
        else
        {
            Ring(at, Vector3.UnitX, Vector3.UnitY, range);
            Ring(at, Vector3.UnitX, Vector3.UnitZ, range);
            Ring(at, Vector3.UnitY, Vector3.UnitZ, range);
            Line(at, at + dir * range * 0.5f);
        }
    }

    private void Preview_Click(object sender, RoutedEventArgs e) => UpdateViewport();

    private void Volumes_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) UpdateViewport();
    }

    private void Select_Click(object sender, RoutedEventArgs e)
    {
        if (_current != null) _host.Select(_current.Node);
    }

    private void Frame_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        _host.Select(_current.Node);
        _host.FrameSelection();
    }

    // ---- a slider row: label, slider and a box to type the value in ----

    private ParamRow AddRow(Panel panel, string label, double min, double max, double step, Func<double, string> format, Action<double> changed)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        text.SetResourceReference(StyleProperty, "DimLabel");
        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            SmallChange = step,
            LargeChange = step * 10,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 6, 0),
        };
        var box = new TextBox { VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(3, 1, 3, 1) };
        Grid.SetColumn(slider, 1);
        Grid.SetColumn(box, 2);
        grid.Children.Add(text);
        grid.Children.Add(slider);
        grid.Children.Add(box);
        panel.Children.Add(grid);

        slider.ValueChanged += (_, e) =>
        {
            box.Text = format(e.NewValue);
            if (!_filling) changed(e.NewValue);
        };
        // Letting go of the thumb ends the gesture.
        slider.PreviewMouseLeftButtonUp += (_, _) => Dispatcher.BeginInvoke(Commit);
        void Typed()
        {
            if (double.TryParse(box.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
            {
                // A typed value may lie past the slider's travel; the light takes it as typed.
                bool past = v < min || v > max;
                slider.Value = Math.Clamp(v, min, max);
                if (past && !_filling) changed(v);
                Commit();
                box.Text = format(v);
                return;
            }
            box.Text = format(slider.Value);
        }
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Typed(); };
        box.LostKeyboardFocus += (_, _) => Typed();
        return new ParamRow { Slider = slider, Box = box, Format = format };
    }
}
