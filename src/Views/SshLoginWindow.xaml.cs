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
            string title = string.Format(Properties.Resources.Title_ConnectingToVm, vmName);
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
                ErrorTextBlock.Text = ExHyperV.Properties.Resources.Validation_HostIpCannotBeEmpty;
                return;
            }

            if (!int.TryParse(PortTextBox.Text, out int sshPort) || sshPort <= 0 || sshPort > 65535)
            {
                ErrorTextBlock.Text = ExHyperV.Properties.Resources.Validation_InvalidSshPort;
                return;
            }

            if (string.IsNullOrWhiteSpace(UsernameTextBox.Text))
            {
                ErrorTextBlock.Text = ExHyperV.Properties.Resources.Validation_UsernameCannotBeEmpty;
                return;
            }

            string proxyHost = ProxyHostTextBox.Text.Trim();
            string proxyPortStr = ProxyPortTextBox.Text.Trim();
            int? proxyPort = null;

            if (!string.IsNullOrEmpty(proxyHost) || !string.IsNullOrEmpty(proxyPortStr))
            {
                if (string.IsNullOrEmpty(proxyHost) || string.IsNullOrEmpty(proxyPortStr))
                {
                    ErrorTextBlock.Text = ExHyperV.Properties.Resources.Validation_ProxyIpAndPortMismatch;
                    return;
                }

                if (int.TryParse(proxyPortStr, out int port) && port > 0 && port < 65536)
                {
                    proxyPort = port;
                }
                else
                {
                    ErrorTextBlock.Text = ExHyperV.Properties.Resources.Validation_InvalidProxyPort;
                    return;
                }
            }

            ConfirmButton.IsEnabled = false;
            ConfirmButton.Content = ExHyperV.Properties.Resources.Status_Connecting;

            Credentials.Host = HostTextBox.Text.Trim();
            Credentials.Port = sshPort;
            Credentials.Username = UsernameTextBox.Text.Trim();
            Credentials.Password = PasswordBox.Password;
            Credentials.ProxyHost = proxyHost;
            Credentials.ProxyPort = proxyPort;
            Credentials.InstallGraphics = GraphicsCheckBox.IsChecked ?? false;

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
                ErrorTextBlock.Text = string.Format(Properties.Resources.Error_ConnectionFailed, Credentials.Host, Credentials.Port);
            }
            catch (Renci.SshNet.Common.SshOperationTimeoutException)
            {
                ErrorTextBlock.Text = string.Format(Properties.Resources.Error_ConnectionTimedOut, Credentials.Host, Credentials.Port);
            }
            catch (Renci.SshNet.Common.SshAuthenticationException)
            {
                ErrorTextBlock.Text = ExHyperV.Properties.Resources.Error_AuthenticationFailed;
            }
            catch (Exception ex)
            {
                ErrorTextBlock.Text = string.Format(Properties.Resources.Error_UnknownConnectionError, ex.Message);
            }
            finally
            {
                ConfirmButton.IsEnabled = true;
                ConfirmButton.Content = Properties.Resources.Button_Connect;
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}