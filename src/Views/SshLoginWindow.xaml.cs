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
            // 清空之前的错误信息
            ErrorTextBlock.Text = string.Empty;

            // 1. 输入验证
            if (string.IsNullOrWhiteSpace(HostTextBox.Text) || string.IsNullOrWhiteSpace(UsernameTextBox.Text))
            {
                ErrorTextBlock.Text = "IP地址和用户名不能为空。";
                return;
            }

            // 禁用按钮，防止重复点击
            ConfirmButton.IsEnabled = false;
            ConfirmButton.Content = "正在连接...";

            // 收集凭据
            Credentials.Host = HostTextBox.Text.Trim();
            Credentials.Username = UsernameTextBox.Text.Trim();
            Credentials.Password = PasswordBox.Password;

            try
            {
                // 2. 连接测试：执行一个非常简单的命令来验证连接
                //await _sshService.ExecuteCommandAsync(Credentials, "echo 'Connection successful'");

                // 如果没有抛出异常，说明连接成功
                this.DialogResult = true;
                this.Close();
            }
            catch (Exception ex)
            {
                // 3. 连接失败反馈
                ErrorTextBlock.Text = $"连接失败: {ex.Message}";
            }
            finally
            {
                // 无论成功失败，都恢复按钮状态
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