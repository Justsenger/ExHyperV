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
                if (Path.GetFileName(child) is not ("bin" or "obj" or "external" or "External" or ".git")) Visit(child);
            files.AddRange(Directory.EnumerateFiles(directory).Where(path => Path.GetExtension(path).ToLowerInvariant()
                is ".cs" or ".csproj" or ".props" or ".targets" or ".resx" or ".xml" or ".json" or ".html" or ".css" or ".md" or ".mjs" or ".woff2" or ".txt"));
        }
        string repository = RepositoryRoot(root);
        foreach (string input in new[] { root, Path.Combine(repository, "doc/api"), Path.Combine(repository, "tools/api-docs"), Path.Combine(repository, "website/src/api") }) Visit(input);
        files.RemoveAll(path => Path.GetFullPath(path) == Path.GetFullPath(DocumentationFile(root, "FieldHelp.json")) || Path.GetFullPath(path) == Path.GetFullPath(DocumentationFile(root, "API.md")) || Path.GetFullPath(path) == Path.GetFullPath(DocumentationFile(root, "API.json")));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string file in files.OrderBy(path => Path.GetRelativePath(repository, path).Replace('\\', '/'), StringComparer.Ordinal))
        {
            string relative = Path.GetRelativePath(repository, file).Replace('\\', '/');
            bool binary = Path.GetExtension(file) == ".woff2";
            string text = binary ? "" : File.ReadAllText(file).Replace("\r\n", "\n");
            if (relative == "doc/api/guide.md")
                text = Regex.Replace(text, @"<!-- api-(reference|examples|example-[\w-]+):start -->.*?<!-- api-\1:end -->", "", RegexOptions.Singleline);
            byte[] content = binary ? File.ReadAllBytes(file) : Encoding.UTF8.GetBytes(text);
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
