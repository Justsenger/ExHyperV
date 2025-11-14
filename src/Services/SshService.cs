using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ExHyperV.Models;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace ExHyperV.Services
{
    /// <summary>
    /// 当 SSH 命令执行失败（退出码非0）时抛出的异常。
    /// </summary>
    public class SshCommandErrorException : Exception
    {
        public SshCommandErrorException(string message) : base(message) { }
    }





    /// <summary>
    /// 提供通过 SSH 和 SFTP 与远程 Linux 主机交互的服务。
    /// </summary>
    public class SshService
    {

        /// <summary>
        /// （新）一次只执行一条命令，并返回完整输出。自动处理 sudo。
        /// </summary>
        // 在 SshService.cs 中
        // 在 SshService.cs 中

        /// <summary>
        /// 一次只执行一条命令，并返回完整输出。自动处理 sudo，并支持自定义超时。
        /// </summary>
        /// <param name="credentials">SSH 连接凭据。</param>
        /// <param name="command">要执行的命令。</param>
        /// <param name="logCallback">用于回传实时日志的委托。</param>
        /// <param name="commandTimeout">为该特定命令设置的超时时间。如果为 null，则使用默认的30分钟长超时。</param>
        /// <returns>命令执行的完整输出。</returns>
        /// <exception cref="SshCommandErrorException">当命令返回非零退出码时抛出。</exception>
        /// <exception cref="SshOperationTimeoutException">当命令执行超时时抛出。</exception>
        // 在 SshService.cs 中

        /// <summary>
        /// (最终版) 一次只执行一条命令，并返回完整输出。自动处理 sudo，支持自定义超时，并能实时处理所有类型的流输出（包括进度条）。
        /// </summary>
        public async Task<string> ExecuteSingleCommandAsync(SshCredentials credentials, string command, Action<string> logCallback, TimeSpan? commandTimeout = null)
        {
            var connectionInfo = new ConnectionInfo(credentials.Host, credentials.Username,
                new PasswordAuthenticationMethod(credentials.Username, credentials.Password))
            {
                Timeout = TimeSpan.FromSeconds(30) // 连接超时
            };

            using (var client = new SshClient(connectionInfo))
            {
                await Task.Run(() => client.Connect());

                if (command.Trim().StartsWith("sudo"))
                {
                    command = $"echo '{credentials.Password}' | sudo -S -p '' bash -c '{command.Replace("sudo ", "").Replace("'", "'\"'\"'")}'";
                }

                logCallback($"\n>>> Executing: {command}\n");

                var sshCommand = client.CreateCommand(command);
                sshCommand.CommandTimeout = commandTimeout ?? TimeSpan.FromMinutes(30);

                var asyncResult = sshCommand.BeginExecute();

                // **关键修改**: 使用 ReadAsync 的辅助方法来处理流
                var stdoutTask = ReadStreamAsync(sshCommand.OutputStream, Encoding.UTF8, logCallback);
                var stderrTask = ReadStreamAsync(sshCommand.ExtendedOutputStream, Encoding.UTF8, logCallback);

                // 等待命令执行完成
                await Task.Run(() => sshCommand.EndExecute(asyncResult));

                // 等待流读取任务完成，确保捕获所有缓冲数据
                await Task.WhenAll(stdoutTask, stderrTask);

                client.Disconnect();

                if (sshCommand.ExitStatus != 0)
                {
                    throw new SshCommandErrorException($"命令执行失败 (Exit code: {sshCommand.ExitStatus}): {sshCommand.Error}");
                }

                return sshCommand.Result;
            }
        }

        /// <summary>
        /// 辅助方法：异步地从流中读取字节并调用回调。不依赖换行符。
        /// </summary>
        private async Task ReadStreamAsync(Stream stream, Encoding encoding, Action<string> logCallback)
        {
            var buffer = new byte[1024];
            int bytesRead;
            try
            {
                // 只要流能读，就一直读
                while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    logCallback(encoding.GetString(buffer, 0, bytesRead));
                }
            }
            catch (ObjectDisposedException)
            {
                // 当命令结束，流被关闭时，ReadAsync 可能会抛出此异常，这是正常现象，忽略即可。
            }
        }

        public Task UploadFileAsync(SshCredentials credentials, string localPath, string remotePath)
        {
            return Task.Run(() =>
            {
                var connectionInfo = new ConnectionInfo(credentials.Host, credentials.Username,
                    new PasswordAuthenticationMethod(credentials.Username, credentials.Password));

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

        // 在 SshService.cs 中

        /// <summary>
        /// 异步上传整个目录（包含所有子目录和文件）到远程主机。
        /// </summary>
        public Task UploadDirectoryAsync(SshCredentials credentials, string localDirectory, string remoteDirectory)
        {
            return Task.Run(() =>
            {
                var connectionInfo = new ConnectionInfo(credentials.Host, credentials.Username,
                    new PasswordAuthenticationMethod(credentials.Username, credentials.Password));

                using (var sftp = new SftpClient(connectionInfo))
                {
                    sftp.Connect();
                    var dirInfo = new DirectoryInfo(localDirectory);
                    if (!dirInfo.Exists)
                    {
                        throw new DirectoryNotFoundException($"本地目录未找到: {localDirectory}");
                    }

                    // 确保远程根目录存在
                    if (!sftp.Exists(remoteDirectory))
                    {
                        sftp.CreateDirectory(remoteDirectory);
                    }

                    UploadDirectoryRecursive(sftp, dirInfo, remoteDirectory);
                    sftp.Disconnect();
                }
            });
        }

        // *** 这是被修正的辅助方法 ***
        private void UploadDirectoryRecursive(SftpClient sftp, DirectoryInfo localDirectory, string remoteDirectory)
        {
            // 上传当前目录下的所有文件
            foreach (var file in localDirectory.GetFiles())
            {
                using (var fileStream = file.OpenRead())
                {
                    var remoteFilePath = $"{remoteDirectory}/{file.Name}";
                    sftp.UploadFile(fileStream, remoteFilePath);
                }
            }

            // 遍历所有子目录
            foreach (var subDir in localDirectory.GetDirectories())
            {
                // **关键修复**: 在递归调用之前，先在远程创建对应的子目录
                var remoteSubDir = $"{remoteDirectory}/{subDir.Name}";
                if (!sftp.Exists(remoteSubDir))
                {
                    sftp.CreateDirectory(remoteSubDir);
                }
                // 然后再递归进入这个子目录进行上传
                UploadDirectoryRecursive(sftp, subDir, remoteSubDir);
            }
        }

        /// <summary>
        /// 将字符串内容异步写入到远程文件。如果文件已存在，则覆盖。
        /// </summary>
        /// <param name="credentials">SSH 连接凭据。</param>
        /// <param name="content">要写入的文本内容。</param>
        /// <param name="remotePath">远程文件路径（包含文件名）。</param>
        public Task WriteTextFileAsync(SshCredentials credentials, string content, string remotePath)
        {
            return Task.Run(() =>
            {
                var connectionInfo = new ConnectionInfo(credentials.Host, credentials.Username,
                    new PasswordAuthenticationMethod(credentials.Username, credentials.Password));

                using (var sftp = new SftpClient(connectionInfo))
                {
                    sftp.Connect();
                    sftp.WriteAllText(remotePath, content, Encoding.UTF8);
                    sftp.Disconnect();
                }
            });
        }

        /// <summary>
        /// 使用 sudo 权限异步执行命令，并等待其完成。能够自动处理 sudo 密码提示。
        /// </summary>
        /// <param name="credentials">SSH 连接凭据。</param>
        /// <param name="command">要执行的命令（不包含 sudo）。</param>
        /// <returns>命令的标准输出和标准错误输出。</returns>
        public Task<string> ExecuteCommandWithSudoAsync(SshCredentials credentials, string command)
        {
            return Task.Run(() =>
            {
                var connectionInfo = new ConnectionInfo(credentials.Host, credentials.Username,
                    new PasswordAuthenticationMethod(credentials.Username, credentials.Password));

                using (var client = new SshClient(connectionInfo))
                {
                    client.Connect();
                    var sshCommand = client.CreateCommand($"echo '{credentials.Password}' | sudo -S -p '' bash -c '{command.Replace("'", "'\"'\"'")}'");
                    var result = sshCommand.Execute();
                    client.Disconnect();

                    if (sshCommand.ExitStatus != 0)
                    {
                        throw new SshCommandErrorException($"命令执行失败 (Exit code: {sshCommand.ExitStatus}): {sshCommand.Error}");
                    }

                    return result;
                }
            });
        }

        /// <summary>
        /// 异步执行一个命令，但不等待其完成。主要用于执行 reboot 或启动后台服务等命令。
        /// </summary>
        /// <param name="credentials">SSH 连接凭据。</param>
        /// <param name="command">要执行的命令。</param>
        public Task ExecuteCommandAsyncFireAndForget(SshCredentials credentials, string command)
        {
            return Task.Run(() =>
            {
                // **修正点：** 将 new PasswordAuthentication-Method(...) 修改为 new PasswordAuthenticationMethod(...)
                var connectionInfo = new ConnectionInfo(credentials.Host, credentials.Username,
                    new PasswordAuthenticationMethod(credentials.Username, credentials.Password));

                using (var client = new SshClient(connectionInfo))
                {
                    client.Connect();
                    var sshCommand = client.CreateCommand(command);
                    sshCommand.BeginExecute();
                    Task.Delay(1000).Wait();
                    client.Disconnect();
                }
            });
        }
    }
}