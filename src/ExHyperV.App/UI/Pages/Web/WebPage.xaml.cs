
using ExHyperV.App.Localization;
using Microsoft.UI.Xaml.Controls;

namespace ExHyperV.App.UI.Pages.Web;

public sealed partial class WebPage : Page
{
    public WebPage()
    {
        InitializeComponent();
        ApplyLocalization();
    }

    internal void ApplyLocalization()
    {
        TitleHeader.Text = L10n.Get("Web_Title");
    }
}
