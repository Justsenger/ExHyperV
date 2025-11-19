namespace ExHyperV.Models
{
    public class SshCredentials
    {
        public string Host { get; set; }

        public int Port { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }

        public string ProxyHost { get; set; } // 新增
        public int? ProxyPort { get; set; }   // 新增, int? 表示可为空


    }
}