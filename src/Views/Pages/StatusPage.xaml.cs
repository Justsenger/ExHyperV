using System.Diagnostics;
using System.Security.Principal;
using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Controls;

namespace ExHyperV.Views.Pages;

public partial class StatusPage
{
    // Constants for working with Hyper-V registry
    private const string RegistryPath = @"HKLM:\SOFTWARE\Policies\Microsoft\Windows\HyperV";
    private const string RequireSecureDeviceAssignmentKey = "RequireSecureDeviceAssignment";
    private const string RequireSupportedDeviceAssignmentKey = "RequireSupportedDeviceAssignment";

    // Flag to prevent circular event triggering when updating toggle programmatically
    private bool _isUpdatingToggle;

    public StatusPage()
    {
        InitializeComponent();

        _ = Task.Run(CpuInfo);
        _ = Task.Run(SysInfo);
        _ = Task.Run(HyperVInfo);
        _ = Task.Run(CheckReg);
        _ = Task.Run(AdminInfo);
        _ = Task.Run(ServerInfo);
    }

    // PowerShell script for adding registry entries
    private static string AddRegistryScript => $$"""
                                                 # Ensure registry path exists, create if it doesn't
                                                 if (-not (Test-Path '{{RegistryPath}}')) {
                                                     New-Item -Path '{{RegistryPath}}' -Force
                                                 }

                                                 # Check and create registry values
                                                 if (-not (Test-Path "{{RegistryPath}}\{{RequireSecureDeviceAssignmentKey}}")) {
                                                     Set-ItemProperty -Path '{{RegistryPath}}' -Name '{{RequireSecureDeviceAssignmentKey}}' -Value 0 -Type DWord
                                                 }

                                                 if (-not (Test-Path "{{RegistryPath}}\{{RequireSupportedDeviceAssignmentKey}}")) {
                                                     Set-ItemProperty -Path '{{RegistryPath}}' -Name '{{RequireSupportedDeviceAssignmentKey}}' -Value 0 -Type DWord
                                                 }
                                                 """;

    // PowerShell script for removing registry entries
    private static string RemoveRegistryScript => $"""
                                                   Remove-ItemProperty -Path '{RegistryPath}' -Name '{RequireSecureDeviceAssignmentKey}'
                                                   Remove-ItemProperty -Path '{RegistryPath}' -Name '{RequireSupportedDeviceAssignmentKey}'
                                                   Remove-Item -Path '{RegistryPath}' -Force
                                                   """;

    /// <summary>
    ///     Safe wrapper for async operations to prevent application crashes
    /// </summary>
    private async Task SafeAsyncTask(Func<Task> asyncAction)
    {
        try
        {
            await asyncAction();
        }
        catch (Exception ex)
        {
            // Critical error handling - prevent application crash
            try
            {
                // Log the exception for diagnostics
                Debug.WriteLine($"Critical error in async void handler: {ex}");

                // Reset toggle to safe state
                _isUpdatingToggle = true;
                Gpustrategy.IsChecked = false;
                _isUpdatingToggle = false;
            }
            catch (Exception recoveryEx)
            {
                // Log recovery failure as well
                Debug.WriteLine($"Error during error recovery in async void handler: {recoveryEx}");
                // The application should remain stable
            }
        }
    }

    private static void AddReg()
    {
        Utils.Run(AddRegistryScript);
    }

    private static void RemoveReg()
    {
        Utils.Run(RemoveRegistryScript);
    }

    private async Task HyperVInfo()
    {
        var hypervstatus = await Task.Run(() => Utils.Run("Get-Module -ListAvailable -Name Hyper-V"));
        await Dispatcher.InvokeAsync(() =>
        {
            Status3.Children.Remove(ProgressRing3);
            var icons = new FontIcon
            {
                FontSize = 20,
                FontFamily = Application.Current.Resources["SegoeFluentIcons"] as FontFamily ??
                             new FontFamily("Segoe Fluent Icons"),
                Glyph = "\xEC61",
                Foreground = new SolidColorBrush(Color.FromArgb(255, 0, 138, 23))
            };
            if (hypervstatus.Count == 0)
            {
                Hyperv.Text = Properties.Resources.String2;
                icons.Glyph = "\xEB90";
                icons.Foreground = new SolidColorBrush(Colors.Red);
            }
            else
            {
                Hyperv.Text = Properties.Resources.String1;
            }

            Status3.Children.Add(icons);
        });
    }

    private async Task SysInfo()
    {
        var buildVersion = Environment.OSVersion.Version.Build;
        await Dispatcher.InvokeAsync(() =>
        {
            Status1.Children.Remove(ProgressRing1);
            var icons = new FontIcon
            {
                FontSize = 20,
                FontFamily = Application.Current.Resources["SegoeFluentIcons"] as FontFamily ??
                             new FontFamily("Segoe Fluent Icons"),
                Glyph = "\xF167",
                Foreground = new SolidColorBrush(Colors.DodgerBlue)
            };
            Status1.Children.Add(icons);
            if (buildVersion >= 22000) //����������ʹ�ù��͵�ϵͳ�汾��WDDM�汾�ͣ��Լ�PS�����������⡣
            {
                icons.Glyph = "\xEC61";
                icons.Foreground = new SolidColorBrush(Color.FromArgb(255, 0, 138, 23));
                Win.Text = Properties.Resources.String3 + buildVersion + Properties.Resources.v19041;
            }
            else
            {
                Win.Text = Properties.Resources.String3 + buildVersion + Properties.Resources.disablegpu;
                icons.Glyph = "\xEB90";
                icons.Foreground = new SolidColorBrush(Colors.Red);
            }
        });
    }

    private async Task CpuInfo()
    {
        var cpuvt1 = await Task.Run(() =>
            Utils.Run("(Get-WmiObject -Class Win32_Processor).VirtualizationFirmwareEnabled"));
        var cpuvt2 = await Task.Run(() => Utils.Run("(Get-WmiObject -Class Win32_ComputerSystem).HypervisorPresent"));

        await Dispatcher.InvokeAsync(() =>
        {
            Status2.Children.Remove(ProgressRing2);
            var icons = new FontIcon
            {
                FontSize = 20,
                FontFamily = Application.Current.Resources["SegoeFluentIcons"] as FontFamily ??
                             new FontFamily("Segoe Fluent Icons"),
                Glyph = "\xEC61",
                Foreground = new SolidColorBrush(Color.FromArgb(255, 0, 138, 23))
            };

            if ((cpuvt1.Count > 0 && cpuvt1[0].ToString() == "True") ||
                (cpuvt2.Count > 0 && cpuvt2[0].ToString() == "True"))
            {
                Cpu.Text = Properties.Resources.GPU1;
            }
            else
            {
                Cpu.Text = Properties.Resources.GPU2;
                icons.Glyph = "\xEB90";
                icons.Foreground = new SolidColorBrush(Colors.Red);
            }

            Status2.Children.Add(icons);
        });
    }

    private async Task CheckReg() // Check if registry keys are set to disable security policies
    {
        try
        {
            const string script = $$"""
                                    [bool]((Test-Path '{{RegistryPath}}') -and
                                            (($k = Get-Item '{{RegistryPath}}' -EA 0) -ne $null) -and
                                            ('{{RequireSecureDeviceAssignmentKey}}', '{{RequireSupportedDeviceAssignmentKey}}' | ForEach-Object {
                                                ($k.GetValue($_, $null) -ne $null) -and ($k.GetValueKind($_) -eq 'DWord')
                                            }) -notcontains $false)
                                    """;
            var result = await Task.Run(() => Utils.Run(script));

            await Dispatcher.InvokeAsync(() =>
            {
                _isUpdatingToggle = true; // Prevent circular event triggering
                try
                {
                    var registryExists = result.Count > 0 &&
                                         string.Equals(result[0].ToString(), "true",
                                             StringComparison.OrdinalIgnoreCase);

                    // Always set the correct state based on actual registry status
                    Gpustrategy.IsChecked = registryExists;
                }
                finally
                {
                    _isUpdatingToggle = false; // Re-enable event handling
                }
            });
        }
        catch (Exception)
        {
            // Log error and set safe state when registry check fails
            await Dispatcher.InvokeAsync(() =>
            {
                _isUpdatingToggle = true;
                try
                {
                    // Set to false as safe default when we can't determine registry state
                    Gpustrategy.IsChecked = false;
                }
                finally
                {
                    _isUpdatingToggle = false;
                }
            });

            // In a production app, you might want to log this exception
            // For now, we silently handle it to maintain UI stability
        }
    }

    private async Task AdminInfo()
    {
        var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        var isadmin = principal.IsInRole(WindowsBuiltInRole.Administrator);
        await Dispatcher.InvokeAsync(() =>
        {
            Status4.Children.Remove(ProgressRing4);
            var icons = new FontIcon
            {
                FontSize = 20,
                FontFamily = Application.Current.Resources["SegoeFluentIcons"] as FontFamily ??
                             new FontFamily("Segoe Fluent Icons"),
                Glyph = "\xEC61",
                Foreground = new SolidColorBrush(Color.FromArgb(255, 0, 138, 23))
            };
            if (isadmin)
            {
                Admin.Text = Properties.Resources.Admin1;
            }
            else // If no admin privileges, disable GPU paravirtualization feature
            {
                if (Application.Current.MainWindow is MainWindow mainWindow) // Get main window
                    mainWindow.Gpupv.IsEnabled = false;
                Admin.Text = Properties.Resources.Admin2;
                icons.Glyph = "\xEB90";
                icons.Foreground = new SolidColorBrush(Colors.Red);
            }

            Status4.Children.Add(icons);
        });
    }

    private async Task ServerInfo()
    {
        var result = await Task.Run(() => Utils.Run("(Get-WmiObject -Class Win32_OperatingSystem).ProductType"));
        await Dispatcher.InvokeAsync(() =>
        {
            Status5.Children.Remove(ProgressRing5);
            var icons = new FontIcon
            {
                FontSize = 20,
                FontFamily = Application.Current.Resources["SegoeFluentIcons"] as FontFamily ??
                             new FontFamily("Segoe Fluent Icons"),
                Glyph = "\xEC61",
                Foreground = new SolidColorBrush(Color.FromArgb(255, 0, 138, 23))
            };
            if (result.Count > 0 && result[0].ToString() == "3")
            {
                Version.Text = Properties.Resources.Isserver;
            }
            else
            {
                Version.Text = Properties.Resources.ddaa;
                icons.Glyph = "\xEB90";
                icons.Foreground = new SolidColorBrush(Colors.Red);

                // Disable DDA navigation for non-server Windows versions
                if (Application.Current.MainWindow is MainWindow mainWindow)
                    mainWindow.Dda.IsEnabled = false;
            }

            Status5.Children.Add(icons);
        });
    }

    private void Addgs(object sender, RoutedEventArgs e)
    {
        _ = SafeAsyncTask(async () =>
        {
            // Ignore programmatic changes to prevent circular event triggering
            if (_isUpdatingToggle) return;

            AddReg();
            // Small delay to ensure registry operation completes before checking
            await Task.Delay(100);
            await CheckReg();
        });
    }

    private void Deletegs(object sender, RoutedEventArgs e)
    {
        _ = SafeAsyncTask(async () =>
        {
            // Ignore programmatic changes to prevent circular event triggering
            if (_isUpdatingToggle) return;

            RemoveReg();
            // Small delay to ensure registry operation completes before checking
            await Task.Delay(100);
            await CheckReg();
        });
    }
}