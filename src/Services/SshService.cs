using ExHyperV.Models;
using Renci.SshNet;
using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ExHyperV.Services
{
    public class SshService
    {
        public Task UploadFileAsync(SshCredentials credentials, string localPath, string remotePath)
        {
            return Task.Run(() =>
            {
                var connectionInfo = new ConnectionInfo(credentials.Host, credentials.Username,
                    new PasswordAuthenticationMethod(credentials.Username, credentials.Password));

                using (var sftp = new SftpClient(connectionInfo))
                {
                    sftp.Connect();
                    using (var fileStream = new FileStream(localPath, FileMode.Open))
                    {
                        sftp.UploadFile(fileStream, remotePath);
                    }
                }
            });
        }
        public Task<string> ExecuteCommandWithSudoAsync(SshCredentials credentials, string command)
        {
            return Task.Run(() =>
            {
                var connectionInfo = new ConnectionInfo(credentials.Host, credentials.Username,
                    new PasswordAuthenticationMethod(credentials.Username, credentials.Password));

                using (var client = new SshClient(connectionInfo))
                {
                    client.Connect();
                    var shellStream = client.CreateShellStream("xterm", 80, 24, 800, 600, 1024);

                    var promptRegex = new Regex(@"\][#$>]"); 
                    string output = shellStream.Expect(promptRegex, TimeSpan.FromSeconds(10));
                    if (output == null)
                    {
                        throw new Exception("SSH shell prompt not found.");
                    }

                    shellStream.WriteLine(command);
                    var sb = new StringBuilder();
                    string line;
                    while ((line = shellStream.ReadLine(TimeSpan.FromSeconds(5))) != null)
                    {
                        sb.AppendLine(line);
                        if (line.Contains("[sudo] password for"))
                        {
                            shellStream.WriteLine(credentials.Password);
                        }
                        if (promptRegex.IsMatch(line))
                        {
                            break;
                        }
                    }
                    Thread.Sleep(500);
                    while (shellStream.DataAvailable)
                    {
                        sb.AppendLine(shellStream.Read());
                    }

                    return sb.ToString();
                }
            });
        }
    }
}