using System.Xml;
using System.Xml.Linq;
using System.Management;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>读取 HGS 提供程序的证书和嵌入实例。授权变更交给 HGS 校验签名与密钥用途。</summary>
internal sealed class HgsGuardians : IDisposable
{
    private const string GuardianClass = "MSFT_HgsGuardian";
    private const string ProtectorClass = "MSFT_HgsKeyProtector";
    private const string DefaultName = "UntrustedGuardian";
    private readonly ManagementScope scope = WmiConnectionCache.GetManagementScope(@"root\Microsoft\Windows\Hgs", WmiContext.Local);
    private readonly List<ManagementBaseObject> registered = [];

    internal HgsGuardians()
    {
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM " + GuardianClass));
        using var rows = searcher.Get();
        registered.AddRange(rows.Cast<ManagementObject>());
    }

    public void Dispose()
    {
        foreach (var entry in registered) entry.Dispose();
    }

    private ManagementBaseObject Invoke(string type, string method, params (string Name, object Value)[] arguments)
    {
        using var cls = new ManagementClass(scope, new ManagementPath(type), null);
        using var input = cls.GetMethodParameters(method);
        foreach (var (name, value) in arguments) input.Properties[name].Value = value;
        var result = cls.InvokeMethod(method, input, null) ?? throw new InvalidDataException($"HGS 的 {method} 没有返回结果。");
        if (result["ReturnValue"] is uint code && code == 0) return result;
        var status = result["ReturnValue"] as uint?;
        result.Dispose();
        throw new CoreOperationException(ApiResponse.Fail($"HGS 的 {method} 调用失败（{status:X8}）。",
            unchecked((int)(status ?? uint.MaxValue)), ApiErrorSource.Wmi, kind: ApiErrorKind.OperationFailed));
    }

    private ManagementBaseObject Output(string type, string method, params (string, object)[] arguments)
    {
        using var result = Invoke(type, method, arguments);
        return result["cmdletOutput"] as ManagementBaseObject ?? throw new InvalidDataException($"HGS 的 {method} 未返回实例。");
    }

    internal ManagementBaseObject Decode(byte[] bytes) => Output(ProtectorClass, "ConvertToByRawBytes", ("Bytes", bytes));

    internal static string Thumbprint(ManagementBaseObject guardian, string property = "SigningCertificate")
    {
        if (guardian[property] is not byte[] der || der.Length == 0)
            throw new InvalidDataException("HGS 未返回宿主证书。");
        using var certificate = X509CertificateLoader.LoadCertificate(der);
        return certificate.Thumbprint;
    }

    private static bool SameIdentity(ManagementBaseObject left, ManagementBaseObject right) =>
        ((byte[])left["SigningCertificate"]).AsSpan().SequenceEqual((byte[])right["SigningCertificate"]) &&
        ((byte[])left["EncryptionCertificate"]).AsSpan().SequenceEqual((byte[])right["EncryptionCertificate"]);

    private static bool Local(ManagementBaseObject guardian) => guardian["HasPrivateSigningKey"] is true &&
        HasEncryptionKey(Thumbprint(guardian, "EncryptionCertificate"));

    private static bool HasEncryptionKey(string thumbprint)
    {
        foreach (var name in new[] { "Shielded VM Local Certificates", "My" })
        {
            using var store = new X509Store(name, StoreLocation.LocalMachine);
            try { store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly); }
            catch (CryptographicException e) when (e.HResult is unchecked((int)0x80070002) or unchecked((int)0x80092004)) { continue; }
            var certificates = store.Certificates;
            try { if (certificates.Any(c => c.Thumbprint == thumbprint && c.HasPrivateKey)) return true; }
            finally { foreach (var certificate in certificates) certificate.Dispose(); }
        }
        return false;
    }

    internal ManagementBaseObject? LocalGuardian(ManagementBaseObject? key)
    {
        if (key != null)
        {
            foreach (var host in Members(key))
            {
                using (host.Guardian)
                {
                    var match = registered.FirstOrDefault(g => SameIdentity(g, host.Guardian) && Local(g));
                    if (match != null) return match;
                }
            }
        }
        return registered.FirstOrDefault(g => (string)g["Name"] == DefaultName && Local(g))
            ?? registered.FirstOrDefault(g => ((string)g["Name"]).StartsWith("ExHyperV-Local-", StringComparison.Ordinal) && Local(g));
    }

    internal ManagementBaseObject EnsureLocal(ManagementBaseObject? key)
    {
        if (LocalGuardian(key) is { } local) return local;
        string name = registered.Any(g => (string)g["Name"] == DefaultName)
            ? $"ExHyperV-Local-{Guid.NewGuid():N}" : DefaultName;
        var created = Output(GuardianClass, "NewByGenerateCertificates", ("Name", name), ("GenerateCertificates", true));
        registered.Add(created);
        return created;
    }

    private static IEnumerable<(ManagementBaseObject Guardian, bool Owner)> Members(ManagementBaseObject key)
    {
        if (key["Owner"] is not ManagementBaseObject owner) throw new InvalidDataException("解锁密钥缺少所有者证书。");
        yield return (owner, true);
        if (key["Guardians"] is ManagementBaseObject[] others)
            foreach (var guardian in others) yield return (guardian, false);
    }

    internal IReadOnlyList<VtpmHost> Describe(ManagementBaseObject key)
    {
        var hosts = new List<VtpmHost>();
        foreach (var (guardian, owner) in Members(key))
        {
            using (guardian)
            {
                var signing = Thumbprint(guardian);
                var encryption = Thumbprint(guardian, "EncryptionCertificate");
                if (hosts.Any(h => h.SigningThumbprint == signing && h.EncryptionThumbprint == encryption)) continue;
                var known = registered.FirstOrDefault(g => SameIdentity(g, guardian));
                var name = (known ?? guardian)["Name"] as string;
                if (string.IsNullOrWhiteSpace(name))
                {
                    using var cert = X509CertificateLoader.LoadCertificate((byte[])guardian["SigningCertificate"]);
                    name = cert.GetNameInfo(X509NameType.SimpleName, false);
                }
                hosts.Add(new(string.IsNullOrEmpty(name) ? signing : name, signing, encryption, known != null && Local(known), owner));
            }
        }
        return hosts.AsReadOnly();
    }

    internal byte[] Issue(ManagementBaseObject owner, ManagementBaseObject? preserve)
    {
        if (preserve == null)
        {
            using var local = Output(ProtectorClass, "NewByGuardians", ("Owner", owner), ("AllowUntrustedRoot", true));
            return Raw(local);
        }

        var members = Members(preserve).Select(m => m.Guardian).ToArray();
        try
        {
            var others = members.Where(g => !SameIdentity(g, owner))
                .DistinctBy(g => (Thumbprint(g), Thumbprint(g, "EncryptionCertificate"))).ToArray();
            using var key = Output(ProtectorClass, "NewByGuardians", ("Owner", owner), ("Guardian", others), ("AllowUntrustedRoot", true));
            return Raw(key);
        }
        finally { foreach (var member in members) member.Dispose(); }
    }

    private static byte[] Raw(ManagementBaseObject key) => key["RawData"] is byte[] { Length: > 4 } raw
        ? raw : throw new InvalidDataException("HGS 未返回有效的解锁密钥。");

    internal byte[] Grant(ManagementBaseObject key, string path, string? password, string? metadataPath)
    {
        using var guardian = FromFile(path, password, metadataPath);
        var hosts = Describe(key);
        if (hosts.Any(g => g.SigningThumbprint == Thumbprint(guardian) && g.EncryptionThumbprint == Thumbprint(guardian, "EncryptionCertificate")))
            return Raw(key);
        using var updated = Output(ProtectorClass, "Grant", ("KeyProtector", key), ("Guardian", guardian), ("AllowUntrustedRoot", true));
        return Raw(updated);
    }

    internal byte[] Revoke(ManagementBaseObject key, string signingThumbprint, string encryptionThumbprint)
    {
        var selected = Describe(key).SingleOrDefault(g => g.SigningThumbprint == signingThumbprint && g.EncryptionThumbprint == encryptionThumbprint);
        if (selected == null || selected.IsLocal || selected.IsOwner)
            throw new InvalidOperationException("只能移除已授权的外部宿主，密钥所有者须通过重新签发更改。");
        foreach (var (guardian, _) in Members(key))
        {
            using (guardian)
            {
                if (Thumbprint(guardian) != signingThumbprint || Thumbprint(guardian, "EncryptionCertificate") != encryptionThumbprint) continue;
                using var updated = Output(ProtectorClass, "Revoke", ("KeyProtector", key), ("Guardian", guardian));
                return Raw(updated);
            }
        }
        throw new InvalidOperationException("授权列表已变化，请重新加载。");
    }

    private ManagementBaseObject FromFile(string path, string? password, string? metadataPath)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension == ".xml") return ImportXml(path);

        // 单张证书不能证明签名证书与加密证书的配对关系。通过 HGS 已登记的元数据查找完整身份。
        var certificates = extension switch
        {
            ".cer" or ".crt" => new X509Certificate2Collection(X509CertificateLoader.LoadCertificateFromFile(path)),
            ".pfx" => X509CertificateLoader.LoadPkcs12CollectionFromFile(path, password, X509KeyStorageFlags.EphemeralKeySet),
            _ => throw new ArgumentException("请选择 XML、CER、CRT 或 PFX 文件。")
        };
        try
        {
            var matches = registered.Where(g => MatchesCertificate(g, certificates)).ToList();
            if (matches.Count == 1) return (ManagementBaseObject)matches[0].Clone();
            if (metadataPath == null) throw new CoreOperationException(ApiResponse.Fail("请提供目标宿主的 Guardian XML 以核对证书对。",
                kind: ApiErrorKind.InvalidArgument).WithDiagnostic(CoreErrorCode.GuardianMetadataRequired));
            return ImportXml(Path.GetFullPath(metadataPath), certificates);
        }
        finally { foreach (var certificate in certificates) certificate.Dispose(); }
    }

    private static bool MatchesCertificate(ManagementBaseObject guardian, X509Certificate2Collection certificates) =>
        certificates.Any(c => c.RawDataMemory.Span.SequenceEqual((byte[])guardian["SigningCertificate"]) ||
            c.RawDataMemory.Span.SequenceEqual((byte[])guardian["EncryptionCertificate"]));

    private ManagementBaseObject ImportXml(string path, X509Certificate2Collection? certificates = null)
    {
        // 同一份元数据重试时复用已导入的实例。名称由内容生成，不覆盖同名宿主。
        using var file = File.OpenRead(path);
        string name = $"{Path.GetFileNameWithoutExtension(path)}-{Convert.ToHexString(SHA256.HashData(file))[..16]}";
        file.Position = 0;
        var pair = ReadGuardianIdentity(file);
        if (certificates != null && !certificates.Any(c => c.RawDataMemory.Span.SequenceEqual(pair.Signing)
                || c.RawDataMemory.Span.SequenceEqual(pair.Encryption)))
            throw new InvalidDataException("所选证书与 Guardian XML 不匹配。");
        var known = registered.SingleOrDefault(g => string.Equals((string)g["Name"], name, StringComparison.OrdinalIgnoreCase));
        var guardian = known != null ? (ManagementBaseObject)known.Clone() :
            Output(GuardianClass, "Import", ("Name", name), ("Path", path), ("AllowUntrustedRoot", true));
        if (MatchesPair(pair, guardian["SigningCertificate"] as byte[], guardian["EncryptionCertificate"] as byte[])) return guardian;
        guardian.Dispose();
        throw new CoreOperationException(ApiResponse.Fail("已登记宿主的签名或加密证书与 XML 不符。",
            kind: known == null ? ApiErrorKind.CleanupRequired : ApiErrorKind.Conflict));
    }

    internal static (byte[] Signing, byte[] Encryption) ReadGuardianIdentity(Stream stream)
    {
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 });
        var root = XDocument.Load(reader).Root;
        XNamespace ns = "http://schemas.microsoft.com/kps/2014/07";
        if (root?.Name != ns + "Metadata") throw new InvalidDataException("无法识别 Guardian XML。");
        var guardian = root.Elements(ns + "GuardianInformation").Single();
        byte[] Certificate(string field)
        {
            byte[] der = Convert.FromBase64String(guardian.Elements(ns + field).Single().Value);
            using var certificate = X509CertificateLoader.LoadCertificate(der);
            if (!certificate.RawDataMemory.Span.SequenceEqual(der)) throw new InvalidDataException(field);
            return der;
        }
        return (Certificate("SigningCertificate"), Certificate("EncryptionCertificate"));
    }

    internal static bool MatchesPair((byte[] Signing, byte[] Encryption) expected, byte[]? signing, byte[]? encryption) =>
        signing != null && encryption != null && expected.Signing.AsSpan().SequenceEqual(signing)
        && expected.Encryption.AsSpan().SequenceEqual(encryption);

    internal void Export(ManagementBaseObject guardian, string path)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".xml" or ".cer" or ".crt"))
            throw new ArgumentException("请将宿主证书保存为 XML、CER 或 CRT 文件。");
        // HGS 写入失败时保留用户已有文件，成功后在同目录替换。
        string temp = Path.Combine(Path.GetDirectoryName(path)!, $"{Guid.NewGuid():N}{extension}");
        try
        {
            if (extension == ".xml")
            {
                using var result = Invoke(GuardianClass, "Export", ("InputObject", guardian), ("Path", temp));
            }
            else
            {
                using var certificate = X509CertificateLoader.LoadCertificate((byte[])guardian["SigningCertificate"]);
                File.WriteAllBytes(temp, certificate.Export(X509ContentType.Cert));
            }
            File.Move(temp, path, overwrite: true);
        }
        finally { File.Delete(temp); }
    }
}
