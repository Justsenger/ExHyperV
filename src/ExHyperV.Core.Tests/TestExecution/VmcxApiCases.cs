using System.Security.Cryptography;
using ExHyperV.Vmcx;
using ExHyperV.Core.Tests.TestPlanning;

namespace ExHyperV.Core.Tests.TestExecution;

internal static class VmcxApiCases
{
    internal static bool Supports(CatalogTest p) => p.Api.StartsWith("VmcxDocument.", StringComparison.Ordinal) || p.Api.StartsWith("VmcxEditSession.", StringComparison.Ordinal) || p.Api == "VmcxException.构造()";

    internal static async Task<string> SampleAsync(ApiCaseContext c)
    {
        await using var vm = await TemporaryVm.CreateAsync(c);
        string root = Path.Combine(c.DirectoryPath, "vmcx-source");
        var result = await WindowsTestCommand.RunAsync("""
            Get-VM -Id ([guid]$a.id)|Export-VM -Path $a.root
            $files=@(Get-ChildItem -LiteralPath $a.root -Filter *.vmcx -Recurse)
            if($files.Count -ne 1){throw '配置样本数量异常'}
            @{path=$files[0].FullName}|ConvertTo-Json
            """, new { id = vm.Id, root }, 120);
        string path = Path.Combine(c.DirectoryPath, "sample.vmcx"); File.Copy(result.GetProperty("path").GetString()!, path); return path;
    }

    internal static async Task RunAsync(CatalogTest p, ApiCaseContext c)
    {
        if (p.Api == "VmcxException.构造()")
        {
            int hr = unchecked((int)0x80070005); var ex = c.Call(() => new VmcxException("原生访问错误", hr));
            c.Equal(hr, ex.HResultCode, "原生 HRESULT"); c.Require(ex.Message.Contains("原生访问错误", StringComparison.Ordinal), "异常保留操作说明"); return;
        }
        string path = await SampleAsync(c); string originalHash = Hash(path);
        if (p.Api == "VmcxDocument.Read()" && p.Id.EndsWith("-2", StringComparison.Ordinal))
        {
            c.Throws<NotSupportedException>(() => c.Call(() => VmcxDocument.Read(Path.ChangeExtension(path, "txt"))), "扩展名校验");
            string large = Path.Combine(c.DirectoryPath, "oversize.vmcx"); using (var stream = File.Create(large)) stream.SetLength(256L * 1024 * 1024 + 1);
            c.Throws<NotSupportedException>(() => c.Call(() => VmcxDocument.Read(large)), "文件大小上限");
            string bad = Path.Combine(c.DirectoryPath, "corrupted.vmcx"); File.WriteAllBytes(bad, new byte[4096]);
            Failure(c, () => c.Call(() => VmcxDocument.Read(bad)), "损坏存储头拒绝读取");
            using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) Failure(c, () => c.Call(() => VmcxDocument.Read(path)), "并发排他写入拒绝读取");
            c.Equal(originalHash, Hash(path), "读取失败保留样本字节"); return;
        }
        var doc = p.Api == "VmcxDocument.Read()" ? c.Call(() => VmcxDocument.Read(path)) : VmcxDocument.Read(path);
        var edit = p.Api == "VmcxDocument.BeginEdit()" ? c.Call(doc.BeginEdit) : doc.BeginEdit();
        VmcxEntry config = doc.Find("/configuration").Single();
        if (p.Api == "VmcxDocument.Read()")
        {
            c.Equal(originalHash, doc.SourceSha256, "源文件独立哈希"); c.Equal(new FileInfo(path).Length, doc.SourceLength, "源文件长度");
            c.Require(doc.Entries.Select(e => e.Id).Distinct().Count() == doc.Entries.Count && doc.Entries.Count > 1, "配置节点身份唯一");
            c.Require(doc.Entries.Where(e => e.ParentId.HasValue).All(e => doc.Entries.Any(parent => parent.Id == e.ParentId)), "每个节点父身份存在");
            using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            return;
        }
        if (p.Api == "VmcxDocument.BeginEdit()")
        {
            var second = c.Call(doc.BeginEdit); edit.AddValue(config, "api_test", VmcxValue.String("first"));
            c.Equal(0, second.Find("/configuration/api_test").Count, "两个编辑批次相互独立");
            c.Equal(0, doc.Find("/configuration/api_test").Count, "源快照保持原样");
        }
        else if (p.Api.EndsWith(".Find()", StringComparison.Ordinal) || p.Api.EndsWith(".GetChildren()", StringComparison.Ordinal))
        {
            var first = edit.AddContainer(config, "api_repeated"); var second = edit.AddContainer(config, "api_repeated");
            edit.AddValue(first, "a", VmcxValue.Integer(1)); edit.AddValue(second, "b", VmcxValue.Integer(2));
            bool document = p.Api.StartsWith("VmcxDocument", StringComparison.Ordinal);
            if (document) { edit.Commit(); doc = VmcxDocument.Read(path); first = doc.Find("/configuration/api_repeated")[0]; second = doc.Find("/configuration/api_repeated")[1]; }
            if (p.Api.EndsWith(".Find()", StringComparison.Ordinal))
            {
                foreach (string key in new[] { "/configuration/api_repeated", "/CONFIGURATION/API_REPEATED" })
                {
                    var rows = c.Call(() => document ? doc.Find(key) : edit.Find(key));
                    c.Equal(2, rows.Count, "返回全部同路径容器"); c.Equal(2, rows.Select(r => r.Id).Distinct().Count(), "重复路径保留独立身份");
                }
                c.Equal(0, c.Call(() => document ? doc.Find("/missing") : edit.Find("/missing")).Count, "未命中返回空集合");
            }
            else
            {
                c.Sequence(new[] { "a" }, c.Call(() => document ? doc.GetChildren(first) : edit.GetChildren(first)).Select(e => e.Key), "第一同名容器的孩子");
                c.Sequence(new[] { "b" }, c.Call(() => document ? doc.GetChildren(second) : edit.GetChildren(second)).Select(e => e.Key), "第二同名容器的孩子");
                var foreign = VmcxDocument.Read(path).Root;
                c.Throws<ArgumentException>(() => c.Call(() => document ? doc.GetChildren(foreign) : edit.GetChildren(foreign)), "拒绝跨文档节点");
            }
        }
        else if (p.Api.EndsWith(".GetDevices()", StringComparison.Ordinal))
        {
            var expected = new[] { VmcxDeviceKind.NetworkAdapter, VmcxDeviceKind.ScsiController, VmcxDeviceKind.GpuPartition, VmcxDeviceKind.PcieDevice }
                .Select(kind => edit.AddDevice(kind, kind == VmcxDeviceKind.PcieDevice ? "PCIROOT(0)#PCI(0100)" : null)).ToArray();
            bool document = p.Api.StartsWith("VmcxDocument", StringComparison.Ordinal);
            if (document) { edit.Commit(); doc = VmcxDocument.Read(path); }
            var found = c.Call(() => document ? doc.GetDevices() : edit.GetDevices());
            foreach (var id in expected) c.Equal(1, found.Count(d => d.Instance == id.ToString("D")), "设备实例 " + id);
            c.Sequence(Enumerable.Range(1, found.Count), found.Select(d => d.Number), "manifest 连续编号");
            var entries = document ? doc.Entries : edit.Entries;
            foreach (var device in found)
            {
                string prefix = "/configuration/manifest/vdev" + device.Number.ToString("D3") + "/";
                c.Equal(entries.Single(e => e.Path == prefix + "device").Value!.GetString(), device.DeviceType, "设备类型来源");
                c.Equal(entries.Single(e => e.Path == prefix + "name").Value!.GetString(), device.Name, "设备名称来源");
            }
        }
        else if (p.Api.EndsWith(".Validate()", StringComparison.Ordinal))
        {
            bool document = p.Api.StartsWith("VmcxDocument", StringComparison.Ordinal);
            c.Equal(0, c.Call(() => document ? doc.Validate() : edit.Validate()).Count, "系统创建样本通过结构校验");
            if (document)
            {
                // A malformed in-memory document exercises the public validator without writing an invalid VM configuration.
                var entries = doc.Entries.ToList(); var missing = entries.Single(e => e.Path == "/configuration/manifest/size");
                var original = entries.IndexOf(missing); entries.Remove(missing);
                var field = typeof(VmcxDocument).GetField("<Entries>k__BackingField", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                field.SetValue(doc, entries.AsReadOnly());
                c.Require(c.Call(doc.Validate).Count > 0, "缺失 manifest 字段产生问题清单");
                entries.Insert(original, missing); field.SetValue(doc, entries.AsReadOnly());
            }
            else
            {
                edit.RemoveSubtree(edit.Find("/configuration/manifest/size").Single());
                c.Require(c.Call(edit.Validate).Count > 0, "缺失 manifest 字段产生问题清单");
            }
        }
        else if (p.Api == "VmcxEditSession.Commit()") { Commit(p, c, doc, edit, config, path); return; }
        else if (p.Api == "VmcxEditSession.AddDevice()")
        {
            foreach (var (kind, host) in new[] { (VmcxDeviceKind.NetworkAdapter, (string?)null), (VmcxDeviceKind.ScsiController, null), (VmcxDeviceKind.GpuPartition, null), (VmcxDeviceKind.GpuPartition, "PCI#GPU_TEST"), (VmcxDeviceKind.PcieDevice, "PCIROOT(0)#PCI(0100)") })
            {
                var id = c.Call(() => edit.AddDevice(kind, host)); c.Equal(1, edit.GetDevices().Count(d => d.Instance == id.ToString("D")), "新增 " + kind);
                if (host is not null) c.Require(edit.Entries.Any(e => e.Path.StartsWith("/configuration/_" + id + "_", StringComparison.OrdinalIgnoreCase) && e.Value?.Text == host), "宿主路径写入配置定义");
                c.Equal(0, edit.Validate().Count, "新增设备后结构有效");
            }
            int count = edit.Entries.Count;
            c.Throws<ArgumentException>(() => c.Call(() => edit.AddDevice(VmcxDeviceKind.PcieDevice)), "DDA 缺失路径拒绝");
            c.Throws<ArgumentException>(() => c.Call(() => edit.AddDevice(VmcxDeviceKind.NetworkAdapter, "wrong")), "网卡拒绝宿主资源参数");
            c.Equal(count, edit.Entries.Count, "失败后批次保持原样");
        }
        else if (p.Api == "VmcxEditSession.RemoveDevice()")
        {
            var id = edit.AddDevice(VmcxDeviceKind.NetworkAdapter); var others = edit.GetDevices().Where(d => d.Instance != id.ToString()).Select(d => d.Instance).Order().ToArray();
            c.Call(() => edit.RemoveDevice(id)); c.Sequence(others, edit.GetDevices().Select(d => d.Instance).Order(), "仅移除目标设备");
            c.Require(edit.Find("/configuration/_" + id + "_").Count == 0 && edit.Validate().Count == 0, "数据根及 manifest 同步清理");
            c.Throws<ArgumentException>(() => c.Call(() => edit.RemoveDevice(id)), "失效设备身份拒绝");
        }
        else if (p.Api == "VmcxEditSession.AddScsiDrive()")
        {
            var controller = edit.AddDevice(VmcxDeviceKind.ScsiController);
            foreach (int index in new[] { 0, 1, 63, 64 })
            {
                var disk = c.Call(() => edit.AddScsiDrive(controller, "test.vhdx", controller: index));
                var optical = c.Call(() => edit.AddScsiDrive(controller, "", optical: true, controller: index));
                c.Equal("drive0", disk.Key, "空控制器首个槽位"); c.Equal("drive1", optical.Key, "下一空闲槽位");
                c.Equal("VHD", edit.GetChildren(disk).Single(e => e.Key == "type").Value!.GetString(), "磁盘介质类型");
                c.Equal("ISO", edit.GetChildren(optical).Single(e => e.Key == "type").Value!.GetString(), "光盘介质类型");
            }
            c.Throws<ArgumentOutOfRangeException>(() => c.Call(() => edit.AddScsiDrive(controller, "x", controller: -1)), "负控制器编号拒绝");
            c.Throws<ArgumentException>(() => c.Call(() => edit.AddScsiDrive(Guid.NewGuid(), "x")), "失效控制器拒绝");
        }
        else { EditNodes(p, c, edit, config); }
        if (!p.Api.StartsWith("VmcxDocument.Find", StringComparison.Ordinal) && !p.Api.StartsWith("VmcxDocument.GetChildren", StringComparison.Ordinal) && !p.Api.StartsWith("VmcxDocument.GetDevices", StringComparison.Ordinal))
            c.Equal(originalHash, Hash(path), "内存操作保留磁盘原文件");
    }

    private static void EditNodes(CatalogTest p, ApiCaseContext c, VmcxEditSession e, VmcxEntry config)
    {
        var parent = e.AddContainer(config, "api_test");
        if (p.Api == "VmcxEditSession.AddContainer()")
        {
            var a = c.Call(() => e.AddContainer(parent, "same")); var b = c.Call(() => e.AddContainer(parent, "same"));
            c.Require(a.Id != b.Id && a.Path == b.Path, "同名容器保留独立身份");
            e.AddValue(parent, "value", VmcxValue.Integer(0));
            c.Throws<ArgumentException>(() => c.Call(() => e.AddContainer(parent, "value")), "容器与值冲突拒绝");
            foreach (string key in new[] { "", "a/b", "a\0b", new string('a', 255) }) c.Throws<ArgumentException>(() => c.Call(() => e.AddContainer(parent, key)), "非法键拒绝");
        }
        else if (p.Api == "VmcxEditSession.AddValue()")
        {
            VmcxValue[] values = [VmcxValue.Integer(-1), VmcxValue.UnsignedInteger(ulong.MaxValue), VmcxValue.Double(1.25), VmcxValue.Boolean(true), VmcxValue.Raw([0, 255]), VmcxValue.String("中文\n")];
            for (int i = 0; i < values.Length; i++) { var v = values[i]; var node = c.Call(() => e.AddValue(parent, "value" + i, v)); c.Equal(v.Type, node.Value!.Type, "节点类型"); c.Equal(v.Text, node.Value.Text, "节点值"); }
            var selected = c.Call(() => e.AddValue(parent, "identity", VmcxValue.String("first"), true)); c.Require(selected.IsSelector, "selector 标志保留");
            c.Throws<ArgumentException>(() => c.Call(() => e.AddValue(parent, "value0", VmcxValue.Integer(2))), "重复值键拒绝");
        }
        else if (p.Api == "VmcxEditSession.SetValue()")
        {
            var node = e.AddValue(parent, "value", VmcxValue.String("old"));
            var integer = e.AddValue(parent, "integer", VmcxValue.Integer(0));
            var raw = e.AddValue(parent, "raw", VmcxValue.Raw([1]));
            foreach (var (target, value) in new[] { (node, VmcxValue.String("短文本")), (node, VmcxValue.String(new string('中', 4096))), (integer, VmcxValue.Integer(long.MinValue)), (raw, VmcxValue.Raw([0, 1, 255])) })
            { c.Call(() => e.SetValue(target, value)); var actual = e.Find(target.Path).Single(); c.Equal(value.Type, actual.Value!.Type, "替换类型"); c.Equal(value.Text, actual.Value.Text, "替换内容"); }
            c.Throws<ArgumentException>(() => c.Call(() => e.SetValue(node, VmcxValue.Integer(1))), "跨类型设值拒绝");
            c.Throws<ArgumentException>(() => c.Call(() => e.SetValue(parent, VmcxValue.Integer(1))), "容器设值拒绝");
        }
        else if (p.Api == "VmcxEditSession.Rename()")
        {
            var node = e.AddValue(parent, "old", VmcxValue.Integer(1)); c.Call(() => e.Rename(node, new string('a', 254)));
            c.Equal(254, e.Entries.Single(n => n.Id == node.Id).Key.Length, "254 字节键接受");
            foreach (string key in new[] { new string('a', 255), "a/b", "a\0b" }) c.Throws<ArgumentException>(() => c.Call(() => e.Rename(node, key)), "非法重命名拒绝");
            c.Call(() => e.Rename(node, "中文")); c.Equal(1L, e.Find(parent.Path + "/中文").Single().Value!.GetInteger(), "重命名保留值");
        }
        else if (p.Api == "VmcxEditSession.RemoveSubtree()")
        {
            var child = e.AddContainer(parent, "child"); e.AddValue(child, "value", VmcxValue.Boolean(true)); var keep = e.AddValue(config, "keep", VmcxValue.Integer(7));
            c.Call(() => e.RemoveSubtree(parent)); c.Require(!e.Entries.Any(n => n.Path.StartsWith(parent.Path, StringComparison.Ordinal)), "指定整棵子树移除"); c.Equal(7L, e.Entries.Single(n => n.Id == keep.Id).Value!.GetInteger(), "其他子树保留");
        }
        else throw new InvalidDataException("VMCX 执行分支缺失：" + p.Api);
    }

    private static void Commit(CatalogTest p, ApiCaseContext c, VmcxDocument doc, VmcxEditSession edit, VmcxEntry config, string path)
    {
        string before = Hash(path); int scenario = int.Parse(p.Id[(p.Id.LastIndexOf('-') + 1)..]);
        if (scenario == 1)
        {
            var unchanged = c.Call(edit.Commit); c.Require(!unchanged.Changed && unchanged.BackupPath is null, "空批次保持文件并省去备份");
            edit = doc.BeginEdit(); edit.AddValue(config, "api_commit", VmcxValue.String("中文\n")); var result = c.Call(edit.Commit);
            c.Require(result.Changed && File.Exists(result.BackupPath), "提交有改动并产生恢复备份");
            c.Equal(before, Hash(result.BackupPath!), "备份逐字节完整"); c.Equal(before, result.OriginalSha256, "原始哈希"); c.Equal(Hash(path), result.ResultSha256, "结果哈希");
            c.Equal("中文\n", VmcxDocument.Read(path).Find("/configuration/api_commit").Single().Value!.GetString(), "重新读取持久化值");
        }
        else if (scenario == 2)
        {
            // Use the original document for both batches; the first commit changes the file before the second starts.
            var other = doc.BeginEdit(); other.AddValue(config, "external", VmcxValue.Integer(1)); other.Commit(); string changed = Hash(path);
            edit.AddValue(config, "ours", VmcxValue.Integer(2)); c.Throws<IOException>(() => c.Call(edit.Commit), "源文件并发变化拒绝提交"); c.Equal(changed, Hash(path), "外部提交内容保留");
        }
        else if (scenario == 3)
        {
            edit.AddValue(config, "locked", VmcxValue.String("value"));
            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read)) Failure(c, () => c.Call(edit.Commit), "写入阶段遇到只读共享锁");
            c.Equal(before, Hash(path), "写入失败保持原文件"); using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        }
        else
        {
            c.Call(edit.Commit); c.Throws<InvalidOperationException>(() => c.Call(edit.Commit), "成功后批次关闭");
            var failed = doc.BeginEdit(); failed.RemoveSubtree(config); c.Throws<InvalidDataException>(() => c.Call(failed.Commit), "非法结构提交失败");
            c.Throws<InvalidOperationException>(() => c.Call(failed.Validate), "失败后批次关闭");
        }
    }
    private static string Hash(string path) { using var input = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(input)); }
    private static void Failure(ApiCaseContext c, Action action, string label)
    {
        Exception? error = null; try { action(); } catch (Exception ex) when (ex is IOException or NotSupportedException or VmcxException or VmcxEditException or InvalidOperationException) { error = ex; }
        c.Require(error is not null, label + "：" + error?.GetType().Name + " " + error?.Message);
    }
}
