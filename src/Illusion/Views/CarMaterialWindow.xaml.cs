using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Illusion.Assets;
using Illusion.Assets.Cars;
using Illusion.Assets.Materials;
using Illusion.Domain.Materials;
using Illusion.Mcp;
using Microsoft.Win32;

namespace Illusion.Views;

/// <summary>
/// Car workshop → Make my own copy…: a material of the car's own from one it is drawn with - the copy with its
/// pictures, the car's meshes re-pointed at it, the game told to treat it like the original, and the same for
/// the original's winter material. Each step is a job of <see cref="ICarWorkshop"/>; a step that fails says
/// what was already done, because the steps before it are kept.
/// </summary>
public sealed partial class CarMaterialWindow : Window
{
    private const string Hint = "The original and every other car drawn with it stay as they are.";

    private readonly CarWorkshopWindow _workshopWindow;
    private readonly AppCarWorkshop _workshop;
    private readonly string _car;
    private readonly string _source;
    private readonly string? _winterSource;
    private readonly IReadOnlyList<MaterialSlotInfo> _samplers;
    // The picture picked for a sampler, by the sampler's id.
    private readonly Dictionary<string, string> _pictures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TextBlock> _pictureTexts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Image> _pictureImages = new(StringComparer.OrdinalIgnoreCase);
    // The textures as they are now, decoded for the rows: a sampler's name says little, its picture says what it is.
    private readonly Viewport.TexturePreviewRenderer _previews = new();
    private readonly string? _workingCopy;

    /// <summary>The material that was made, once the window has closed with true.</summary>
    public string? Made { get; private set; }

    /// <summary>What was done, in a sentence or two for the workshop window to show.</summary>
    public string? Outcome { get; private set; }

    /// <param name="listed">Whether the game does anything to <paramref name="source"/> on this car - whether
    /// there is something for the copy to be treated like.</param>
    internal CarMaterialWindow(CarWorkshopWindow workshopWindow, AppCarWorkshop workshop, string car, string carName, string source, bool listed)
    {
        InitializeComponent();
        _workshopWindow = workshopWindow;
        _workshop = workshop;
        _car = car;
        _source = source;
        try
        {
            _workingCopy = Assets.Sds.SdsMeshLoader.EnsureExtracted(new FileInfo(car));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _workingCopy = null;        // the rows go without pictures; the job itself will say what is wrong
        }
        Closed += (_, _) => _previews.Dispose();
        ulong? hash = MafiaMaterials.FindHashByName(source);
        _samplers = hash is { } known ? MafiaMaterialCatalog.Instance.GetMaterial(known)?.TextureSlots ?? [] : [];
        string winter = source + CarWinter.WinterSuffix;
        _winterSource = !source.Contains('^') && MafiaMaterials.FindHashByName(winter) != null ? winter : null;

        IntroText.Text = $"A copy of {source} under a name of its own, with your pictures in place of some of its textures - the car's "
            + "own paint without another car's emblem pressed into it, its own interior, its own sheet of markings.";
        // As text blocks: a check box given a string takes the '_' of a material's name for an access key.
        UseBox.Content = new TextBlock { Text = $"Put it on the car in place of {source}" };
        LikeBox.Content = new TextBlock { Text = $"Have the game treat it like {source} on this car (car colour, dirt, dents)" };
        LikeBox.Visibility = listed ? Visibility.Visible : Visibility.Collapsed;
        WinterBox.Content = new TextBlock { Text = $"Make its winter twin too, from {winter}" };
        WinterBox.Visibility = _winterSource != null ? Visibility.Visible : Visibility.Collapsed;

        foreach (MaterialSlotInfo sampler in _samplers) SamplerRows.Children.Add(Row(sampler));
        if (_samplers.Count == 0)
        {
            SamplerRows.Children.Add(new TextBlock
            {
                Style = (Style)FindResource("DimLabel"),
                Text = "This material has no textures.",
                Margin = new Thickness(0, 4, 0, 4),
            });
        }

        string proposed = new([.. $"{carName}_{source}".Where(c => char.IsAsciiLetterOrDigit(c) || c == '_')]);
        string name = proposed;
        for (int n = 2; MafiaMaterials.FindHashByName(name) != null && n < 100; n++) name = proposed + n;
        NameBox.Text = name;
        Validate();
    }

    // One sampler: what it is, the texture it has now or the picture picked for it, and the two buttons.
    private Grid Row(MaterialSlotInfo sampler)
    {
        var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new TextBlock
        {
            Text = sampler.FriendlyName,
            FontSize = 12,
            Foreground = System.Windows.Media.Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = sampler.SlotId,
        };
        var texture = new TextBlock
        {
            Style = (Style)FindResource("DimLabel"),
            Text = sampler.TextureName ?? "(none)",
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(8, 0, 8, 0),
        };
        _pictureTexts[sampler.SlotId] = texture;
        var picture = new Image { Stretch = System.Windows.Media.Stretch.UniformToFill, Source = Preview(sampler.TextureName) };
        _pictureImages[sampler.SlotId] = picture;
        var frame = new Border
        {
            Width = 48,
            Height = 48,
            CornerRadius = new CornerRadius(3),
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
            HorizontalAlignment = HorizontalAlignment.Left,
            ClipToBounds = true,
            Child = picture,
        };
        var choose = new Button
        {
            Style = (Style)FindResource("NeutralButton"),
            Content = "Picture…",
            Height = 26,
            Padding = new Thickness(10, 0, 10, 0),
            ToolTip = $"A picture of yours for {sampler.FriendlyName}",
        };
        choose.Click += (_, _) => Choose(sampler);
        var clear = new Button
        {
            Style = (Style)FindResource("NeutralButton"),
            Content = "Keep",
            Height = 26,
            Padding = new Thickness(10, 0, 10, 0),
            Margin = new Thickness(6, 0, 0, 0),
            ToolTip = "Leave this texture the original's",
        };
        clear.Click += (_, _) =>
        {
            _pictures.Remove(sampler.SlotId);
            texture.Text = sampler.TextureName ?? "(none)";
            texture.Foreground = (System.Windows.Media.Brush)FindResource("TextDim");
            picture.Source = Preview(sampler.TextureName);
        };

        Grid.SetColumn(frame, 0);
        Grid.SetColumn(title, 1);
        Grid.SetColumn(texture, 2);
        Grid.SetColumn(choose, 3);
        Grid.SetColumn(clear, 4);
        row.Children.Add(frame);
        row.Children.Add(title);
        row.Children.Add(texture);
        row.Children.Add(choose);
        row.Children.Add(clear);
        return row;
    }

    // A texture of the game as a picture: the car's own copy of it where the archive carries one, else the
    // game's. Null when there is none or it cannot be decoded - the row then shows an empty frame.
    private System.Windows.Media.ImageSource? Preview(string? texture)
    {
        if (string.IsNullOrEmpty(texture)) return null;
        try
        {
            string? own = _workingCopy == null ? null : Path.Combine(_workingCopy, texture);
            string? path = own != null && File.Exists(own) ? own : Assets.Textures.TextureSearchIndex.FindPath(texture);
            return path == null ? null : _previews.Render(new FileInfo(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            return null;
        }
    }

    private void Choose(MaterialSlotInfo sampler)
    {
        var dialog = new OpenFileDialog
        {
            Title = $"Picture for {sampler.FriendlyName}",
            Filter = "Pictures|*.png;*.bmp;*.tif;*.tiff;*.jpg;*.jpeg|All files|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;
        SetPicture(sampler.SlotId, dialog.FileName);
    }

    /// <summary>Picks a picture for a sampler, as the Picture… button does once a file is chosen.</summary>
    internal void SetPicture(string sampler, string file)
    {
        if (!_pictureTexts.TryGetValue(sampler, out TextBlock? text)) return;
        _pictures[sampler] = file;
        text.Text = "→ " + Path.GetFileName(file);
        text.Foreground = System.Windows.Media.Brushes.White;
        if (_pictureImages.TryGetValue(sampler, out Image? image))
        {
            try
            {
                // Read whole and let go of: the file must stay free for whoever is still painting it.
                var shown = new System.Windows.Media.Imaging.BitmapImage();
                shown.BeginInit();
                shown.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                shown.DecodePixelWidth = 96;
                shown.UriSource = new Uri(file);
                shown.EndInit();
                image.Source = shown;
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or FileFormatException)
            {
                image.Source = null;
            }
        }
    }

    private void Field_Changed(object sender, RoutedEventArgs e)
    {
        if (IsInitialized) Validate();
    }

    private void Validate()
    {
        string name = NameBox.Text.Trim();
        string? wrong = null;
        if (name.Length > 0 && !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')) wrong = "A name is letters, digits and '_'.";
        else if (name.Length > 0 && MafiaMaterials.FindHashByName(name) != null) wrong = "A material of that name already exists.";
        RunBtn.IsEnabled = name.Length > 0 && wrong == null;
        HintText.Text = wrong ?? Hint;
    }

    private void Run_Click(object sender, RoutedEventArgs e)
    {
        Validate();
        if (!RunBtn.IsEnabled) return;
        string name = NameBox.Text.Trim();
        var done = new List<string>();
        string? failed = null;
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            failed = Make(name, done);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException
            or NotSupportedException)
        {
            failed = ex.Message;
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        if (done.Count > 0) _workshopWindow.Landed();
        if (failed != null)
        {
            // What was done before the step that failed is kept: said, so it is not made twice.
            _workshopWindow.NotDone(done.Count == 0 ? failed : string.Join(" ", done) + "\n\nThen it stopped: " + ToolText.ForPeople(failed));
            if (done.Count == 0) return;
        }
        Made = name;
        Outcome = string.Join(" ", done) + " The winter twin of the car shares all of this: make it again when you are done.";
        DialogResult = true;
    }

    // The steps, in the order that leaves the car drawable at every point: the material first, then the meshes
    // that name it, then what the game does to it. Returns why it stopped, with what was done so far in 'done'.
    private string? Make(string name, List<string> done)
    {
        if (_workshop.MaterialVariant(_source, name, _car, _pictures, out MaterialVariantInfo? made) is { } noCopy) return noCopy;
        done.Add($"{name} is in {Path.GetFileName(made!.Library)}"
            + (made.Textures.Count == 0 ? "." : $" with {made.Textures.Count} texture(s) of its own ({string.Join(", ", made.Textures.Select(t => t.Texture))})."));

        string? winterName = null;
        if (_winterSource != null && WinterBox.IsChecked == true)
        {
            winterName = name + CarWinter.WinterSuffix;
            // The winter material has its own samplers: a picture goes to those it shares with the summer one.
            var theirs = MafiaMaterials.FindHashByName(_winterSource) is { } hash
                ? MafiaMaterialCatalog.Instance.GetMaterial(hash)?.TextureSlots.Select(s => s.SlotId).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? []
                : [];
            var pictures = _pictures.Where(p => theirs.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
            if (_workshop.MaterialVariant(_winterSource, winterName, _car, pictures, out _) is { } noWinter) return "the winter twin of the material: " + noWinter;
            done.Add($"Its winter twin {winterName} is made from {_winterSource}.");
        }

        if (UseBox.IsChecked == true)
        {
            if (_workshop.CarMaterialUse(_car, name, _source, out int slots, out _) is { } notUsed) return notUsed;
            done.Add(slots > 0 ? $"The car is drawn with it in place of {_source} ({slots} slot(s))." : $"No mesh of the car names {_source}: nothing was re-pointed.");
        }

        if (LikeBox.Visibility == Visibility.Visible && LikeBox.IsChecked == true)
        {
            if (_workshop.CarMaterialLike(_car, name, _source, out _, out _) is { } notLiked) return notLiked;
            if (winterName != null && _workshop.CarMaterialLike(_car, winterName, _source, out _, out _) is { } winterNotLiked) return winterNotLiked;
            done.Add($"The game treats it like {_source}.");
        }
        return null;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
