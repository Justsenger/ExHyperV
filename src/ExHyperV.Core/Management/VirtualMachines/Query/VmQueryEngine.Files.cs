namespace ExHyperV.Services;

internal sealed partial class VmQueryEngine
{
    private VmQueryValue VmFilePath(Guid id, VmQueryField field)
    {
        var source = Read("Settings");
        if (source.State != VmQueryValueState.Available) return Failure(field, source);
        var rows = source.Rows.Where(row => Owned(row, id)).ToArray();
        if (rows.Length > 1)
            return field.Make(VmQueryValueState.Ambiguous, null, source.Time, "当前虚拟机存在多份当前配置。");
        return ProjectVmFilePath(field, rows.SingleOrDefault(), source.Time);
    }

    internal static VmQueryValue ProjectVmFilePath(VmQueryField field, RawRow? row, DateTimeOffset time)
    {
        if (row == null)
            return field.Make(VmQueryValueState.Unavailable, null, time, "未找到当前虚拟机配置。");
        bool guestState = field == VmIdentityFields.GuestStateFilePath;
        string fileProperty = guestState ? "GuestStateFile" : "ConfigurationFile";
        string rootProperty = guestState ? "GuestStateDataRoot" : "ConfigurationDataRoot";
        if (!row.Values.TryGetValue(fileProperty, out var value))
            return field.Make(VmQueryValueState.Unsupported, null, time, $"当前提供者没有 {fileProperty} 属性。");
        if (value is not string file || string.IsNullOrWhiteSpace(file))
            return field.Make(VmQueryValueState.Unavailable, null, time, "提供者未返回文件路径。");
        try
        {
            if (Path.GetFileName(file) is "" or "." or "..")
                throw new InvalidDataException("提供者返回了目录而非文件路径。");
            string path;
            if (Path.IsPathFullyQualified(file))
                path = VmConfigurationPaths.Resolve(file, null);
            else
            {
                if (!row.Values.ContainsKey(rootProperty))
                    return field.Make(VmQueryValueState.Unsupported, null, time, $"当前提供者没有 {rootProperty} 属性。");
                string root = row.Text(rootProperty);
                if (string.IsNullOrWhiteSpace(root))
                    return field.Make(VmQueryValueState.Unavailable, null, time, "提供者未返回文件的基础路径。");
                // Drive-relative and rooted-without-drive paths must not use this process's current directory.
                if (!Path.IsPathFullyQualified(root) || Path.IsPathRooted(file))
                    throw new InvalidDataException("提供者返回的路径无法解析为完整文件路径。");
                path = VmConfigurationPaths.Resolve(file, root);
            }
            if (string.IsNullOrEmpty(Path.GetFileName(path)))
                throw new InvalidDataException("提供者返回了目录而非文件路径。");
            if (field == VmIdentityFields.RuntimeStateFilePath)
            {
                if (!string.Equals(Path.GetExtension(path), ".vmcx", StringComparison.OrdinalIgnoreCase))
                    return field.Make(VmQueryValueState.Unsupported, null, time, "当前配置格式未使用 VMRS 运行状态文件。");
                // VMMS GetRuntimeFilePath uses the same configuration store/type/object ID with .VMRS.
                path = Path.ChangeExtension(path, ".vmrs");
            }
            return field.Make(VmQueryValueState.Available, path, time);
        }
        catch (Exception error) when (error is ArgumentException or IOException or InvalidDataException or NotSupportedException)
        {
            return field.Make(VmQueryValueState.Failed, null, time, error.Message);
        }
    }
}
