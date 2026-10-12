using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Illusion.Assets;
using Illusion.Assets.Materials;
using Illusion.Mcp;

namespace Illusion.Views;

/// <summary>
/// Tools → Car workshop…: the car on the resource editor's stage, page by page - its materials and what the
/// game does to them, its lights, its winter twin, and a check of what only the game would show. The jobs are
/// the ones behind the car tools of the MCP server (<c>car_materials</c>, <c>car_material_like</c>,
/// <c>material_variant</c>, <c>car_lights</c>, <c>car_light_set</c>, <c>car_beacon</c>, <c>car_winter</c>,
/// <c>car_check</c>…), run through the same <see cref="ICarWorkshop"/>, so they refuse the same things. They
/// work on the SAVED car: a job that writes asks to save the stage first, and the stage is loaded again from
/// disk when it is done.
/// </summary>
public sealed partial class CarWorkshopWindow : Window
{
    /// <summary>A material of the car as the list shows it.</summary>
    public sealed record MaterialRow(string Name, string GameText, string NoteText, bool OnTheCar, bool Listed, bool Added,
        ImageSource? Thumb, bool Paint, bool Dirt, bool Dents, bool Interior)
    {
        /// <summary>The game does nothing to it on this car.</summary>
        public bool Plain => !Listed;
    }

    /// <summary>A light of the car as the list shows it.</summary>
    public sealed record LightRow(CarLightInfo Light, string Bone, string KindText, string Side, string ModelText, string CheckText);

    // What a light model of cars_universal is called in the lists. Internal: the light window offers the same.
    internal static readonly (string Model, string Title)[] LightModels =
    [
        ("car_headlight_glow", "Headlight beam"),
        ("car_brake_glow", "Brake glow"),
        ("car_indicator", "Indicator"),
        ("car_back", "Back light"),
        ("car_beacon", "Turning beacon"),
    ];

    // The car a car was made from, as it was last picked here - so it is asked once a session, not on every page.
    private static readonly Dictionary<string, string> References = new(StringComparer.OrdinalIgnoreCase);

    private readonly ResourceEditorWindow _editor;
    private readonly AppCarWorkshop _workshop = new();
    private readonly CarStageMarks _marks;
    private readonly Func<Point, bool> _clicks;
    // A material's picture, by name: rendered once, the list is rebuilt after every job.
    private readonly Dictionary<string, ImageSource?> _thumbs = new(StringComparer.OrdinalIgnoreCase);
    // The window asking where a new bone or a beacon stands, while it is open: a click on the car answers it.
    private CarLightWindow? _placeWindow;
    // The bone a light window has in its list right now: marked on the car while the window is open.
    private string? _previewBone;
    private string _car = "";
    private string _name = "";
    private CarLightsInfo? _lights;

    /// <param name="editor">The resource editor whose stage holds the car.</param>
    /// <param name="car">The car's summer archive.</param>
    public CarWorkshopWindow(ResourceEditorWindow editor, string car)
    {
        InitializeComponent();
        _editor = editor;
        _marks = new CarStageMarks(editor.TargetStage);
        // The car in the view is part of the window: a click on it is offered here first.
        _clicks = Clicked;
        editor.TargetStage.TakeSceneClicks(_clicks);
        Action sceneChanged = () => Dispatcher.BeginInvoke(() =>
        {
            // Another car came onto the stage: a place asked for the one before is not a place on this one.
            if (_placeWindow != null && CarOnStage(_editor) is { } now && !string.Equals(now, _car, StringComparison.OrdinalIgnoreCase))
            {
                _placeWindow.Close();
            }
            Mark();
        });
        editor.TargetStage.SceneChanged += sceneChanged;
        Closed += (_, _) =>
        {
            editor.TargetStage.SceneChanged -= sceneChanged;
            editor.TargetStage.ReleaseSceneClicks(_clicks);
            _placeWindow?.Close();
            _viewKept?.Stop();
            _marks.Dispose();
        };
        List<string> shipped = ShippedPairs();
        WinterReference.ItemsSource = shipped;
        CheckReference.ItemsSource = shipped;
        Open(car);
        Activated += (_, _) =>
        {
            editor.TargetStage.TakeSceneClicks(_clicks);        // another tool window may have asked since
            FollowStage();
        };
    }

    /// <summary>The summer archive of the car on a resource editor's stage, or null when what it holds is not a
    /// car. A winter twin on the stage answers with its summer car: the twin is made from it, not edited.</summary>
    public static string? CarOnStage(ResourceEditorWindow editor)
    {
        if (editor.StagedEntry?.File is not { } file) return null;
        if (!string.Equals(file.Directory?.Name, "cars", StringComparison.OrdinalIgnoreCase)) return null;
        string name = Path.GetFileNameWithoutExtension(file.Name);
        if (name.EndsWith("_z", StringComparison.OrdinalIgnoreCase))
        {
            string summer = Path.Combine(file.DirectoryName!, name[..^2] + ".sds");
            return File.Exists(summer) ? summer : null;
        }
        return file.FullName;
    }

    // The cars the game has in both seasons: what a winter twin can be read off.
    private static List<string> ShippedPairs()
    {
        if (!MafiaEnvironment.IsInitialized) return [];
        string folder = Path.Combine(MafiaEnvironment.PcFolder, "sds", "cars");
        if (!Directory.Exists(folder)) return [];
        var names = Directory.EnumerateFiles(folder, "*.sds").Select(f => Path.GetFileNameWithoutExtension(f)!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. names.Where(n => !n.EndsWith("_z", StringComparison.OrdinalIgnoreCase) && names.Contains(n + "_z"))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)];
    }

    private void Open(string car)
    {
        _car = car;
        _name = Path.GetFileNameWithoutExtension(car);
        Title = "Car Workshop - " + _name;
        string reference = References.GetValueOrDefault(_name, "");
        WinterReference.Text = reference;
        CheckReference.Text = reference;
        WinterText.Text = "Nothing made yet.";
        CheckHeadline.Text = "Not checked yet.";
        CheckBody.Text = "";
        Reload();
    }

    // The window belongs to the car on the stage: another car staged while it is open is the car it shows from
    // then on, and with no car on the stage it has nothing to show.
    private void FollowStage()
    {
        string? staged = CarOnStage(_editor);
        if (staged == null)
        {
            Close();
            return;
        }
        if (string.Equals(staged, _car, StringComparison.OrdinalIgnoreCase)) return;
        _placeWindow?.Close();      // it was asking for a place on the car before
        Open(staged);
    }

    // The place window stays open while the stage is worked with, and this window hears of another car only
    // when it comes forward: what was asked for one car is not written into the car it no longer shows.
    private bool StillOnStage()
    {
        if (string.Equals(CarOnStage(_editor), _car, StringComparison.OrdinalIgnoreCase)) return true;
        NotDone($"The place was asked for {_name}, and another car is on the stage now. Nothing was written.");
        Dispatcher.BeginInvoke(() =>
        {
            _placeWindow?.Close();
            FollowStage();
        });
        return false;
    }

    private void Page_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        PaintPage.Visibility = PaintTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        LightsPage.Visibility = LightsTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        WinterPage.Visibility = WinterTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        CheckPage.Visibility = CheckTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        Mark();
    }

    // ── the car in the view ──

    // What the page in front is about, drawn on the car: the picked material's triangles, the lights' bones, or
    // the place a bone or a beacon is being given.
    private void Mark()
    {
        if (!IsLoaded && !IsInitialized) return;
        if (_placeWindow != null)
        {
            if (_placeWindow.Place is { } place) _marks.ShowPlace(place, (float)_placeWindow.Reach);
            else _marks.Clear();
        }
        else if (PaintTab.IsChecked == true && PickedMaterial is { } material)
        {
            _marks.ShowMaterial(material.Name);
        }
        else if (LightsTab.IsChecked == true && _lights != null)
        {
            _marks.ShowLights(_lights.Lights.Select(l => l.Bone), _previewBone ?? PickedLight?.Bone);
        }
        else
        {
            _marks.Clear();
        }
    }

    // A click on the scene, offered here before it selects anything. True when it was the window's own.
    private bool Clicked(Point pixel)
    {
        if (_placeWindow != null)
        {
            if (_marks.PointAt(pixel) is not { } at) return false;
            _placeWindow.SetPlace(at);
            return true;
        }
        if (PaintTab.IsChecked == true)
        {
            if (_marks.MaterialAt(pixel) is not { } name) return false;
            MaterialRow? row = (MaterialList.ItemsSource as IEnumerable<MaterialRow>)?
                .FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
            if (row == null) return false;
            MaterialList.SelectedItem = row;
            MaterialList.ScrollIntoView(row);
            return true;
        }
        if (LightsTab.IsChecked == true && _lights != null)
        {
            if (_marks.LightAt(pixel, _lights.Lights.Select(l => l.Bone)) is not { } bone) return false;
            LightRow? row = (LightList.ItemsSource as IEnumerable<LightRow>)?
                .FirstOrDefault(r => string.Equals(r.Bone, bone, StringComparison.OrdinalIgnoreCase));
            if (row == null) return false;
            LightList.SelectedItem = row;
            LightList.ScrollIntoView(row);
            return true;
        }
        return false;
    }

    private ImageSource? Thumb(string material)
    {
        if (_thumbs.TryGetValue(material, out ImageSource? known)) return known;
        ImageSource? made = null;
        if (MafiaMaterials.FindHashByName(material) is { } hash && MafiaMaterialCatalog.Instance.GetMaterial(hash) is { } info)
        {
            made = _editor.TargetStage.RenderMaterialThumbnail(info);
        }
        return _thumbs[material] = made;
    }

    // ── what every job shares ──

    // Reads both lists from the saved car. With unsaved edits on the stage the lists are still shown - they are
    // what a job would work on - and the pages say so.
    private void Reload(string? pickMaterial = null, string? pickLight = null)
    {
        string? stale = _workshop.StageUnsaved(_car) == null ? null : "The stage has unsaved edits: this is the car as it was last saved. ";

        string? noMaterials = Run(() => _workshop.CarMaterials(_car, out CarMaterialsInfo? read) is { } refused ? (refused, null) : (null, read),
            out CarMaterialsInfo? materials);
        ShowMaterials(materials, noMaterials, pickMaterial);
        if (stale != null && noMaterials == null && MaterialList.SelectedItem == null) MaterialText.Text = stale + "Pick a material.";

        string? noLights = Run(() => _workshop.CarLights(_car, out CarLightsInfo? read) is { } refused ? (refused, null) : (null, read),
            out CarLightsInfo? lights);
        ShowLights(lights, noLights, pickLight);
        if (stale != null && noLights == null && LightList.SelectedItem == null) LightText.Text = stale;
    }

    // One job under the wait cursor; a failure the job did not foresee is its refusal, not the end of the program.
    private static string? Run<T>(Func<(string? Refused, T? Result)> job, out T? result)
        where T : class
    {
        result = null;
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            (string? refused, T? made) = job();
            result = made;
            return refused;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException
            or NotSupportedException or FormatException)
        {
            return ex.Message;
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    /// <summary>
    /// Whether a job that writes the car's working copy may run now. The jobs work on the saved car, and the
    /// stage is loaded again from disk afterwards: unsaved edits would be lost under it, so they are saved
    /// first - asked, not assumed.
    /// </summary>
    internal bool MayWrite()
    {
        if (_editor.TargetStage.BridgeEditedCount > 0)
        {
            NotDone("The car is in a Blender edit. Send what you changed and leave the edit first (Tab).");
            return false;
        }
        if (_workshop.StageBusy(_car) == null) return true;
        DialogOutcome save = AppDialog.Show(this, new DialogOptions
        {
            Title = Title,
            Icon = DialogIcon.Question,
            Heading = "Save the car first?",
            Text = "This works on the saved car, and the stage is loaded again from disk when it is done. "
                + "The stage has edits that are not saved yet.",
            Buttons = DialogButtons.YesCancel,
            ConfirmText = "Save",
        });
        if (!save.Confirmed) return false;
        _editor.SaveForTool();
        if (_workshop.StageBusy(_car) is not { } still) return true;
        NotDone(ToolText.ForPeople(still));
        return false;
    }

    /// <summary>
    /// What follows a write: the archive is on the build list and the stage shows the car on disk. Loading the
    /// stage again frames the car afresh; the view is put back where it was, so that a job does not throw away
    /// the angle the car was being looked at from.
    /// </summary>
    internal void Landed()
    {
        Illusion.Rendering.Controls.CameraPose pose = _editor.TargetStage.CameraPose;
        if (!_workshop.Landed(_car)) return;
        _viewKept?.Stop();
        DateTime started = DateTime.UtcNow;
        bool seenLoading = false;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        timer.Tick += (_, _) =>
        {
            bool loading = _editor.TargetStage.IsLoading;
            seenLoading |= loading;
            double waited = (DateTime.UtcNow - started).TotalSeconds;
            // Once the load that was started has ended - or, when it was over before the first look, a moment later.
            if (loading && waited < 30) return;
            if (!seenLoading && waited < 1) return;
            timer.Stop();
            _editor.TargetStage.CameraPose = pose;
            Mark();
        };
        _viewKept = timer;
        timer.Start();
    }

    private System.Windows.Threading.DispatcherTimer? _viewKept;

    internal void NotDone(string why) => AppDialog.Show(this, new DialogOptions
    {
        Title = Title,
        Icon = DialogIcon.Warning,
        Heading = "Not done",
        Text = ToolText.ForPeople(why),
    });

    private bool Sure(string heading, string text, string confirm) => AppDialog.Show(this, new DialogOptions
    {
        Title = Title,
        Icon = DialogIcon.Question,
        Heading = heading,
        Text = text,
        Buttons = DialogButtons.YesCancel,
        ConfirmText = confirm,
    }).Confirmed;

    // ── Paint and materials ──

    private void ShowMaterials(CarMaterialsInfo? info, string? refused, string? pick)
    {
        var rows = new List<MaterialRow>();
        if (info != null)
        {
            var seen = new Dictionary<string, (int Flags, bool Deform, bool OnTheCar)>(StringComparer.OrdinalIgnoreCase);
            foreach (CarMaterialRowInfo row in info.Rows)
            {
                (int flags, bool deform, bool on) = seen.GetValueOrDefault(row.Material);
                seen[row.Material] = (flags | row.Flags, deform, on || row.OnTheCar);
            }
            foreach (CarDeformRowInfo row in info.Deform)
            {
                (int flags, _, bool on) = seen.GetValueOrDefault(row.Material);
                seen[row.Material] = (flags, true, on || row.OnTheCar);
            }
            foreach (string unlisted in info.Unlisted) seen.TryAdd(unlisted, (0, false, true));

            foreach ((string name, (int flags, bool deform, bool on)) in seen)
            {
                var does = new List<string>();
                if ((flags & 1) != 0) does.Add("paints it in the car's colour");
                if ((flags & 2) != 0) does.Add("dirties and burns it");
                if ((flags & 4) != 0) does.Add("lights it as the interior");
                if (deform) does.Add("deforms it");
                bool listed = does.Count > 0 || flags != 0;
                bool added = MafiaMaterials.FindHashByName(name) is { } hash && MafiaMaterialCatalog.Instance.IsAdded(hash);
                // A winter material is not on the summer car by its nature: the winter twin of the car names it.
                bool winter = name.EndsWith(Assets.Cars.CarWinter.WinterSuffix, StringComparison.OrdinalIgnoreCase);
                string note = (added, on) switch
                {
                    (true, true) => "yours",
                    (true, false) => winter ? "yours, for winter" : "yours, not on the car",
                    (false, false) => winter ? "for winter" : "not on the car",
                    _ => "",
                };
                rows.Add(new MaterialRow(name, does.Count > 0 ? string.Join(", ", does) : "draws it as it is", note, on, listed, added,
                    Thumb(name), (flags & 1) != 0, (flags & 2) != 0, deform, (flags & 4) != 0));
            }
        }
        rows = [.. rows.OrderByDescending(r => r.OnTheCar).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)];
        MaterialList.ItemsSource = rows;
        MaterialEmpty.Text = refused != null ? ToolText.ForPeople(refused) : "The car names no material.";
        MaterialEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        MaterialList.SelectedItem = pick == null ? null : rows.FirstOrDefault(r => string.Equals(r.Name, pick, StringComparison.OrdinalIgnoreCase));
        if (MaterialList.SelectedItem is { } picked) MaterialList.ScrollIntoView(picked);
        MaterialChoiceChanged();
    }

    private MaterialRow? PickedMaterial => MaterialList.SelectedItem as MaterialRow;

    private void MaterialList_SelectionChanged(object sender, SelectionChangedEventArgs e) => MaterialChoiceChanged();

    private void MaterialChoiceChanged()
    {
        MaterialRow? row = PickedMaterial;
        var rows = (MaterialList.ItemsSource as IEnumerable<MaterialRow>)?.ToList() ?? [];
        string? likeWas = LikeCombo.SelectedItem as string;
        List<string> likes = row == null ? [] : [.. rows.Where(r => r.Listed && r != row).Select(r => r.Name)];
        LikeCombo.ItemsSource = likes;
        LikeCombo.SelectedItem = likes.FirstOrDefault(l => l == likeWas);
        LikeCombo.IsEnabled = likes.Count > 0;
        LikeBtn.IsEnabled = LikeCombo.SelectedItem != null;
        // A name the libraries do not have ("0x…") cannot be copied: there is nothing to copy.
        OwnCopyBtn.IsEnabled = row is { OnTheCar: true } && MafiaMaterials.FindHashByName(row.Name) != null;
        ForgetBtn.IsEnabled = row is { Listed: true };
        DeleteMaterialBtn.IsEnabled = row is { Added: true, OnTheCar: false };

        Mark();
        MaterialText.Text = row switch
        {
            null => "Pick a material, or click a part of the car in the view.",
            { Listed: false } => $"The game draws {row.Name} as it is: not painted in the car's colour, no dirt, and it does not deform with "
                + "the body. Right for an engine bay or a sheet of markings. A copy of the paint, the chrome or the glass should be "
                + "treated like the one it was copied from.",
            { OnTheCar: false } => $"No mesh of the car is drawn with {row.Name}. On this car the game {row.GameText}; that does no harm, "
                + "and Stop takes it out.",
            _ => $"On this car the game {row.GameText}.",
        };
    }

    private void LikeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        LikeBtn.IsEnabled = PickedMaterial != null && LikeCombo.SelectedItem != null;

    private void OwnCopy_Click(object sender, RoutedEventArgs e)
    {
        if (PickedMaterial is not { } row || !MayWrite()) return;
        var window = new CarMaterialWindow(this, _workshop, _car, _name, row.Name, row.Listed) { Owner = this };
        if (window.ShowDialog() != true) return;
        Reload(pickMaterial: window.Made);
        if (window.Outcome is { } outcome) MaterialText.Text = outcome;
    }

    private void Like_Click(object sender, RoutedEventArgs e)
    {
        if (PickedMaterial is not { } row || LikeCombo.SelectedItem is not string like || !MayWrite()) return;
        int changed = 0;
        string? refused = Run(() => _workshop.CarMaterialLike(_car, row.Name, like, out changed, out CarMaterialsInfo? read) is { } no
            ? (no, null) : (null, read), out CarMaterialsInfo? result);
        if (refused != null)
        {
            NotDone(refused);
            return;
        }
        if (changed > 0) Landed();
        ShowMaterials(result, null, row.Name);
        MaterialText.Text = changed > 0
            ? $"{row.Name} is treated like {like} now. The winter twin shares this: make it again when you are done."
            : $"{row.Name} was already treated like {like}.";
    }

    private void Forget_Click(object sender, RoutedEventArgs e)
    {
        if (PickedMaterial is not { } row) return;
        if (!Sure($"Stop treating {row.Name}?", $"On this car the game {row.GameText}. After this it draws {row.Name} as it is: "
            + "one colour whatever the car is painted, no dirt, no dents.", "Stop") || !MayWrite())
        {
            return;
        }
        int changed = 0;
        string? refused = Run(() => _workshop.CarMaterialLike(_car, row.Name, null, out changed, out CarMaterialsInfo? read) is { } no
            ? (no, null) : (null, read), out CarMaterialsInfo? result);
        if (refused != null)
        {
            NotDone(refused);
            return;
        }
        if (changed > 0) Landed();
        ShowMaterials(result, null, row.Name);
    }

    private void DeleteMaterial_Click(object sender, RoutedEventArgs e)
    {
        if (PickedMaterial is not { } row) return;
        if (!Sure($"Delete {row.Name}?", "The material is taken out of the library. Every model drawn with it - in this archive or "
            + "another - would be drawn without a material. Its textures stay in the archive.", "Delete") || !MayWrite())
        {
            return;
        }
        // Its rows first: a row naming a material that is gone is a row nobody can read any more.
        if (row.Listed)
        {
            int changed = 0;
            string? notDropped = Run(() => _workshop.CarMaterialLike(_car, row.Name, null, out changed, out CarMaterialsInfo? read) is { } no
                ? (no, null) : (null, read), out CarMaterialsInfo? _);
            if (notDropped != null)
            {
                NotDone(notDropped);
                return;
            }
            if (changed > 0) Landed();
        }
        string? refused = Run(() => (_workshop.MaterialDelete(row.Name, out string? library), library), out string? _);
        if (refused != null) NotDone(refused);
        Reload();
    }

    // ── Lights ──

    private static string ModelTitle(string model) =>
        LightModels.FirstOrDefault(m => string.Equals(m.Model, model, StringComparison.OrdinalIgnoreCase)).Title ?? model;

    private void ShowLights(CarLightsInfo? info, string? refused, string? pick)
    {
        _lights = info;
        List<LightRow> rows = info == null ? [] : [.. info.Lights.Select(l => new LightRow(l, l.Bone,
            l.KindName.Length > 0 ? char.ToUpperInvariant(l.KindName[0]) + l.KindName[1..] : l.Kind.ToString(CultureInfo.InvariantCulture),
            l.Side, l.Model.Length == 0 ? "nothing" : ModelTitle(l.Model), l.CheckBones.Count == 0 ? "-" : string.Join(", ", l.CheckBones)))];
        LightList.ItemsSource = rows;
        LightEmpty.Text = refused != null ? ToolText.ForPeople(refused) : "The car has no lights.";
        LightEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LightList.SelectedItem = pick == null ? null : rows.FirstOrDefault(r => string.Equals(r.Bone, pick, StringComparison.OrdinalIgnoreCase));
        if (LightList.SelectedItem is { } picked) LightList.ScrollIntoView(picked);
        bool readable = info != null;
        AddLightBtn.IsEnabled = readable;
        BoneBtn.IsEnabled = readable;
        BeaconBtn.IsEnabled = readable;
        LightChoiceChanged();
    }

    private LightRow? PickedLight => LightList.SelectedItem as LightRow;

    private void LightList_SelectionChanged(object sender, SelectionChangedEventArgs e) => LightChoiceChanged();

    private void LightChoiceChanged()
    {
        LightRow? row = PickedLight;
        Mark();
        ChangeLightBtn.IsEnabled = row != null;
        RemoveLightBtn.IsEnabled = row != null;
        LightText.Text = row == null
            ? _lights == null ? "" : $"{_lights.Lights.Count} light(s) on a rig of {_lights.Bones.Count} bones."
            : $"The piece of the body on '{row.Bone}' glows {row.Light.Power.ToString("0.##", CultureInfo.InvariantCulture)} strong and "
                + (row.Light.Model.Length == 0 ? "throws no light of its own." : $"shines as: {row.ModelText.ToLowerInvariant()}.")
                + (row.Light.CheckBones.Count == 0 ? "" : $" It goes dark when {string.Join(", ", row.Light.CheckBones)} is wrecked.");
    }

    private void LightList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (PickedLight != null) ChangeLight_Click(sender, e);
    }

    private void AddLight_Click(object sender, RoutedEventArgs e) => EditLight(null);

    private void ChangeLight_Click(object sender, RoutedEventArgs e)
    {
        if (PickedLight is { } row) EditLight(row.Light);
    }

    private void EditLight(CarLightInfo? light)
    {
        if (_lights == null) return;
        var window = new CarLightWindow(light == null ? CarLightJob.Add : CarLightJob.Change, _lights, light) { Owner = this };
        window.BoneChosen = bone =>
        {
            _previewBone = bone;
            Mark();
        };
        bool asked = window.ShowDialog() == true;
        _previewBone = null;
        Mark();
        if (!asked || !MayWrite()) return;
        bool added = false;
        string? refused = Run(() => _workshop.CarLightSet(_car, window.Bone, window.Kind, window.Side, window.Model, window.CheckBones,
            window.Power, out added, out CarLightsInfo? read) is { } no ? (no, null) : (null, read), out CarLightsInfo? result);
        if (refused != null)
        {
            NotDone(refused);
            return;
        }
        Landed();
        ShowLights(result, null, window.Bone);
        LightText.Text = (added ? $"'{window.Bone}' has a light now. " : $"The light of '{window.Bone}' is changed. ")
            + "The winter twin shares the lights: make it again when you are done.";
    }

    private void RemoveLight_Click(object sender, RoutedEventArgs e)
    {
        if (PickedLight is not { } row) return;
        if (!Sure($"Remove the light of '{row.Bone}'?", "The piece of the body on that bone stops glowing and shines no light. "
            + "The bone and the mesh stay.", "Remove") || !MayWrite())
        {
            return;
        }
        string? refused = Run(() => _workshop.CarLightRemove(_car, row.Bone, out CarLightsInfo? read) is { } no ? (no, null) : (null, read),
            out CarLightsInfo? result);
        if (refused != null)
        {
            NotDone(refused);
            return;
        }
        Landed();
        ShowLights(result, null, null);
    }

    private void Bone_Click(object sender, RoutedEventArgs e) => AskPlace(CarLightJob.Bone);

    private void Beacon_Click(object sender, RoutedEventArgs e) => AskPlace(CarLightJob.Beacon);

    // A place on the car is asked beside the view, not over it: the window stays open while the car is turned
    // and clicked, shows the place it has as a mark, and runs the job when it is answered.
    private void AskPlace(CarLightJob job)
    {
        if (_lights == null) return;
        if (_placeWindow != null)
        {
            _placeWindow.Activate();
            return;
        }
        var window = new CarLightWindow(job, _lights, null, job == CarLightJob.Beacon ? _workshop.BodyTop(_car) : null) { Owner = this };
        window.PlaceChanged += Mark;
        window.Accept = job == CarLightJob.Bone ? AddBone : AddBeacon;
        window.Closed += (_, _) =>
        {
            _placeWindow = null;
            Mark();
        };
        _placeWindow = window;
        window.Show();
        Mark();
    }

    private bool AddBone(CarLightWindow window)
    {
        if (!StillOnStage() || !MayWrite()) return false;
        string? refused = Run(() => _workshop.CarBoneAdd(_car, window.Bone, window.Part, window.At.X, window.At.Y, window.At.Z, window.Reach,
            out CarBoneInfo? made) is { } no ? (no, null) : (null, made), out CarBoneInfo? bone);
        if (refused != null)
        {
            NotDone(refused);
            return false;
        }
        Landed();
        Reload();
        LightText.Text = $"'{bone!.Name}' is bone {bone.Index} of {bone.Bones}, under '{bone.Parent}'. Put the lamp's mesh on it in Blender, "
            + "then Add light for it.";
        return true;
    }

    private bool AddBeacon(CarLightWindow window)
    {
        if (!StillOnStage() || !MayWrite()) return false;
        string? refused = Run(() => _workshop.CarBeacon(_car, window.At.X, window.At.Y, window.At.Z, window.Part, window.Reach,
            out CarBeaconInfo? made) is { } no ? (no, null) : (null, made), out CarBeaconInfo? beacon);
        if (refused != null)
        {
            NotDone(refused);
            return false;
        }
        Landed();
        Reload(pickLight: beacon!.Casing);
        LightText.Text = (beacon.BonesAdded ? $"Two bones were added ('{beacon.Casing}' and '{beacon.Lamp}')" : "The car had the two bones")
            + (beacon.LightAdded ? " and the beacon light is on the list." : " and the beacon light was already on the list.")
            + $" Put the lamp's glass on '{beacon.Casing}' in Blender, and what should turn inside it on '{beacon.Lamp}'.";
        return true;
    }

    // ── Winter twin ──

    private void Reference_Changed(object sender, RoutedEventArgs e)
    {
        if (IsInitialized) WinterBtn.IsEnabled = WinterReference.Text.Trim().Length > 0;
    }

    private void Winter_Click(object sender, RoutedEventArgs e)
    {
        string reference = WinterReference.Text.Trim();
        if (reference.Length == 0 || !MayWrite()) return;
        string twin = Path.Combine(Path.GetDirectoryName(_car)!, _name + "_z.sds");
        if (_workshop.StageBusy(twin) is { } busy)
        {
            NotDone(busy);
            return;
        }
        string? refused = Run(() => _workshop.CarWinter(_car, reference, out CarWinterInfo? made) is { } no ? (no, null) : (null, made),
            out CarWinterInfo? winter);
        if (refused != null)
        {
            NotDone(refused);
            return;
        }
        _workshop.Landed(twin);
        References[_name] = reference;
        CheckReference.Text = reference;

        var lines = new List<string> { $"{winter!.Winter} is made again from {_name}, read off {reference}." };
        if (winter.Materials.Count == 0) lines.Add("No material changes for winter.");
        foreach (CarWinterSwap swap in winter.Materials) lines.Add($"• {swap.Summer}  →  {swap.Winter}   ({swap.Slots} slot(s))");
        if (winter.TexturesAdded.Count > 0) lines.Add("Textures added: " + string.Join(", ", winter.TexturesAdded));
        if (winter.TexturesRemoved.Count > 0) lines.Add("Textures left out: " + string.Join(", ", winter.TexturesRemoved));
        if (winter.TexturesCarried.Count > 0) lines.Add("Kept from the old twin: " + string.Join(", ", winter.TexturesCarried));
        if (winter.EffectsReplaced) lines.Add("The effects file is the winter one.");
        lines.Add("");
        lines.Add("Build packs both archives.");
        WinterText.Text = string.Join("\n", lines);
    }

    // ── Check ──

    private void Check_Click(object sender, RoutedEventArgs e)
    {
        if (_workshop.StageUnsaved(_car) != null && !MayWrite()) return;
        string reference = CheckReference.Text.Trim();
        string? refused = Run(() => _workshop.CarCheck(_car, reference.Length == 0 ? null : reference,
            new Dictionary<string, string>(), out CarCheckInfo? read) is { } no ? (no, null) : (null, read), out CarCheckInfo? check);
        if (refused != null)
        {
            NotDone(refused);
            return;
        }
        if (reference.Length > 0) References[_name] = reference;

        CheckHeadline.Text = check!.Clean ? "Nothing wrong was found." : $"{check.Problems.Count} thing(s) the game would show";
        var lines = new List<string>();
        foreach (string problem in check.Problems) lines.Add("• " + problem);
        if (lines.Count > 0) lines.Add("");
        foreach (CarCheckLevel level in check.Levels)
        {
            lines.Add($"Level of detail {level.Lod}: {level.Vertices} vertices, {level.Triangles} triangles"
                + (level.Skinned ? "" : ", not skinned"));
        }
        if (check.ComparedAs.Count > 0)
        {
            lines.Add("");
            lines.Add("Compared as: " + string.Join("; ", check.ComparedAs));
        }
        if (check.NotCompared.Count > 0)
        {
            lines.Add("");
            lines.Add("Not compared (no counterpart on the other car): " + string.Join(", ", check.NotCompared));
        }
        if (reference.Length == 0)
        {
            lines.Add("");
            lines.Add("Without a car to compare with, the hidden channels of new panels were not looked at.");
        }
        CheckBody.Text = string.Join("\n", lines);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
