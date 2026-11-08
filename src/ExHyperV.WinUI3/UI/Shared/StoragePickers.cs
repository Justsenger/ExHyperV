using Microsoft.UI.Xaml;
using Microsoft.Windows.Storage.Pickers;

namespace ExHyperV.WinUI3.UI.Shared;

/// <summary>将文件选择窗口关联到主窗口；支持管理员进程，取消时不修改文件。</summary>
internal static class StoragePickers
{
    private static Microsoft.UI.WindowId WindowId => ((App)Application.Current).MainWindowId;

    internal static async Task<string?> OpenAsync(params string[] extensions)
    {
        var picker = new FileOpenPicker(WindowId);
        foreach (var extension in extensions) picker.FileTypeFilter.Add(extension);
        return (await picker.PickSingleFileAsync())?.Path;
    }

    internal static async Task<string?> SaveAsync(string suggestedFileName, string description, params string[] extensions)
    {
        var picker = new FileSavePicker(WindowId) { SuggestedFileName = suggestedFileName };
        picker.FileTypeChoices.Add(description, extensions.ToList());
        return (await picker.PickSaveFileAsync())?.Path;
    }

    internal static async Task<string?> FolderAsync() =>
        (await new FolderPicker(WindowId).PickSingleFolderAsync())?.Path;
}
