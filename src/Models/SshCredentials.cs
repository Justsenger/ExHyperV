namespace ExHyperV.Models
{
    public class SshCredentials
    {
        public string Host { get; set; }

        public int Port { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }

        public string ProxyHost { get; set; } 
        public int? ProxyPort { get; set; }   

        public bool InstallGraphics { get; set; } = true;


    }
}