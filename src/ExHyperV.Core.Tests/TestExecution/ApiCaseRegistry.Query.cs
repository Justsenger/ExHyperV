using ExHyperV.Models;
using ExHyperV.Services;

namespace ExHyperV.Core.Tests.TestExecution;

internal static partial class ApiCaseRegistry
{
    private static void AddQueryCases(Action<string, Action<ApiCaseContext>[]> add)
    {
        add("VmQueryFields.Find()", [c => {
            foreach(var field in VmQueryFields.All) c.Require(ReferenceEquals(field,c.Call(()=>VmQueryFields.Find(field.Id))),"字段标识："+field.Id);
            c.Equal<VmQueryField?>(null,c.Call(()=>VmQueryFields.Find("Missing.Test.Field")),"未知字段");
            c.Equal<VmQueryField?>(null,c.Call(()=>VmQueryFields.Find("identity.name")),"字段标识区分大小写");
        }]);
        add("VmQueryRequest.构造()", [c => {
            var id=Guid.NewGuid();var fields=new List<VmQueryField>{VmIdentityFields.Name,VmIdentityFields.State,VmIdentityFields.Name};var ids=new List<Guid>{id,id};
            var request=c.Call(()=>new VmQueryRequest(fields,ids));fields.Clear();ids.Clear();
            c.Sequence(new[]{"Identity.Name","Identity.State"},request.Fields.Select(f=>f.Id),"字段去重并保留首次顺序");c.Sequence(new[]{id},request.VmIds!,"目标去重和复制");
            c.Require(c.Call(()=>new VmQueryRequest([])).VmIds is null,"省略目标表示全部虚拟机");c.Equal(0,c.Call(()=>new VmQueryRequest([],[])).VmIds!.Count,"显式空目标");
            c.Throws<ArgumentException>(()=>c.Call(()=>new VmQueryRequest([],[Guid.Empty])),"空 GUID 参数");c.Throws<ArgumentNullException>(()=>c.Call(()=>new VmQueryRequest(null!)),"null 字段集合");
        }]);
        add("VmQueryRow.Get<T>()", [c => {
            var time=new DateTimeOffset(2020,1,1,0,0,0,TimeSpan.Zero);
            var values=new Dictionary<string,VmQueryValue>{[VmMemoryFields.Startup.Id]=VmMemoryFields.Startup.Make(VmQueryValueState.Available,0L,time),[VmMemoryFields.Maximum.Id]=VmMemoryFields.Maximum.Make(VmQueryValueState.Failed,null,time,"样例错误")};
            var row=new VmQueryRow("sample",values);values.Clear();
            var zero=c.Call(()=>row.Get(VmMemoryFields.Startup));c.Require(zero.HasValue&&zero.Value==0&&zero.ObservedAtUtc==time,"有效零值及采样时间完整保留");
            var failed=c.Call(()=>row.Get(VmMemoryFields.Maximum));c.Require(!failed.HasValue&&failed.State==VmQueryValueState.Failed&&failed.UntypedValue==null&&failed.Error=="样例错误","失败状态与有效零值分开表达");
            c.Equal(VmQueryValueState.NotRequested,c.Call(()=>row.Get(VmMemoryFields.Minimum)).State,"未请求字段");
        }]);
        add("VmQueryRow.GetCollection()", [c => {
            var time=new DateTimeOffset(2020,1,1,0,0,0,TimeSpan.Zero);
            var collections=new Dictionary<VmQueryCollection,VmQueryCollectionResult>{[VmQueryCollection.Storage]=new(VmQueryValueState.Available,[],null,time),[VmQueryCollection.NetworkAdapters]=new(VmQueryValueState.Failed,[],"样例错误",time)};
            var row=new VmQueryRow("sample",new Dictionary<string,VmQueryValue>(),collections);collections.Clear();
            var empty=c.Call(()=>row.GetCollection(VmQueryCollection.Storage));c.Require(empty.State==VmQueryValueState.Available&&empty.Rows.Count==0&&empty.ObservedAtUtc==time,"成功空集合保留状态与时间");
            var failed=c.Call(()=>row.GetCollection(VmQueryCollection.NetworkAdapters));c.Require(failed.State==VmQueryValueState.Failed&&failed.Error=="样例错误"&&failed.ObservedAtUtc==time,"集合失败原因与时间保留");
            c.Equal(VmQueryValueState.NotRequested,c.Call(()=>row.GetCollection(VmQueryCollection.GpuAssignments)).State,"未请求集合");
        }]);
    }
}