using ExHyperV.Models;
using ExHyperV.Services; // 需要引用 SshService
using System;
using System.Windows;
using Wpf.Ui.Controls;

namespace ExHyperV.Views
{
    public partial class SshLoginWindow : FluentWindow
    {
        public SshCredentials Credentials { get; private set; }
        private readonly SshService _sshService;

        public SshLoginWindow()
        {
            InitializeComponent();
            _sshService = new SshService();
            Credentials = new SshCredentials();
            UsernameTextBox.Text = "root"; // 设置默认用户名
        }

        private async void ConfirmButton_Click(object sender, RoutedEventArgs e)
        {
            ErrorTextBlock.Text = string.Empty;

            if (string.IsNullOrWhiteSpace(HostTextBox.Text) || string.IsNullOrWhiteSpace(UsernameTextBox.Text))
            {
                ErrorTextBlock.Text = "IP地址和用户名不能为空。";
                return;
            }

            // 收集代理信息
            string proxyHost = ProxyHostTextBox.Text.Trim();
            string proxyPortStr = ProxyPortTextBox.Text.Trim();
            int? proxyPort = null;

            if (!string.IsNullOrEmpty(proxyHost) && !string.IsNullOrEmpty(proxyPortStr))
            {
                if (int.TryParse(proxyPortStr, out int port) && port > 0 && port < 65536)
                {
                    proxyPort = port;
                }
                else
                {
                    ErrorTextBlock.Text = "代理端口号无效。";
                    return;
                }
            }
            else if (!string.IsNullOrEmpty(proxyHost) || !string.IsNullOrEmpty(proxyPortStr))
            {
                ErrorTextBlock.Text = "代理 IP 和端口必须同时填写或同时为空。";
                return;
            }

            ConfirmButton.IsEnabled = false;
            ConfirmButton.Content = "正在连接...";

            Credentials.Host = HostTextBox.Text.Trim();
            Credentials.Username = UsernameTextBox.Text.Trim();
            Credentials.Password = PasswordBox.Password;
            Credentials.ProxyHost = proxyHost;
            Credentials.ProxyPort = proxyPort;

            try
            {
                // 测试连接（可以执行一个简单的 'echo' 或 'pwd'）
                await _sshService.ExecuteSingleCommandAsync(Credentials, "echo 'Connection test successful'", (log) => { }, TimeSpan.FromSeconds(15));
                this.DialogResult = true;
                this.Close();
            }
            catch (Exception ex)
            {
                ErrorTextBlock.Text = $"连接测试失败: {ex.Message}";
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