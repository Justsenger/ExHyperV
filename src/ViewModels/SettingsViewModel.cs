using System.Diagnostics;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Properties;
using ExHyperV.Services;
using ExHyperV.Tools;
using System.Threading.Tasks;
using System;

namespace ExHyperV.ViewModels
{
    public partial class SettingsViewModel : ObservableObject
    {
        private bool _isInitializing = true;

        [ObservableProperty] private List<string> _availableThemes;
        [ObservableProperty] private string _selectedTheme;
        [ObservableProperty] private List<string> _availableLanguages;
        [ObservableProperty] private string _selectedLanguage;

        [ObservableProperty] private string _updateStatusText;
        [ObservableProperty] private bool _isCheckingForUpdate;
        [ObservableProperty] private string _updateActionIcon;
        [ObservableProperty] private IRelayCommand _updateActionCommand;
        [ObservableProperty] private bool _isUpdateActionEnabled;
        private string _latestVersionTag;

        [ObservableProperty]
        private bool _showUpdateIndicator;


        [RelayCommand]
        private async Task CheckForUpdateAsync()
        {
            IsCheckingForUpdate = true;
            IsUpdateActionEnabled = false;
            ShowUpdateIndicator = false;
            UpdateStatusText = ExHyperV.Properties.Resources.Status_CheckingForUpdates;

            try
            {
                var result = await SettingsService.CheckForUpdateAsync(Utils.Version);

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
                    UpdateStatusText = Properties.Resources.Label_Beta; // 或者从资源文件读
                    UpdateActionIcon = "\uF196"; // 实验室/烧瓶图标
                    UpdateActionCommand = CheckForUpdateCommand;
                }
                else
                {
                    UpdateStatusText = ExHyperV.Properties.Resources.Info_AlreadyLatestVersion;
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

            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch{}
        }
        public string CopyrightInfo => "© 2026 | " + Utils.Author+ " | " + Utils.Version;

        public SettingsViewModel()
        {
            AvailableThemes = new List<string> { Resources.light, Resources.dark };
            AvailableLanguages = new List<string> { Properties.Resources.Lang_Chinese, "English" };

            LoadCurrentSettings();
            _isInitializing = false;

            UpdateActionCommand = CheckForUpdateCommand;
            _ = CheckForUpdateCommand.ExecuteAsync(null);
        }

        private void LoadCurrentSettings()
        {
            _selectedTheme = SettingsService.GetTheme();
            string langCode = SettingsService.GetLanguage();
            _selectedLanguage = langCode == "zh-CN" ? Properties.Resources.Lang_Chinese : "English";
        }

        partial void OnSelectedThemeChanged(string value)
        {
            if (_isInitializing || value == null) return;
            SettingsService.ApplyTheme(value);
        }

        partial void OnSelectedLanguageChanged(string value)
        {
            if (_isInitializing || value == null) return;
            SettingsService.SetLanguageAndRestart(value);
        }
    }
}