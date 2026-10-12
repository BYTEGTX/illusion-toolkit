using System.IO;
using System.Windows;

namespace Illusion.Views;

// The Tools menu of the resource editor: windows over the jobs that used to be reachable only through the MCP
// server. Each runs what the matching tool runs; this file only opens the window.
public partial class ResourceEditorWindow
{
    private HideTrianglesWindow? _hideTriangles;
    private StreamMapWindow? _streamMap;
    private CarWorkshopWindow? _carWorkshop;
    private ArchiveMaterialsWindow? _archiveMaterials;

    private void HideTriangles_Click(object sender, RoutedEventArgs e)
    {
        if (_hideTriangles != null)
        {
            _hideTriangles.Activate();
            return;
        }
        _hideTriangles = new HideTrianglesWindow(Stage) { Owner = this };
        _hideTriangles.Closed += (_, _) => _hideTriangles = null;
        _hideTriangles.Show();
    }

    private void StreamMap_Click(object sender, RoutedEventArgs e)
    {
        if (_streamMap != null)
        {
            _streamMap.Activate();
            return;
        }
        _streamMap = new StreamMapWindow { Owner = this };
        _streamMap.Closed += (_, _) => _streamMap = null;
        _streamMap.Show();
    }

    private void ArchiveMaterials_Click(object sender, RoutedEventArgs e)
    {
        if (_archiveMaterials != null)
        {
            _archiveMaterials.Activate();
            return;
        }
        _archiveMaterials = new ArchiveMaterialsWindow(Stage, StagedEntry?.File.FullName) { Owner = this };
        _archiveMaterials.Closed += (_, _) => _archiveMaterials = null;
        _archiveMaterials.Show();
    }

    // The workshop works on the car on the stage and stays open beside it, like Hide triangles.
    private void CarWorkshop_Click(object sender, RoutedEventArgs e)
    {
        if (_carWorkshop != null)
        {
            _carWorkshop.Activate();
            return;
        }
        CommitFocusedField();
        if (CarWorkshopWindow.CarOnStage(this) is not { } car)
        {
            AppDialog.Show(this, new DialogOptions
            {
                Title = "Car Workshop",
                Icon = DialogIcon.Info,
                Heading = "Open a car first",
                Text = "The workshop works on the car on the stage: pick one under cars in the library on the left.",
            });
            return;
        }
        _carWorkshop = new CarWorkshopWindow(this, car) { Owner = this };
        _carWorkshop.Closed += (_, _) => _carWorkshop = null;
        _carWorkshop.Show();
    }

    private void CloneCar_Click(object sender, RoutedEventArgs e) => RunCarTool(CarTool.Clone);

    private void SubstituteCar_Click(object sender, RoutedEventArgs e) => RunCarTool(CarTool.Substitute);

    private void ExportCar_Click(object sender, RoutedEventArgs e) => RunCarTool(CarTool.Export);

    // The car on the stage is the one a car job most likely means, so the window starts from it - when what
    // is staged is a car at all.
    private void RunCarTool(CarTool tool)
    {
        CommitFocusedField();
        string? staged = StagedEntry?.File.FullName;
        bool isCar = staged != null
            && string.Equals(Path.GetFileName(Path.GetDirectoryName(staged)), "cars", StringComparison.OrdinalIgnoreCase);
        bool done = new CarToolWindow(tool, isCar ? staged : null) { Owner = this }.ShowDialog() == true;
        // A clone is an archive that was not there when the library was walked (once, as the window opened):
        // without a second walk the car just made is nowhere in the browser until the window is opened again.
        if (done && tool == CarTool.Clone)
        {
            _catalog = null;
            BuildCatalog();
        }
    }
}
