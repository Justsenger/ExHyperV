using System.Runtime.InteropServices;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;

namespace ExHyperV.Core.Tests.TestExecution;

internal static partial class ApiCaseRegistry
{
    private static void AddRuleCases(Action<string, Action<ApiCaseContext>[]> add)
    {
        add("VmEditService.GetNameError()", [c => {
            foreach(var value in new string?[]{null,""," \t\r\n"}) c.Require(c.Call(()=>VmEditService.GetNameError(value)) is not null,"空白名称返回错误");
            foreach(var value in new[]{"中文 虚拟机","A'B","<样例>"}) c.Equal<string?>(null,c.Call(()=>VmEditService.GetNameError(value)),"非空名称交由后续操作核验");
        }]);
        add("OsImages.Canonical()", [c => {
            foreach(var value in new[]{("windows","Windows"),("LINUX","Linux"),("ubuntu","Ubuntu"),("未知分类","Windows"),((string?)null,"Windows")}) c.Equal(value.Item2,c.Call(()=>OsImages.Canonical(value.Item1)),"系统分类 "+(value.Item1??"null"));
        }]);
        add("HostGpuService.IsGpuPartitioningVersionSupported()", [c => { foreach (var row in new[] { (17133, false), (17134, true), (17135, true) }) c.Equal(row.Item2, c.Call(() => HostGpuService.IsGpuPartitioningVersionSupported(row.Item1)), "Build " + row.Item1); c.Equal(Environment.OSVersion.Version.Build >= 17134, c.Call(() => HostGpuService.IsGpuPartitioningVersionSupported()), "省略版本时使用宿主版本"); }]);
        add("HostNvmeService.IsNativeNvmeSupported()", [c => { foreach (var row in new[] { (26099, false), (26100, true), (26101, true) }) c.Equal(row.Item2, c.Call(() => HostNvmeService.IsNativeNvmeSupported(row.Item1)), "Build " + row.Item1); c.Equal(Environment.OSVersion.Version.Build >= 26100, c.Call(() => HostNvmeService.IsNativeNvmeSupported()), "省略版本时使用宿主版本"); }]);
        add("HostNetworkService.NormalizeDynamicMacAddress()", [c => {
            foreach(var input in new[]{"00abCD123456","00:ab:cd:12:34:56","00-ab-cd-12-34-56"," 00abcd123456 "}) c.Equal("00ABCD123456",c.Call(()=>HostNetworkService.NormalizeDynamicMacAddress(input)),"MAC "+input);
            foreach(var input in new string?[]{null,"","123","GG:ab:cd:12:34:56"}) c.Equal<string?>(null,c.Call(()=>HostNetworkService.NormalizeDynamicMacAddress(input)),"无效 MAC");
        }]);
        add("HostNetworkService.ValidateDynamicMacAddressRange()", [c => {
            foreach(var row in new[]{("00:15:5d:00:00:00","00:15:5d:ff:ff:ff",true),("00155D000000","00155D000000",true),("00155DFFFFFF","00155D000000",false),("bad","00155DFFFFFF",false)}) {
                var r=c.Call(()=>HostNetworkService.ValidateDynamicMacAddressRange(row.Item1,row.Item2)); c.Equal(row.Item3,r.Error is null,"MAC 范围 "+row.Item1+" → "+row.Item2);
                if(row.Item3){c.Equal(row.Item1.Replace(":","").ToUpperInvariant(),r.Minimum,"规范下界");c.Equal(row.Item2.Replace(":","").ToUpperInvariant(),r.Maximum,"规范上界");}
            }
        }]);
        add("VmNetworkService.CanEditMacAddress()", [c => { c.Equal(true, c.Call(() => VmNetworkService.CanEditMacAddress(false)), "静止状态允许编辑"); c.Equal(false, c.Call(() => VmNetworkService.CanEditMacAddress(true)), "活动状态锁定 MAC"); }]);
        add("VmNetworkService.GetConnectionError()", [c => {
            foreach(var row in new[]{(true,0,true),(true,1,false),(false,0,false),(false,2,false)}) c.Equal(row.Item3,c.Call(()=>VmNetworkService.GetConnectionError(row.Item1,row.Item2)) is not null,"连接 "+row.Item1+"，交换机数量 "+row.Item2);
        }]);
        add("VmProcessorService.CanEditOfflineSettings()", [c => { c.Equal(true, c.Call(() => VmProcessorService.CanEditOfflineSettings(true)), "已关机"); c.Equal(false, c.Call(() => VmProcessorService.CanEditOfflineSettings(false)), "其他状态"); }]);
        bool?[] states = [null, false, true];
        add("VmProcessorService.CanTogglePerfmonDependent()", [c => {
            foreach(var current in states) foreach(var pmu in states) c.Equal(current==true||pmu==true,c.Call(()=>VmProcessorService.CanTogglePerfmonDependent(current,pmu)),"依赖="+current+" PMU="+pmu);
        }]);
        add("VmProcessorService.CanTogglePerfmonPmu()", [c => {
            foreach(var pmu in states) foreach(var pebs in states) foreach(var ipt in states) c.Equal(pmu!=true||(pebs!=true&&ipt!=true),c.Call(()=>VmProcessorService.CanTogglePerfmonPmu(pmu,pebs,ipt)),"PMU="+pmu+" PEBS="+pebs+" IPT="+ipt);
        }]);
        add("VmProcessorService.GetProcessorCountOptions()", [c => {
            foreach(var row in new[]{(1,new[]{1}),(8,new[]{1,2,4,8}),(6,new[]{1,2,4,6}),(48,new[]{1,2,4,8,16,32,48})}) c.Sequence(row.Item2,c.Call(()=>VmProcessorService.GetProcessorCountOptions(row.Item1)),"逻辑处理器 "+row.Item1);
        }]);
        add("VmProcessorService.GetHostCapabilities()", [c => {
            foreach(var platform in Enum.GetValues<HostProcessorPlatform>()) foreach(bool supported in new[]{false,true}) foreach(var types in new[]{Array.Empty<string>(),new[]{"SNP"},new[]{"TDX"},new[]{"VBS"}}) {
                var r=c.Call(()=>VmProcessorService.GetHostCapabilities(platform,supported,types));
                c.Require(r.IsIntel==(platform==HostProcessorPlatform.Intel) && r.IsArm64==(platform==HostProcessorPlatform.Arm64) && r.HardwareIsolationSupported==(supported&&(types.Contains("SNP")||types.Contains("TDX"))),"平台 "+platform+"，支持="+supported+"，类型="+string.Join(',',types));
            }
        }]);
        add("VmSecurityService.CanEdit()", [c => {
            foreach(int generation in new[]{1,2}) foreach(var state in Enum.GetValues<VmState>()) c.Equal(generation==2&&state==VmState.PoweredOff,c.Call(()=>VmSecurityService.CanEdit(generation,state)),"代数 "+generation+"，状态 "+state);
        }]);
        add("VmStorageService.GetNewDiskSize()", [c => {
            foreach(var row in new[]{("128",128),("1",1),("0",128),("-2",128),("",128),("abc",128),("999999999999",128)}) c.Equal(row.Item2,c.Call(()=>VmStorageService.GetNewDiskSize(row.Item1)),"磁盘容量输入 "+row.Item1);
        }]);
        add("VmStorageService.GetControllerNumbers()", [c => { c.Sequence(new[] { 0, 1 }, c.Call(() => VmStorageService.GetControllerNumbers("IDE")), "IDE 控制器编号"); c.Sequence(new[] { 0, 1, 2, 3 }, c.Call(() => VmStorageService.GetControllerNumbers("SCSI")), "SCSI 控制器编号"); }]);
        add("VmStorageService.GetControllerTypes()", [c => {
            foreach(var row in new[]{(1,false,"HardDisk",new[]{"IDE","SCSI"}),(1,true,"HardDisk",new[]{"SCSI"}),(1,false,"DvdDrive",new[]{"IDE"}),(1,true,"DvdDrive",new[]{"IDE"}),(2,false,"HardDisk",new[]{"SCSI"}),(2,true,"HardDisk",new[]{"SCSI"}),(2,false,"DvdDrive",new[]{"SCSI"}),(2,true,"DvdDrive",new[]{"SCSI"})}) c.Sequence(row.Item4,c.Call(()=>VmStorageService.GetControllerTypes(row.Item1,row.Item2,row.Item3)),"代数/活动/类型 "+row.Item1+"/"+row.Item2+"/"+row.Item3);
        }]);
        add("VmStorageService.CanEditStoragePath()", [c => {
            c.Equal(false,c.Call(()=>VmStorageService.CanEditStoragePath(null)),"目标缺席");c.Equal(false,c.Call(()=>VmStorageService.CanEditStoragePath(new(){DiskType="Physical"})),"物理磁盘");c.Equal(true,c.Call(()=>VmStorageService.CanEditStoragePath(new(){DiskType="Dynamic"})),"虚拟磁盘");
        }]);
        add("VmStorageService.GetAvailableLocations()", [c => {
            VmStorageAttachment[] occupied=[new(){ControllerType="SCSI",ControllerNumber=0,ControllerLocation=0},new(){ControllerType="SCSI",ControllerNumber=0,ControllerLocation=2},new(){ControllerType="SCSI",ControllerNumber=1,ControllerLocation=3}];
            c.Sequence(Enumerable.Range(0,64).Except([0,2]),c.Call(()=>VmStorageService.GetAvailableLocations("SCSI",0,occupied)),"仅排除所选控制器的占用位置");
            c.Sequence(new[]{0,1},c.Call(()=>VmStorageService.GetAvailableLocations("IDE",0,[])),"空 IDE 控制器");
            c.Equal(0,c.Call(()=>VmStorageService.GetAvailableLocations("IDE",0,[new(){ControllerType="IDE",ControllerNumber=0,ControllerLocation=0},new(){ControllerType="IDE",ControllerNumber=0,ControllerLocation=1}])).Count,"已满 IDE 控制器");
        }]);
        add("VmIpService.GetPrimaryAddress()", [c => {
            VmNetworkAdapter[] adapters=[new(){IpAddresses=["bad","::1","fe80::1"]},new(){IpAddresses=["192.0.2.8,192.0.2.9"]}];
            c.Equal("192.0.2.8",c.Call(()=>VmIpService.GetPrimaryAddress(adapters)),"跳过无效文本与 IPv6 后取得首个 IPv4");
        },c=>{c.Equal("",c.Call(()=>VmIpService.GetPrimaryAddress([])),"空列表默认值");c.Equal("备用",c.Call(()=>VmIpService.GetPrimaryAddress([],"备用")),"显式回退文本");}]);
        add("HostProcessorPlatformService.GetArchitecture()", [c => { CheckArchitecture(c); c.Evidence.Add("本进程架构：" + RuntimeInformation.ProcessArchitecture); }]);
    }
    private static void CheckArchitecture(ApiCaseContext c)
    {
        GetNativeSystemInfo(out var system);
        var expected = system.Architecture switch { 0 => Architecture.X86, 5 => Architecture.Arm, 9 => Architecture.X64, 12 => Architecture.Arm64, _ => throw new InvalidDataException("系统架构代码尚待支持：" + system.Architecture) };
        c.Equal(expected, c.Call(HostProcessorPlatformService.GetArchitecture), "与 GetNativeSystemInfo 独立对照宿主架构");
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSystemInfo
    {
        public ushort Architecture, Reserved; public uint PageSize; public IntPtr Minimum, Maximum; public UIntPtr Mask; public uint Count, Type, Granularity; public ushort Level, Revision;
    }
    [DllImport("kernel32.dll")] private static extern void GetNativeSystemInfo(out NativeSystemInfo system);
}
