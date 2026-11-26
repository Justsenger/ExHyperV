using System.Globalization;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace ExHyperV.App.Diagnostics;

internal static class DiagnosticBundle
{
    // Reconstruct from typed values. Unknown elements, text, comments, attributes,
    // nested markup and future credential fields never enter the archive.
    private static XElement ReadConfig(string path)
    {
        var safe = new XElement("Config");
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024
            });
            var root = XDocument.Load(reader).Root;
            if (root?.Name != "Config") return new XElement("Config", new XAttribute("status", "invalid"));
            if (root.Element("MmioCeilingMb") is { HasElements: false } mmio &&
                ulong.TryParse(mmio.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var ceiling))
                safe.Add(new XElement("MmioCeilingMb", ceiling));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            safe.SetAttributeValue("status", "missing");
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            safe.SetAttributeValue("status", "unavailable");
        }
        return safe;
    }

    internal static void Export(RollingLog logs, string configPath, string destination, string environment)
    {
        if (!string.Equals(Path.GetExtension(destination), ".zip", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Expected a .zip destination.", nameof(destination));
        var snapshot = logs.Snapshot(out int? lastWriteError);
        var config = ReadConfig(configPath);
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
            {
                foreach (var (name, bytes) in snapshot)
                {
                    using var entry = archive.CreateEntry("Logs/" + name).Open();
                    entry.Write(bytes);
                }
                using (var writer = new StreamWriter(archive.CreateEntry("environment.txt").Open()))
                {
                    writer.WriteLine(environment);
                    if (lastWriteError.HasValue) writer.WriteLine($"lastLogWriteHResult=0x{lastWriteError:X8}");
                }
                using var xml = archive.CreateEntry("Config.xml").Open();
                config.Save(xml);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }
}
