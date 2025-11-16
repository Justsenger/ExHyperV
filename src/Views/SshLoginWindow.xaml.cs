// ExHyperV/Views/SshLoginWindow.xaml.cs

using ExHyperV.Models;
using ExHyperV.Services;
using System;
using System.Windows;
using Wpf.Ui.Controls;

namespace ExHyperV.Views
{
    public partial class SshLoginWindow : FluentWindow
    {
        public SshCredentials Credentials { get; private set; }
        private readonly SshService _sshService;
        private readonly string _hostIpAddress; // 用于在内部存储自动获取的IP地址

        /// <summary>
        /// 默认构造函数，供设计器和链式调用使用。
        /// </summary>
        public SshLoginWindow()
        {
            InitializeComponent();
            _sshService = new SshService();
            Credentials = new SshCredentials();
            UsernameTextBox.Text = "root"; // 设置默认用户名
        }

        /// <summary>
        /// 用于自动化流程的构造函数，接收虚拟机名和IP地址。
        /// </summary>
        /// <param name="vmName">虚拟机的名称。</param>
        /// <param name="ipAddress">自动获取到的虚拟机IP地址。</param>
        public SshLoginWindow(string vmName, string ipAddress) : this()
        {
            _hostIpAddress = ipAddress; // 在私有字段中保存IP

            // 动态设置窗口内的可见标题，包含虚拟机名和IP
            TitleTextBlock.Text = $"连接到 {vmName} ({_hostIpAddress})";

            // (可选) 设置窗口的后台Title属性，可能用于Windows任务栏等
            this.Title = $"连接到 {vmName}";
        }

        private async void ConfirmButton_Click(object sender, RoutedEventArgs e)
        {
            ErrorTextBlock.Text = string.Empty;

            // 由于IP地址是自动传入的，我们只需要验证用户名
            if (string.IsNullOrWhiteSpace(UsernameTextBox.Text))
            {
                ErrorTextBlock.Text = "用户名不能为空。";
                return;
            }

            // 收集代理信息
            string proxyHost = ProxyHostTextBox.Text.Trim();
            string proxyPortStr = ProxyPortTextBox.Text.Trim();
            int? proxyPort = null;

            if (!string.IsNullOrEmpty(proxyHost) || !string.IsNullOrEmpty(proxyPortStr))
            {
                if (string.IsNullOrEmpty(proxyHost) || string.IsNullOrEmpty(proxyPortStr))
                {
                    ErrorTextBlock.Text = "代理 IP 和端口必须同时填写或同时为空。";
                    return;
                }
                if (int.TryParse(proxyPortStr, out int port) && port > 0 && port < 65536)
                {
                    proxyPort = port;
                }
                else
                {
                    ErrorTextBlock.Text = "代理端口号无效 (应为 1-65535)。";
                    return;
                }
            }

            ConfirmButton.IsEnabled = false;
            ConfirmButton.Content = "正在连接...";

            // 从私有字段获取IP地址来构建凭据对象
            Credentials.Host = _hostIpAddress;
            Credentials.Username = UsernameTextBox.Text.Trim();
            Credentials.Password = PasswordBox.Password;
            Credentials.ProxyHost = proxyHost;
            Credentials.ProxyPort = proxyPort;

            try
            {
                // 测试连接
                await _sshService.ExecuteSingleCommandAsync(Credentials, "echo 'Connection test successful'", (log) => { }, TimeSpan.FromSeconds(15));
                this.DialogResult = true;
                this.Close();
            }
            catch (Exception ex)
            {
                // 使用最终确定的错误提示
                ErrorTextBlock.Text = $"连接失败: {ex.Message}";
            }
            finally
            {
                ConfirmButton.IsEnabled = true;
                ConfirmButton.Content = "连接";
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = false;
            this.Close();
        }
    }
}