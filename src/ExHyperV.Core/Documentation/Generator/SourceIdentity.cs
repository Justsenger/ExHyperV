using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

internal static partial class ApiDocument
{
    private sealed record SourceIdentity(string? Revision, string ContentSha256);

    private static SourceIdentity ReadSourceIdentity(string root)
    {
        var files = new List<string>();
        void Visit(string directory)
        {
            foreach (string child in Directory.EnumerateDirectories(directory))
                if (Path.GetFileName(child) is not ("bin" or "obj" or "External" or ".git")) Visit(child);
            files.AddRange(Directory.EnumerateFiles(directory).Where(path => Path.GetExtension(path).ToLowerInvariant()
                is ".cs" or ".csproj" or ".props" or ".targets" or ".resx" or ".xml" or ".html" or ".css" or ".md" or ".mjs"));
        }
        Visit(root);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string file in files.OrderBy(path => Path.GetRelativePath(root, path).Replace('\\', '/'), StringComparer.Ordinal))
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            string text = File.ReadAllText(file).Replace("\r\n", "\n");
            if (relative == "API.md")
                text = Regex.Replace(text, @"<!-- api-(reference|examples):start -->.*?<!-- api-\1:end -->", "", RegexOptions.Singleline);
            byte[] content = Encoding.UTF8.GetBytes(text);
            hash.AppendData(Encoding.UTF8.GetBytes(relative + "\0" + content.Length + "\0"));
            hash.AppendData(content);
        }
        string? revision = null;
        try
        {
            var start = new ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (string arg in new[] { "rev-parse", "HEAD" }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode == 0) revision = output.Trim();
        }
        catch { /* An exported source archive need not include Git metadata. */ }
        return new(revision, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }
}
