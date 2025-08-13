using CommunityToolkit.Mvvm.ComponentModel;
using ExHyperV.Services;
using ExHyperV.Properties;
using System.Collections.Generic;
using System.Linq;

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

        // 关于信息
        public string AppVersion => "ExHyperV"; 
        public string CopyrightInfo => "© 2025 | Saniye";

        public SettingsViewModel()
        {
            // 1. 初始化数据源
            AvailableThemes = new List<string> { Resources.light, Resources.dark };
            AvailableLanguages = new List<string> { "中文", "English" };

            // 2. 从服务加载当前设置并应用到属性
            LoadCurrentSettings();

            // 3. 初始化完成，后续的属性更改将触发逻辑
            _isInitializing = false;
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