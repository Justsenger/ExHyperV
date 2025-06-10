using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Management.Automation;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wpf.Ui.Controls;
using Image = Wpf.Ui.Controls.Image;
using TextBlock = Wpf.Ui.Controls.TextBlock;

namespace ExHyperV;

/// <summary>
///     PowerShell script execution result
/// </summary>
public class PowerShellResult
{
    public Collection<PSObject> Output { get; } = [];
    public Collection<ErrorRecord> Errors { get; } = [];
}

public static class Utils
{
    public static string Version => "V1.0.8";
    public static string Author => "ɰ  Ҷ";

    public static Collection<PSObject> Run(string script)
    {
        using var ps = PowerShell.Create();
        ps.AddScript(script);
        return ps.Invoke();
    }

    /// <summary>
    ///     Asynchronous PowerShell script execution with full error handling
    /// </summary>
    /// <param name="script">PowerShell script to execute</param>
    /// <returns>Execution result with output and errors</returns>
    private static async Task<PowerShellResult> RunAsync(string script)
    {
        using var ps = PowerShell.Create();
        ps.AddScript(script);

        var result = new PowerShellResult();

        try
        {
            var psOutput = await Task.Run(() => ps.Invoke<PSObject>());

            if (ps.HadErrors)
                foreach (var error in ps.Streams.Error)
                    result.Errors.Add(error);

            foreach (var item in psOutput) result.Output.Add(item);
        }
        catch (Exception ex)
        {
            // Log the exception details for debugging
            Debug.WriteLine($"Exception in RunAsync: {ex}");
            Debug.WriteLine($"PowerShell script: {script}");

            // Create ErrorRecord for exception
            var errorRecord = new ErrorRecord(ex, "PowerShellExecutionError", ErrorCategory.NotSpecified, null);
            result.Errors.Add(errorRecord);
        }

        return result;
    }

    /// <summary>
    ///     Asynchronous PowerShell script execution returning strings (compatible with DDAPage.ExecutePowerShellAsync)
    /// </summary>
    /// <param name="script">PowerShell script to execute</param>
    /// <returns>List of strings with results and errors</returns>
    public static async Task<List<string>> RunAsyncAsStrings(string script)
    {
        var logOutput = new List<string>();
        try
        {
            var result = await RunAsync(script);

            // Add execution results
            logOutput.AddRange(result.Output.Select(x => x.ToString()));

            // Add errors with "Error:" prefix
            if (result.Errors.Count > 0) logOutput.AddRange(result.Errors.Select(x => $"Error: {x}"));
        }
        catch (Exception ex)
        {
            logOutput.Add($"Error: {ex.Message}");
        }

        return logOutput;
    }

    /// <summary>
    ///     Synchronous PowerShell script execution with error handling
    /// </summary>
    /// <param name="script">PowerShell script to execute</param>
    /// <returns>Execution result with output and errors</returns>
    public static PowerShellResult RunWithErrors(string script)
    {
        using var ps = PowerShell.Create();
        ps.AddScript(script);

        var result = new PowerShellResult();

        try
        {
            var psOutput = ps.Invoke<PSObject>();

            if (ps.HadErrors)
                foreach (var error in ps.Streams.Error)
                    result.Errors.Add(error);

            foreach (var item in psOutput) result.Output.Add(item);
        }
        catch (Exception ex)
        {
            // Log the exception details for debugging
            Debug.WriteLine($"Exception in RunWithErrors: {ex}");
            Debug.WriteLine($"PowerShell script: {script}");

            // Create ErrorRecord for exception
            var errorRecord = new ErrorRecord(ex, "PowerShellExecutionError", ErrorCategory.NotSpecified, null);
            result.Errors.Add(errorRecord);
        }

        return result;
    }

    /// <summary>
    ///     Execute multiple PowerShell commands in a single session without returning results
    /// </summary>
    /// <param name="commands">Array of PowerShell commands to execute</param>
    public static void RunMultipleVoid(params string[] commands)
    {
        foreach (var command in commands)
        {
            using var ps = PowerShell.Create();
            ps.AddScript(command);
            ps.Invoke();
        }
    }

    public static CardExpander CardExpander1()
    {
        var cardExpander = new CardExpander
        {
            Margin = new Thickness(20, 5, 0, 0),
            ContentPadding = new Thickness(6)
        };

        return cardExpander;
    }

    public static CardExpander CardExpander2()
    {
        var cardExpander = new CardExpander
        {
            Margin = new Thickness(30, 5, 10, 0),
            ContentPadding = new Thickness(6)
        };

        return cardExpander;
    }

    private static string IconPath(string deviceType, string friendlyName)
    {
        return deviceType switch
        {
            "Display" => "\xF211" //  Կ ͼ   
            ,
            "Net" => "\xE839" //     ͼ  
            ,
            "USB" => friendlyName.Contains("USB4")
                ? "\xE945" //  ׵ ӿ ͼ  
                : "\xECF0" //   ͨUSBͼ  
            ,
            "HIDClass" => "\xE928" // HID 豸ͼ  
            ,
            "SCSIAdapter" or "HDC" => "\xEDA2" //  洢      ͼ  
            ,
            _ => friendlyName.Contains("Audio")
                ? "\xE995" //   Ƶ 豸ͼ  
                : "\xE950"
        };
    }

    public static FontIcon FontIcon1(string classType, string friendlyName)
    {
        var icon = new FontIcon
        {
            FontSize = 24,
            FontFamily = Application.Current.Resources["SegoeFluentIcons"] as FontFamily
                         ?? new FontFamily("Segoe Fluent Icons"),
            Glyph = IconPath(classType, friendlyName)
        };
        return icon;
    }

    public static TextBlock CreateHeaderTextBlock(string friendlyName)
    {
        var headerText = new TextBlock
        {
            Text = friendlyName,
            FontSize = 16,
            Margin = new Thickness(0, -2, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        return headerText;
    }

    public static DropDownButton DropDownButton1(string status)
    {
        var menu = new DropDownButton
        {
            Content = status,
            Margin = new Thickness(10, 0, 5, 0)
        };
        return menu;
    }

    public static TextBlock CreateCenteredTextBlock(string friendlyName)
    {
        var headerText = new TextBlock
        {
            Text = friendlyName,
            FontSize = 16,
            Margin = new Thickness(0, -2, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };
        return headerText;
    }

    public static TextBlock CreateGridTextBlock(string text, int row, int column)
    {
        var textBlock = new TextBlock
        {
            Text = text, FontSize = 16, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 10)
        };
        Grid.SetRow(textBlock, row);
        Grid.SetColumn(textBlock, column);

        return textBlock;
    }

    public static StackPanel CreateStackPanel(string text)
    {
        var stackPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0)
        };

        stackPanel.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 16,
            VerticalAlignment = VerticalAlignment.Center
        });

        return stackPanel;
    }

    public static string GpuImagePath(string manu, string name)
    {
        string imageName;

        if (manu.Contains("NVIDIA"))
        {
            imageName = "NVIDIA.png";
        }
        else if (manu.Contains("Advanced"))
        {
            imageName = "AMD.png";
        }
        else if (manu.Contains("Microsoft"))
        {
            imageName = "Microsoft.png";
        }
        else if (manu.Contains("Intel"))
        {
            imageName = "Intel.png";
            if (name.Contains("iris", StringComparison.OrdinalIgnoreCase)) imageName = "Intel_Iris_Xe_Graphics.png";
            if (name.Contains("arc", StringComparison.OrdinalIgnoreCase)) imageName = "ARC.png";
            if (name.Contains("data", StringComparison.OrdinalIgnoreCase))
                imageName = "data-center-gpu-flex-badge-centered-transparent-rwd_1920-1080.png";
        }
        else if (manu.Contains("Moore"))
        {
            imageName = "Moore.png";
        }
        else if (manu.Contains("Qualcomm"))
        {
            imageName = "Qualcomm.png";
        }
        else if (manu.Contains("DisplayLink"))
        {
            imageName = "DisplayLink.png";
        }
        else if (manu.Contains("Silicon"))
        {
            imageName = "Silicon.png";
        }
        else
        {
            imageName = "GPU.png";
        }

        return $"pack://application:,,,/Assets/Gpuicons/{imageName}";
    }

    public static Image CreateGpuImage(string key, string name, int size)
    {
        var image = new Image
        {
            Source = new BitmapImage(new Uri(GpuImagePath(key, name)))
            {
                CreateOptions = BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                CacheOption = BitmapCacheOption.OnLoad
            },
            Height = size,
            Width = size,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top
        };


        image.SetValue(RenderOptions.BitmapScalingModeProperty, BitmapScalingMode.HighQuality);
        image.SetValue(RenderOptions.EdgeModeProperty, EdgeMode.Aliased);

        return image;
    }

    public static FontIcon FontIcon(int size, string glyph)
    {
        var icon = new FontIcon
        {
            FontSize = size,
            FontFamily = Application.Current.Resources["SegoeFluentIcons"] as FontFamily
                         ?? new FontFamily("Segoe Fluent Icons"),
            Glyph = glyph
        };
        return icon;
    }

    public static DateTime LinkerTime()
    {
        var filePath = Assembly.GetExecutingAssembly().Location;
        var fileInfo = new FileInfo(filePath);
        var linkerTime = fileInfo.LastWriteTime;
        return linkerTime;
    }
}