using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Illusion.Assets;
using Illusion.Assets.Materials;
using Illusion.Mcp;
using Illusion.Viewport;
using Microsoft.Win32;

namespace Illusion.Views;

/// <summary>
/// Tools → Materials of an archive…: what an archive is drawn with - the game's own, changed, added, missing -
/// and the added and changed ones saved as a material library of their own or as a document. The job behind
/// <c>archive_materials</c>, through the same session. Nothing of the game is written.
/// </summary>
public sealed partial class ArchiveMaterialsWindow : Window
{
    /// <summary>A material of the archive as the list shows it.</summary>
    public sealed record Row(string Name, ImageSource? Thumb, bool IsAdded, bool IsChanged, bool IsMissing, bool IsShipped,
        string DetailText, string TexturesText);

    private readonly D3DImageHost? _host;
    private readonly AppEditorSession _session = new();
    private ArchiveMaterials.Report? _report;
    private FileInfo? _archive;

    /// <param name="host">The viewport that renders the materials' pictures; null leaves the rows without one.</param>
    /// <param name="archive">The archive to start from - the one open in the Resource Editor - or null.</param>
    public ArchiveMaterialsWindow(D3DImageHost? host, string? archive)
    {
        InitializeComponent();
        _host = host;
        if (archive != null)
        {
            ArchiveBox.Text = archive;
            Read();
        }
    }

    private static FileInfo? Resolve(string typed)
    {
        string asked = typed.Trim().Trim('"').Replace('/', Path.DirectorySeparatorChar);
        if (asked.Length == 0 || !MafiaEnvironment.IsInitialized) return null;
        if (!asked.EndsWith(".sds", StringComparison.OrdinalIgnoreCase)) asked += ".sds";
        return new FileInfo(Path.IsPathRooted(asked) ? asked : Path.Combine(MafiaEnvironment.PcFolder, "sds", asked));
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Archive to read", Filter = "Archives|*.sds" };
        string? start = Resolve(ArchiveBox.Text)?.DirectoryName
            ?? (MafiaEnvironment.IsInitialized ? Path.Combine(MafiaEnvironment.PcFolder, "sds") : null);
        if (start != null && Directory.Exists(start)) dialog.InitialDirectory = start;
        if (dialog.ShowDialog(this) != true) return;
        ArchiveBox.Text = dialog.FileName;
        Read();
    }

    private void ArchiveBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Read();
    }

    private void Read_Click(object sender, RoutedEventArgs e) => Read();

    private void Travel_Click(object sender, RoutedEventArgs e) => Fill(_report);

    private void Read()
    {
        _report = null;
        _archive = Resolve(ArchiveBox.Text);
        string? refused = _archive == null ? "Name the archive to read."
            : !_archive.Exists ? $"No such archive: {_archive.FullName}"
            : null;
        if (refused == null)
        {
            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                _report = ArchiveMaterials.Read(_archive!, ArchiveMaterials.LoadLibraries(MafiaEnvironment.GameRoot));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException
                or InvalidOperationException)
            {
                refused = "Could not read the archive's materials: " + ex.Message;
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }
        if (refused != null) SummaryText.Text = refused;
        Fill(_report);
    }

    private ImageSource? Thumb(ulong hash) =>
        _host != null && MafiaMaterialCatalog.Instance.GetMaterial(hash) is { } info ? _host.RenderMaterialThumbnail(info) : null;

    private void Fill(ArchiveMaterials.Report? report)
    {
        var rows = new List<Row>();
        if (report != null)
        {
            bool travelOnly = TravelBox.IsChecked == true && report.OriginKnown;
            foreach (ArchiveMaterials.Missing missing in report.Missing)
            {
                rows.Add(new Row(missing.Name ?? $"0x{missing.Hash:X16}", null, false, false, true, false,
                    $"{missing.Triangles} triangles in {missing.Parts} part(s)", ""));
            }
            foreach (ArchiveMaterials.Material material in report.Materials
                         .OrderBy(m => m.Origin == MaterialOrigin.Shipped).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (travelOnly && material.Origin == MaterialOrigin.Shipped) continue;
                int inside = material.Samplers.Count(s => s.InArchive);
                string textures = string.Join("\n", material.Samplers.Select(s => $"{s.Name}: {s.Texture}" + (s.InArchive ? "  (in the archive)" : "")));
                rows.Add(new Row(material.Name, Thumb(material.Hash), material.Origin == MaterialOrigin.Added,
                    material.Origin == MaterialOrigin.Changed, false, material.Origin == MaterialOrigin.Shipped,
                    $"{material.Triangles} triangles · {inside} of {material.Samplers.Count} textures in the archive", textures));
            }

            int Of(MaterialOrigin origin) => report.Materials.Count(m => m.Origin == origin);
            int travel = Of(MaterialOrigin.Added) + Of(MaterialOrigin.Changed);
            SummaryText.Text = $"{Path.GetFileName(report.Archive)} is drawn with {report.Materials.Count + report.Missing.Count} material(s): "
                + $"{Of(MaterialOrigin.Shipped)} the game's own, {Of(MaterialOrigin.Added)} added, {Of(MaterialOrigin.Changed)} changed"
                + (report.Missing.Count > 0 ? $", {report.Missing.Count} that no library has" : "") + ". "
                + (!report.OriginKnown ? "This edition of the game is not on the toolkit's list of shipped materials, so every material is shown."
                    : travel == 0 ? "It needs nothing registered: whoever has the game can draw it."
                    : $"{travel} of them must travel with the archive.");
            EmptyLabel.Text = "Nothing has to travel with this archive.";
            LibraryBtn.IsEnabled = report.NotShipped.Any();
        }
        else
        {
            EmptyLabel.Text = "";
            LibraryBtn.IsEnabled = false;
        }
        MaterialList.ItemsSource = rows;
        EmptyLabel.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ListBtn.IsEnabled = report != null;
    }

    private void Library_Click(object sender, RoutedEventArgs e) =>
        Save("Material library|*.mtl", ".mtl", (file, all) => (null, file), made =>
            $"{made.LibraryMaterials} material(s) written to {made.Library}. A multiplayer host loads it beside the game's libraries.");

    private void List_Click(object sender, RoutedEventArgs e) =>
        Save("Document|*.json", ".json", (file, all) => (file, null), made => $"The list is written to {made.SavedTo}.");

    // One of the two files, through the same job the tool runs. Never into the game's own material folder: the
    // job refuses that, and the dialog starts beside the game instead.
    private void Save(string filter, string extension, Func<string, bool, (string? Document, string? Library)> where, Func<ArchiveMaterialsInfo, string> said)
    {
        if (_archive == null || _report == null) return;
        string folder = Path.Combine(MafiaEnvironment.GameRoot, "_illusion_export");
        var dialog = new SaveFileDialog
        {
            Title = "Save",
            Filter = filter,
            FileName = Path.GetFileNameWithoutExtension(_archive.Name) + extension,
        };
        if (Directory.Exists(folder)) dialog.InitialDirectory = folder;
        if (dialog.ShowDialog(this) != true) return;
        bool all = TravelBox.IsChecked != true;
        (string? document, string? library) = where(dialog.FileName, all);
        string? refused;
        ArchiveMaterialsInfo? made;
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            refused = _session.ArchiveMaterials(_archive.FullName, all, document, library, out made);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
        if (refused != null || made == null)
        {
            AppDialog.Show(this, new DialogOptions { Title = Title, Icon = DialogIcon.Warning, Heading = "Not saved", Text = ToolText.ForPeople(refused ?? "nothing was written") });
            return;
        }
        AppDialog.Show(this, new DialogOptions { Title = Title, Icon = DialogIcon.Success, Heading = "Saved", Text = said(made) });
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
