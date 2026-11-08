using ExHyperV.Services;
using System.Globalization;

namespace ExHyperV.UI.Pages.VirtualMachines;

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
        Model = checkpoint;
        TreePrefix = treePrefix;
    }

    public string Id { get; }
    public string? ParentId { get; }
    public string Name { get; }
    public DateTime CreatedDate { get; }
    public string CreatedDateDisplay => CreatedDate.ToString(
        Properties.Resources.VmExport_CheckpointDateFormat,
        CultureInfo.CurrentCulture);
    public VmExportService.CheckpointInfo Model { get; }
    public string TreePrefix { get; }
}
