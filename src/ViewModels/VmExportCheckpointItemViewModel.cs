using ExHyperV.Services;
using System.Globalization;

namespace ExHyperV.ViewModels;

public sealed class VmExportCheckpointItemViewModel
{
    public VmExportCheckpointItemViewModel(
        VmExportService.CheckpointInfo checkpoint,
        string treePrefix)
    {
        Id = checkpoint.Id;
        ParentId = checkpoint.ParentId;
        Name = checkpoint.Name;
        CreatedDate = checkpoint.CreatedDate;
        Path = checkpoint.Path;
        TreePrefix = treePrefix;
    }

    public string Id { get; }
    public string? ParentId { get; }
    public string Name { get; }
    public DateTime CreatedDate { get; }
    public string CreatedDateDisplay => CreatedDate.ToString(
        Properties.Resources.VmExport_CheckpointDateFormat,
        CultureInfo.CurrentCulture);
    public string Path { get; }
    public string TreePrefix { get; }
}
