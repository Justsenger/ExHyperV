#nullable disable
// 基于 VmDataStore.dll 的 .vmcx 读写接口，仅包含 GPU-PV 和 DDA 设备修复所需能力。
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ExHyperV.Vmcx {

/// <summary>.vmcx 节点(值或子节点)。</summary>
public struct VmcxNode {
    public string Path;     // 全路径,如 /configuration/properties/version
    public string Type;     // Int / String / Bool / Double …(值节点)
    public string Value;    // 值的字符串表示(值节点)
    public bool   IsValue;  // true=值,false=容器节点
}

/// <summary>
/// 打开一个 .vmcx 进行读/写。基于官方 VmDataStore.dll。支持改值、删键、
/// 不变量感知的 RemoveDevice、ValidateManifest。
/// </summary>
public sealed class VmcxStore : IDisposable {
    [DllImport("kernel32", CharSet=CharSet.Unicode)] static extern IntPtr LoadLibrary(string s);
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
    const int OFF_LOCK=0x110, OFF_UNLOCK=0x118, OFF_CLOSE=0x128, OFF_COMMIT=0x148;
    static readonly Guid IID_STATICS = new Guid("04ce619a-6775-4f29-be77-b4e2bc2dda3a");
    static readonly Guid IID_KVS     = new Guid("6de696aa-007c-4612-8392-7e2143eef6db");
    static readonly Guid IID_ITER    = new Guid("968eec6a-c6bf-50e3-ac88-ac10a4c163b5");

    static IntPtr s_statics, s_create;
    static readonly object s_initLock = new object();
    IntPtr _store, _ikv, _hvs;
    bool _disposed;

    static void EnsureInit() {
        if (s_statics != IntPtr.Zero) return;
        lock (s_initLock) {
            if (s_statics != IntPtr.Zero) return;
            IntPtr dll = LoadLibrary(@"C:\Windows\System32\VmDataStore.dll");
            if (dll == IntPtr.Zero) throw new InvalidOperationException(Properties.Resources.Vmcx_DllLoadFail);
            IntPtr fac;
            Hr(D<DGetFac>(GetProcAddress(dll, "DllGetActivationFactory"))(MakeHStr("Microsoft.HyperV.DataStore.KeyValueStore"), out fac), "GetActivationFactory");
            Guid s = IID_STATICS; IntPtr statics;
            Hr(D<DQI>(Slot(fac, 0))(fac, ref s, out statics), "QI IKeyValueStoreStatics");
            s_create = Slot(statics, 6);
            s_statics = statics;
        }
    }

    private VmcxStore() { }

    /// <summary>打开 .vmcx(读写共用 mode=0)。失败抛 VmcxException。</summary>
    public static VmcxStore Open(string path) {
        if (string.IsNullOrEmpty(path)) throw new ArgumentNullException("path");
        EnsureInit();
        var st = new VmcxStore();
        bool ok = false;
        try {
            IntPtr hp = MakeHStr(path);
            try { Hr(D<DCreate>(s_create)(s_statics, hp, 0, out st._store), "CreateInstance(" + path + ")"); }
            finally { WindowsDeleteString(hp); }
            Guid k = IID_KVS;
            Hr(D<DQI>(Slot(st._store, 0))(st._store, ref k, out st._ikv), "QI IKeyValueStore");
            st._hvs = Marshal.ReadIntPtr(st._ikv, 0x40);
            ok = true;
            return st;
        } finally { if (!ok) st.Dispose(); }
    }

    public long   GetInteger(string keyPath){ long v=0; Hr(WithKey(keyPath, k=>D<DGetI>(Slot(_ikv,GET_INT))(_ikv,k, out v)), "GetInteger"); return v; }
    // out HSTRING 归调用方所有:复制成托管串后须 WindowsDeleteString 归还(与 Enumerate 同一约定;NULL 句柄删除为 no-op)
    public string GetString (string keyPath){
        IntPtr h = IntPtr.Zero;
        try {
            Hr(WithKey(keyPath, k=>D<DGetS>(Slot(_ikv,GET_STR))(_ikv,k, out h)), "GetString");
            return FromHStr(h);
        } finally { if (h != IntPtr.Zero) WindowsDeleteString(h); }
    }

    int WithKey(string keyPath, Func<IntPtr,int> call){ IntPtr h=MakeHStr(keyPath); try { return call(h); } finally { WindowsDeleteString(h); } }

    /// <summary>枚举整棵树(值 + 容器节点),路径为全路径。
    /// COM 调用失败时抛出 VmcxException，并在异常路径释放迭代器。</summary>
    public List<VmcxNode> Enumerate() {
        var result = new List<VmcxNode>();
        Guid it = IID_ITER; IntPtr i0 = IntPtr.Zero, iter = IntPtr.Zero;
        try {
            Hr(D<DQI>(Slot(_store, 0))(_store, ref it, out i0), "QI IIterable");
            Hr(D<DOut>(Slot(i0, 6))(i0, out iter), "First");
            var getCur = D<DOut>(Slot(iter, 6)); var hasCur = D<DByte>(Slot(iter, 7)); var moveN = D<DByte>(Slot(iter, 8));
            byte has; Hr(hasCur(iter, out has), "HasCurrent"); int guard = 0;
            while (has != 0 && guard++ < 1000000) {
                IntPtr node = IntPtr.Zero;
                Hr(getCur(iter, out node), "Current");
                if (node == IntPtr.Zero) throw new VmcxException(Properties.Resources.Vmcx_CurrentNullNode, -1);
                try {
                    byte isv; Hr(D<DByte>(Slot(node, 7))(node, out isv), "IsValue");
                    IntPtr hk = IntPtr.Zero, ht = IntPtr.Zero, hv = IntPtr.Zero;
                    try {
                        Hr(D<DOut>(Slot(node, 9))(node, out hk), "Key");
                        // 容器节点只有 Key。对容器调用 TypeName/ValueText 会返回
                        // ERROR_INVALID_DATA；编辑器旧实现忽略了该 HRESULT，因此看起来
                        // 可以枚举，主程序的严格检查反而会中断。只在值节点读取它们。
                        if (isv != 0) {
                            Hr(D<DOut>(Slot(node, 11))(node, out ht), "TypeName");
                            Hr(D<DOut>(Slot(node, 12))(node, out hv), "ValueText");
                        }
                        result.Add(new VmcxNode { Path = FromHStr(hk), Type = FromHStr(ht), Value = FromHStr(hv), IsValue = isv != 0 });
                    } finally {
                        // out HSTRING 与 Current 节点的所有权归调用方。
                        if (hk != IntPtr.Zero) WindowsDeleteString(hk);
                        if (ht != IntPtr.Zero) WindowsDeleteString(ht);
                        if (hv != IntPtr.Zero) WindowsDeleteString(hv);
                    }
                } finally { D<DRel>(Slot(node, 2))(node); }
                Hr(moveN(iter, out has), "MoveNext");
            }
        } finally {
            if (iter != IntPtr.Zero) D<DRel>(Slot(iter, 2))(iter);
            if (i0   != IntPtr.Zero) D<DRel>(Slot(i0, 2))(i0);
        }
        return result;
    }

    /// <summary>先压缩并重写 manifest，再删除设备数据节点；返回被删除的 vdev 编号。
    /// 此顺序使中断只留下不影响启动的孤立数据，不会留下缺少数据的 manifest 条目。</summary>
    public int RemoveDevice(string instanceGuid) {
        string g = instanceGuid.Trim('{','}','_',' ').ToLowerInvariant();
        var nodes = Enumerate();
        var entries = new SortedDictionary<int, string[]>();   // 编号 → [device,flags,instance,name]
        foreach (var n in nodes) {
            if (!n.IsValue) continue;
            var m = System.Text.RegularExpressions.Regex.Match(n.Path, @"^/configuration/manifest/vdev(\d+)/(device|flags|instance|name)$");
            if (!m.Success) continue;
            int num = int.Parse(m.Groups[1].Value);
            string[] e;
            if (!entries.TryGetValue(num, out e)) { e = new string[4]; entries[num] = e; }
            int idx = m.Groups[2].Value == "device" ? 0 : m.Groups[2].Value == "flags" ? 1 : m.Groups[2].Value == "instance" ? 2 : 3;
            e[idx] = n.Value ?? "";
        }
        int K = -1;
        foreach (var kv in entries)
            if (((kv.Value[2] ?? "").Trim('{','}').ToLowerInvariant()) == g) { K = kv.Key; break; }
        if (K < 0) throw new VmcxException(string.Format(Properties.Resources.Vmcx_ManifestEntryNotFound, instanceGuid), -1);
        int maxN = 0; foreach (var kv in entries) if (kv.Key > maxN) maxN = kv.Key;

        using (var w = BeginWrite()) {
            int dst = 0;
            foreach (var kv in entries) {
                if (kv.Key == K) continue;
                dst++;
                string vp = VdevPath(dst);
                w.SetString (vp+"/device",   kv.Value[0] ?? "");
                long fl; if (!long.TryParse(kv.Value[1], out fl)) fl = 1;
                w.SetInteger(vp+"/flags",    fl);
                w.SetString (vp+"/instance", kv.Value[2] ?? "");
                w.SetString (vp+"/name",     kv.Value[3] ?? "");
            }
            for (int i = dst+1; i <= maxN; i++) {
                string vp = VdevPath(i);
                foreach (var f in new[]{"/device","/flags","/instance","/name"}) { try { w.Remove(vp+f); } catch {} }
            }
            w.SetInteger("/configuration/manifest/size", dst);
            w.Commit();
        }

        // VDEVVersion 需要先在独立事务中删除，其余值才能随节点一起清除。
        string devNode = "/configuration/_"+g+"_";
        var leaves = new List<string>();
        foreach (var n in nodes) if (n.IsValue && n.Path.StartsWith(devNode+"/", StringComparison.OrdinalIgnoreCase)) leaves.Add(n.Path);
        foreach (var lf in leaves) if (lf.EndsWith("/VDEVVersion", StringComparison.OrdinalIgnoreCase))
            using (var w = BeginWrite()) { w.Remove(lf); w.Commit(); }
        var rest = new List<string>();
        foreach (var lf in leaves) if (!lf.EndsWith("/VDEVVersion", StringComparison.OrdinalIgnoreCase)) rest.Add(lf);
        if (rest.Count > 0)
            using (var w = BeginWrite()) { foreach (var lf in rest) w.Remove(lf); w.Commit(); }
        // 未知残留键需要逐个事务删除。
        for (int pass = 0; pass < 5; pass++) {
            var rem = new List<string>();
            foreach (var n in Enumerate())
                if (n.IsValue && n.Path.StartsWith(devNode+"/", StringComparison.OrdinalIgnoreCase)) rem.Add(n.Path);
            if (rem.Count == 0) break;
            foreach (var lf in rem) using (var w = BeginWrite()) { w.Remove(lf); w.Commit(); }
        }
        return K;
    }
    static string VdevPath(int i){ return "/configuration/manifest/vdev"+i.ToString("D3"); }

    /// <summary>校验 manifest 不变量,返回问题列表(空=健康):size 不符 / 编号空洞 / 孤儿数据节点。</summary>
    public List<string> ValidateManifest() {
        var issues = new List<string>();
        var nodes = Enumerate();
        long size = -1;
        try { size = GetInteger("/configuration/manifest/size"); } catch { issues.Add(Properties.Resources.Vmcx_ManifestSizeReadFail); }
        var vdev = new SortedDictionary<int,string>();
        var vdevType = new SortedDictionary<int,string>(); // vdev 号 → 设备类型 GUID
        var devVals = new Dictionary<string, List<string>>(); // 设备节点 GUID → 其值键(相对路径)
        foreach (var n in nodes) {
            var m = System.Text.RegularExpressions.Regex.Match(n.Path, @"^/configuration/manifest/vdev(\d+)/instance$");
            if (m.Success && n.IsValue) vdev[int.Parse(m.Groups[1].Value)] = (n.Value??"").Trim('{','}').ToLowerInvariant();
            var mt = System.Text.RegularExpressions.Regex.Match(n.Path, @"^/configuration/manifest/vdev(\d+)/device$");
            if (mt.Success && n.IsValue) vdevType[int.Parse(mt.Groups[1].Value)] = (n.Value??"").Trim('{','}').ToLowerInvariant();
            var dm = System.Text.RegularExpressions.Regex.Match(n.Path, @"^/configuration/_([0-9a-fA-F-]{36})_(/(.+))?$");
            if (dm.Success) {
                string gg2 = dm.Groups[1].Value.ToLowerInvariant();
                if (!devVals.ContainsKey(gg2)) devVals[gg2] = new List<string>();
                if (n.IsValue && dm.Groups[3].Success) devVals[gg2].Add(dm.Groups[3].Value);
            }
        }
        if (size>=0 && size != vdev.Count) issues.Add(string.Format(Properties.Resources.Vmcx_SizeMismatch, size, vdev.Count));
        var nums = new List<int>(vdev.Keys);
        if (nums.Count>0)
            for (int i=nums[0]; i<=nums[nums.Count-1]; i++)
                if (!vdev.ContainsKey(i)) issues.Add(string.Format(Properties.Resources.Vmcx_ManifestGap, i.ToString("D3")));
        var vdevInst = new HashSet<string>();
        foreach (var kv in vdev) vdevInst.Add(kv.Value);
        foreach (var kv in devVals) {
            if (vdevInst.Contains(kv.Key)) continue; // 有 manifest 条目 = 正常
            // 仅剩 VDEVVersion(或空)= 已删设备的瞬态残留桩:无害,VM 照常启动,下次打开会自清。不报为问题。
            bool benign = kv.Value.Count == 0 || (kv.Value.Count == 1 && kv.Value[0].Equals("VDEVVersion", StringComparison.OrdinalIgnoreCase));
            if (!benign) issues.Add(string.Format(Properties.Resources.Vmcx_OrphanNode, kv.Key));
        }
        // 同类设备存在数据时，缺少数据节点的 manifest 条目视为损坏；平台设备等整类无数据节点的条目除外。
        var typesWithData = new HashSet<string>();
        foreach (var kv in vdev) {
            int c = devVals.ContainsKey(kv.Value) ? devVals[kv.Value].Count : 0;
            if (c > 0 && vdevType.ContainsKey(kv.Key)) typesWithData.Add(vdevType[kv.Key]);
        }
        foreach (var kv in vdev) {
            int c = devVals.ContainsKey(kv.Value) ? devVals[kv.Value].Count : -1;
            string typ = vdevType.ContainsKey(kv.Key) ? vdevType[kv.Key] : "";
            if (c <= 0 && (typesWithData.Contains(typ) || VmcxSchema.FunctionalDeviceTypes.Contains(typ)))
                issues.Add(string.Format(Properties.Resources.Vmcx_GhostDevice,
                    kv.Key, kv.Value, c < 0 ? Properties.Resources.Vmcx_GhostMissing : Properties.Resources.Vmcx_GhostEmpty));
        }
        // DDA 设备必须包含 HostResources/HostResource/Instance。
        const string DDA_TYPE = "2fcc454e-a36a-4c77-bb5e-a2d75a51f02c";
        foreach (var kv in vdev) {
            string t2; if (!vdevType.TryGetValue(kv.Key, out t2) || t2 != DDA_TYPE) continue;
            var vals = devVals.ContainsKey(kv.Value) ? devVals[kv.Value] : new List<string>();
            if (!vals.Exists(x => x.Equals("HostResources/HostResource/Instance", StringComparison.OrdinalIgnoreCase)))
                issues.Add(string.Format(Properties.Resources.Vmcx_IncompleteDda, kv.Key, kv.Value));
        }
        // GPU-PV may legitimately omit HostResource when it uses the generic pool. Its own
        // data node must still contain InstanceGuid and VDEVVersion. A manifest entry with
        // only VDEVVersion is a broken, WMI-invisible device that makes vmwp fail with
        // 0x80070057; validate it explicitly instead of treating it as a generic adapter.
        foreach (var kv in vdev) {
            string t2; if (!vdevType.TryGetValue(kv.Key, out t2) || t2 != VmcxSchema.GpuPartitionType) continue;
            var vals = devVals.ContainsKey(kv.Value) ? devVals[kv.Value] : new List<string>();
            bool hasInstanceGuid = vals.Exists(x => x.Equals("InstanceGuid", StringComparison.OrdinalIgnoreCase));
            bool hasVdevVersion = vals.Exists(x => x.Equals("VDEVVersion", StringComparison.OrdinalIgnoreCase));
            if (!hasInstanceGuid || !hasVdevVersion)
                issues.Add(string.Format(Properties.Resources.Vmcx_IncompleteGpuPv, kv.Key, kv.Value));
        }
        return issues;
    }

    /// <summary>开始写事务(获取排他锁)。SetXxx/Remove 后 Commit() 落盘,Dispose() 释放锁。</summary>
    public VmcxWriter BeginWrite() {
        ThrowIfDisposed();
        Hr(D<DLock>(SlotOff(_hvs, OFF_LOCK))(_hvs, 1), "Lock");
        return new VmcxWriter(this);
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        try { if (_hvs != IntPtr.Zero) D<DVoid>(SlotOff(_hvs, OFF_CLOSE))(_hvs); } catch { }
        try { if (_ikv != IntPtr.Zero) D<DRel>(Slot(_ikv, 2))(_ikv); } catch { }
        try { if (_store != IntPtr.Zero) D<DRel>(Slot(_store, 2))(_store); } catch { }
        _ikv = _store = _hvs = IntPtr.Zero;
    }

    internal void DoSetInteger(string k, long v){ Hr(WithKey(k, h=>D<DSetI>(Slot(_ikv,SET_INT))(_ikv,h,v)), "SetInteger"); }
    internal void DoSetString (string k, string v){ IntPtr hv=MakeHStr(v); try { Hr(WithKey(k, h=>D<DSetS>(Slot(_ikv,SET_STR))(_ikv,h,hv)), "SetString"); } finally { WindowsDeleteString(hv); } }
    internal void DoRemove    (string k){ Hr(WithKey(k, h=>D<DRemove>(Slot(_ikv,REMOVE))(_ikv,h)), "Remove"); }
    internal void DoCommit(){ int hr=D<DVoid>(SlotOff(_hvs, OFF_COMMIT))(_hvs); if(hr!=0 && hr!=1) throw new VmcxException(string.Format(Properties.Resources.Vmcx_OpFailHr, "Commit", hr.ToString("X8")), hr); } // hr==1(S_FALSE)=无改动可提交,非错误
    internal void DoUnlock(){ try { D<DVoid>(SlotOff(_hvs, OFF_UNLOCK))(_hvs); } catch { } }

    static IntPtr Slot(IntPtr o, int i){ IntPtr vt=Marshal.ReadIntPtr(o); return Marshal.ReadIntPtr(vt, i*IntPtr.Size); }
    static IntPtr SlotOff(IntPtr o, int off){ IntPtr vt=Marshal.ReadIntPtr(o); return Marshal.ReadIntPtr(vt, off); }
    static T D<T>(IntPtr f){ return Marshal.GetDelegateForFunctionPointer<T>(f); }
    static IntPtr MakeHStr(string s){ IntPtr h; WindowsCreateString(s ?? "", (uint)(s ?? "").Length, out h); return h; }
    static string FromHStr(IntPtr h){ if(h==IntPtr.Zero) return ""; uint l; IntPtr b=WindowsGetStringRawBuffer(h, out l); return b==IntPtr.Zero?"":Marshal.PtrToStringUni(b,(int)l); }
    static void Hr(int hr, string what){ if(hr!=0) throw new VmcxException(string.Format(Properties.Resources.Vmcx_OpFailHr, what, hr.ToString("X8")), hr); }
    void ThrowIfDisposed(){ if(_disposed) throw new ObjectDisposedException("VmcxStore"); }
}

/// <summary>写事务:持有排他锁。SetXxx/Remove 后 Commit() 落盘;Dispose() 释放锁(未 Commit 的改动丢弃)。</summary>
public sealed class VmcxWriter : IDisposable {
    readonly VmcxStore _s; bool _done;
    internal VmcxWriter(VmcxStore s){ _s = s; }
    public void SetInteger(string keyPath, long v){ _s.DoSetInteger(keyPath, v); }
    public void SetString (string keyPath, string v){ _s.DoSetString(keyPath, v); }
    public void Remove    (string keyPath){ _s.DoRemove(keyPath); }
    public void Commit(){ _s.DoCommit(); }
    public void Dispose(){ if(_done) return; _done = true; _s.DoUnlock(); }
}

/// <summary>VmDataStore 调用失败(HResult 非 0)。</summary>
public sealed class VmcxException : Exception {
    public int HResultCode;
    public VmcxException(string msg, int hr) : base(msg) { HResultCode = hr; }
}

}
