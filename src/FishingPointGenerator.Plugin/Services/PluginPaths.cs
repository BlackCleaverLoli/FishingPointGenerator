using Dalamud.Plugin;

namespace FishingPointGenerator.Plugin.Services;

internal sealed class PluginPaths
{
    public PluginPaths(IDalamudPluginInterface pluginInterface)
    {
        RootDirectory = pluginInterface.GetPluginConfigDirectory();
        DataDirectory = Path.Combine(RootDirectory, "data");
        CatalogDirectory = Path.Combine(DataDirectory, "catalog");
        ScansDirectory = Path.Combine(DataDirectory, "scans");
        GeneratedDirectory = Path.Combine(DataDirectory, "generated");
        LabelsDirectory = Path.Combine(DataDirectory, "labels");
        ReviewDirectory = Path.Combine(DataDirectory, "review");
        ReportsDirectory = Path.Combine(DataDirectory, "reports");
        ExportsDirectory = Path.Combine(DataDirectory, "exports");

        Directory.CreateDirectory(CatalogDirectory);
        Directory.CreateDirectory(ScansDirectory);
        Directory.CreateDirectory(GeneratedDirectory);
        Directory.CreateDirectory(LabelsDirectory);
        Directory.CreateDirectory(ReviewDirectory);
        Directory.CreateDirectory(ReportsDirectory);
        Directory.CreateDirectory(ExportsDirectory);
    }

    public string RootDirectory { get; }
    public string DataDirectory { get; }
    public string CatalogDirectory { get; }
    public string ScansDirectory { get; }
    public string GeneratedDirectory { get; }
    public string LabelsDirectory { get; }
    public string ReviewDirectory { get; }
    public string ReportsDirectory { get; }
    public string ExportsDirectory { get; }
}
