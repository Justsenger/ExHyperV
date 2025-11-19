using ExHyperV.Models;
using System.Windows;
using Wpf.Ui.Controls;
using Renci.SshNet;

namespace ExHyperV.Views
{
    public partial class SshLoginWindow : FluentWindow
    {
        public SshCredentials Credentials { get; private set; }

        public SshLoginWindow()
        {
            InitializeComponent();
            Credentials = new SshCredentials();
            UsernameTextBox.Text = "root";
            PortTextBox.Text = "22";
        }

        public SshLoginWindow(string vmName, string ipAddress) : this()
        {
            string title = $"连接到 {vmName}";
            TitleTextBlock.Text = title;
            Title = title;

            if (!string.IsNullOrWhiteSpace(ipAddress))
            {
                HostTextBox.Text = ipAddress;
            }
            else
            {
                Loaded += (s, e) => HostTextBox.Focus();
            }
        }

        private async void ConfirmButton_Click(object sender, RoutedEventArgs e)
        {
            ErrorTextBlock.Text = string.Empty;

            if (string.IsNullOrWhiteSpace(HostTextBox.Text))
            {
                ErrorTextBlock.Text = "主机 IP 地址不能为空。";
                return;
            }

            if (!int.TryParse(PortTextBox.Text, out int sshPort) || sshPort <= 0 || sshPort > 65535)
            {
                ErrorTextBlock.Text = "SSH 端口无效 (应为 1-65535)。";
                return;
            }

            if (string.IsNullOrWhiteSpace(UsernameTextBox.Text))
            {
                ErrorTextBlock.Text = "用户名不能为空。";
                return;
            }

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
            ConfirmButton.Content = "正在验证连接...";

            Credentials.Host = HostTextBox.Text.Trim();
            Credentials.Port = sshPort;
            Credentials.Username = UsernameTextBox.Text.Trim();
            Credentials.Password = PasswordBox.Password;
            Credentials.ProxyHost = proxyHost;
            Credentials.ProxyPort = proxyPort;

            try
            {
                await Task.Run(() =>
                {
                    using var client = new SshClient(Credentials.Host, Credentials.Port, Credentials.Username, Credentials.Password);
                    client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(5);
                    client.Connect();
                    client.Disconnect();
                });

                DialogResult = true;
                Close();
            }
            catch (System.Net.Sockets.SocketException)
            {
                ErrorTextBlock.Text = $"连接失败: 无法连接到主机 {Credentials.Host} 的端口 {Credentials.Port}。\n请检查 IP 和端口是否正确。";
            }
            catch (Renci.SshNet.Common.SshOperationTimeoutException)
            {
                ErrorTextBlock.Text = $"连接超时: 无法连接到主机 {Credentials.Host} 的端口 {Credentials.Port}。";
            }
            catch (Renci.SshNet.Common.SshAuthenticationException)
            {
                ErrorTextBlock.Text = "身份验证失败: 用户名或口令不正确。";
            }
            catch (Exception ex)
            {
                ErrorTextBlock.Text = $"连接发生未知错误: {ex.Message}";
            }
            finally
            {
                ConfirmButton.IsEnabled = true;
                ConfirmButton.Content = "连接";
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}