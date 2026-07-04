#nullable disable
// VmcxStore — .vmcx 读写引擎(基于官方 VmDataStore.dll / WinRT KeyValueStore)。
// 一切编辑通过官方引擎,产物必然合法(B树/校验/replaylog 全自动)。
//   using (var s = VmcxStore.Open(path)) {
//       foreach (var n in s.Enumerate()) ...
//       using (var w = s.BeginWrite()) { w.SetInteger(p, v); w.Commit(); }
//   }
// 本文件为 GPU-PV/DDA 类幽灵设备修复所需的最小集;编辑器全量能力(建设备/挂盘/任意类型读写)
// 在独立项目 ExHyperV-Edit,扩展编辑功能时从那边整体引入。
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
            if (s_statics != IntPtr.Zero) return;   // 双重检查:并发首调下只初始化一次
            IntPtr dll = LoadLibrary(@"C:\Windows\System32\VmDataStore.dll");
            if (dll == IntPtr.Zero) throw new InvalidOperationException("无法加载 VmDataStore.dll(需在 Hyper-V 主机上运行)");
            IntPtr fac;
            Hr(D<DGetFac>(GetProcAddress(dll, "DllGetActivationFactory"))(MakeHStr("Microsoft.HyperV.DataStore.KeyValueStore"), out fac), "GetActivationFactory");
            Guid s = IID_STATICS; IntPtr statics;
            Hr(D<DQI>(Slot(fac, 0))(fac, ref s, out statics), "QI IKeyValueStoreStatics");
            s_create = Slot(statics, 6);
            s_statics = statics;                     // 最后再赋值,确保其它线程看到的是已完整初始化的状态
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
        } finally { if (!ok) st.Dispose(); }   // 失败时释放已创建的 _store,避免泄漏
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
    /// 每步 COM 调用都检查 HRESULT(失败抛 VmcxException 而非闷头读空指针崩进程——损坏文件正是修复场景的常态输入);
    /// iter/i0 的释放放在 finally,异常路径不泄漏 COM 引用。</summary>
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
                if (node == IntPtr.Zero) throw new VmcxException("Current 返回空节点", -1);
                try {
                    byte isv; Hr(D<DByte>(Slot(node, 7))(node, out isv), "IsValue");
                    IntPtr hk = IntPtr.Zero, ht = IntPtr.Zero, hv = IntPtr.Zero;
                    try {
                        Hr(D<DOut>(Slot(node, 9))(node, out hk), "Key");
                        Hr(D<DOut>(Slot(node, 11))(node, out ht), "TypeName");
                        Hr(D<DOut>(Slot(node, 12))(node, out hv), "ValueText");
                        result.Add(new VmcxNode { Path = FromHStr(hk), Type = FromHStr(ht), Value = FromHStr(hv), IsValue = isv != 0 });
                    } finally {
                        // 释放本轮拥有的资源(out HSTRING 与 Current 节点均归调用方所有),否则长生命周期(GUI)会累积泄漏。
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

    /// <summary>不变量感知地删除一个设备:删数据节点 + manifest 条目 + size−1 + 重排 vdev 连续编号。返回被删 vdev 编号。</summary>
    public int RemoveDevice(string instanceGuid) {
        string g = instanceGuid.Trim('{','}','_',' ').ToLowerInvariant();
        var nodes = Enumerate();
        var vdevNum = new SortedDictionary<int,string>();
        foreach (var n in nodes) {
            if (!n.IsValue) continue;
            var m = System.Text.RegularExpressions.Regex.Match(n.Path, @"^/configuration/manifest/vdev(\d+)/instance$");
            if (m.Success) vdevNum[int.Parse(m.Groups[1].Value)] = (n.Value??"").Trim('{','}').ToLowerInvariant();
        }
        int K = -1;
        foreach (var kv in vdevNum) if (kv.Value == g) { K = kv.Key; break; }
        if (K < 0) throw new VmcxException("未找到 instance="+instanceGuid+" 的 manifest 条目", -1);
        var nums = new List<int>(vdevNum.Keys);
        int maxN = nums[nums.Count-1];
        string devNode = "/configuration/_"+g+"_";
        // ① 删设备数据子树。★VDEVVersion 是设备节点的粘键:它在"多删同一事务"或"它正好清空节点"的那一删里删不掉。
        //   实测可靠做法:节点尚有别的子键时,把 VDEVVersion 单独一个事务先删;其余值再一并删(随最后一个删除而节点消失)。
        var leaves = new List<string>();
        foreach (var n in nodes) if (n.IsValue && n.Path.StartsWith(devNode+"/", StringComparison.OrdinalIgnoreCase)) leaves.Add(n.Path);
        foreach (var lf in leaves) if (lf.EndsWith("/VDEVVersion", StringComparison.OrdinalIgnoreCase))
            using (var w = BeginWrite()) { w.Remove(lf); w.Commit(); }
        var rest = new List<string>();
        foreach (var lf in leaves) if (!lf.EndsWith("/VDEVVersion", StringComparison.OrdinalIgnoreCase)) rest.Add(lf);
        if (rest.Count > 0)   // ★幽灵设备(空数据节点)时 leaves 为空,别开空事务(空提交返回 hr=1)
            using (var w = BeginWrite()) { foreach (var lf in rest) w.Remove(lf); w.Commit(); }
        // 兜底:若仍有残留(其它未知粘键),逐个单独事务删除。
        for (int pass = 0; pass < 5; pass++) {
            var rem = new List<string>();
            foreach (var n in Enumerate())
                if (n.IsValue && n.Path.StartsWith(devNode+"/", StringComparison.OrdinalIgnoreCase)) rem.Add(n.Path);
            if (rem.Count == 0) break;
            foreach (var lf in rem) using (var w = BeginWrite()) { w.Remove(lf); w.Commit(); }
        }
        // ② manifest 维护(renumber 补空号 + size−1),单独事务。
        using (var w = BeginWrite()) {
            for (int M = K+1; M <= maxN; M++) {
                if (!vdevNum.ContainsKey(M)) continue;
                string dst = VdevPath(M-1), src = VdevPath(M);
                w.SetString (dst+"/device",   GetString (src+"/device"));
                w.SetInteger(dst+"/flags",    GetInteger(src+"/flags"));
                w.SetString (dst+"/instance", GetString (src+"/instance"));
                w.SetString (dst+"/name",     GetString (src+"/name"));
            }
            string lastV = VdevPath(maxN);
            foreach (var f in new[]{"/device","/flags","/instance","/name"}) { try { w.Remove(lastV+f); } catch {} }
            long size = GetInteger("/configuration/manifest/size");
            w.SetInteger("/configuration/manifest/size", size-1);
            w.Commit();
        }
        return K;
    }
    static string VdevPath(int i){ return "/configuration/manifest/vdev"+i.ToString("D3"); }

    /// <summary>校验 manifest 不变量,返回问题列表(空=健康):size 不符 / 编号空洞 / 孤儿数据节点。</summary>
    public List<string> ValidateManifest() {
        var issues = new List<string>();
        var nodes = Enumerate();
        long size = -1;
        try { size = GetInteger("/configuration/manifest/size"); } catch { issues.Add("manifest/size 读取失败"); }
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
        if (size>=0 && size != vdev.Count) issues.Add(string.Format("size={0} 与实际 vdev 数 {1} 不符", size, vdev.Count));
        var nums = new List<int>(vdev.Keys);
        if (nums.Count>0)
            for (int i=nums[0]; i<=nums[nums.Count-1]; i++)
                if (!vdev.ContainsKey(i)) issues.Add("manifest 编号空洞: vdev"+i.ToString("D3"));
        var vdevInst = new HashSet<string>();
        foreach (var kv in vdev) vdevInst.Add(kv.Value);
        foreach (var kv in devVals) {
            if (vdevInst.Contains(kv.Key)) continue; // 有 manifest 条目 = 正常
            // 仅剩 VDEVVersion(或空)= 已删设备的瞬态残留桩:无害,VM 照常启动,下次打开会自清。不报为问题。
            bool benign = kv.Value.Count == 0 || (kv.Value.Count == 1 && kv.Value[0].Equals("VDEVVersion", StringComparison.OrdinalIgnoreCase));
            if (!benign) issues.Add("孤儿数据节点: /_"+kv.Key+"_ 有残留数据但无 manifest 条目(真孤儿,应清除)");
        }
        // 反向检查:幽灵设备 = manifest 有条目但数据节点空/缺失,且同类型的其它设备有数据节点。
        //   (致命:VM 报 0x80070002 起不来。)用"同类型兄弟有数据"作判据,避免误报本就无数据节点的平台设备
        //   (如 BIOS/总线等;它们整族都无数据节点)。
        var typesWithData = new HashSet<string>();
        foreach (var kv in vdev) {
            int c = devVals.ContainsKey(kv.Value) ? devVals[kv.Value].Count : 0;
            if (c > 0 && vdevType.ContainsKey(kv.Key)) typesWithData.Add(vdevType[kv.Key]);
        }
        foreach (var kv in vdev) {
            int c = devVals.ContainsKey(kv.Value) ? devVals[kv.Value].Count : -1;
            string typ = vdevType.ContainsKey(kv.Key) ? vdevType[kv.Key] : "";
            if (c <= 0 && (typesWithData.Contains(typ) || VmcxSchema.FunctionalDeviceTypes.Contains(typ)))
                issues.Add(string.Format("幽灵设备: manifest vdev{0:D3}(instance={1})数据节点{2}(应为功能设备却无数据)→ VM 将无法启动(应删此条目)",
                    kv.Key, kv.Value, c < 0 ? "缺失" : "为空"));
        }
        // DDA(Virtual Pci Express Port)完整性:必须有 HostResources/HostResource/Instance(物理设备路径);
        //   只剩 VDEVVersion 的残缺 DDA 不会被上面的幽灵检测抓到(它有1个值),但同样起不来。这是用户的核心用例。
        const string DDA_TYPE = "2fcc454e-a36a-4c77-bb5e-a2d75a51f02c";
        foreach (var kv in vdev) {
            string t2; if (!vdevType.TryGetValue(kv.Key, out t2) || t2 != DDA_TYPE) continue;
            var vals = devVals.ContainsKey(kv.Value) ? devVals[kv.Value] : new List<string>();
            if (!vals.Exists(x => x.Equals("HostResources/HostResource/Instance", StringComparison.OrdinalIgnoreCase)))
                issues.Add(string.Format("残缺 DDA 设备: vdev{0:D3}(instance={1})缺 HostResources/HostResource/Instance → VM 将无法启动", kv.Key, kv.Value));
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
    internal void DoCommit(){ int hr=D<DVoid>(SlotOff(_hvs, OFF_COMMIT))(_hvs); if(hr!=0 && hr!=1) throw new VmcxException("Commit 失败 hr=0x"+hr.ToString("X8"), hr); } // hr==1(S_FALSE)=无改动可提交,非错误
    internal void DoUnlock(){ try { D<DVoid>(SlotOff(_hvs, OFF_UNLOCK))(_hvs); } catch { } }

    static IntPtr Slot(IntPtr o, int i){ IntPtr vt=Marshal.ReadIntPtr(o); return Marshal.ReadIntPtr(vt, i*IntPtr.Size); }
    static IntPtr SlotOff(IntPtr o, int off){ IntPtr vt=Marshal.ReadIntPtr(o); return Marshal.ReadIntPtr(vt, off); }
    static T D<T>(IntPtr f){ return Marshal.GetDelegateForFunctionPointer<T>(f); }
    static IntPtr MakeHStr(string s){ IntPtr h; WindowsCreateString(s ?? "", (uint)(s ?? "").Length, out h); return h; }
    static string FromHStr(IntPtr h){ if(h==IntPtr.Zero) return ""; uint l; IntPtr b=WindowsGetStringRawBuffer(h, out l); return b==IntPtr.Zero?"":Marshal.PtrToStringUni(b,(int)l); }
    static void Hr(int hr, string what){ if(hr!=0) throw new VmcxException(what + " 失败 hr=0x" + hr.ToString("X8"), hr); }
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
