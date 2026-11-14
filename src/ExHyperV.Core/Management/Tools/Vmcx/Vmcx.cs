// VMCX 配置工具：文档模型、编辑流程及 VmDataStore 原生接口。
using ExHyperV.Vmcx.Internal;
using System.Buffers.Binary;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Text;
using System.Xml;
using System;

namespace ExHyperV.Vmcx
{
    #region 文档与编辑入口

#nullable restore
    /// <summary>VMCX 配置文件的只读快照与编辑入口。</summary>
    /// <remarks>
    /// 通过本机 VmDataStore.dll 读取文件副本，使用 BeginEdit 创建编辑批次。
    /// 文件上限为 256 MiB；提交时需要独占访问源文件。
    /// 依赖宿主的 VMCX 原生接口。
    /// </remarks>
    /// <example>
    /// <code>
    /// using ExHyperV.Vmcx;
    /// public static class VmcxFileExample
    /// {
    ///     public static VmcxEditResult SetExistingString(string exportedVmcxPath, string nodePath, string text)
    ///     {
    ///         var document = VmcxDocument.Read(exportedVmcxPath);
    ///         var edit = document.BeginEdit();
    ///         var entry = document.Find(nodePath).Single();
    ///         edit.SetValue(entry, VmcxValue.String(text));
    ///         return edit.Commit(); // 备份后由 DLL 提交修改
    ///     }
    /// }
    /// </code>
    /// </example>
    public sealed class VmcxDocument
    {
        internal const long MaximumLength = 256L * 1024 * 1024;
        internal Guid Identity { get; } = Guid.NewGuid();
        /// <summary>源文件绝对路径。</summary>
        /// <fielddoc><meaning>读取的 VMCX 配置文件在宿主上的完整路径。</meaning><values kind="format">Windows 文件路径，扩展名为 <c>.vmcx</c>。</values></fielddoc>
        public string FilePath { get; }
        /// <summary>读取时整份源文件的 SHA-256。提交必须仍匹配此值。</summary>
        /// <fielddoc><meaning>读取时计算的文件指纹。提交修改前用它检查源文件是否发生变化。</meaning><values kind="format">64 个大写十六进制字符（<c>0–9</c>、<c>A–F</c>）。</values></fielddoc>
        public string SourceSha256 { get; }
        /// <summary>源文件字节数。</summary>
        /// <fielddoc><meaning>读取时源文件的大小。</meaning><values kind="format">单位为字节；读取入口支持最大 256 MiB 的文件。</values></fielddoc>
        public long SourceLength { get; }
        /// <summary>根节点以及所有活动节点的不可变快照。</summary>
        public IReadOnlyList<VmcxEntry> Entries { get; }
        /// <summary>合成根节点，可用于访问顶层容器。</summary>
        public VmcxEntry Root => Entries[0];

        private VmcxDocument(string path, string hash, long length, VmcxStore source)
        {
            FilePath = path; SourceSha256 = hash; SourceLength = length;
            Entries = source.ReadEntries(Identity).AsReadOnly();
            if (!Entries.Any(n => n.Path == "/configuration" && n.IsContainer))
                throw new InvalidDataException("文件中没有 configuration 容器。");
        }

        /// <summary>由本机 VmDataStore 读取已有 VMCX 的副本，并验证源文件读取前后哈希一致。</summary>
        /// <param name="path">已有 .vmcx 文件路径。</param>
        /// <returns>独立的内存快照，不持有文件句柄。</returns>
        /// <exception cref="IOException">读取失败、文件损坏或读取期间发生修改。</exception>
        /// <exception cref="NotSupportedException">文件种类、节点类型或大小超出支持范围。</exception>
        /// <exception cref="VmcxException">VmDataStore 返回原生错误，例如文件锁定或无效存储。</exception>
        public static VmcxDocument Read(string path)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            string full = System.IO.Path.GetFullPath(path);
            if (!System.IO.Path.GetExtension(full).Equals(".vmcx", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("此入口只读取现有 .vmcx 配置文件。");
            return ReadVerified(full);
        }
        internal static VmcxDocument ReadVerified(string path)
        {
            long length = new FileInfo(path).Length;
            if (length > MaximumLength) throw new NotSupportedException("VMCX 文件超过 256 MiB。");
            string hash = VmcxFileIO.HashFile(path);
            string copy = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".vmcx");
            try
            {
                File.Copy(path, copy);
                // File.Copy 会继承只读属性；只调整副本，保证 DLL 可恢复副本且结束后能清理。
                File.SetAttributes(copy, File.GetAttributes(copy) & ~FileAttributes.ReadOnly);
                if (VmcxFileIO.HashFile(copy) != hash || VmcxFileIO.HashFile(path) != hash)
                    throw new IOException("读取期间 VMCX 已变化，请重新读取。");
                using var apartment = new VmcxStore.Apartment();
                using var native = VmcxStore.Open(copy);
                using var locked = native.BeginWrite();
                var result = new VmcxDocument(path, hash, length, native);
                if (VmcxFileIO.HashFile(path) != hash) throw new IOException("读取期间 VMCX 已变化，请重新读取。");
                return result;
            }
            finally { if (File.Exists(copy)) File.Delete(copy); }
        }
        /// <summary>按展示路径查找全部匹配节点。</summary>
        /// <param name="path">完整逻辑路径，按不区分大小写匹配。</param>
        /// <returns>匹配节点的只读集合；未命中时为空。</returns>
        public IReadOnlyList<VmcxEntry> Find(string path) => Array.AsReadOnly(Entries.Where(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase)).ToArray());
        /// <summary>读取指定节点的直接子节点，保留同名条目的边界。</summary>
        /// <param name="parent">本快照的容器节点。</param>
        /// <returns>直接子节点的只读集合；无子节点时为空。</returns>
        public IReadOnlyList<VmcxEntry> GetChildren(VmcxEntry parent)
        {
            RequireEntry(parent);
            return Array.AsReadOnly(Entries.Where(e => e.ParentId == parent.Id).ToArray());
        }
        /// <summary>开始一批内存编辑；调用 Commit 前不写文件。</summary>
        /// <returns>独立编辑批次；同一源快照的多个批次提交时仍执行并发检查。</returns>
        public VmcxEditSession BeginEdit() => new(this);
        /// <summary>检查 manifest、设备结构和已知互斥规则。</summary>
        /// <returns>已发现的问题；未发现已知问题时为空。</returns>
        public IReadOnlyList<string> Validate() => VmcxValidation.Check(ToLegacyNodes());
        /// <summary>从 manifest 读取设备定义。</summary>
        /// <returns>按 manifest 编号排列的设备描述。</returns>
        public IReadOnlyList<VmcxDevice> GetDevices() => VmcxDeviceTemplates.ReadDevices(Entries);
        internal List<VmcxNode> ToLegacyNodes() => Entries.Select(e => new VmcxNode { Path = e.Path,
            IsValue = e.Value is not null, Value = e.Value?.Text ?? "", Type = e.Value?.Type switch {
                VmcxValueType.Integer => "Int", VmcxValueType.UnsignedInteger => "UInt", VmcxValueType.Raw => "Array",
                VmcxValueType.Boolean => "Bool", null => "Node", _ => e.Value.Type.ToString() } }).ToList();
        internal void RequireEntry(VmcxEntry entry)
        {
            ArgumentNullException.ThrowIfNull(entry);
            if (entry.Owner != Identity) throw new ArgumentException("节点来自其他文档。", nameof(entry));
        }
    }

#nullable restore
    /// <summary>针对现有 VMCX 快照的单次编辑批次。修改先保存在内存，Commit 才写文件。</summary>
    /// <remarks>
    /// 实例不支持并发调用。丢弃实例即可取消未提交改动；无需释放文件句柄。
    /// Commit 无论成功或失败均结束此批次，后续操作应重新读取文件。
    /// </remarks>
    public sealed class VmcxEditSession
    {
    #nullable restore
        internal sealed class Node
        {
            internal int Id;
            internal int? ParentId;
            internal string Key = "";
            internal VmcxValue? Value;
            internal bool Selector;
            internal VmcxEntry? Original;
            internal uint Ordinal;
            internal Node Clone() => (Node)MemberwiseClone();
        }
        private readonly VmcxDocument document;
        private readonly Guid identity = Guid.NewGuid();
        private Dictionary<int, Node> nodes;
        private int nextId;
        private bool ended;

        internal VmcxEditSession(VmcxDocument document)
        {
            this.document = document;
            nodes = document.Entries.ToDictionary(e => e.Id, e => new Node
            {
                Id = e.Id, ParentId = e.ParentId, Key = e.Key, Value = e.Value, Selector = e.IsSelector,
                Original = e, Ordinal = (uint)e.Id
            });
            nextId = nodes.Keys.Max();
        }
        /// <summary>当前批次的节点快照；可包含尚未保存的新节点。</summary>
        public IReadOnlyList<VmcxEntry> Entries { get { RequireActive(); return Snapshot(); } }
        /// <summary>按路径查找当前批次的全部匹配项。重复路径必须由调用方进一步区分。</summary>
        /// <param name="path">完整逻辑路径，匹配不区分大小写。</param>
        /// <returns>当前批次的全部匹配节点；未命中时为空。</returns>
        public IReadOnlyList<VmcxEntry> Find(string path) => Array.AsReadOnly(Entries.Where(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase)).ToArray());
        /// <summary>读取当前批次中指定节点的直接子节点。</summary>
        /// <param name="parent">源文档或本批次的容器节点。</param>
        /// <returns>当前批次的直接子节点快照。</returns>
        public IReadOnlyList<VmcxEntry> GetChildren(VmcxEntry parent)
        {
            Node node = RequireNode(parent);
            return Array.AsReadOnly(Snapshot().Where(e => e.ParentId == node.Id).ToArray());
        }
        /// <summary>按节点身份修改值，要求类型与原节点相同。</summary>
        /// <param name="entry">源文档或本批次的值节点。</param>
        /// <param name="value">新值。</param>
        /// <exception cref="ArgumentException">类型不匹配、节点来自其他批次或已删除。</exception>
        public void SetValue(VmcxEntry entry, VmcxValue value)
        {
            ArgumentNullException.ThrowIfNull(value);
            Node node = RequireNode(entry);
            if (node.Value is null || node.Value.Type != value.Type) throw new ArgumentException("修改值必须保持原节点类型。", nameof(value));
            node.Value = value;
        }
        /// <summary>新增容器。允许同父同名容器，返回的节点身份用于后续精确编辑。</summary>
        /// <param name="parent">源文档或本批次的父容器。</param>
        /// <param name="key">单段键名，不含路径运算符或空字符，UTF-8 最长 254 字节。</param>
        /// <returns>新增容器，可用于同一批次的后续修改。</returns>
        public VmcxEntry AddContainer(VmcxEntry parent, string key) => Add(parent, key, null, false);
        /// <summary>在容器中新增类型值；同父同名的值拒绝重复。</summary>
        /// <param name="parent">源文档或本批次的父容器。</param>
        /// <param name="key">单段键名。</param>
        /// <param name="value">新值。</param>
        /// <param name="isSelector">是否用此值限定父容器。目前每个容器支持一个字符串选择键，其名称须以 ASCII 字母或数字开头。</param>
        /// <returns>新增值节点，可用于同一批次的后续修改。</returns>
        public VmcxEntry AddValue(VmcxEntry parent, string key, VmcxValue value, bool isSelector = false)
        {
            ArgumentNullException.ThrowIfNull(value);
            if (isSelector && (value.Type != VmcxValueType.String || string.IsNullOrEmpty(key) || !char.IsAsciiLetterOrDigit(key[0])))
                throw new NotSupportedException("VmDataStore 支持名称以 ASCII 字母或数字开头的字符串选择键。");
            if (isSelector && GetChildren(parent).Any(e => e.IsSelector))
                throw new NotSupportedException("一个容器只能创建一个选择键。");
            return Add(parent, key, value, isSelector);
        }
        /// <summary>删除节点及其整个子树；不能删除合成根节点。</summary>
        /// <param name="entry">源文档或本批次的目标节点。</param>
        public void RemoveSubtree(VmcxEntry entry)
        {
            Node node = RequireNode(entry);
            if (node.ParentId is null) throw new ArgumentException("不能删除根节点。", nameof(entry));
            var remove = new HashSet<int> { node.Id };
            bool added;
            do { added = false; foreach (Node n in nodes.Values) if (n.ParentId is int p && remove.Contains(p)) added |= remove.Add(n.Id); } while (added);
            foreach (int id in remove) nodes.Remove(id);
        }
        /// <summary>修改节点的单段键名，不改变父节点或类型。</summary>
        /// <param name="entry">源文档或本批次的目标节点。</param>
        /// <param name="key">新键名；允许同名容器，不允许同名值。</param>
        public void Rename(VmcxEntry entry, string key)
        {
            Node node = RequireNode(entry);
            if (node.ParentId is null) throw new ArgumentException("不能重命名根节点。", nameof(entry));
            ValidateKey(key);
            CheckSiblingKey(node.ParentId.Value, key, node.Value is null, node.Id);
            node.Key = key;
        }
        /// <summary>检查本批次当前的 manifest、设备结构和已知互斥规则。</summary>
        /// <returns>当前批次中已发现的结构问题。</returns>
        public IReadOnlyList<string> Validate() { RequireActive(); return VmcxValidation.Check(LegacyNodes()); }
        /// <summary>校验整批修改，备份后通过本机 VmDataStore 一次提交到原文件。</summary>
        /// <returns>是否修改、保留的备份路径以及前后哈希。无改动不创建备份。</returns>
        /// <remarks>提交前核对源文件并取得排他锁。失败时保留备份和当前文件，恢复材料通过 VmcxEditException 返回，由调用方处理恢复。</remarks>
        /// <exception cref="InvalidDataException">批次含已知结构错误。</exception>
        /// <exception cref="IOException">源文件已变化或无法访问。</exception>
        /// <exception cref="VmcxEditException">文件事务失败；异常包含备份和恢复状态。</exception>
        /// <exception cref="NotSupportedException">节点名称、类型与 selector 形态无法由原生接口无损重建；原文件不会被写入。</exception>
        public VmcxEditResult Commit()
        {
            RequireActive();
            var issues = Validate();
            ended = true;
            if (issues.Count != 0) throw new InvalidDataException("VMCX 校验未通过：" + string.Join("；", issues));
            if (!string.Equals(VmcxFileIO.HashFile(document.FilePath), document.SourceSha256, StringComparison.Ordinal))
                throw new IOException("源文件已变化，请重新读取并检查修改。");
            bool changed = nodes.Count != document.Entries.Count || nodes.Values.Any(n => n.Original is null
                || n.Key != n.Original.Key || n.Selector != n.Original.IsSelector || n.Value?.Text != n.Original.Value?.Text);
            if (!changed) return new(document.FilePath, false, null, document.SourceSha256, document.SourceSha256);
            return VmcxNativeEditor.Commit(document, Snapshot());
        }
        private VmcxEntry Add(VmcxEntry parent, string key, VmcxValue? value, bool selector)
        {
            Node p = RequireNode(parent);
            if (p.Value is not null) throw new ArgumentException("父节点必须是容器。", nameof(parent));
            ValidateKey(key);
            CheckSiblingKey(p.Id, key, value is null, null);
            uint ordinal = checked(nodes.Values.Where(n => n.ParentId == p.Id).Select(n => n.Ordinal).DefaultIfEmpty(0u).Max() + 1);
            var node = new Node { Id = checked(++nextId), ParentId = p.Id, Key = key, Value = value, Selector = selector, Ordinal = ordinal };
            nodes.Add(node.Id, node);
            return Snapshot().Single(e => e.Id == node.Id);
        }
        private void CheckSiblingKey(int parent, string key, bool container, int? except)
        {
            if (nodes.Values.Any(n => n.ParentId == parent && n.Id != except && n.Key.Equals(key, StringComparison.OrdinalIgnoreCase)
                    && (!container || n.Value is not null))) throw new ArgumentException("同一容器中已存在此名称的值或冲突节点。", nameof(key));
        }
        private static void ValidateKey(string key)
        {
            ArgumentException.ThrowIfNullOrEmpty(key);
            if (key.IndexOfAny(['/', '[', ']', '=', '"', '\0']) >= 0 || new UTF8Encoding(false, true).GetByteCount(key) > 254)
                throw new ArgumentException("键名须为 UTF-8 最长 254 字节的单段名称，不能包含路径运算符或空字符。", nameof(key));
        }
        private Node RequireNode(VmcxEntry entry)
        {
            RequireActive(); ArgumentNullException.ThrowIfNull(entry);
            if ((entry.Owner != document.Identity && entry.Owner != identity) || !nodes.TryGetValue(entry.Id, out Node? node))
                throw new ArgumentException("节点来自其他文档、批次，或已删除。", nameof(entry));
            return node;
        }
        private void RequireActive() { if (ended) throw new InvalidOperationException("此编辑批次已结束，请重新读取文件。"); }
        private IReadOnlyList<VmcxEntry> Snapshot()
        {
            var paths = new Dictionary<int, string>();
            string GetPath(Node node)
            {
                if (paths.TryGetValue(node.Id, out string? found)) return found;
                if (node.ParentId is null) return paths[node.Id] = "RootNode";
                var selectors = nodes.Values.Where(n => n.ParentId == node.Id && n.Selector).OrderBy(n => n.Ordinal).ToArray();
                string segment = node.Key + (selectors.Length == 0 ? "" : "[" + string.Join(',', selectors.Select(n => n.Key + "=\"" + n.Value!.Text.Replace("\"", "\\\"") + "\"")) + "]");
                string prefix = nodes[node.ParentId.Value].ParentId is null ? "" : GetPath(nodes[node.ParentId.Value]);
                return paths[node.Id] = prefix + "/" + segment;
            }
            return Array.AsReadOnly(nodes.Values.Select(n => new VmcxEntry(identity, n.Id, n.ParentId, n.Key, GetPath(n), n.Value, n.Selector)).ToArray());
        }
        private List<VmcxNode> LegacyNodes() => Snapshot().Select(e => new VmcxNode { Path = e.Path, IsValue = e.Value is not null,
            Value = e.Value?.Text ?? "", Type = e.Value?.Type switch { VmcxValueType.Integer => "Int", VmcxValueType.UnsignedInteger => "UInt", VmcxValueType.Raw => "Array", VmcxValueType.Boolean => "Bool", null => "Node", _ => e.Value.Type.ToString() } }).ToList();
    #nullable restore
        /// <summary>读取当前批次的设备清单。</summary>
        /// <returns>按 manifest 编号排列的设备描述，包含尚未提交的修改。</returns>
        public IReadOnlyList<VmcxDevice> GetDevices() { RequireActive(); return VmcxDeviceTemplates.ReadDevices(Snapshot()); }

        /// <summary>在现有配置中新增设备定义及 manifest 条目。</summary>
        /// <param name="kind">网卡、SCSI 控制器、GPU 分区或 DDA。</param>
        /// <param name="hostResource">DDA 必填的宿主设备路径；GPU 可选固定卡路径；其他种类必须为 null。</param>
        /// <returns>新增设备的实例 GUID，供同一批次后续操作使用。</returns>
        /// <remarks>网卡默认断开；GPU 默认使用通用池。</remarks>
        public Guid AddDevice(VmcxDeviceKind kind, string? hostResource = null) => Atomic(() =>
        {
            string type = kind switch
            {
                VmcxDeviceKind.NetworkAdapter => "2fc216b0-d2e2-4967-9b6d-b8a5c9ca2778",
                VmcxDeviceKind.ScsiController => "d422512d-2bf2-4752-809d-7b82b5fcb1b4",
                VmcxDeviceKind.GpuPartition => VmcxSchema.GpuPartitionType,
                VmcxDeviceKind.PcieDevice => "2fcc454e-a36a-4c77-bb5e-a2d75a51f02c",
                _ => throw new ArgumentOutOfRangeException(nameof(kind))
            };
            if (kind == VmcxDeviceKind.PcieDevice && string.IsNullOrWhiteSpace(hostResource))
                throw new ArgumentException("DDA 定义需要宿主设备路径。", nameof(hostResource));
            if (kind is VmcxDeviceKind.NetworkAdapter or VmcxDeviceKind.ScsiController && hostResource is not null)
                throw new ArgumentException("此设备种类不接受宿主资源路径。", nameof(hostResource));
            var issues = Validate();
            if (issues.Count != 0) throw new InvalidDataException("新增设备前请先修复配置：" + string.Join("；", issues));
            var template = VmcxDeviceTemplates.DeviceTemplates[type];
            VmcxEntry config = Unique("/configuration");
            VmcxEntry manifest = Unique("/configuration/manifest");
            VmcxEntry size = Child(manifest, "size");
            int number = checked((int)size.Value!.GetInteger() + 1);
            Guid instance = Guid.NewGuid();
            VmcxEntry vdev = AddContainer(manifest, "vdev" + number.ToString("D3", CultureInfo.InvariantCulture));
            AddValue(vdev, "device", VmcxValue.String(type));
            AddValue(vdev, "flags", VmcxValue.Integer(1));
            AddValue(vdev, "instance", VmcxValue.String(instance.ToString("D")));
            AddValue(vdev, "name", VmcxValue.String(template.Name));
            VmcxEntry root = AddContainer(config, "_" + instance.ToString("D") + "_");
            string setting = Guid.NewGuid().ToString("D").ToUpperInvariant();
            foreach (var field in template.Keys)
            {
                string key = field.Key.Replace("$S", setting);
                string text = field.Value switch
                {
                    "$B" => Guid.NewGuid().ToString("B").ToUpperInvariant(),
                    "$S" => setting,
                    "$MAC" => "00155D" + Convert.ToHexString(RandomNumberGenerator.GetBytes(3)),
                    "$P:devicepath" => hostResource!,
                    _ => field.Value
                };
                var value = new VmcxValue(field.Type, text);
                string[] segments = key.Split('/');
                VmcxEntry parent = root;
                foreach (string segment in segments[..^1])
                {
                    var found = GetChildren(parent).Where(e => e.Key == segment).ToArray();
                    parent = found.Length == 0 ? AddContainer(parent, segment) : found.Single();
                }
                AddValue(parent, segments[^1], value);
            }
            if (kind == VmcxDeviceKind.GpuPartition && !string.IsNullOrEmpty(hostResource))
                AddValue(root, "HostResource", VmcxValue.String(hostResource));
            SetValue(size, VmcxValue.Integer(number));
            return instance;
        });

        /// <summary>移除指定设备的数据子树及 manifest 条目，并连续重编号其余条目。</summary>
        /// <param name="instance">设备实例 GUID。</param>
        /// <remarks>删除设备的配置定义及 manifest 条目；数据节点缺失时仍可删除条目。宿主资源和磁盘文件保持原状。</remarks>
        public void RemoveDevice(Guid instance) => Atomic(() =>
        {
            var devices = GetDevices();
            VmcxDevice target = devices.SingleOrDefault(d => Guid.TryParse(d.Instance, out Guid id) && id == instance)
                ?? throw new ArgumentException("找不到设备实例。", nameof(instance));
            VmcxEntry manifest = Unique("/configuration/manifest");
            VmcxEntry vdev = Child(manifest, "vdev" + target.Number.ToString("D3", CultureInfo.InvariantCulture));
            RemoveSubtree(vdev);
            var roots = Find("/configuration/_" + instance.ToString("D") + "_");
            if (roots.Count > 1) throw new InvalidDataException("设备数据根重复。");
            if (roots.Count == 1) RemoveSubtree(roots[0]);
            int number = 0;
            foreach (var device in devices.Where(d => d.Number != target.Number).OrderBy(d => d.Number))
            {
                var remaining = Child(manifest, "vdev" + device.Number.ToString("D3", CultureInfo.InvariantCulture));
                Rename(remaining, "vdev" + (++number).ToString("D3", CultureInfo.InvariantCulture));
            }
            SetValue(Child(manifest, "size"), VmcxValue.Integer(number));
            return true;
        });

        /// <summary>向指定 SCSI 设备定义添加磁盘或光驱，选择该逻辑控制器下第一个空闲 drive 编号。</summary>
        /// <param name="scsiInstance">已有 SCSI 控制器的实例 GUID。</param>
        /// <param name="mediaPath">磁盘路径；光驱可为空以表示空介质。</param>
        /// <param name="optical">true 为 ISO 光驱，false 为 VHD/VHDX 磁盘。</param>
        /// <param name="controller">数据节点中的 controller 编号，必须非负。</param>
        /// <returns>新增 drive 容器，可通过通用字段编辑和 RemoveSubtree 继续操作。</returns>
        /// <remarks>介质路径按配置值写入；介质文件保持原状。</remarks>
        public VmcxEntry AddScsiDrive(Guid scsiInstance, string mediaPath, bool optical = false, int controller = 0) => Atomic(() =>
        {
            if (controller < 0) throw new ArgumentOutOfRangeException(nameof(controller));
            ArgumentNullException.ThrowIfNull(mediaPath);
            if (!optical && string.IsNullOrWhiteSpace(mediaPath)) throw new ArgumentException("磁盘路径不能为空。", nameof(mediaPath));
            if (!GetDevices().Any(d => d.DeviceType.Equals("d422512d-2bf2-4752-809d-7b82b5fcb1b4", StringComparison.OrdinalIgnoreCase)
                && Guid.TryParse(d.Instance, out Guid id) && id == scsiInstance)) throw new ArgumentException("指定实例不是 SCSI 控制器。", nameof(scsiInstance));
            VmcxEntry root = Unique("/configuration/_" + scsiInstance.ToString("D") + "_");
            string controllerKey = "controller" + controller.ToString(CultureInfo.InvariantCulture);
            var containers = GetChildren(root).Where(e => e.Key.Equals(controllerKey, StringComparison.OrdinalIgnoreCase)).ToArray();
            VmcxEntry container = containers.Length == 0 ? AddContainer(root, controllerKey) : containers.Single();
            var keys = GetChildren(container).Select(e => e.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            int drive = 0;
            while (keys.Contains("drive" + drive.ToString(CultureInfo.InvariantCulture))) drive++;
            VmcxEntry node = AddContainer(container, "drive" + drive.ToString(CultureInfo.InvariantCulture));
            AddValue(node, "pathname", VmcxValue.String(mediaPath));
            AddValue(node, "type", VmcxValue.String(optical ? "ISO" : "VHD"));
            if (!optical)
            {
                foreach (string key in new[] { "caching_mode", "iops_limit", "iops_reservation", "write_hardening_method" }) AddValue(node, key, VmcxValue.Integer(0));
                foreach (string key in new[] { "ignore_flushes", "persistent_reservations_supported" }) AddValue(node, key, VmcxValue.Boolean(false));
                AddValue(node, "qos_policy_id", VmcxValue.String(Guid.Empty.ToString("D")));
                AddValue(node, "weight", VmcxValue.Integer(100));
            }
            return node;
        });
        private VmcxEntry Unique(string path) => Find(path).Count == 1 ? Find(path)[0] : throw new InvalidDataException("路径未唯一命中：" + path);
        private VmcxEntry Child(VmcxEntry parent, string key) => GetChildren(parent).Single(e => e.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        private T Atomic<T>(Func<T> action)
        {
            RequireActive();
            var before = nodes.ToDictionary(p => p.Key, p => p.Value.Clone());
            int previousId = nextId;
            try { return action(); }
            catch { nodes = before; nextId = previousId; throw; }
        }
    }

    #endregion
    #region 数据类型与结果

#nullable restore
    /// <summary>现有 VMCX 中的一个节点快照。同名同路径节点仍各自具有独立身份。</summary>
    /// <remarks>Id 的有效范围为本次文档及编辑批次，保存后重新读取。</remarks>
    public sealed class VmcxEntry
    {
        /// <summary>节点在当前文档内的唯一标识。</summary>
        /// <fielddoc><meaning>节点在当前文档及其编辑批次中的身份，用于精确定位同名节点。</meaning><values kind="format">整数；仅在当前文档及编辑批次内有效。</values></fielddoc>
        public int Id { get; }
        /// <summary>父节点标识；根节点为 null。</summary>
        /// <fielddoc><meaning>直接父节点的身份。</meaning><values kind="format">与父节点的 <see cref="Id"/> 相同；根节点为 <c>null</c>。</values></fielddoc>
        public int? ParentId { get; }
        /// <summary>原始单段键名，不含 selector 谓词。</summary>
        public string Key { get; }
        /// <summary>用于展示和搜索的路径；同名容器或值可使路径不唯一。</summary>
        /// <fielddoc><meaning>节点在 VMCX 内部的逻辑路径，用于展示和搜索。同名节点可以具有相同路径。</meaning><values kind="format">以 <c>/</c> 分隔层级，例如 <c>/configuration/manifest</c>。</values></fielddoc>
        public string Path { get; }
        /// <summary>值节点的类型和值；容器为 null。</summary>
        /// <fielddoc><meaning>值节点保存的数据。</meaning><values kind="options">值节点返回 <see cref="VmcxValue"/>；容器节点为 <c>null</c>。</values></fielddoc>
        public VmcxValue? Value { get; }
        /// <summary>是否为容器。</summary>
        public bool IsContainer => Value is null;
        /// <summary>是否为限定父容器身份的 selector 值。</summary>
        public bool IsSelector { get; }
        internal Guid Owner { get; }
        internal VmcxEntry(Guid owner, int id, int? parentId, string key, string path, VmcxValue? value, bool selector)
        { Owner = owner; Id = id; ParentId = parentId; Key = key; Path = path; Value = value; IsSelector = selector; }
    }

#nullable restore
    /// <summary>VMCX 值节点支持的存储类型。</summary>
    public enum VmcxValueType
    {
        /// <summary>64 位有符号整数。</summary>
        Integer = 3,
        /// <summary>64 位无符号整数。</summary>
        UnsignedInteger = 4,
        /// <summary>有限双精度浮点数。</summary>
        Double = 5,
        /// <summary>UTF-16 字符串。</summary>
        String = 6,
        /// <summary>字节数组。</summary>
        Raw = 7,
        /// <summary>布尔值。</summary>
        Boolean = 8
    }

#nullable restore
    /// <summary>不可变的 VMCX 类型值。用于现有配置文件的字段读取和编辑。</summary>
    public sealed class VmcxValue
    {
        /// <summary>存储类型。</summary>
        public VmcxValueType Type { get; }
        /// <summary>不随区域设置变化的文本表示；Raw 为 Base64，Boolean 为 True/False。</summary>
        /// <fielddoc><meaning>当前类型值的文本表示，格式不随区域设置变化。</meaning><values kind="format">整数和浮点数使用十进制；字符串保留原文；二进制数据使用 Base64；布尔值为 <c>True</c> 或 <c>False</c>。</values></fielddoc>
        public string Text { get; }

        internal VmcxValue(VmcxValueType type, string text) { Type = type; Text = text; }
        /// <summary>构造有符号整数值。</summary>
        /// <param name="value">待保存的值。</param>
        /// <returns>有符号整数类型值。</returns>
        public static VmcxValue Integer(long value) => new(VmcxValueType.Integer, value.ToString(CultureInfo.InvariantCulture));
        /// <summary>构造无符号整数值。</summary>
        /// <param name="value">待保存的值。</param>
        /// <returns>无符号整数类型值。</returns>
        public static VmcxValue UnsignedInteger(ulong value) => new(VmcxValueType.UnsignedInteger, value.ToString(CultureInfo.InvariantCulture));
        /// <summary>构造有限双精度值；拒绝 NaN 和无穷大。</summary>
        /// <param name="value">待保存的值。</param>
        /// <exception cref="ArgumentOutOfRangeException">值不是有限数。</exception>
        /// <returns>双精度类型值。</returns>
        public static VmcxValue Double(double value) => double.IsFinite(value)
            ? new(VmcxValueType.Double, value.ToString("R", CultureInfo.InvariantCulture))
            : throw new ArgumentOutOfRangeException(nameof(value));
        /// <summary>构造字符串值，保留换行、空格和大小写。</summary>
        /// <param name="value">非 null 字符串。</param>
        /// <exception cref="ArgumentException">字符串包含无效 UTF-16。</exception>
        /// <returns>保存原文的字符串类型值。</returns>
        public static VmcxValue String(string value)
        {
            ArgumentNullException.ThrowIfNull(value);
            _ = new UnicodeEncoding(false, false, true).GetByteCount(value);
            return new(VmcxValueType.String, value);
        }
        /// <summary>复制数据并构造独立的二进制值。</summary>
        /// <param name="value">待保存的字节。</param>
        /// <returns>保存独立数据的二进制类型值。</returns>
        public static VmcxValue Raw(ReadOnlySpan<byte> value) => new(VmcxValueType.Raw, Convert.ToBase64String(value));
        /// <summary>构造布尔值。</summary>
        /// <param name="value">待保存的值。</param>
        /// <returns>布尔类型值。</returns>
        public static VmcxValue Boolean(bool value) => new(VmcxValueType.Boolean, value ? "True" : "False");

        /// <summary>读取有符号整数；类型不匹配时抛出 InvalidOperationException。</summary>
        /// <returns>64 位有符号整数。</returns>
        public long GetInteger() { Require(VmcxValueType.Integer); return long.Parse(Text, CultureInfo.InvariantCulture); }
        /// <summary>读取无符号整数；类型不匹配时抛出 InvalidOperationException。</summary>
        /// <returns>64 位无符号整数。</returns>
        public ulong GetUnsignedInteger() { Require(VmcxValueType.UnsignedInteger); return ulong.Parse(Text, CultureInfo.InvariantCulture); }
        /// <summary>读取双精度值；类型不匹配时抛出 InvalidOperationException。</summary>
        /// <returns>双精度浮点数。</returns>
        public double GetDouble() { Require(VmcxValueType.Double); return double.Parse(Text, CultureInfo.InvariantCulture); }
        /// <summary>读取字符串；类型不匹配时抛出 InvalidOperationException。</summary>
        /// <returns>字符串原文。</returns>
        public string GetString() { Require(VmcxValueType.String); return Text; }
        /// <summary>读取独立的字节数组；类型不匹配时抛出 InvalidOperationException。</summary>
        /// <returns>由调用方拥有的独立字节数组。</returns>
        public byte[] GetRaw() { Require(VmcxValueType.Raw); return Convert.FromBase64String(Text); }
        /// <summary>读取布尔值；类型不匹配时抛出 InvalidOperationException。</summary>
        /// <returns>布尔值。</returns>
        public bool GetBoolean() { Require(VmcxValueType.Boolean); return bool.Parse(Text); }
        private void Require(VmcxValueType type)
        {
            if (Type != type) throw new InvalidOperationException($"值类型为 {Type}，不是 {type}。");
        }
    }

#nullable disable
    /// <summary>VMCX 存储树中的容器或值节点快照。</summary>
    internal struct VmcxNode {
        /// <summary>存储树全路径，例如 /configuration/properties/version。</summary>
        public string Path;     // 全路径,如 /configuration/properties/version
        /// <summary>值节点的原生类型名称，例如 Int 或 String；容器无值类型。</summary>
        public string Type;     // Int / String / Bool / Double …(值节点)
        /// <summary>值的字符串表示；是否为值节点由 IsValue 判断。</summary>
        public string Value;    // 值的字符串表示(值节点)
        /// <summary>true 表示值节点，false 表示容器节点。</summary>
        public bool   IsValue;  // true=值,false=容器节点
    }

#nullable restore
    /// <summary>可向现有配置添加的设备定义种类。</summary>
    public enum VmcxDeviceKind
    {
        /// <summary>合成网卡，初始不连接虚拟交换机。</summary>
        NetworkAdapter,
        /// <summary>合成 SCSI 控制器。</summary>
        ScsiController,
        /// <summary>GPU 分区，可使用通用池或指定 HostResource。</summary>
        GpuPartition,
        /// <summary>离散设备分配（DDA）的配置定义。</summary>
        PcieDevice
    }

#nullable restore
    /// <summary>由 manifest 读取的设备描述。无效 GUID 保留原文，由 Validate 报告。</summary>
    public sealed class VmcxDevice
    {
        /// <summary>manifest 中的 vdev 编号。</summary>
        public int Number { get; }
        /// <summary>设备实例 GUID 文本，通常对应 configuration 下的同名数据容器。</summary>
        public string Instance { get; }
        /// <summary>设备类型 GUID 文本。</summary>
        public string DeviceType { get; }
        /// <summary>manifest 中的设备名称。</summary>
        public string Name { get; }
        internal VmcxDevice(int number, string instance, string type, string name)
        { Number = number; Instance = instance; DeviceType = type; Name = name; }
    }

#nullable restore
    /// <summary>一次 VMCX 文件提交的结果；备份由调用方保留或自行归档。</summary>
    public sealed class VmcxEditResult
    {
        /// <summary>被编辑文件的绝对路径。</summary>
        public string FilePath { get; }
        /// <summary>是否实际写入了文件。</summary>
        public bool Changed { get; }
        /// <summary>提交前完整备份的路径；无修改时为 null。</summary>
        public string? BackupPath { get; }
        /// <summary>提交前源文件 SHA-256。</summary>
        public string OriginalSha256 { get; }
        /// <summary>提交后文件 SHA-256。</summary>
        public string ResultSha256 { get; }
        internal VmcxEditResult(string path, bool changed, string? backup, string originalHash, string resultHash)
        { FilePath = path; Changed = changed; BackupPath = backup; OriginalSha256 = originalHash; ResultSha256 = resultHash; }
    }

#nullable restore
    /// <summary>文件提交失败，包含可用于人工恢复的路径和状态。</summary>
    public sealed class VmcxEditException : Exception
    {
        /// <summary>提交前的完整备份；尚未创建备份时为 null。</summary>
        public string? BackupPath { get; }
        /// <summary>目标可能已被原生提交或恢复改写；读取失败无法核对时也为 true。</summary>
        public bool TargetWasModified { get; }
        /// <summary>失败后核对目标哈希是否仍与读取时的源文件一致。</summary>
        public bool OriginalIntact { get; }
        /// <summary>状态核对失败的说明；能够核对时为 null。</summary>
        public string? RestoreError { get; }
        /// <summary>保留的恢复材料路径。调用方应核对目标和备份后再决定是否恢复。</summary>
        public IReadOnlyList<string> RecoveryPaths { get; }
        internal VmcxEditException(Exception cause, string? backup, bool intact, string? stateError)
            : base("VMCX 原生提交失败：" + cause.Message + (intact ? "；原文件哈希未变。" : "；请核对目标文件与保留的备份。"), cause)
        { BackupPath = backup; TargetWasModified = !intact; OriginalIntact = intact; RestoreError = stateError;
          RecoveryPaths = Array.AsReadOnly(backup is null ? Array.Empty<string>() : new[] { backup }); }
    }

#nullable disable
    /// <summary>表示 VMCX 原生存储操作失败，保留其返回码供诊断。</summary>
    public sealed class VmcxException : Exception {
        /// <summary>原生调用返回的 HRESULT；与异常继承的 HResult 属性分开保存。</summary>
        public int HResultCode;
        /// <summary>创建带原生 HRESULT 的 VMCX 失败异常。</summary>
        /// <param name="msg">失败操作说明。</param>
        /// <param name="hr">原生 HRESULT 或封装定义的失败码。</param>
        public VmcxException(string msg, int hr) : base(msg) { HResultCode = hr; }
    }

    #endregion
    #region DLL 调用与原生资源

#nullable disable
    /// <summary>使用 Windows VmDataStore 打开并读写离线 VMCX 配置。</summary>
    /// <remarks>文件须可独占访问。实例及写入锁在创建线程使用和释放。打开时 DLL 可执行存储恢复；只读快照和带备份的批量编辑使用 VmcxDocument。</remarks>
    internal sealed class VmcxStore : IDisposable {
    #nullable disable
        [DllImport("kernel32", CharSet=CharSet.Unicode)] static extern IntPtr LoadLibrary(string s);
        [DllImport("kernel32")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool FreeLibrary(IntPtr module);
        [DllImport("kernel32")] static extern IntPtr GetProcAddress(IntPtr h, string n);
        [DllImport("combase", CharSet=CharSet.Unicode)] static extern int WindowsCreateString(string s, uint l, out IntPtr h);
        [DllImport("combase")] static extern IntPtr WindowsGetStringRawBuffer(IntPtr h, out uint len);
        [DllImport("combase")] static extern int WindowsDeleteString(IntPtr h);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int DGetFac(IntPtr h, out IntPtr f);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int DQI(IntPtr s, ref Guid i, out IntPtr p);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int DCreate(IntPtr self, IntPtr path, uint mode, out IntPtr store);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int DLock(IntPtr self, byte excl);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int DVoid(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate uint DRel(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int DGetI(IntPtr self, IntPtr key, out long v);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int DSetI(IntPtr self, IntPtr key, long v);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int DGetS(IntPtr self, IntPtr key, out IntPtr v);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int DSetS(IntPtr self, IntPtr key, IntPtr v);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int DRemove(IntPtr self, IntPtr key);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int DOut(IntPtr self, out IntPtr o);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int DByte(IntPtr self, out byte b);

        const int SET_INT=6, GET_INT=7, SET_STR=14, GET_STR=15, REMOVE=21;
        static readonly Guid IID_STATICS = new Guid("04ce619a-6775-4f29-be77-b4e2bc2dda3a");
        static readonly Guid IID_KVS     = new Guid("6de696aa-007c-4612-8392-7e2143eef6db");
        static readonly Guid IID_ITER    = new Guid("968eec6a-c6bf-50e3-ac88-ac10a4c163b5");

        // One successful module reference and statics interface remain process-owned.
        // Individual stores/writers release their own interfaces/locks, never this factory.
        // No unload API: live store vtables and the shared create entry depend on the module.
        static IntPtr s_module, s_statics, s_create;
        static readonly object s_initLock = new object();
        IntPtr _store, _ikv;
        string _path;
        bool _locked;
        readonly int _thread = Environment.CurrentManagedThreadId;
        readonly Apartment _apartment = new Apartment();
        bool _disposed;

        static void EnsureInit() {
            lock (s_initLock) {
                if (s_statics != IntPtr.Zero) return;
                (s_module, s_statics, s_create) = InitializeFactory(FactoryNative.Instance);
            }
        }

        // Return transfers the module/statics references. Every failure releases what it acquired.
        internal static (IntPtr Module, IntPtr Statics, IntPtr Create) InitializeFactory(IFactoryNative native) {
            IntPtr module = IntPtr.Zero, name = IntPtr.Zero, factory = IntPtr.Zero, statics = IntPtr.Zero;
            bool retained = false;
            try {
                module = native.LoadModule();
                if (module == IntPtr.Zero) throw new InvalidOperationException(Properties.Resources.Vmcx_DllLoadFail);
                IntPtr create;
                try {
                    Hr(native.CreateClassName(out name), "WindowsCreateString");
                    Hr(native.GetFactory(module, name, out factory), "GetActivationFactory");
                    if (factory == IntPtr.Zero) throw new InvalidOperationException("VmDataStore returned a null activation factory.");
                    Hr(native.QueryStatics(factory, out statics), "QI IKeyValueStoreStatics");
                    if (statics == IntPtr.Zero) throw new InvalidOperationException("VmDataStore returned a null statics interface.");
                    create = native.GetCreateEntry(statics);
                    if (create == IntPtr.Zero) throw new EntryPointNotFoundException("IKeyValueStoreStatics.CreateInstance");
                } finally {
                    try { if (factory != IntPtr.Zero) native.Release(factory); }
                    finally { if (name != IntPtr.Zero) native.DeleteString(name); }
                }
                retained = true;
                return (module, statics, create);
            } finally {
                if (!retained) {
                    try { if (statics != IntPtr.Zero) native.Release(statics); }
                    finally { if (module != IntPtr.Zero) native.FreeModule(module); }
                }
            }
        }

        internal interface IFactoryNative {
            IntPtr LoadModule();
            int CreateClassName(out IntPtr name);
            int GetFactory(IntPtr module, IntPtr name, out IntPtr factory);
            int QueryStatics(IntPtr factory, out IntPtr statics);
            IntPtr GetCreateEntry(IntPtr statics);
            void Release(IntPtr value);
            void DeleteString(IntPtr name);
            void FreeModule(IntPtr module);
        }

        sealed class FactoryNative : IFactoryNative {
            internal static readonly FactoryNative Instance = new FactoryNative();
            public IntPtr LoadModule() => LoadLibrary(System.IO.Path.Combine(Environment.SystemDirectory, "VmDataStore.dll"));
            public int CreateClassName(out IntPtr name) {
                const string className = "Microsoft.HyperV.DataStore.KeyValueStore";
                return WindowsCreateString(className, (uint)className.Length, out name);
            }
            public int GetFactory(IntPtr module, IntPtr name, out IntPtr factory) {
                IntPtr entry = GetProcAddress(module, "DllGetActivationFactory");
                if (entry == IntPtr.Zero) throw new EntryPointNotFoundException("VmDataStore.DllGetActivationFactory");
                return D<DGetFac>(entry)(name, out factory);
            }
            public int QueryStatics(IntPtr factory, out IntPtr statics) {
                Guid iid = IID_STATICS;
                return D<DQI>(Slot(factory, 0))(factory, ref iid, out statics);
            }
            public IntPtr GetCreateEntry(IntPtr statics) => Slot(statics, 6);
            public void Release(IntPtr value) => D<DRel>(Slot(value, 2))(value);
            public void DeleteString(IntPtr name) => WindowsDeleteString(name);
            public void FreeModule(IntPtr module) => FreeLibrary(module);
        }

        private VmcxStore() { }

        /// <summary>打开 VMCX 数据存储并取得本实例持有的原生接口。</summary>
        /// <returns>必须 Dispose 的存储实例；原生失败抛 VmcxException，原生库加载等错误也可传播。</returns>
        /// <param name="path">离线 VMCX 文件完整路径；不能为空。</param>
        public static VmcxStore Open(string path) {
            if (string.IsNullOrEmpty(path)) throw new ArgumentNullException("path");
            string fullPath = System.IO.Path.GetFullPath(path);
            var st = new VmcxStore { _path = fullPath };
            bool ok = false;
            try {
                EnsureInit();
                IntPtr hp = MakeHStr(st._path);
                try { Hr(D<DCreate>(s_create)(s_statics, hp, 0, out st._store), "CreateInstance(" + path + ")"); }
                finally { WindowsDeleteString(hp); }
                Guid k = IID_KVS;
                Hr(D<DQI>(Slot(st._store, 0))(st._store, ref k, out st._ikv), "QI IKeyValueStore");
                ok = true;
                return st;
            } finally { if (!ok) st.Dispose(); }
        }

        /// <summary>读取指定存储键的整数值。</summary>
        /// <returns>原生有符号 64 位整数；键缺失或类型错误等原生失败抛 VmcxException。</returns>
        /// <param name="keyPath">VMCX 存储树中的节点全路径。</param>
        public long   GetInteger(string keyPath){ long v=0; Hr(WithKey(keyPath, k=>D<DGetI>(Slot(_ikv,GET_INT))(_ikv,k, out v)), "GetInteger"); return v; }
        // out HSTRING 归调用方所有:复制成托管串后须 WindowsDeleteString 归还(与 Enumerate 同一约定;NULL 句柄删除为 no-op)
        /// <summary>读取指定存储键并复制为托管字符串，释放临时原生字符串。</summary>
        /// <returns>字符串内容；原生失败抛 VmcxException。</returns>
        /// <param name="keyPath">存储树全路径。</param>
        public string GetString (string keyPath){
            IntPtr h = IntPtr.Zero;
            try {
                Hr(WithKey(keyPath, k=>D<DGetS>(Slot(_ikv,GET_STR))(_ikv,k, out h)), "GetString");
                return FromHStr(h);
            } finally { if (h != IntPtr.Zero) WindowsDeleteString(h); }
        }

        int WithKey(string keyPath, Func<IntPtr,int> call){ ThrowIfDisposed(); IntPtr h=MakeHStr(keyPath); try { return call(h); } finally { WindowsDeleteString(h); } }

        /// <summary>枚举整棵存储树并复制容器和值节点到托管列表。</summary>
        /// <returns>独立节点快照；原生枚举错误抛 VmcxException，迭代器会在异常路径释放。</returns>
        public List<VmcxNode> Enumerate() {
            using var apartment = new Apartment();
            var entries = ReadEntries(Guid.NewGuid());
            var children = entries.Where(e => e.ParentId is not null).ToLookup(e => e.ParentId.Value);
            var result = new List<VmcxNode>();
            void Visit(VmcxEntry entry) {
                var node = new VmcxNode { Path = entry.Path, IsValue = entry.Value is not null,
                    Value = entry.Value?.Text ?? "", Type = entry.Value?.Type switch {
                        VmcxValueType.Integer => "Int", VmcxValueType.UnsignedInteger => "UInt", VmcxValueType.Boolean => "Bool",
                        VmcxValueType.Raw => "Array", null => "Node", _ => entry.Value.Type.ToString() } };
                result.Add(node);
                foreach (var child in children[entry.Id]) Visit(child);
                if (entry.IsContainer) result.Add(node);
            }
            Visit(entries[0]);
            return result;
        }

        /// <summary>通过统一文件编辑批次移除设备，并连续重排 manifest。</summary>
        /// <param name="instanceGuid">设备实例 GUID。</param>
        /// <returns>删除前的设备编号。</returns>
        /// <remarks>须在写入器之外调用；提交时保留文件备份。</remarks>
        public int RemoveDevice(string instanceGuid) {
            ThrowIfDisposed();
            if (_locked) throw new InvalidOperationException("请先释放写入器。");
            var doc = VmcxDocument.Read(_path);
            Guid id = Guid.Parse(instanceGuid.Trim('{', '}', '_', ' '));
            int number = doc.GetDevices().Single(d => Guid.TryParse(d.Instance, out Guid found) && found == id).Number;
            var edit = doc.BeginEdit(); edit.RemoveDevice(id); edit.Commit();
            // 文件提交创建了独立原生存储；重新打开，避免本实例继续读取旧的缓存树。
            using (var refreshed = Open(_path)) {
                ReleaseInterfaces();
                _store = refreshed._store; _ikv = refreshed._ikv;
                refreshed._store = refreshed._ikv = IntPtr.Zero;
            }
            return number;
        }
        /// <summary>按统一规则检查 manifest 和设备结构。</summary>
        /// <returns>已发现的结构问题；未发现问题时为空列表。</returns>
        public List<string> ValidateManifest() => Internal.VmcxValidation.ValidateManifest(Enumerate());

        /// <summary>取得存储的排他写入锁并返回写入器。</summary>
        /// <remarks>写入器释放前应保持所属存储有效；不要嵌套获取同一存储写入锁。</remarks>
        /// <returns>调用方必须 Dispose 的写入器；保存修改须显式 Commit。</returns>
        public VmcxWriter BeginWrite() {
            ThrowIfDisposed();
            if (_locked) throw new InvalidOperationException("不能嵌套写入器。");
            Hr(D<DLock>(Slot(_ikv, 25))(_ikv, 1), "Lock");
            _locked = true;
            return new VmcxWriter(this);
        }

        /// <summary>关闭本存储持有的原生接口；不卸载进程共享的 VmDataStore 工厂。</summary>
        /// <remarks>应先释放关联写入器；释放后不得继续调用读写方法。</remarks>
        public void Dispose() {
            if (_disposed) return;
            ThrowIfDisposed();
            try { if (_locked) DoUnlock(); }
            finally { _disposed = true; ReleaseInterfaces(); _apartment.Dispose(); }
        }

        void ReleaseInterfaces() {
            try { if (_ikv != IntPtr.Zero) D<DRel>(Slot(_ikv, 2))(_ikv); } catch { }
            try { if (_store != IntPtr.Zero) D<DRel>(Slot(_store, 2))(_store); } catch { }
            _ikv = _store = IntPtr.Zero;
        }

        internal void DoSetInteger(string k, long v){ Hr(WithKey(k, h=>D<DSetI>(Slot(_ikv,SET_INT))(_ikv,h,v)), "SetInteger"); }
        internal void DoSetString (string k, string v){
            ThrowIfDisposed();
            if (v is not null && v.Contains('\0')) { SetStringWithNulls(k, v); return; }
            IntPtr hv=MakeHStr(v); try { Hr(WithKey(k, h=>D<DSetS>(Slot(_ikv,SET_STR))(_ikv,h,hv)), "SetString"); } finally { WindowsDeleteString(hv); }
        }
        internal void DoRemove    (string k){ Hr(WithKey(k, h=>D<DRemove>(Slot(_ikv,REMOVE))(_ikv,h)), "Remove"); }
        internal void DoCommit(){ ThrowIfDisposed(); Hr(D<DVoid>(Slot(_ikv, 32))(_ikv), "Commit"); }
        internal void DoUnlock(){ if (_disposed) return; ThrowIfDisposed(); if (!_locked) return; Hr(D<DVoid>(Slot(_ikv, 26))(_ikv), "Unlock"); _locked = false; }

        static IntPtr Slot(IntPtr o, int i){ IntPtr vt=Marshal.ReadIntPtr(o); return Marshal.ReadIntPtr(vt, i*IntPtr.Size); }
        static T D<T>(IntPtr f){ return Marshal.GetDelegateForFunctionPointer<T>(f); }
        static IntPtr MakeHStr(string s){ IntPtr h; Hr(WindowsCreateString(s ?? "", (uint)(s ?? "").Length, out h), "WindowsCreateString"); return h; }
        static string FromHStr(IntPtr h){ if(h==IntPtr.Zero) return ""; uint l; IntPtr b=WindowsGetStringRawBuffer(h, out l); return b==IntPtr.Zero?"":Marshal.PtrToStringUni(b,(int)l); }
        static void Hr(int hr, string what){ if(hr<0) throw new VmcxException(string.Format(Properties.Resources.Vmcx_OpFailHr, what, hr.ToString("X8")), hr); }
        void ThrowIfDisposed(){ if(_disposed) throw new ObjectDisposedException("VmcxStore"); if(_thread != Environment.CurrentManagedThreadId) throw new InvalidOperationException("VMCX 原生存储须在创建它的线程使用和释放。"); }
    #nullable restore
        // 使用 IKeyValueStore 的前 33 个接口槽位；后续扩展接口的位置随 DLL 版本变化。
        // 文件头部及数据版本由原生存储维护。
        [DllImport("combase", ExactSpelling = true)] private static extern int RoInitialize(uint mode);
        [DllImport("combase", ExactSpelling = true)] private static extern void RoUninitialize();
        [DllImport("combase", ExactSpelling = true)] private static extern int RoGetActivationFactory(nint name, in Guid iid, out nint factory);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int DUIntOut(nint self, out uint value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int DUIntIn(nint self, uint value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int DBuffer(nint self, uint capacity, out nint buffer);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int DData(nint self, nint buffer, out uint used);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int DSetUInt(nint self, nint key, ulong value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int DSetDouble(nint self, nint key, double value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int DSetBool(nint self, nint key, byte value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int DPtr(nint self, nint value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int DImport(nint self, nint stream, int type, byte fragment, nint prefix);

        internal sealed class Apartment : IDisposable
        {
            private readonly bool initialized;
            internal Apartment()
            {
                int hr = RoInitialize(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA ? 0u : 1u);
                if (hr < 0 && hr != unchecked((int)0x80010106)) Hr(hr, "RoInitialize");
                initialized = hr >= 0;
            }
            public void Dispose() { if (initialized) RoUninitialize(); }
        }

        internal static VmcxStore CreateTransient()
        {
            nint factory = 0;
            var result = new VmcxStore();
            try
            {
                EnsureInit();
                Guid iid = new("01d7d0f9-d311-4699-8a4c-75096b7e597e");
                Hr(D<DQI>(Slot(s_statics, 0))(s_statics, ref iid, out factory), "QI IKeyValueStoreStatics");
                Hr(D<DOut>(Slot(factory, 6))(factory, out result._store), "CreateTransientInstance");
                iid = IID_KVS;
                Hr(D<DQI>(Slot(result._store, 0))(result._store, ref iid, out result._ikv), "QI IKeyValueStore");
                return result;
            }
            catch { result.Dispose(); throw; }
            finally { if (factory != 0) Marshal.Release(factory); }
        }
        internal void ImportXml(byte[] xml, string prefix = "")
        {
            ThrowIfDisposed();
            using var stream = new VmcxNativeStream(xml);
            Hr(WithKey(prefix, key => D<DImport>(Slot(_ikv, 29))(_ikv, stream.Pointer, 0, 1, key)), "ReadFromStream(XML)");
        }
        internal void SetValue(string path, VmcxValue value)
        {
            ThrowIfDisposed();
            switch (value.Type)
            {
                case VmcxValueType.Integer: DoSetInteger(path, value.GetInteger()); break;
                case VmcxValueType.UnsignedInteger:
                    Hr(WithKey(path, k => D<DSetUInt>(Slot(_ikv, 8))(_ikv, k, value.GetUnsignedInteger())), "SetUnsignedInteger"); break;
                case VmcxValueType.Double:
                    Hr(WithKey(path, k => D<DSetDouble>(Slot(_ikv, 12))(_ikv, k, value.GetDouble())), "SetFloatingPoint"); break;
                case VmcxValueType.Boolean:
                    Hr(WithKey(path, k => D<DSetBool>(Slot(_ikv, 10))(_ikv, k, value.GetBoolean() ? (byte)1 : (byte)0)), "SetBoolean"); break;
                case VmcxValueType.String: DoSetString(path, value.GetString()); break;
                case VmcxValueType.Raw:
                    using (var buffer = new NativeBuffer(value.GetRaw()))
                        Hr(WithKey(path, k => D<DSetS>(Slot(_ikv, 16))(_ikv, k, buffer.Pointer)), "SetRaw");
                    break;
                default: throw new NotSupportedException("未知的 VMCX 值类型。");
            }
        }

        // SetStringValue 按零结尾字符串取长度；VARIANT 的 BSTR 分支保留显式长度。
        // 仅包含空字符时使用此分支，仍由 DLL 负责值的类型和持久化。
        private void SetStringWithNulls(string path, string value)
        {
            nint wrapper = 0, payload = 0, bstr = 0;
            try
            {
                DoSetString(path, "");
                Hr(WithKey(path, key => D<DGetS>(Slot(_ikv, 19))(_ikv, key, out wrapper)), "GetVariant");
                int size = IntPtr.Size == 8 ? 24 : 16;
                payload = Marshal.AllocCoTaskMem(size);
                for (int offset = 0; offset < size; offset += 8) Marshal.WriteInt64(payload, offset, 0);
                bstr = Marshal.StringToBSTR(value);
                Marshal.WriteInt16(payload, 8); // VT_BSTR
                Marshal.WriteIntPtr(payload, 8, bstr);
                Hr(D<DPtr>(Slot(wrapper, 7))(wrapper, payload), "Variant.SetValue");
                Hr(WithKey(path, key => D<DSetS>(Slot(_ikv, 18))(_ikv, key, wrapper)), "SetVariant");
            }
            finally
            {
                if (wrapper != 0) Marshal.Release(wrapper);
                if (payload != 0) Marshal.FreeCoTaskMem(payload);
                if (bstr != 0) Marshal.FreeBSTR(bstr);
            }
        }

        internal List<VmcxEntry> ReadEntries(Guid owner)
        {
            ThrowIfDisposed();
            var entries = new List<VmcxEntry>();
            var ancestors = new List<int>();
            nint iterable = 0, iterator = 0;
            try
            {
                Guid iid = IID_ITER;
                Hr(D<DQI>(Slot(_store, 0))(_store, ref iid, out iterable), "QI IIterable");
                Hr(D<DOut>(Slot(iterable, 6))(iterable, out iterator), "First");
                Hr(D<DByte>(Slot(iterator, 7))(iterator, out byte has), "HasCurrent");
                int visited = 0;
                while (has != 0)
                {
                    if (++visited > 1_000_000) throw new InvalidDataException("VMCX 节点过多。");
                    nint node = 0;
                    try
                    {
                        Hr(D<DOut>(Slot(iterator, 6))(iterator, out node), "Current");
                        Hr(D<DByte>(Slot(node, 16))(node, out byte closed), "LastIncrementClosedNode");
                        Hr(D<DUIntOut>(Slot(node, 17))(node, out uint depth), "GetTreeDepth");
                        if (closed == 0)
                        {
                            if (depth > 256 || depth > ancestors.Count) throw new InvalidDataException("VMCX 节点层级无效或超过 256 层。");
                            while (ancestors.Count > depth) ancestors.RemoveAt(ancestors.Count - 1);
                            Hr(D<DByte>(Slot(node, 7))(node, out byte isValue), "IsValue");
                            byte selector = 0;
                            if (depth != 0) Hr(D<DByte>(Slot(node, 8))(node, out selector), "IsSubComponent");
                            string path = NodeString(node, 9);
                            string name = depth == 0 ? "RootNode" : NodeString(node, 10);
                            if (isValue == 0 && name.IndexOf('[') is int bracket && bracket >= 0) name = name[..bracket];
                            VmcxValue? value = isValue == 0 ? null : ReadValue(node);
                            int id = entries.Count;
                            entries.Add(new(owner, id, depth == 0 ? null : ancestors[^1], name, path, value, selector != 0));
                            if (isValue == 0) ancestors.Add(id);
                        }
                    }
                    finally { if (node != 0) Marshal.Release(node); }
                    Hr(D<DByte>(Slot(iterator, 8))(iterator, out has), "MoveNext");
                }
            }
            finally { if (iterator != 0) Marshal.Release(iterator); if (iterable != 0) Marshal.Release(iterable); }
            return entries;
        }
        private static string NodeString(nint node, int index)
        {
            nint value = 0;
            try { Hr(D<DOut>(Slot(node, index))(node, out value), "NodeString"); return FromHStr(value); }
            finally { if (value != 0) WindowsDeleteString(value); }
        }
        private static VmcxValue ReadValue(nint node)
        {
            Hr(D<DUIntOut>(Slot(node, 13))(node, out uint type), "GetDataType");
            Hr(D<DUIntOut>(Slot(node, 14))(node, out uint length), "GetDataLength");
            if (length > VmcxDocument.MaximumLength) throw new InvalidDataException("VMCX 值过大。");
            using var buffer = new NativeBuffer(checked((int)length));
            Hr(D<DData>(Slot(node, 15))(node, buffer.Pointer, out uint used), "GetDataValue");
            if (used > length) throw new InvalidDataException("VmDataStore 返回了不一致的数据长度。");
            byte[] data = buffer.Read(checked((int)used));
            return (VmcxValueType)type switch
            {
                VmcxValueType.Integer when data.Length == 8 => VmcxValue.Integer(BinaryPrimitives.ReadInt64LittleEndian(data)),
                VmcxValueType.UnsignedInteger when data.Length == 8 => VmcxValue.UnsignedInteger(BinaryPrimitives.ReadUInt64LittleEndian(data)),
                VmcxValueType.Double when data.Length == 8 && double.IsFinite(BitConverter.ToDouble(data)) => VmcxValue.Double(BitConverter.ToDouble(data)),
                VmcxValueType.Boolean when data.Length == 4 => VmcxValue.Boolean(BinaryPrimitives.ReadUInt32LittleEndian(data) != 0),
                VmcxValueType.String when data.Length % 2 == 0 => new(VmcxValueType.String, new UnicodeEncoding(false, false, true).GetString(data)),
                VmcxValueType.Raw => VmcxValue.Raw(data),
                _ => throw new NotSupportedException($"VmDataStore 返回了未支持的值类型或长度：{type}/{data.Length}。")
            };
        }
        private sealed class NativeBuffer : IDisposable
        {
            internal nint Pointer { get; private set; }
            private nint bytes;
            internal NativeBuffer(int capacity)
            {
                nint name = MakeHStr("Windows.Storage.Streams.Buffer"), factory = 0, access = 0, buffer = 0;
                try
                {
                    Guid iid = new("71af914d-c10f-484b-bc50-14bc623b3a27");
                    Hr(RoGetActivationFactory(name, iid, out factory), "Buffer factory");
                    Hr(D<DBuffer>(Slot(factory, 6))(factory, (uint)capacity, out buffer), "Buffer.Create");
                    Hr(D<DUIntIn>(Slot(buffer, 8))(buffer, (uint)capacity), "Buffer length");
                    iid = new("905a0fef-bc53-11df-8c49-001e4fc686da");
                    Hr(D<DQI>(Slot(buffer, 0))(buffer, ref iid, out access), "IBufferByteAccess");
                    Hr(D<DOut>(Slot(access, 3))(access, out bytes), "Buffer bytes");
                    Pointer = buffer; buffer = 0;
                }
                finally
                {
                    if (buffer != 0) Marshal.Release(buffer);
                    if (access != 0) Marshal.Release(access);
                    if (factory != 0) Marshal.Release(factory);
                    WindowsDeleteString(name);
                }
            }
            internal NativeBuffer(byte[] data) : this(data.Length)
            {
                try { Marshal.Copy(data, 0, bytes, data.Length); Hr(D<DUIntIn>(Slot(Pointer, 8))(Pointer, (uint)data.Length), "Buffer length"); }
                catch { Dispose(); throw; }
            }
            internal byte[] Read(int length) { var data = new byte[length]; Marshal.Copy(bytes, data, 0, length); return data; }
            public void Dispose() { if (Pointer != 0) { Marshal.Release(Pointer); Pointer = 0; bytes = 0; } }
        }
    }

#nullable disable
    /// <summary>持有 VMCX 排他写入锁的写入器，供一次受控修改和显式提交使用。</summary>
    /// <remarks>Set/Remove 修改原生存储，Commit 提交落盘，Dispose 释放写入锁。每次 Commit 独立提交。</remarks>
    internal sealed class VmcxWriter : IDisposable {
        readonly VmcxStore _s; bool _done;
        internal VmcxWriter(VmcxStore s){ _s = s; }
        /// <summary>在当前写入锁内设置整数键，等待显式提交。</summary>
        /// <remarks>原生失败抛 VmcxException；写入器和所属存储必须仍然有效。</remarks>
        /// <param name="keyPath">存储树全路径。</param>
        /// <param name="v">要写入的有符号 64 位整数。</param>
        public void SetInteger(string keyPath, long v){ RequireActive(); _s.DoSetInteger(keyPath, v); }
        /// <summary>在当前写入锁内设置字符串键，等待显式提交。</summary>
        /// <remarks>原生失败抛 VmcxException。</remarks>
        /// <param name="keyPath">存储树全路径。</param>
        /// <param name="v">要写入的字符串。</param>
        public void SetString (string keyPath, string v){ RequireActive(); _s.DoSetString(keyPath, v); }
        /// <summary>在当前写入锁内移除指定键，等待显式提交。</summary>
        /// <remarks>此方法删除指定键；删除完整设备并更新 manifest 使用 VmcxStore.RemoveDevice。</remarks>
        /// <param name="keyPath">要删除的存储树键路径。</param>
        public void Remove    (string keyPath){ RequireActive(); _s.DoRemove(keyPath); }
        /// <summary>调用原生存储提交当前修改；提交后仍由 Dispose 释放写入锁。</summary>
        /// <remarks>每次调用独立提交当前修改；原生失败抛 VmcxException。</remarks>
        public void Commit(){ RequireActive(); _s.DoCommit(); }
        void RequireActive(){ if(_done) throw new ObjectDisposedException(nameof(VmcxWriter)); }
        /// <summary>释放当前写入锁；重复调用不会重复解锁。</summary>
        /// <remarks>释放锁后保留已提交修改，结束当前写入器；保存修改须先调用 Commit。</remarks>
        public void Dispose(){ if(_done) return; _s.DoUnlock(); _done = true; }
    }

    #endregion
    #region 配置结构与元数据

#nullable disable
    /// <summary>解释离线 VMCX 中 GPU 分区及功能设备的固定结构键。</summary>
    internal static class VmcxSchema {

        /// <summary>数据节点不可为空的内置功能设备类型 GUID 集合，只读且匹配不区分大小写。</summary>
        public static readonly IReadOnlySet<string> FunctionalDeviceTypes = new[] {
            "2fc216b0-d2e2-4967-9b6d-b8a5c9ca2778", // Synthetic Ethernet Port (NIC)
            "d422512d-2bf2-4752-809d-7b82b5fcb1b4", // Synthetic SCSI Controller
            "2fcc454e-a36a-4c77-bb5e-a2d75a51f02c", // Virtual Pci Express Port (DDA)
            "99dcd00c-fbd6-42d3-9dfd-1b5ad7058f61", // GPU Partition
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        /// <summary>GPU Partition 设备类型的固定 GUID 字符串。</summary>
        public const string GpuPartitionType = "99dcd00c-fbd6-42d3-9dfd-1b5ad7058f61";

        /// <summary>从 VMCX manifest 与数据节点解析出的 GPU 分区设备描述。</summary>
        /// <remarks>通用池设备可省略 HostResource。结构检查使用各字段的存在标志。</remarks>
        public sealed class VmcxGpuPv {
            /// <summary>设备在 manifest 中的 vdev 编号。</summary>
            public int    VdevNumber;
            /// <summary>设备实例 GUID 文本，不带外围括号，用于匹配数据节点。</summary>
            public string Instance;      // manifest 实例 GUID(= 数据节点 /_<instance>_)
            /// <summary>固定物理 GPU 的宿主路径；空字符串表示未找到固定路径或使用自动池匹配。</summary>
            public string HostResource;  // 钉死的物理 GPU 路径;"" = 自动池匹配
            /// <summary>数据节点是否实际包含 HostResource 键，与其值为空区分。</summary>
            public bool HostResourcePresent;
            /// <summary>数据节点是否含 InstanceGuid 键。</summary>
            public bool InstanceGuidPresent;
            /// <summary>数据节点是否含 PoolID 键；旧系统可能省略默认池值。</summary>
            public bool PoolIdPresent;
            /// <summary>数据节点是否含 VDEVVersion 键。</summary>
            public bool VdevVersionPresent;

            // 未钉死物理卡的合法 GPU-PV 可以没有 HostResource，但官方创建的
            // 通用池设备仍包含 InstanceGuid 和 VDEVVersion（当前系统还会写入
            // PoolID，但旧系统可能省略默认值）。保留键是否
            // 存在的信息，避免把仅剩 VDEVVersion 的残缺设备误判为合法通用池设备。
            /// <summary>是否同时包含 InstanceGuid 和 VDEVVersion。</summary>
            public bool IsStructurallyComplete =>
                InstanceGuidPresent && VdevVersionPresent;
        }

        /// <summary>从枚举节点解析 GPU 分区设备及其物理卡路径与关键字段存在情况。</summary>
        /// <returns>按 manifest 编号排列的 GPU 分区设备列表。</returns>
        /// <param name="nodes">VmcxStore.Enumerate 返回的节点快照或等价节点序列。</param>
        public static List<VmcxGpuPv> GpuPvDevices(IEnumerable<VmcxNode> nodes) {
            var instByNum = new SortedDictionary<int,string>();
            var typeByNum = new Dictionary<int,string>();
            var hostRes   = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase); // 设备节点GUID → HostResource
            var deviceKeys = new Dictionary<string,HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in nodes) {
                if (!n.IsValue) continue;
                var mi = Regex.Match(n.Path, @"^/configuration/manifest/vdev(\d+)/instance$");
                if (mi.Success) instByNum[int.Parse(mi.Groups[1].Value)] = (n.Value??"").Trim('{','}').ToLowerInvariant();
                var mt = Regex.Match(n.Path, @"^/configuration/manifest/vdev(\d+)/device$");
                if (mt.Success) typeByNum[int.Parse(mt.Groups[1].Value)] = (n.Value??"").Trim('{','}').ToLowerInvariant();
                var md = Regex.Match(n.Path, @"^/configuration/_([0-9a-fA-F-]{36})_/([^/]+)$");
                if (md.Success) {
                    string instance = md.Groups[1].Value.ToLowerInvariant();
                    string key = md.Groups[2].Value;
                    if (!deviceKeys.TryGetValue(instance, out var keys)) {
                        keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        deviceKeys[instance] = keys;
                    }
                    keys.Add(key);
                    if (key.Equals("HostResource", StringComparison.OrdinalIgnoreCase))
                        hostRes[instance] = n.Value ?? "";
                }
            }
            var list = new List<VmcxGpuPv>();
            foreach (var kv in instByNum) {
                string t; if (!typeByNum.TryGetValue(kv.Key, out t) || t != GpuPartitionType) continue;
                string hr; hostRes.TryGetValue(kv.Value, out hr);
                deviceKeys.TryGetValue(kv.Value, out var keys);
                list.Add(new VmcxGpuPv {
                    VdevNumber = kv.Key,
                    Instance = kv.Value,
                    HostResource = hr ?? "",
                    HostResourcePresent = keys?.Contains("HostResource") == true,
                    InstanceGuidPresent = keys?.Contains("InstanceGuid") == true,
                    PoolIdPresent = keys?.Contains("PoolID") == true,
                    VdevVersionPresent = keys?.Contains("VDEVVersion") == true,
                });
            }
            return list;
        }
    }

#nullable restore
    internal static class VmcxMetadata
    {
        private const string HostOnlyItemPath =
            "/configuration/_2a34b1c2-fd73-4043-8a5b-dd2159bc743f_/host-only/kvp";

        internal static List<VmcxNode> ReadSnapshot(string configurationPath)
        {
            // DLL 读取独立副本，避免源文件被恢复写入。投影保留容器进入/退出边界，
            // 让现有导入预览继续按独立 kvp 配对，不合并重复路径。
            var document = VmcxDocument.Read(configurationPath);
            var children = document.Entries.Where(e => e.ParentId is not null).ToLookup(e => e.ParentId!.Value);
            var result = new List<VmcxNode>();
            void Visit(VmcxEntry entry)
            {
                string type = entry.Value?.Type switch
                {
                    VmcxValueType.Integer => "Int", VmcxValueType.UnsignedInteger => "UInt",
                    VmcxValueType.Boolean => "Bool", VmcxValueType.Raw => "Array",
                    null => "Node", _ => entry.Value.Type.ToString()
                };
                var node = new VmcxNode { Path = entry.Path, Type = type,
                    Value = entry.Value?.Text ?? "", IsValue = !entry.IsContainer };
                result.Add(node);
                foreach (var child in children[entry.Id]) Visit(child);
                if (entry.IsContainer) result.Add(node);
            }
            Visit(document.Root);
            return result;
        }

        internal static IReadOnlyDictionary<string, string> ReadHostOnlyItems(IEnumerable<VmcxNode> nodes)
        {
            var items = new Dictionary<string, string>(StringComparer.Ordinal);
            bool insideItem = false;
            string? key = null;
            string? value = null;
            foreach (VmcxNode node in nodes)
            {
                // 原生迭代器按进入、子节点、退出输出容器。多个 kvp 的路径相同，
                // 必须保留每次进入/退出的边界，不能按路径去重后再拼接 key/value。
                if (!node.IsValue && string.Equals(node.Path, HostOnlyItemPath, StringComparison.OrdinalIgnoreCase))
                {
                    if (insideItem)
                    {
                        if (key is null || value is null || !items.TryAdd(key, value))
                            throw new InvalidDataException("VMCX HostOnlyItems 包含不完整或重名的条目。");
                    }
                    insideItem = !insideItem;
                    key = value = null;
                    continue;
                }

                bool isKey = string.Equals(node.Path, HostOnlyItemPath + "/key", StringComparison.OrdinalIgnoreCase);
                bool isValue = string.Equals(node.Path, HostOnlyItemPath + "/value", StringComparison.OrdinalIgnoreCase);
                if (!isKey && !isValue) continue;
                if (!insideItem || !node.IsValue || !string.Equals(node.Type, "String", StringComparison.Ordinal))
                    throw new InvalidDataException("VMCX HostOnlyItems 的条目边界或值类型无效。");
                if (isKey)
                {
                    if (key is not null) throw new InvalidDataException("VMCX HostOnlyItems 条目包含多个 key。");
                    key = node.Value ?? string.Empty;
                }
                else
                {
                    if (value is not null) throw new InvalidDataException("VMCX HostOnlyItems 条目包含多个 value。");
                    value = node.Value ?? string.Empty;
                }
            }
            if (insideItem) throw new InvalidDataException("VMCX HostOnlyItems 条目未完整结束。");
            return items;
        }
    }

    #endregion
}

namespace ExHyperV.Vmcx.Internal
{
    #region 编辑提交与辅助实现

#nullable restore
    internal static class VmcxNativeEditor
    {
        internal static VmcxEditResult Commit(VmcxDocument document, IReadOnlyList<VmcxEntry> entries)
        {
            using var apartment = new VmcxStore.Apartment();
            using var prepared = VmcxStore.CreateTransient();
            using var preparedLock = prepared.BeginWrite();
            Build(prepared, entries);
            Verify(entries, prepared.ReadEntries(Guid.NewGuid()));

            string path = document.FilePath;
            string? backup = null;
            try
            {
                // 防止路径在原生句柄打开之前被替换；写入互斥由 DLL 的文件锁负责。
                using var guard = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (VmcxFileIO.HashFile(path) != document.SourceSha256) throw new IOException("源文件已变化，请重新读取。");
                backup = path + "." + Guid.NewGuid().ToString("N") + ".bak";
                File.Copy(path, backup);
                if (VmcxFileIO.HashFile(backup) != document.SourceSha256 || VmcxFileIO.HashFile(path) != document.SourceSha256)
                    throw new IOException("备份期间源文件已变化，未提交。");
                using (var target = VmcxStore.Open(path))
                using (var targetLock = target.BeginWrite())
                {
                    // 原生文件锁同时阻止其他句柄读取原始字节；在锁内通过同一存储核对完整逻辑树。
                    Verify(document.Entries, target.ReadEntries(Guid.NewGuid()));
                    // CopyData 在长 BSTR 含空字符时会产生多余的同名空值。
                    // 使用同一套原生逐节点写入，避免原生整树复制的该缺陷。
                    RemoveContent(target, document);
                    Build(target, entries);
                    Verify(entries, target.ReadEntries(Guid.NewGuid()));
                    targetLock.Commit();
                    Verify(entries, target.ReadEntries(Guid.NewGuid()));
                }
                // 重新打开，验证落盘结果，而非只检查尚在内存中的树。
                var saved = VmcxDocument.Read(path);
                Verify(entries, saved.Entries);
                return new(path, true, backup, document.SourceSha256, saved.SourceSha256);
            }
            catch (Exception error)
            {
                bool intact = false; string? stateError = null;
                try { intact = VmcxFileIO.HashFile(path) == document.SourceSha256; }
                catch (Exception checkError) { stateError = checkError.Message; }
                throw new VmcxEditException(error, backup, intact, stateError);
            }
        }

        private static void RemoveContent(VmcxStore store, VmcxDocument document)
        {
            // 逐个删除顶层容器，保留原生存储的文件头和数据版本。
            // 无选择键的同名容器每次删除首项，循环至全部移除。
            int remaining = document.Entries.Count(e => e.ParentId == document.Root.Id);
            while (remaining-- > 0)
            {
                var current = store.ReadEntries(Guid.NewGuid());
                var root = current.FirstOrDefault(e => e.ParentId == current[0].Id);
                if (root is null) break;
                store.DoRemove("/" + root.Key);
            }
            if (store.ReadEntries(Guid.NewGuid()).Count != 1)
                throw new InvalidDataException("原生存储未完整删除待替换的内容，已停止提交。");
        }

        private static void Build(VmcxStore store, IReadOnlyList<VmcxEntry> entries)
        {
            var children = entries.Where(e => e.ParentId is not null).ToLookup(e => e.ParentId!.Value);
            var keys = entries.Select(e => e.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries.Where(e => e.ParentId is not null))
                if (entry.Key.IndexOfAny(['/', '[', ']', '=', '"', '\0']) >= 0)
                    throw new NotSupportedException("节点名称包含 VmDataStore 路径运算符，不能无歧义地编辑：" + entry.Key);
            string marker, anchor;
            do { marker = "edit" + Guid.NewGuid().ToString("N"); } while (keys.Contains(marker));
            do { anchor = "edit" + Guid.NewGuid().ToString("N"); } while (keys.Contains(anchor));
            void Write(VmcxEntry node, string parent)
            {
                if (node.Key.IndexOfAny(['/', '[', ']', '=', '"', '\0']) >= 0)
                    throw new NotSupportedException("节点名称包含 VmDataStore 路径运算符，不能无歧义地编辑：" + node.Key);
                var content = children[node.Id].ToArray();
                if (content.Length == 0)
                {
                    // DLL 的 XML 导入可以建立空容器。值不经过 XML，避免浮点与 UInt64 精度损失。
                    using var xml = new MemoryStream();
                    using (var writer = XmlWriter.Create(xml, new XmlWriterSettings { Encoding = Encoding.Unicode, OmitXmlDeclaration = true }))
                    { writer.WriteStartElement(node.Key); writer.WriteEndElement(); }
                    store.ImportXml(xml.ToArray(), parent);
                    return;
                }
                var selectors = content.Where(e => e.IsSelector).ToArray();
                if (selectors.Length > 1) throw new NotSupportedException("VmDataStore 路径接口不支持同一容器的多个 selector。");
                var selector = selectors.SingleOrDefault();
                if (selector is not null && (selector.Value?.Type != VmcxValueType.String || !char.IsAsciiLetterOrDigit(selector.Key[0])))
                    throw new NotSupportedException("VmDataStore 的 selector 创建接口仅支持名称以字母或数字开头的单个字符串选择键。");
                string selectorKey = selector?.Key ?? marker;
                string token = Guid.NewGuid().ToString("N");
                string path = parent + "/" + node.Key + "[" + selectorKey + "=\"" + token + "\"]";
                store.DoSetString(path + "/" + anchor, "");
                foreach (var value in content.Where(e => e.Value is not null && !e.IsSelector))
                    store.SetValue(path + "/" + value.Key, value.Value!);
                foreach (var child in content.Where(e => e.IsContainer)) Write(child, path);
                store.DoRemove(path + "/" + anchor);
                if (selector is null)
                {
                    try { store.DoRemove(path + "/" + marker); }
                    catch (Exception ex) { throw new InvalidOperationException("清理临时选择键失败：" + path, ex); }
                }
                else store.SetValue(path + "/" + selector.Key, selector.Value!);
            }
            foreach (var child in children[entries.Single(e => e.ParentId is null).Id])
            {
                if (!child.IsContainer) throw new NotSupportedException("VMCX 顶层值不在编辑范围内。");
                Write(child, "");
            }
        }

        internal static void Verify(IReadOnlyList<VmcxEntry> expected, IReadOnlyList<VmcxEntry> actual)
        {
            if (Fingerprint(expected) != Fingerprint(actual))
                throw new InvalidDataException("VmDataStore 读回的节点、类型、selector 或值与待提交内容不一致。");
        }
        private static string Fingerprint(IReadOnlyList<VmcxEntry> entries)
        {
            var children = entries.Where(e => e.ParentId is not null).ToLookup(e => e.ParentId!.Value);
            string Hash(VmcxEntry e)
            {
                using var bytes = new MemoryStream();
                using (var writer = new BinaryWriter(bytes, Encoding.Unicode, true))
                {
                    writer.Write(e.Key); writer.Write(e.IsSelector); writer.Write((int?)e.Value?.Type ?? 0); writer.Write(e.Value?.Text ?? "");
                    foreach (var child in children[e.Id].Select(Hash).Order(StringComparer.Ordinal)) writer.Write(child);
                }
                return Convert.ToHexString(SHA256.HashData(bytes.GetBuffer().AsSpan(0, (int)bytes.Length)));
            }
            return Hash(entries.Single(e => e.ParentId is null));
        }
    }

#nullable restore
    // VmDataStore 的同步流桥接。只转交 IStream，不解释或改写 VMCX 字节。
    // 原生读写在调用返回前完成；COM 引用计数负责流与桥接对象的寿命。
    internal sealed unsafe class VmcxNativeStream : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Bridge { internal nint Vtable; internal int References; internal nint Stream; }
        private static readonly Guid WrapperId = new("9fca2b5e-24ec-4551-a283-bc9fe12720e7");
        private static readonly Guid UnknownId = new("00000000-0000-0000-c000-000000000046");
        private static readonly Guid InspectableId = new("af86e2e0-b12d-4c6a-9c5a-d7aa65101e90");
        private static readonly nint Vtable = CreateVtable();
        private nint pointer;
        internal nint Pointer => pointer != 0 ? pointer : throw new ObjectDisposedException(nameof(VmcxNativeStream));

        [DllImport("shlwapi", ExactSpelling = true)]
        private static extern nint SHCreateMemStream(byte[] data, uint length);

        internal VmcxNativeStream(byte[] data)
        {
            nint stream = SHCreateMemStream(data, checked((uint)data.Length));
            if (stream == 0) throw new OutOfMemoryException();
            var bridge = (Bridge*)NativeMemory.AllocZeroed((nuint)sizeof(Bridge));
            if (bridge == null) { Marshal.Release(stream); throw new OutOfMemoryException(); }
            bridge->Vtable = Vtable; bridge->References = 1; bridge->Stream = stream;
            pointer = (nint)bridge;
        }
        private static nint CreateVtable()
        {
            var table = (nint*)RuntimeHelpers.AllocateTypeAssociatedMemory(typeof(VmcxNativeStream), 7 * IntPtr.Size);
            table[0] = (nint)(delegate* unmanaged[Stdcall]<Bridge*, Guid*, nint*, int>)&Query;
            table[1] = (nint)(delegate* unmanaged[Stdcall]<Bridge*, uint>)&AddRef;
            table[2] = (nint)(delegate* unmanaged[Stdcall]<Bridge*, uint>)&Release;
            table[3] = (nint)(delegate* unmanaged[Stdcall]<Bridge*, uint*, nint*, int>)&GetIids;
            table[4] = (nint)(delegate* unmanaged[Stdcall]<Bridge*, nint*, int>)&GetClassName;
            table[5] = (nint)(delegate* unmanaged[Stdcall]<Bridge*, int*, int>)&GetTrust;
            table[6] = (nint)(delegate* unmanaged[Stdcall]<Bridge*, nint*, int>)&GetStream;
            return (nint)table;
        }
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int Query(Bridge* self, Guid* iid, nint* result)
        {
            *result = 0;
            if (*iid != WrapperId && *iid != UnknownId && *iid != InspectableId) return unchecked((int)0x80004002);
            Interlocked.Increment(ref self->References); *result = (nint)self; return 0;
        }
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static uint AddRef(Bridge* self) => (uint)Interlocked.Increment(ref self->References);
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static uint Release(Bridge* self) => ReleaseCore(self);
        private static uint ReleaseCore(Bridge* self)
        {
            int count = Interlocked.Decrement(ref self->References);
            if (count == 0) { Marshal.Release(self->Stream); NativeMemory.Free(self); }
            return (uint)count;
        }
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int GetIids(Bridge* self, uint* count, nint* ids) { *count = 0; *ids = 0; return 0; }
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int GetClassName(Bridge* self, nint* name) { *name = 0; return 0; }
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int GetTrust(Bridge* self, int* trust) { *trust = 0; return 0; }
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int GetStream(Bridge* self, nint* result) { Marshal.AddRef(self->Stream); *result = self->Stream; return 0; }
        public void Dispose()
        {
            nint old = Interlocked.Exchange(ref pointer, 0);
            if (old != 0) ReleaseCore((Bridge*)old);
        }
    }

#nullable restore
    internal static class VmcxFileIO
    {
        internal static string HashFile(string path)
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
    }

#nullable disable
    internal static class VmcxDeviceTemplates {
        internal static IReadOnlyList<VmcxDevice> ReadDevices(IReadOnlyList<VmcxEntry> entries) {
            var result = new List<VmcxDevice>();
            foreach (var e in entries.Where(e => e.IsContainer)) {
                var m = System.Text.RegularExpressions.Regex.Match(e.Path, @"^/configuration/manifest/vdev(\d+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (!m.Success || !int.TryParse(m.Groups[1].Value, out int number)) continue;
                string Field(string name) => entries.Where(v => v.ParentId == e.Id && v.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).SingleOrDefault()?.Value?.Text ?? "";
                result.Add(new VmcxDevice(number, Field("instance").Trim('{', '}'), Field("device").Trim('{', '}'), Field("name")));
            }
            return result.OrderBy(d => d.Number).ToList().AsReadOnly();
        }
        public sealed class DeviceTemplateKey {
            public string Key; public VmcxValueType Type; public string Value;
            public DeviceTemplateKey(string k, VmcxValueType t, string v){ Key=k; Type=t; Value=v; }
        }
        /// <summary>设备配置模板：类型 GUID、manifest 名称及数据节点必填键。</summary>
        public sealed class DeviceTemplate {
            public string DeviceType; public string Name; public DeviceTemplateKey[] Keys; public string[] RequiredParams;
        }
        static DeviceTemplateKey K(string k, VmcxValueType t, string v){ return new DeviceTemplateKey(k,t,v); }

        /// <summary>AddDevice 使用的各设备类型配置模板。</summary>
        public static readonly Dictionary<string,DeviceTemplate> DeviceTemplates = new Dictionary<string,DeviceTemplate>(StringComparer.OrdinalIgnoreCase) {
            { "d422512d-2bf2-4752-809d-7b82b5fcb1b4", new DeviceTemplate { DeviceType="d422512d-2bf2-4752-809d-7b82b5fcb1b4", Name="Synthetic SCSI Controller", Keys=new[]{
                K("ChannelInstanceGuid", VmcxValueType.String,  "$B"),
                K("TargetVtl",           VmcxValueType.Integer, "0"),
                K("VDEVVersion",         VmcxValueType.Integer, "512"),
            }}},
            { "99dcd00c-fbd6-42d3-9dfd-1b5ad7058f61", new DeviceTemplate { DeviceType="99dcd00c-fbd6-42d3-9dfd-1b5ad7058f61", Name="GPU Partition", Keys=new[]{
                K("InstanceGuid", VmcxValueType.String,  "$B"),
                K("PoolID",       VmcxValueType.String,  ""),
                K("VDEVVersion",  VmcxValueType.Integer, "256"),
            }}},
            { "2fcc454e-a36a-4c77-bb5e-a2d75a51f02c", new DeviceTemplate { DeviceType="2fcc454e-a36a-4c77-bb5e-a2d75a51f02c", Name="Virtual Pci Express Port", RequiredParams=new[]{"devicepath"}, Keys=new[]{
                K("HostResources/count",                   VmcxValueType.Integer, "1"),
                K("HostResources/HostResource/Instance",   VmcxValueType.String,  "$P:devicepath"), // PCIP\VEN_...\...
                K("InstanceGuid",        VmcxValueType.String,  "$B"),
                K("NumaAwarePlacement",  VmcxValueType.Boolean, "False"),
                K("PoolId",              VmcxValueType.String,  ""),
                K("TargetVtl",           VmcxValueType.Integer, "0"),
                K("VDEVVersion",         VmcxValueType.Integer, "256"),
            }}},
            { "2fc216b0-d2e2-4967-9b6d-b8a5c9ca2778", new DeviceTemplate { DeviceType="2fc216b0-d2e2-4967-9b6d-b8a5c9ca2778", Name="Synthetic Ethernet Port", Keys=new[]{
                K("AllowDirectTranslatedP2P", VmcxValueType.Boolean, "False"),
                K("AllowPacketDirect",        VmcxValueType.Boolean, "False"),
                K("ChannelInstanceGuid",      VmcxValueType.String,  "$B"),
                K("ClusterMonitored",         VmcxValueType.Boolean, "True"),
                K("Connection/AltPortName",      VmcxValueType.String,  ""),
                K("Connection/AltSwitchName",    VmcxValueType.String,  ""),
                K("Connection/AuthorizationScope", VmcxValueType.String, ""),
                K("Connection/ChimneyOffloadWeight", VmcxValueType.Integer, "0"),
                K("Connection/Feature_C885BFD1-ABB7-418F-8163-9F379C9F7166/DisplayName", VmcxValueType.String, ""),
                K("Connection/Feature_C885BFD1-ABB7-418F-8163-9F379C9F7166/Flags", VmcxValueType.Integer, "0"),
                K("Connection/Feature_C885BFD1-ABB7-418F-8163-9F379C9F7166/Setting_$S/Data", VmcxValueType.Raw, "AAIAAGQAAAAAAAAAAQAAAAAAAAAAAAAAQAAAAEBCDwABAQAAEAAAAAEAAAAAAAAAAAAAAAMAAAABAAAA"),
                K("Connection/Feature_C885BFD1-ABB7-418F-8163-9F379C9F7166/Setting_$S/Version", VmcxValueType.Integer, "1280"),
                K("Connection/Feature_C885BFD1-ABB7-418F-8163-9F379C9F7166/Settings/Id", VmcxValueType.String, "$S"),
                K("Connection/Features/Id",      VmcxValueType.String,  "C885BFD1-ABB7-418F-8163-9F379C9F7166"),
                K("Connection/PoolId",           VmcxValueType.String,  ""),
                K("Connection/PreventIPSpoofing", VmcxValueType.Boolean, "False"),
                K("Connection/TestReplicaPoolId", VmcxValueType.String, ""),
                K("Connection/TestReplicaSwitchName", VmcxValueType.String, ""),
                K("DeviceNamingEnabled",      VmcxValueType.Boolean, "False"),
                K("FriendlyName",             VmcxValueType.String,  "Network Adapter"),
                K("InterruptModerationDisabled", VmcxValueType.Boolean, "False"),
                K("IsConnected",              VmcxValueType.Boolean, "False"),
                K("MacAddress",               VmcxValueType.String,  "$MAC"),
                K("MacAddressIsStatic",       VmcxValueType.Boolean, "False"),
                K("MediaType",                VmcxValueType.Integer, "0"),
                K("NumaAwarePlacement",       VmcxValueType.Boolean, "False"),
                K("PortName",                 VmcxValueType.String,  ""),
                K("SwitchName",               VmcxValueType.String,  ""),
                K("VDEVVersion",              VmcxValueType.Integer, "768"),
                K("VpciInstanceGuid",         VmcxValueType.String,  "$B"),
            }}},
        };

    }

#nullable disable
    internal static class VmcxValidation {
        static IReadOnlySet<string> FunctionalDeviceTypes => VmcxSchema.FunctionalDeviceTypes;
        internal static IReadOnlyList<string> Check(List<VmcxNode> nodes) {
            var issues = ValidateManifest(nodes);
            foreach (var gpu in VmcxSchema.GpuPvDevices(nodes))
                if (!gpu.IsStructurallyComplete) issues.Add($"GPU 分区 vdev{gpu.VdevNumber:D3} 缺少 InstanceGuid 或 VDEVVersion。");
            var trueKeys = nodes.Where(n => n.IsValue && n.Type == "Bool" && n.Value == "True")
                .Select(n => n.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (trueKeys.Contains("/configuration/settings/memory/bank/dynamic_memory_enabled")) {
                if (trueKeys.Contains("/configuration/settings/vnuma/enabled")) issues.Add("动态内存与虚拟 NUMA 不能同时启用。");
                if (trueKeys.Contains("/configuration/settings/processors/nested_virtualization/enabled")) issues.Add("动态内存与嵌套虚拟化不能同时启用。");
            }
            return issues.AsReadOnly();
        }
        public static List<string> ValidateManifest(IEnumerable<VmcxNode> source) {
            if (source == null) throw new ArgumentNullException(nameof(source));
            List<VmcxNode> nodes = source.ToList();
            var issues = new List<string>();
            VmcxNode[] sizeNodes = nodes.Where(node => node.IsValue
                && node.Path.Equals("/configuration/manifest/size",
                    StringComparison.OrdinalIgnoreCase)).ToArray();
            long size = -1;
            if (sizeNodes.Length != 1
                    || !string.Equals(sizeNodes[0].Type, "Int", StringComparison.OrdinalIgnoreCase)
                    || !long.TryParse(sizeNodes[0].Value, out size) || size < 0)
                issues.Add("manifest/size 读取失败");

            var vdev = new SortedDictionary<int, string>();
            var vdevType = new SortedDictionary<int, string>();
            var manifestFields = new Dictionary<int, Dictionary<string, VmcxNode>>();
            var devVals = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (VmcxNode node in nodes) {
                Match instanceMatch = Regex.Match(node.Path,
                    @"^/configuration/manifest/vdev(\d+)/instance$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (instanceMatch.Success && node.IsValue
                        && int.TryParse(instanceMatch.Groups[1].Value, out int instanceNumber))
                    vdev[instanceNumber] = (node.Value ?? string.Empty).Trim('{', '}').ToLowerInvariant();
                Match typeMatch = Regex.Match(node.Path,
                    @"^/configuration/manifest/vdev(\d+)/device$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (typeMatch.Success && node.IsValue
                        && int.TryParse(typeMatch.Groups[1].Value, out int typeNumber))
                    vdevType[typeNumber] = (node.Value ?? string.Empty).Trim('{', '}').ToLowerInvariant();
                Match fieldMatch = Regex.Match(node.Path,
                    @"^/configuration/manifest/vdev(\d+)/([^/]+)$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (fieldMatch.Success && node.IsValue
                        && int.TryParse(fieldMatch.Groups[1].Value, out int fieldNumber)) {
                    if (!manifestFields.TryGetValue(fieldNumber,
                            out Dictionary<string, VmcxNode> fields))
                        manifestFields[fieldNumber] = fields =
                            new Dictionary<string, VmcxNode>(StringComparer.OrdinalIgnoreCase);
                    if (!fields.TryAdd(fieldMatch.Groups[2].Value, node))
                        issues.Add("manifest vdev" + fieldNumber.ToString("D3") + " 字段重复: "
                            + fieldMatch.Groups[2].Value);
                }
                Match dataMatch = Regex.Match(node.Path,
                    @"^/configuration/_([0-9a-fA-F-]{36})_(/(.+))?$",
                    RegexOptions.CultureInvariant);
                if (dataMatch.Success) {
                    string instance = dataMatch.Groups[1].Value.ToLowerInvariant();
                    if (!devVals.TryGetValue(instance, out List<string> values))
                        devVals[instance] = values = new List<string>();
                    if (node.IsValue && dataMatch.Groups[3].Success)
                        values.Add(dataMatch.Groups[3].Value);
                }
            }
            if (size >= 0 && size != manifestFields.Count)
                issues.Add(string.Format("size={0} 与实际 vdev 数 {1} 不符", size,
                    manifestFields.Count));
            int[] numbers = vdev.Keys.ToArray();
            if (numbers.Length != 0) {
                if (numbers[0] != 1) issues.Add("manifest 编号必须从 vdev001 开始");
                for (int index = 1; index < numbers.Length; index++)
                    if ((long)numbers[index] != (long)numbers[index - 1] + 1)
                        issues.Add("manifest 编号不连续: vdev" + numbers[index - 1].ToString("D3")
                            + " 之后为 vdev" + numbers[index].ToString("D3"));
            }
            foreach ((int number, Dictionary<string, VmcxNode> fields) in manifestFields)
                foreach ((string field, string type) in new[] {
                        ("device", "String"), ("flags", "Int"),
                        ("instance", "String"), ("name", "String") }) {
                    if (!fields.TryGetValue(field, out VmcxNode value))
                        issues.Add("manifest vdev" + number.ToString("D3") + " 缺少 " + field);
                    else if (!string.Equals(value.Type, type, StringComparison.OrdinalIgnoreCase))
                        issues.Add("manifest vdev" + number.ToString("D3") + " 的 " + field
                            + " 类型应为 " + type + "，实际为 " + value.Type);
                }
            foreach ((int number, string value) in vdevType)
                if (!Guid.TryParse(value, out _))
                    issues.Add("manifest vdev" + number.ToString("D3") + " 的 device 不是有效 GUID");
            foreach ((int number, string value) in vdev)
                if (!Guid.TryParse(value, out _))
                    issues.Add("manifest vdev" + number.ToString("D3") + " 的 instance 不是有效 GUID");
            foreach (IGrouping<string, KeyValuePair<int, string>> duplicate in vdev
                    .GroupBy(pair => pair.Value, StringComparer.OrdinalIgnoreCase)
                    .Where(group => group.Count() > 1))
                issues.Add("manifest 设备实例重复: " + duplicate.Key);

            var manifestInstances = new HashSet<string>(vdev.Values, StringComparer.OrdinalIgnoreCase);
            foreach ((string instance, List<string> values) in devVals) {
                if (manifestInstances.Contains(instance)) continue;
                bool benign = values.Count == 0 || (values.Count == 1
                    && values[0].Equals("VDEVVersion", StringComparison.OrdinalIgnoreCase));
                if (!benign) issues.Add("孤儿数据节点: /_" + instance
                    + "_ 有残留数据但无 manifest 条目(真孤儿,应清除)");
            }
            var typesWithData = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach ((int number, string instance) in vdev) {
                int count = devVals.TryGetValue(instance, out List<string> values) ? values.Count : 0;
                if (count > 0 && vdevType.TryGetValue(number, out string type)) typesWithData.Add(type);
            }
            foreach ((int number, string instance) in vdev) {
                int count = devVals.TryGetValue(instance, out List<string> values) ? values.Count : -1;
                string type = vdevType.TryGetValue(number, out string foundType) ? foundType : string.Empty;
                if (count <= 0 && (typesWithData.Contains(type) || FunctionalDeviceTypes.Contains(type)))
                    issues.Add(string.Format("幽灵设备: manifest vdev{0:D3}(instance={1})数据节点{2}"
                        + "(应为功能设备却无数据)→ VM 将无法启动(应删此条目)", number, instance,
                        count < 0 ? "缺失" : "为空"));
            }
            const string DdaType = "2fcc454e-a36a-4c77-bb5e-a2d75a51f02c";
            foreach ((int number, string instance) in vdev) {
                if (!vdevType.TryGetValue(number, out string type)
                        || !type.Equals(DdaType, StringComparison.OrdinalIgnoreCase)) continue;
                List<string> values = devVals.TryGetValue(instance, out List<string> found)
                    ? found : new List<string>();
                if (!values.Exists(value => value.Equals("HostResources/HostResource/Instance",
                        StringComparison.OrdinalIgnoreCase)))
                    issues.Add(string.Format("残缺 DDA 设备: vdev{0:D3}(instance={1})缺 "
                        + "HostResources/HostResource/Instance → VM 将无法启动", number, instance));
            }
            return issues;
        }


    }

    #endregion
}
#nullable restore
