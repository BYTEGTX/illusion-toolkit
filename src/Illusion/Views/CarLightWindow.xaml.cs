using System.Globalization;
using System.Numerics;
using System.Windows;
using Illusion.Assets.Cars;
using Illusion.Mcp;

namespace Illusion.Views;

/// <summary>What a <see cref="CarLightWindow"/> is opened to ask for.</summary>
public enum CarLightJob
{
    /// <summary>A light on a bone that has none.</summary>
    Add,
    /// <summary>Another kind, side, look or glow for a light the car has.</summary>
    Change,
    /// <summary>A new bone of the body's rig.</summary>
    Bone,
    /// <summary>The police cars' roof beacon.</summary>
    Beacon,
}

/// <summary>
/// The questions behind Add light… / Change… / Add bone… / Add roof beacon… of the car workshop. The window
/// runs nothing: it closes with the answers, in the words <see cref="ICarWorkshop"/> takes them in, and the
/// workshop window does the job.
/// </summary>
public sealed partial class CarLightWindow : Window
{
    private sealed record Choice(string Title, string? Value);

    // What stands for "no part at all" and "as it is" in the list of parts.
    private const string NoPart = "(nothing - it never goes dark)";

    private readonly CarLightJob _job;
    private readonly CarLightInfo? _light;
    private readonly string _hint;
    private readonly string? _keepParts;
    // A light is asked over the workshop window and answered at once; a place is asked beside the view, which
    // stays live so the car can be turned and clicked.
    private readonly bool _modal;

    /// <summary>Told the bone picked in the list, each time it changes: the workshop marks it on the car.</summary>
    public Action<string?>? BoneChosen { get; set; }

    /// <summary>Raised when the place or the reach typed changes: the workshop moves its mark.</summary>
    public event Action? PlaceChanged;

    /// <summary>For a place (a bone, a beacon): the job run with the answers. True closes the window; false -
    /// refused, said by the job itself - leaves it open with what was typed.</summary>
    public Func<CarLightWindow, bool>? Accept { get; set; }
    // The game's cars write a brake light as one of three kinds. A new one is written as this car writes most
    // of its own; with none to go by, as the tool names "brake".
    private readonly string _brakeKind = "brake";

    /// <param name="lights">The car's lights and the bones of its rig.</param>
    /// <param name="light">The light to change; null for the other jobs.</param>
    /// <param name="top">For a beacon: the top of the body, where the lamp is proposed to stand.</param>
    public CarLightWindow(CarLightJob job, CarLightsInfo lights, CarLightInfo? light, Vector3? top = null)
    {
        InitializeComponent();
        _job = job;
        _light = light;
        _modal = job is CarLightJob.Add or CarLightJob.Change;

        bool aboutLight = job is CarLightJob.Add or CarLightJob.Change;
        LightRows.Visibility = aboutLight ? Visibility.Visible : Visibility.Collapsed;
        PlaceRows.Visibility = aboutLight ? Visibility.Collapsed : Visibility.Visible;
        NameRows.Visibility = job == CarLightJob.Bone ? Visibility.Visible : Visibility.Collapsed;
        List<string> bones = [.. lights.Bones.Where(b => b.Length > 0)];

        if (aboutLight)
        {
            if (lights.Lights.Where(l => l.KindName == "brake").GroupBy(l => l.Kind).OrderByDescending(g => g.Count()).FirstOrDefault() is { } usual)
            {
                _brakeKind = usual.Key.ToString(CultureInfo.InvariantCulture);
            }
            var lit = lights.Lights.Select(l => l.Bone).ToHashSet(StringComparer.OrdinalIgnoreCase);
            BoneCombo.ItemsSource = job == CarLightJob.Add ? [.. bones.Where(b => !lit.Contains(b))] : bones;
            KindCombo.ItemsSource = new List<Choice>
            {
                new("Headlight", "headlight"),
                new("Indicator", "indicator"),
                new("Back light", "back"),
                new("Brake light", "brake"),
                new("Beacon", "beacon"),
                new("Taxi sign", "taxi sign"),
            };
            SideCombo.ItemsSource = new List<string> { "left", "right" };
            // No model named: a new light gets the one its kind has, a changed light keeps its own - whatever
            // its kind is changed to.
            var models = new List<Choice> { new(light == null ? "As its kind does" : "As it is", null) };
            models.AddRange(CarWorkshopWindow.LightModels.Select(m => new Choice(m.Title, m.Model)));
            ModelCombo.ItemsSource = models;
            var parts = new List<string> { NoPart };
            if (light is { CheckBones.Count: > 1 })
            {
                _keepParts = $"(as it is: {string.Join(", ", light.CheckBones)})";
                parts.Insert(0, _keepParts);
            }
            parts.AddRange(bones);
            CheckCombo.ItemsSource = parts;

            if (light == null)
            {
                KindCombo.SelectedIndex = 0;
                SideCombo.SelectedIndex = 0;
                ModelCombo.SelectedIndex = 0;
                CheckCombo.SelectedIndex = 0;
            }
            else
            {
                BoneCombo.SelectedItem = bones.FirstOrDefault(b => string.Equals(b, light.Bone, StringComparison.OrdinalIgnoreCase));
                BoneCombo.IsEnabled = false;
                var kinds = (List<Choice>)KindCombo.ItemsSource;
                KindCombo.SelectedItem = kinds.FirstOrDefault(k => string.Equals(k.Value, light.KindName, StringComparison.OrdinalIgnoreCase));
                SideCombo.SelectedItem = light.Side is "left" or "right" ? light.Side : null;
                ModelCombo.SelectedItem = models.FirstOrDefault(m => string.Equals(m.Value, light.Model, StringComparison.OrdinalIgnoreCase))
                    ?? models[0];
                PowerBox.Text = light.Power.ToString("0.###", CultureInfo.InvariantCulture);
                CheckCombo.SelectedItem = light.CheckBones.Count switch
                {
                    0 => NoPart,
                    1 => parts.FirstOrDefault(p => string.Equals(p, light.CheckBones[0], StringComparison.OrdinalIgnoreCase)) ?? NoPart,
                    _ => _keepParts,
                };
            }
        }
        else
        {
            ParentCombo.ItemsSource = bones;
            if (job == CarLightJob.Bone)
            {
                ParentLabel.Text = "Hangs on";
                ParentCombo.ToolTip = "The bone it moves with: the body's for a lamp on the shell, a door's for one on a door";
                ParentCombo.SelectedItem = bones.FirstOrDefault(b => string.Equals(b, lights.ScaleBone, StringComparison.OrdinalIgnoreCase))
                    ?? bones.FirstOrDefault();
                ReachBox.Text = "0.1";
            }
            else
            {
                ParentLabel.Text = "Goes dark when this part is wrecked";
                ParentCombo.ToolTip = "The part the beacon sits on: the roof's";
                ParentCombo.SelectedItem = bones.FirstOrDefault(b => b.Contains("top", StringComparison.OrdinalIgnoreCase))
                    ?? bones.FirstOrDefault(b => b.Contains("roof", StringComparison.OrdinalIgnoreCase))
                    ?? bones.FirstOrDefault(b => b.StartsWith("deform", StringComparison.OrdinalIgnoreCase));
                ReachBox.Text = "0.12";
                if (top is { } at)
                {
                    XBox.Text = Number(at.X);
                    YBox.Text = Number(at.Y);
                    ZBox.Text = Number(at.Z);
                }
            }
        }

        switch (job)
        {
            case CarLightJob.Add:
                Title = "Add Light";
                IntroText.Text = "Makes the piece of the body on a bone a lamp: it glows when its kind is switched on and shines "
                    + "from where the bone stands.";
                HintText.Text = "Bones that already have a light are not offered: pick the light in the list and Change it.";
                RunBtn.Content = "Add";
                break;
            case CarLightJob.Change:
                Title = "Change Light";
                IntroText.Text = $"The light of '{light?.Bone}'. Only what you change is written; the rest of the entry stays as it is.";
                HintText.Text = "";
                RunBtn.Content = "Change";
                break;
            case CarLightJob.Bone:
                Title = "Add Bone";
                IntroText.Text = "A new bone of the body's rig, standing at a place with no turn of its own: what a lamp in a new "
                    + "place needs to shine from there. The lamp's mesh is then put on the bone in Blender. "
                    + "Click the car in the view where it should stand, or type the place.";
                HintText.Text = "Every table that names bones by number is renumbered with it.";
                RunBtn.Content = "Add";
                break;
            default:
                Title = "Add Roof Beacon";
                IntroText.Text = "The turning light the police cars have: a bone for the lamp's housing, one inside it for what "
                    + "turns, and a light of the beacon kind. The lamp's glass is then put on the housing bone in Blender. "
                    + "Click the car in the view where it should stand, or type the place."
                    + (top == null ? "" : " It starts at the top of the body.");
                HintText.Text = "A car that has a beacon somewhere else is refused: this does not move one.";
                RunBtn.Content = "Add";
                break;
        }
        _hint = HintText.Text;
        Validate();
    }

    private static string Number(float value) => Math.Round(value, 2).ToString("0.##", CultureInfo.InvariantCulture);

    // A number as people type it: a comma for the point is a point.
    private static bool Parse(string text, out double value) =>
        double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    /// <summary>The bone the light is on; for <see cref="CarLightJob.Bone"/>, the new bone's name.</summary>
    public string Bone => _job == CarLightJob.Bone ? NameBox.Text.Trim() : BoneCombo.SelectedItem as string ?? "";

    /// <summary>The kind, as the tools name it; null to keep the entry's - which a kind left as it was does,
    /// so a brake light stays the kind of brake light it is.</summary>
    public string? Kind => (KindCombo.SelectedItem as Choice)?.Value switch
    {
        null => null,
        string kind when string.Equals(kind, _light?.KindName, StringComparison.OrdinalIgnoreCase) => null,
        "brake" => _brakeKind,
        string kind => kind,
    };

    /// <summary>"left" or "right"; null to keep the entry's.</summary>
    public string? Side => SideCombo.SelectedItem as string;

    /// <summary>The light model; null for the one its kind has (a new light) or the one it has (a changed one).</summary>
    public string? Model => (ModelCombo.SelectedItem as Choice)?.Value;

    /// <summary>The parts it goes dark without: empty keeps what the entry has, the single "-" takes them off.</summary>
    public IReadOnlyList<string> CheckBones => CheckCombo.SelectedItem switch
    {
        string part when part == _keepParts => [],
        NoPart or null => _light is { CheckBones.Count: > 0 } ? [CarLights.NoCheckBones] : [],
        string part => [part],
        _ => [],
    };

    /// <summary>How strongly it glows; null for what its kind is written with.</summary>
    public double? Power => Parse(PowerBox.Text, out double power) ? power : null;

    /// <summary>A new bone's parent; for a beacon, the part it goes dark without.</summary>
    public string Part => ParentCombo.SelectedItem as string ?? "";

    /// <summary>Where a new bone or the beacon stands, in the model's space.</summary>
    public (double X, double Y, double Z) At =>
        (Parse(XBox.Text, out double x) ? x : 0, Parse(YBox.Text, out double y) ? y : 0, Parse(ZBox.Text, out double z) ? z : 0);

    /// <summary>The same place once all three numbers are typed; null before that.</summary>
    public Vector3? Place => !_modal && Parse(XBox.Text, out double x) && Parse(YBox.Text, out double y) && Parse(ZBox.Text, out double z)
        ? new Vector3((float)x, (float)y, (float)z)
        : null;

    /// <summary>Puts the place at a point of the model's space - a click on the car.</summary>
    public void SetPlace(Vector3 at)
    {
        XBox.Text = Math.Round(at.X, 3).ToString("0.###", CultureInfo.InvariantCulture);
        YBox.Text = Math.Round(at.Y, 3).ToString("0.###", CultureInfo.InvariantCulture);
        ZBox.Text = Math.Round(at.Z, 3).ToString("0.###", CultureInfo.InvariantCulture);
    }

    /// <summary>How far what hangs on the bone reaches from it.</summary>
    public double Reach => Parse(ReachBox.Text, out double reach) ? reach : 0.1;

    private void Field_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        Validate();
        if (ReferenceEquals(sender, BoneCombo)) BoneChosen?.Invoke(BoneCombo.SelectedItem as string);
        if (!_modal) PlaceChanged?.Invoke();
    }

    // The button is live only for answers the job would not refuse on sight; what is wrong with a typed
    // answer is said in the footer, where the hint otherwise stands.
    private void Validate()
    {
        bool complete;
        string? wrong = null;
        if (_job is CarLightJob.Add or CarLightJob.Change)
        {
            complete = BoneCombo.SelectedItem != null && (KindCombo.SelectedItem != null || _job == CarLightJob.Change);
            string power = PowerBox.Text.Trim();
            if (power.Length > 0 && (!Parse(power, out double asked) || asked < 0 || asked > CarLights.MaxPower))
            {
                wrong = $"Glow strength is a number from 0 to {CarLights.MaxPower.ToString(CultureInfo.InvariantCulture)}.";
            }
            if (_job == CarLightJob.Add && BoneCombo.Items.Count == 0) wrong = "Every bone of the rig already has a light.";
        }
        else
        {
            complete = ParentCombo.SelectedItem != null && XBox.Text.Trim().Length > 0 && YBox.Text.Trim().Length > 0
                && ZBox.Text.Trim().Length > 0 && ReachBox.Text.Trim().Length > 0;
            if (_job == CarLightJob.Bone)
            {
                string name = NameBox.Text.Trim();
                complete &= name.Length > 0;
                if (ParentCombo.Items.Cast<string>().Any(b => string.Equals(b, name, StringComparison.OrdinalIgnoreCase)))
                {
                    wrong = "The rig already has a bone of that name.";
                }
            }
            foreach (string typed in new[] { XBox.Text, YBox.Text, ZBox.Text })
            {
                if (typed.Trim().Length > 0 && !Parse(typed, out _)) wrong = "The place is three numbers, in metres.";
            }
            if (ReachBox.Text.Trim().Length > 0 && (!Parse(ReachBox.Text, out double reach) || reach <= 0 || reach > 5))
            {
                wrong = "The reach is a number of metres above 0, such as 0.1.";
            }
        }
        RunBtn.IsEnabled = complete && wrong == null;
        HintText.Text = wrong ?? _hint;
    }

    private void Run_Click(object sender, RoutedEventArgs e)
    {
        Validate();
        if (!RunBtn.IsEnabled) return;
        if (_modal) DialogResult = true;
        else if (Accept?.Invoke(this) == true) Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_modal) DialogResult = false;
        else Close();
    }
}
