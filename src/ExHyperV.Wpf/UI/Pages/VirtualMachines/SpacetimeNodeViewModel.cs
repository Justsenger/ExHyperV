using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Models;

namespace ExHyperV.UI.Pages.VirtualMachines;

public partial class SpacetimeNodeViewModel : ObservableObject
{
    public SpacetimeNodeViewModel(SpacetimeNode model)
    {
        Model = model;
        ParentId = model.ParentId;
        CreatedDate = model.CreatedDate;
        _editedName = model.Name;
        model.PropertyChanged += (_, e) => OnPropertyChanged(e.PropertyName);
    }

    public SpacetimeNode Model { get; }
    public string Id => Model.Id;
    public string? ParentId { get; set; }
    public DateTime CreatedDate { get; set; }
    public string Name { get => Model.Name; set => Model.Name = value; }
    public VmScreenshotImage? Thumbnail => Model.Thumbnail;
    public bool IsCurrent => Model.IsCurrent;
    public SpacetimeNodeType NodeType => Model.NodeType;
    public bool IsLogicalNode => Model.IsLogicalNode;
    public bool IsWormhole => Model.IsWormhole;
    public string VhdPath => Model.VhdPath;

    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _editedName;

    [RelayCommand]
    private void StartEdit()
    {
        if (IsLogicalNode) return;
        EditedName = Name;
        IsEditing = true;
    }
}
