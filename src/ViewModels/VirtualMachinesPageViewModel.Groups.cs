using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Interaction;
using ExHyperV.Models;
using ExHyperV.Properties;
using ExHyperV.Services;
using ExHyperV.Views;
using Wpf.Ui.Controls;

namespace ExHyperV.ViewModels;

public partial class VirtualMachinesPageViewModel
{
    private VmGroupSnapshot _groups = new([]);
    private readonly HashSet<string> _collapsedGroups = [];
    private bool _updatingNavigation;
    public bool IsDraggingNavigation { get; set; }
    private bool _groupsLoaded;
    private bool _refreshingGroups;
    private bool _disposed;
    private DateTime _nextGroupRefresh;
    public ObservableCollection<VmNavigationItem> NavigationItems { get; } = [];
    [ObservableProperty] private VmNavigationItem? _selectedNavigationItem;
    [ObservableProperty] private string _groupLoadError = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVmListEnabled))]
    [NotifyPropertyChangedFor(nameof(CanEditGroups))]
    private bool _isChangingGroups;
    public bool CanEditGroups => _groupsLoaded && !_refreshingGroups && !IsChangingGroups && IsVmListEnabled;

    partial void OnSelectedNavigationItemChanged(VmNavigationItem? value)
    {
        if (!_updatingNavigation && value?.Vm != null) SelectedVm = value.Vm;
    }

    private void SyncNavigationSelection()
    {
        if (SelectedNavigationItem?.Vm == SelectedVm && (SelectedNavigationItem == null || NavigationItems.Contains(SelectedNavigationItem))) return;
        SelectedNavigationItem = NavigationItems.FirstOrDefault(n => n.Vm != null && n.Vm == SelectedVm);
    }

    private void RefreshNavigation()
    {
        if (_disposed || !IsVmListEnabled || IsDraggingNavigation || NavigationItems.Any(n => n.NameEdit.IsEditing)) return;
        var rows = VmGroupNavigation.Build(_groups, VmList, _collapsedGroups, SearchText);
        _updatingNavigation = true;
        try
        {
            // Keep existing row objects on normal polls so selection, menus and inline edits survive.
            var old = NavigationItems.ToDictionary(n => n.Key);
            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (old.TryGetValue(row.Key, out var previous))
                {
                    row = row with { NameEdit = previous.NameEdit };
                    if (previous == row) row = previous;
                }
                if (i < NavigationItems.Count && ReferenceEquals(NavigationItems[i], row)) continue;
                int index = NavigationItems.IndexOf(row);
                if (index >= 0) NavigationItems.Move(index, i);
                else NavigationItems.Insert(i, row);
            }
            while (NavigationItems.Count > rows.Count) NavigationItems.RemoveAt(NavigationItems.Count - 1);
            SyncNavigationSelection();
        }
        finally { _updatingNavigation = false; }
    }

    [RelayCommand]
    private async Task ToggleGroupAsync(VmNavigationItem? item)
    {
        if (!IsVmListEnabled || item?.Group == null || item.NameEdit.IsEditing) return;
        // Group mouse-down is handled for dragging, so it does not move keyboard focus by itself.
        // Commit explicitly and await the write/refresh before applying the user's toggle.
        var editing = NavigationItems.FirstOrDefault(n => n.NameEdit.IsEditing);
        if (editing != null) await CommitGroupRenameCommand.ExecuteAsync(editing);
        item = NavigationItems.FirstOrDefault(n => n.Key == item.Key);
        if (!IsVmListEnabled || item?.Group == null || !item.HasChildren || !string.IsNullOrWhiteSpace(SearchText)) return;
        if (!_collapsedGroups.Add(item.Key)) _collapsedGroups.Remove(item.Key);
        RefreshNavigation();
    }

    [RelayCommand]
    private async Task RefreshGroupsAsync()
    {
        if (_refreshingGroups || IsChangingGroups || _disposed || IsDraggingNavigation || NavigationItems.Any(n => n.NameEdit.IsEditing)) return;
        _refreshingGroups = true;
        OnPropertyChanged(nameof(CanEditGroups));
        try
        {
            var snapshot = await VmGroupService.ReadAsync();
            if (_disposed || !IsVmListEnabled) return;
            if (!_groupsLoaded || GroupSignature(snapshot) != GroupSignature(_groups)) _groups = snapshot;
            _groupsLoaded = true;
            GroupLoadError = string.Empty;
            RefreshNavigation();
        }
        catch (Exception ex)
        {
            if (_disposed || !IsVmListEnabled) return;
            // A failed query must not silently turn every VM into "ungrouped".
            _groupsLoaded = false;
            GroupLoadError = Resources.VmGroup_LoadFailed + " " + ex.Message;
            // Keep the last known tree, and show the read failure rather than inventing new membership.
            RefreshNavigation();
        }
        finally
        {
            _refreshingGroups = false;
            _nextGroupRefresh = DateTime.UtcNow.AddSeconds(10);
            OnPropertyChanged(nameof(CanEditGroups));
        }
    }

    private static string GroupSignature(VmGroupSnapshot snapshot) => string.Join("|", snapshot.Groups.OrderBy(g => g.Id)
        .Select(g => $"{g.Id}:{g.Name}:{g.Kind}:{string.Join(',', g.VmIds.Order())}:{string.Join(',', g.ChildIds.Order())}"));

    private async Task<bool> GroupDialogAsync(string title, object content)
    {
        if (Application.Current.MainWindow is not MainWindow window) return false;
        var dialog = new ContentDialog { Title = title, Content = content,
            PrimaryButtonText = Resources.Btn_Confirm, CloseButtonText = Resources.Btn_Cancel,
            DialogHostEx = window.ContentPresenterForDialogs };
        return await dialog.ShowAsync(CancellationToken.None) == ContentDialogResult.Primary;
    }

    private async Task ChangeGroupsAsync(Func<Task> action)
    {
        if (!CanEditGroups || _refreshingGroups) return;
        IsChangingGroups = true;
        try { await action(); }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { IsChangingGroups = false; await RefreshGroupsAsync(); }
    }

    [RelayCommand]
    private Task CreateGroupAsync(VmNavigationItem? parentItem) => ChangeGroupsAsync(async () =>
    {
        var name = new Wpf.Ui.Controls.TextBox { PlaceholderText = Resources.VmGroup_Name, MinWidth = 300, MaxLength = 100 };
        if (!await GroupDialogAsync(Resources.VmGroup_Create, name) || string.IsNullOrWhiteSpace(name.Text)) return;
        await VmGroupService.CreateAsync(name.Text, VmGroupKind.VirtualMachines, parentItem?.Group?.Id);
    });

    [RelayCommand]
    private void RenameGroup(VmNavigationItem? item)
    {
        if (!CanEditGroups || item?.Group == null || item.NameEdit.IsEditing) return;
        foreach (var row in NavigationItems.Where(n => n.NameEdit.IsEditing)) CancelGroupRename(row);
        item.NameEdit.EditedName = item.Name;
        item.NameEdit.IsEditing = true;
    }

    [RelayCommand]
    private void CancelGroupRename(VmNavigationItem? item)
    {
        if (item == null) return;
        item.NameEdit.IsEditing = false;
        item.NameEdit.EditedName = item.Name;
    }

    [RelayCommand]
    private Task CommitGroupRenameAsync(VmNavigationItem? item)
    {
        if (item?.Group == null || !item.NameEdit.IsEditing) return Task.CompletedTask;
        string name = item.NameEdit.EditedName.Trim();
        // End editing before hiding the TextBox raises LostFocus a second time.
        item.NameEdit.IsEditing = false;
        if (name.Length == 0 || name == item.Name) return Task.CompletedTask;
        return ChangeGroupsAsync(() => VmGroupService.RenameAsync(item.Group.Id, name));
    }

    [RelayCommand]
    private Task DeleteGroupAsync(VmNavigationItem? item) => ChangeGroupsAsync(async () =>
    {
        if (item?.Group is not VmGroup group) return;
        if (await Dialogs.ShowConfirmAsync(Resources.VmGroup_Delete, string.Format(Resources.VmGroup_DeleteConfirm, group.Name),
                Resources.Xaml_Delete, Resources.Btn_Cancel, isDanger: true))
            await VmGroupService.DeleteAsync(group.Id);
    });

    public bool CanDropNavigation(IReadOnlyList<VmGroupMove> items, VmNavigationItem? target) =>
        CanEditGroups && (target == null || target.IsGroup) && VmGroupMoveRules.CanMove(_groups, items, target?.Group?.Id);

    public Task DropNavigationAsync(IReadOnlyList<VmGroupMove> items, VmNavigationItem? target) => ChangeGroupsAsync(async () =>
    {
        if ((target != null && !target.IsGroup) || !VmGroupMoveRules.CanMove(_groups, items, target?.Group?.Id)) return;
        await VmGroupService.MoveAsync(items, target?.Group?.Id);
        if (target != null) _collapsedGroups.Remove(target.Key);
    });
}
