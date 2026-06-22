namespace ExHyperV.Models
{
    /// <summary>Linux GPU 部署用的 SSH 连接凭据。Host 在部署时会被改写为解析后的目标 IP，故可变。</summary>
    public class SshCredentials
    {
        public string Host { get; set; } = string.Empty;
        public int Port { get; set; }
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;

        public string? ProxyHost { get; set; }
        public int? ProxyPort { get; set; }
        public bool UseProxy { get; set; }

        public bool InstallGraphics { get; set; } = true;
    }
}
