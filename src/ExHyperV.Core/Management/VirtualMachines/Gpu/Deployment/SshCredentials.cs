namespace ExHyperV.Models
{
    /// <summary>Linux GPU 部署用的 SSH 连接凭据。Host 在部署时会被改写为解析后的目标 IP，故可变。</summary>
    public class SshCredentials
    {
        /// <summary>来宾 IPv4 地址；直接调用部署服务时会被改写为选出的目标地址，会话入口会先复制凭据。</summary>
        public string Host { get; set; } = string.Empty;
        /// <summary>SSH 端口，调用方须指定 1 至 65535；模型默认值 0 不是有效端口。</summary>
        public int Port { get; set; }
        /// <summary>来宾 SSH 用户名，Linux 部署要求非空。</summary>
        public string Username { get; set; } = string.Empty;
        /// <summary>SSH 密码及部署所需的 sudo 密码；由调用方安全保管，不应写入日志或示例。</summary>
        public string Password { get; set; } = string.Empty;

        /// <summary>来宾部署脚本下载所用 HTTP 代理主机；不启用代理时可为 null，并非 SSH 连接代理。</summary>
        public string? ProxyHost { get; set; }
        /// <summary>HTTP 代理端口；不启用代理时可为 null。</summary>
        public int? ProxyPort { get; set; }
        /// <summary>是否把代理配置传给来宾部署脚本；为 true 时需同时提供主机和有效端口。</summary>
        public bool UseProxy { get; set; }

        /// <summary>是否请求脚本安装图形支持，默认 true。</summary>
        public bool InstallGraphics { get; set; } = true;
    }
}
