using System.IO;
using System.Text;
using ExHyperV.Models;
using Renci.SshNet;

namespace ExHyperV.Services
{
    public class SshCommandErrorException : Exception
    {
        public SshCommandErrorException(string message) : base(message) { }
    }

    public class SshCommandResult
    {
        public string Output { get; }
        public int ExitStatus { get; }

        public SshCommandResult(string output, int exitStatus)
        {
            Output = output;
            ExitStatus = exitStatus;
        }
    }

    public class SshService
    {
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
        private static ConnectionInfo CreateConnectionInfo(SshCredentials credentials, TimeSpan? timeout = null)
        {
            return new ConnectionInfo(
                credentials.Host,
                credentials.Port,
                credentials.Username,
                new PasswordAuthenticationMethod(credentials.Username, credentials.Password))
            {
                Timeout = timeout ?? TimeSpan.FromSeconds(30)
            };
        }

        public async Task<string> ExecuteSingleCommandAsync(SshCredentials credentials, string command, Action<string> logCallback, TimeSpan? commandTimeout = null)
        {
            string commandToExecute = command;

            var connectionInfo = CreateConnectionInfo(credentials);

            using (var client = new SshClient(connectionInfo))
            {
                await Task.Run(() => client.Connect());

                if (command.Trim().StartsWith("sudo"))
                {
                    string actualCommand = command.Substring(5).Trim(); 
                    string escapedCommand = actualCommand.Replace("'", "'\\''");
                    commandToExecute = $"echo '{credentials.Password}' | sudo -S -p '' bash -c '{escapedCommand}'";
                }
                var sshCommand = client.CreateCommand(commandToExecute);
                sshCommand.CommandTimeout = commandTimeout ?? TimeSpan.FromMinutes(30);

                var asyncResult = sshCommand.BeginExecute();
                var stdoutTask = ReadStreamAsync(sshCommand.OutputStream, Encoding.UTF8, logCallback);
                var stderrTask = ReadStreamAsync(sshCommand.ExtendedOutputStream, Encoding.UTF8, logCallback);
                await Task.Run(() => sshCommand.EndExecute(asyncResult));
                await Task.WhenAll(stdoutTask, stderrTask);

                client.Disconnect();

                if (sshCommand.ExitStatus != 0)
                {
                    throw new SshCommandErrorException(string.Format(Properties.Resources.Error_SshCommandFailed, sshCommand.ExitStatus, sshCommand.Error));
                }
                return sshCommand.Result;
            }
        }

        public async Task<SshCommandResult> ExecuteCommandAndCaptureOutputAsync(SshCredentials credentials, string command, Action<string> logCallback, TimeSpan? commandTimeout = null)
        {
            string commandToExecute = command;
            var outputBuilder = new StringBuilder();

            Action<string> combinedLogCallback = (log) =>
            {
                logCallback(log);
                outputBuilder.Append(log);
            };

            var connectionInfo = CreateConnectionInfo(credentials);

            using (var client = new SshClient(connectionInfo))
            {
                await Task.Run(() => client.Connect());

                if (command.Trim().StartsWith("sudo"))
                {
                    string actualCommand = command.Substring(5).Trim();
                    string escapedCommand = actualCommand.Replace("'", "'\\''");
                    commandToExecute = $"echo '{credentials.Password}' | sudo -S -p '' bash -c '{escapedCommand}'";
                }

                var sshCommand = client.CreateCommand(commandToExecute);
                sshCommand.CommandTimeout = commandTimeout ?? TimeSpan.FromMinutes(30);

                var asyncResult = sshCommand.BeginExecute();
                var stdoutTask = ReadStreamAsync(sshCommand.OutputStream, Encoding.UTF8, combinedLogCallback);
                var stderrTask = ReadStreamAsync(sshCommand.ExtendedOutputStream, Encoding.UTF8, combinedLogCallback);

                await Task.Run(() => sshCommand.EndExecute(asyncResult));
                await Task.WhenAll(stdoutTask, stderrTask);

                client.Disconnect();
                return new SshCommandResult(outputBuilder.ToString(), sshCommand.ExitStatus ?? -1);
            }
        }
        
        private async Task ReadStreamAsync(Stream stream, Encoding encoding, Action<string> logCallback)
        {
            var buffer = new byte[1024];
            int bytesRead;
            try
            {
                while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    logCallback(encoding.GetString(buffer, 0, bytesRead));
                }
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public Task UploadFileAsync(SshCredentials credentials, string localPath, string remotePath)
        {
            return Task.Run(() =>
            {
                var connectionInfo = CreateConnectionInfo(credentials);

                using (var sftp = new SftpClient(connectionInfo))
                {
                    sftp.Connect();
                    using (var fileStream = new FileStream(localPath, FileMode.Open, FileAccess.Read))
                    {
                        sftp.UploadFile(fileStream, remotePath);
                    }
                    sftp.Disconnect();
                }
            });
        }
        public Task UploadDirectoryAsync(SshCredentials credentials, string localDirectory, string remoteDirectory)
        {
            return Task.Run(() =>
            {
                var connectionInfo = CreateConnectionInfo(credentials);

                using (var sftp = new SftpClient(connectionInfo))
                {
                    sftp.Connect();
                    var dirInfo = new DirectoryInfo(localDirectory);
                    if (!dirInfo.Exists)
                    {
                        throw new DirectoryNotFoundException(string.Format(Properties.Resources.Error_LocalDirectoryNotFound, localDirectory));
                    }
                    if (!sftp.Exists(remoteDirectory))
                    {
                        sftp.CreateDirectory(remoteDirectory);
                    }

                    UploadDirectoryRecursive(sftp, dirInfo, remoteDirectory);
                    sftp.Disconnect();
                }
            });
        }
        private void UploadDirectoryRecursive(SftpClient sftp, DirectoryInfo localDirectory, string remoteDirectory)
        {
            foreach (var file in localDirectory.GetFiles())
            {
                if (file.Extension.Equals(".log", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                try
                {
                    using (var fileStream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        var remoteFilePath = $"{remoteDirectory}/{file.Name}";
                        sftp.UploadFile(fileStream, remoteFilePath);
                    }
                }
                catch (IOException)
                {

                    continue;
                }
            }
            foreach (var subDir in localDirectory.GetDirectories())
            {
                var remoteSubDir = $"{remoteDirectory}/{subDir.Name}";
                if (!sftp.Exists(remoteSubDir))
                {
                    sftp.CreateDirectory(remoteSubDir);
                }
                UploadDirectoryRecursive(sftp, subDir, remoteSubDir);
            }
        }
        public Task WriteTextFileAsync(SshCredentials credentials, string content, string remotePath)
        {
            return Task.Run(() =>
            {
                var connectionInfo = CreateConnectionInfo(credentials);

                using (var sftp = new SftpClient(connectionInfo))
                {
                    sftp.Connect();
                    sftp.WriteAllText(remotePath, content, Utf8NoBom);
                    sftp.Disconnect();
                }
            });
        }
    }
}
