using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Interaction;
using ExHyperV.Services;

namespace ExHyperV.ViewModels
{
    public partial class SettingsPageViewModel : PageViewModelBase
    {
        private bool _isInitializing = true;

        // ===== 属性 =====

        [ObservableProperty] private List<string> _availableThemes;
        [ObservableProperty] private string _selectedTheme = string.Empty;
        [ObservableProperty] private List<string> _availableLanguages;
        [ObservableProperty] private string _selectedLanguage = string.Empty;
        [ObservableProperty] private bool _isPerformanceMode;

        [ObservableProperty] private string _updateStatusText = string.Empty;
        [ObservableProperty] private bool _isCheckingForUpdate;
        [ObservableProperty] private string _updateActionIcon = string.Empty;
        [ObservableProperty] private IRelayCommand _updateActionCommand;
        [ObservableProperty] private bool _isUpdateActionEnabled;
        private string _latestVersionTag = string.Empty;

        [ObservableProperty]
        private bool _showUpdateIndicator;


        // ===== 命令 =====

        [RelayCommand]
        private async Task CheckForUpdateAsync()
        {
            IsCheckingForUpdate = true;
            IsUpdateActionEnabled = false;
            ShowUpdateIndicator = false;
            UpdateStatusText = Properties.Resources.Status_CheckingForUpdates;

            try
            {
                var result = await SettingsService.CheckForUpdateAsync(AppInfoService.Version);

                if (result.IsUpdateAvailable)
                {
                    UpdateStatusText = string.Format(Properties.Resources.Info_NewVersionFound, result.LatestVersion);
                    UpdateActionIcon = "\uE71B";
                    UpdateActionCommand = GoToReleasePageCommand;
                    _latestVersionTag = result.LatestVersion;
                    ShowUpdateIndicator = true;
                }
                else if (result.IsInnerTest) // 直接在这里合并判断
                {
                    UpdateStatusText = Properties.Resources.Label_Beta;
                    UpdateActionIcon = "\uF196"; // 实验室/烧瓶图标
                    UpdateActionCommand = CheckForUpdateCommand;
                }
                else
                {
                    UpdateStatusText = Properties.Resources.Info_AlreadyLatestVersion;
                    UpdateActionIcon = "\uE73E";
                    UpdateActionCommand = CheckForUpdateCommand;
                }
            }
            catch (Exception ex)
            {
                UpdateStatusText = ex.Message;
                UpdateActionIcon = "\uE72C";
                UpdateActionCommand = CheckForUpdateCommand;
            }
            finally
            {
                IsCheckingForUpdate = false;
                IsUpdateActionEnabled = true;
            }
        }
        [RelayCommand]
        private void GoToReleasePage()
        {
            if (string.IsNullOrEmpty(_latestVersionTag)) return;

            var url = $"https://github.com/Justsenger/ExHyperV/releases/tag/{_latestVersionTag}";
            Shell.OpenUrl(url);
        }
        public string CopyrightInfo => "© 2026 | " + AppInfoService.Author+ " | " + AppInfoService.Version;

        // ===== 构造 =====

        public SettingsPageViewModel()
        {
            AvailableThemes = new List<string>
            {
                Properties.Resources.Theme_System,
                Properties.Resources.Theme_Light,
                Properties.Resources.Theme_Dark
            };
            AvailableLanguages = new List<string> { Properties.Resources.Lang_Chinese, "English" };

            LoadCurrentSettings();
            _isInitializing = false;

            UpdateActionCommand = CheckForUpdateCommand;
            _ = CheckForUpdateCommand.ExecuteAsync(null);
        }

        // ===== 内部方法 =====

        private void LoadCurrentSettings()
        {
            SelectedTheme = SettingsService.GetTheme();
            string langCode = SettingsService.GetLanguage();
            SelectedLanguage = langCode == "zh-CN" ? Properties.Resources.Lang_Chinese : "English";
            IsPerformanceMode = SettingsService.GetPerformanceMode();
        }

        partial void OnSelectedThemeChanged(string value)
        {
            if (_isInitializing || value == null) return;
            var mainWindow = System.Windows.Application.Current?.MainWindow;
            SettingsService.ApplyTheme(value, mainWindow);
        }

        partial void OnSelectedLanguageChanged(string value)
        {
            if (_isInitializing || value == null) return;
            SettingsService.SetLanguageAndRestart(value);
        }

        partial void OnIsPerformanceModeChanged(bool value)
        {
            if (_isInitializing) return;
            SettingsService.SavePerformanceMode(value);
            SettingsService.RestartApp();
        }
    }
}