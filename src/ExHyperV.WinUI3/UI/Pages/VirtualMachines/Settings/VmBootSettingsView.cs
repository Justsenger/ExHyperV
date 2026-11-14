using System.Collections.ObjectModel;
using ExHyperV.Models;
using ExHyperV.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace ExHyperV.WinUI3.UI.Pages.VirtualMachines.Settings;

public sealed class VmBootSettingsView : VmSettingsView
{
    private readonly ObservableCollection<BootEntry> entries = [];
    private readonly ListView list;
    private BootOrderItem[] beforeDrag = [];

    public VmBootSettingsView(Guid vmId) : base(vmId)
    {
        list = new ListView
        {
            ItemsSource = entries,
            SelectionMode = ListViewSelectionMode.Single,
            CanDragItems = true,
            CanReorderItems = true,
            AllowDrop = true,
            IsEnabled = false,
            Margin = new Thickness(24, 4, 24, 24),
            ItemTemplate = (DataTemplate)XamlReader.Load("""
                <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                  <Border Background="{ThemeResource ControlFillColorDefaultBrush}" BorderBrush="{ThemeResource CardStrokeColorDefaultBrush}" BorderThickness="1" CornerRadius="8" Padding="18" Margin="0,0,0,10">
                    <Grid ColumnSpacing="18">
                      <Grid.ColumnDefinitions><ColumnDefinition Width="Auto"/><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
                      <FontIcon Glyph="{Binding Glyph}" FontFamily="Segoe Fluent Icons" FontSize="20" VerticalAlignment="Center"/>
                      <Grid Grid.Column="1" ColumnSpacing="16" VerticalAlignment="Center">
                        <Grid.ColumnDefinitions><ColumnDefinition Width="Auto"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                        <TextBlock Text="{Binding Name}" FontSize="15" FontWeight="Medium" VerticalAlignment="Center"/>
                        <TextBlock Grid.Column="1" Text="{Binding Description}" FontSize="14" Opacity="0.5" TextTrimming="CharacterEllipsis" VerticalAlignment="Center"/>
                      </Grid>
                      <FontIcon Grid.Column="2" Glyph="&#xE700;" FontFamily="Segoe Fluent Icons" FontSize="18" Foreground="{ThemeResource TextFillColorSecondaryBrush}" VerticalAlignment="Center"/>
                    </Grid>
                  </Border>
                </DataTemplate>
                """)
        };
        list.ItemContainerStyle = (Style)XamlReader.Load("""
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="ListViewItem">
              <Setter Property="HorizontalContentAlignment" Value="Stretch"/><Setter Property="Padding" Value="0"/>
            </Style>
            """);
        list.DragItemsStarting += (_, _) => beforeDrag = entries.Select(e => e.Item).ToArray();
        list.DragItemsCompleted += async (_, _) =>
        {
            if (Updating || IsBusy || IsStopped) return;
            var order = entries.Select(e => e.Item).ToList();
            if (beforeDrag.SequenceEqual(order)) return;
            await RunAsync(async () =>
            {
                var result = await VmBootService.SetBootOrderAsync(VmId, order);
                if (result.Success) NotifyChanged();
                // 保存失败时也重新读取，不能把本地拖放结果当成已写入。
                await ReloadAfterWriteAsync(result);
            });
        };
        SetBody(list);
    }

    protected override async Task LoadAsync()
    {
        var contextTask = ReadContextAsync();
        var orderTask = VmBootService.GetBootOrderAsync(VmId);
        await Task.WhenAll(contextTask, orderTask);
        var context = await contextTask;
        var result = await orderTask;
        if (!result.HasData) throw new InvalidOperationException(result.Error);
        if (IsStopped) return;
        Updating = true;
        try
        {
            entries.Clear();
            foreach (var item in result.Data!) entries.Add(new BootEntry(item));
            var condition = VmBootService.GetBootOrderModificationCondition(context.Generation, context.State);
            bool canEdit = condition.CanModify;
            list.IsEnabled = canEdit;
            list.Opacity = canEdit ? 1 : .6;
            if (!canEdit) ShowInformation(condition.Reason);
        }
        finally { Updating = false; }
    }

    [Microsoft.UI.Xaml.Data.Bindable]
    public sealed class BootEntry(BootOrderItem item)
    {
        public BootOrderItem Item { get; } = item;
        public string Name => Item.Name;
        public string Description => Item.Description;
        public string Glyph => Item.Kind switch
        {
            BootDeviceKind.Floppy => "\uE74E",
            BootDeviceKind.OpticalDrive => "\uE958",
            BootDeviceKind.HardDisk => "\uEDA2",
            BootDeviceKind.Network => "\uE774",
            BootDeviceKind.FirmwareFile => "\uE74C",
            BootDeviceKind.Firmware => "\uE713",
            _ => "\uE9CE"
        };
    }
}
