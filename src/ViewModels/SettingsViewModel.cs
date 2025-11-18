using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Properties;
using ExHyperV.Services;
using ExHyperV.Tools;

namespace ExHyperV.ViewModels
{
    public partial class SettingsViewModel : ObservableObject
    {
        private bool _isInitializing = true;

        // 可用主题列表
        [ObservableProperty]
        private List<string> _availableThemes;

        // 当前选中的主题
        [ObservableProperty]
        private string _selectedTheme;

        // 可用语言列表
        [ObservableProperty]
        private List<string> _availableLanguages;

        // 当前选中的语言
        [ObservableProperty]
        private string _selectedLanguage;

        [ObservableProperty] private string _updateStatusText;
        [ObservableProperty] private bool _isCheckingForUpdate;
        [ObservableProperty] private string _updateActionIcon;
        [ObservableProperty] private IRelayCommand _updateActionCommand;
        [ObservableProperty] private bool _isUpdateActionEnabled; // 新增：控制 Anchor 是否可点击

        private string _latestVersionTag;

        [RelayCommand]
        private async Task CheckForUpdateAsync()
        {
            // 1. 检查开始，设置初始状态
            IsCheckingForUpdate = true;
            IsUpdateActionEnabled = false;
            UpdateStatusText = "正在检查更新...";

            try
            {
                // 调用服务进行网络请求
                var result = await SettingsService.CheckForUpdateAsync(AppVersion);

                // 根据结果更新 UI 状态
                if (result.IsUpdateAvailable)
                {
                    // 发现新版本
                    UpdateStatusText = $"发现新版本: {result.LatestVersion}";
                    _updateActionIcon = "\uE71B"; // 外部链接图标
                    _updateActionCommand = GoToReleasePageCommand; // 整行点击行为变为“跳转”
                    _latestVersionTag = result.LatestVersion;
                }
                else
                {
                    // 已是最新版本
                    UpdateStatusText = "已是最新版本";
                    _updateActionIcon = "\uE73E"; // 对勾图标
                    _updateActionCommand = CheckForUpdateCommand; // 整行点击行为变为“再次检查”
                }
            }
            catch (Exception ex)
            {
                // 捕获到异常（例如网络错误）
                UpdateStatusText = ex.Message;
                _updateActionIcon = "\uE72C"; // 刷新图标
                _updateActionCommand = CheckForUpdateCommand; // 整行点击行为变为“重试”
            }
            finally
            {
                // 2. 检查结束，无论成功与否，都恢复最终状态
                //    这是让结果图标显示出来的关键
                IsCheckingForUpdate = false;
                IsUpdateActionEnabled = true; // 恢复可点击
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
            catch (Exception ex)
            {
                Debug.WriteLine($"无法打开链接: {ex.Message}");
            }
        }

        // 关于信息
        public string AppVersion
        {
            get
            {
                var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                // 只取前三位 Major.Minor.Build
                return version != null ? $"v{version.Major}.{version.Minor}.{version.Build}" : "vUnknown";
            }
        }


        public string CopyrightInfo => "© 2025 | Saniye | "+Utils.Version;

        // 构造函数现在非常简单，不需要任何参数
        public SettingsViewModel()
        {
            // 1. 初始化数据源 (这部分你已有了)
            AvailableThemes = new List<string> { Resources.light, Resources.dark };
            AvailableLanguages = new List<string> { "中文", "English" };

            // 2. 从服务加载当前设置 (这部分你已有了)
            LoadCurrentSettings();
            _isInitializing = false;

            // 3. 【新增】页面加载时自动开始检查更新 (无需等待)
            _ = CheckForUpdateCommand.ExecuteAsync(null);
        }



        private void LoadCurrentSettings()
        {
            // 直接设置字段，避免在初始化时触发OnChanged逻辑
            _selectedTheme = SettingsService.GetTheme();

            string langCode = SettingsService.GetLanguage();
            _selectedLanguage = langCode == "zh-CN" ? "中文" : "English";
        }

        // 当 SelectedTheme 属性发生变化时，此方法会被自动调用
        partial void OnSelectedThemeChanged(string value)
        {
            if (_isInitializing || value == null) return;
            SettingsService.ApplyTheme(value);
        }

        // 当 SelectedLanguage 属性发生变化时，此方法会被自动调用
        partial void OnSelectedLanguageChanged(string value)
        {
            if (_isInitializing || value == null) return;
            SettingsService.SetLanguageAndRestart(value);
        }
    }
}