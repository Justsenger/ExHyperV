using System.Configuration;
using System.Data;
using System.Globalization;
using System.Windows;

namespace ExHyperV
{
    public partial class App : Application
    {
        // 在启动时设置语言
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 设置语言为中文
            SetLanguage("zh-CN");  // 设置为中文（简体）
            //SetLanguage("en-US");  // 设置为英文

        }

        // 设置当前线程的文化信息
        private void SetLanguage(string cultureCode)
        {
            CultureInfo culture = new CultureInfo(cultureCode);
            Thread.CurrentThread.CurrentCulture = culture;
            Thread.CurrentThread.CurrentUICulture = culture;
        }
    }
}
