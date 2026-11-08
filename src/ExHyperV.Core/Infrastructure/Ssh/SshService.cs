using System.IO;
using System.Text;
using ExHyperV.Models;
using Renci.SshNet;

namespace ExHyperV.Services
{
    /// <summary>表示要求成功退出的 SSH 命令返回了非零退出状态。</summary>
    internal class SshCommandErrorException : Exception
    {
        /// <summary>创建包含远端命令失败说明的异常。</summary>
        /// <param name="message">命令退出状态及失败详情的说明。</param>
        public SshCommandErrorException(string message) : base(message) { }
    }

    /// <summary>保存 SSH 命令采集的输出文本及退出状态。</summary>
    internal class SshCommandResult
    {
        /// <summary>按读取顺序合并的标准输出和标准错误文本；两条流分别采集。</summary>
        public string Output { get; }
        /// <summary>远端命令退出码；零通常表示成功，未提供退出码时使用 -1。</summary>
        public int ExitStatus { get; }

        /// <summary>创建命令输出结果，不执行任何远端操作。</summary>
        /// <param name="output">采集到的输出文本。</param>
        /// <param name="exitStatus">远端退出码，未知时可为 -1。</param>
        public SshCommandResult(string output, int exitStatus)
        {
            Output = output;
            ExitStatus = exitStatus;
        }
    }

    /// <summary>通过密码认证执行 SSH 命令及 SFTP 上传；每次调用创建并释放连接。</summary>
    /// <remarks>连接默认超时 30 秒，命令默认超时 30 分钟；命令及文件权限由远端账户决定。命令以 sudo 开头时会用所提供密码执行提权包装。没有 CancellationToken 入口。</remarks>
    internal static class SshService
    {
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

        /// <summary>执行 SSH 命令并要求零退出码，同时转发输出片段。</summary>
        /// <returns>命令标准输出；非零退出时抛出 SshCommandErrorException，连接/认证异常可传播。</returns>
        /// <param name="credentials">远端主机、端口、用户名和密码。</param>
        /// <param name="command">要在远端执行的 shell 命令。</param>
        /// <param name="logCallback">接收输出文本片段；stdout/stderr 读取任务可并发调用，界面更新需调度到界面线程。</param>
        /// <param name="commandTimeout">命令执行超时；null 使用 30 分钟，与连接超时分开。</param>
        public static async Task<string> ExecuteSingleCommandAsync(SshCredentials credentials, string command, Action<string> logCallback, TimeSpan? commandTimeout = null)
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
                    string escapedPassword = credentials.Password.Replace("'", "'\\''");
                    commandToExecute = $"echo '{escapedPassword}' | sudo -S -p '' bash -c '{escapedCommand}'";
                }
                using var sshCommand = client.CreateCommand(commandToExecute);
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

        /// <summary>执行 SSH 命令，采集输出和退出码供调用方自行判断成功。</summary>
        /// <remarks>连接、认证、超时及回调异常仍可传播；输出回调必须支持并发调用。</remarks>
        /// <returns>包含标准输出/错误片段和退出码的结果；非零退出码本身不会触发 SshCommandErrorException。</returns>
        /// <param name="credentials">远端主机、端口、用户名和密码。</param>
        /// <param name="command">要在远端执行的 shell 命令。</param>
        /// <param name="logCallback">接收输出文本片段；stdout/stderr 读取任务可并发调用，界面更新需调度到界面线程。</param>
        /// <param name="commandTimeout">命令执行超时；null 使用 30 分钟，与连接超时分开。</param>
        public static async Task<SshCommandResult> ExecuteCommandAndCaptureOutputAsync(SshCredentials credentials, string command, Action<string> logCallback, TimeSpan? commandTimeout = null)
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
                    string escapedPassword = credentials.Password.Replace("'", "'\\''");
                    commandToExecute = $"echo '{escapedPassword}' | sudo -S -p '' bash -c '{escapedCommand}'";
                }

                using var sshCommand = client.CreateCommand(commandToExecute);
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
        
        private static async Task ReadStreamAsync(Stream stream, Encoding encoding, Action<string> logCallback)
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

        /// <summary>通过 SFTP 将本机文件上传到指定远端路径。</summary>
        /// <remarks>目标可能被覆盖；失败可能留下部分远端文件，不执行自动回滚。</remarks>
        /// <param name="credentials">远端主机、端口、用户名和密码。</param>
        /// <param name="localPath">本机待读取文件完整路径。</param>
        /// <param name="remotePath">远端目标文件路径。</param>
        public static Task UploadFileAsync(SshCredentials credentials, string localPath, string remotePath)
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
        /// <summary>递归上传本机目录内容，并在远端创建缺少的子目录。</summary>
        /// <remarks>已存在同名文件可能被覆盖；失败不会删除已上传内容。需要本地读取与远端创建/写入权限。</remarks>
        /// <param name="credentials">远端主机、端口、用户名和密码。</param>
        /// <param name="localDirectory">本机源目录。</param>
        /// <param name="remoteDirectory">远端目标目录。</param>
        public static Task UploadDirectoryAsync(SshCredentials credentials, string localDirectory, string remoteDirectory)
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
        private static void UploadDirectoryRecursive(SftpClient sftp, DirectoryInfo localDirectory, string remoteDirectory)
        {
            foreach (var file in localDirectory.GetFiles())
            {
                using (var fileStream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var remoteFilePath = $"{remoteDirectory}/{file.Name}";
                    sftp.UploadFile(fileStream, remoteFilePath);
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
    }
}
