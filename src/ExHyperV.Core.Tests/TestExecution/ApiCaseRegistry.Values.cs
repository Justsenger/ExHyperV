using System.Globalization;
using ExHyperV.Tools;
using ExHyperV.Vmcx;

namespace ExHyperV.Core.Tests.TestExecution;

internal static partial class ApiCaseRegistry
{
    private static void AddValueCases(Action<string, Action<ApiCaseContext>[]> add)
    {
        add("ApiResponse.Ok()", [c => { var r = c.Call(ApiResponse.Ok); c.Require(r.Success && r.ErrorKind == ApiErrorKind.None && r.Error == "", "成功结果保留成功状态和空错误"); }]);
        add("ApiResponse.Fail()", [c => {
            var error=new IOException("受控样例"); var r=c.Call(()=>ApiResponse.Fail("样例错误",5,ApiErrorSource.Win32,error,ApiErrorKind.AccessDenied));
            c.Require(!r.Success && r.ErrorKind==ApiErrorKind.AccessDenied && r.ErrorSource==ApiErrorSource.Win32 && r.Code==5 && r.Error=="样例错误" && ReferenceEquals(r.Exception,error), "失败类别、来源、代码、文本及异常引用完整保留");
        }]);
        add("ApiResponse.op_Implicit()", [c => {
            c.Equal(true,c.Call(()=>(bool)ApiResponse.Ok()),"成功转换"); c.Equal(false,c.Call(()=>(bool)ApiResponse.Fail("样例")),"失败转换"); c.Equal(false,c.Call(()=>(bool)(ApiResponse?)null),"null 转换");
        }]);
        add("ApiResponse.ToString()", [c => {
            var ok=c.Call(()=>ApiResponse.Ok().ToString()); var failure=c.Call(()=>ApiResponse.Fail("样例",5,ApiErrorSource.Win32).ToString());
            c.Require(ok.Contains("Ok") && failure.Contains("Win32") && failure.Contains("5") && failure.Contains("样例"),"诊断文本包含成功状态或错误来源、代码和说明");
        }]);
        add("ApiResponse<T>.Empty()", [c => { var r = c.Call(ApiResponse<string>.Empty); c.Require(r.Success && r.IsEmpty && !r.HasData && r.Data == null, "成功空结果与失败结果分开表达"); }]);
        add("ApiResponse<T>.Ok()", [c => {
            var data=new[] {"样例"}; var r=c.Call(()=>ApiResponse<string[]>.Ok(data)); var empty=c.Call(()=>ApiResponse<string>.Ok(null));
            c.Require(r.Success && r.HasData && !r.IsEmpty && ReferenceEquals(data,r.Data),"成功结果保留原数据引用"); c.Require(empty.Success && empty.IsEmpty && !empty.HasData,"null 数据归为成功空结果");
        }]);
        add("ApiResponse<T>.Fail()", [c => {
            var error=new IOException("受控样例"); var r=c.Call(()=>ApiResponse<string>.Fail("样例错误",5,ApiErrorSource.Win32,error,ApiErrorKind.AccessDenied));
            c.Require(!r.Success && !r.HasData && !r.IsEmpty && r.ErrorKind==ApiErrorKind.AccessDenied && r.Code==5 && r.ErrorSource==ApiErrorSource.Win32 && r.Error=="样例错误" && ReferenceEquals(error,r.Exception),"泛型失败结果完整保留错误，数据状态明确");
        }]);
        add("ApiResponse<T>.op_Implicit()", [c => {
            c.Equal(true,c.Call(()=>(bool)ApiResponse<bool>.Ok(false)),"成功读取 false 仍是成功响应"); c.Equal(true,c.Call(()=>(bool)ApiResponse<string>.Empty()),"成功空结果转换"); c.Equal(false,c.Call(()=>(bool)ApiResponse<bool>.Fail("样例")),"失败转换"); c.Equal(false,c.Call(()=>(bool)(ApiResponse<bool>?)null),"null 转换");
        }]);
        add("ApiResponse<T>.ToString()", [c => {
            var ok=c.Call(()=>ApiResponse<int>.Ok(0).ToString()); var empty=c.Call(()=>ApiResponse<int>.Empty().ToString()); var failure=c.Call(()=>ApiResponse<int>.Fail("样例",5,ApiErrorSource.Win32).ToString());
            c.Require(ok.Contains("Ok") && empty.Contains("Empty") && failure.Contains("Win32") && failure.Contains("5") && failure.Contains("样例"),"诊断文本区分数据、空结果和失败");
        }]);
        add("VmcxValue.Integer()", [c => { foreach (long v in new[] { 0L, -1L, long.MinValue, long.MaxValue }) { var r = c.Call(() => VmcxValue.Integer(v)); c.Equal(VmcxValueType.Integer, r.Type, "整数类型"); c.Equal(v.ToString(CultureInfo.InvariantCulture), r.Text, "整数原文"); } }]);
        add("VmcxValue.GetInteger()", [c => { foreach (long v in new[] { 0L, -1L, long.MinValue, long.MaxValue }) c.Equal(v, c.Call(() => VmcxValue.Integer(v).GetInteger()), "读取整数"); c.Throws<InvalidOperationException>(() => c.Call(() => VmcxValue.Boolean(true).GetInteger()), "类型校验"); }]);
        add("VmcxValue.UnsignedInteger()", [c => { foreach (ulong v in new[] { 0UL, 1UL, ulong.MaxValue }) { var r = c.Call(() => VmcxValue.UnsignedInteger(v)); c.Equal(VmcxValueType.UnsignedInteger, r.Type, "无符号类型"); c.Equal(v.ToString(CultureInfo.InvariantCulture), r.Text, "无符号原文"); } }]);
        add("VmcxValue.GetUnsignedInteger()", [c => { foreach (ulong v in new[] { 0UL, 1UL, ulong.MaxValue }) c.Equal(v, c.Call(() => VmcxValue.UnsignedInteger(v).GetUnsignedInteger()), "读取无符号整数"); c.Throws<InvalidOperationException>(() => c.Call(() => VmcxValue.Boolean(true).GetUnsignedInteger()), "类型校验"); }]);
        double[] finite = [0, -1, 0.125, double.MinValue, double.MaxValue];
        add("VmcxValue.Double()", [c => { foreach (var v in finite) { var r = c.Call(() => VmcxValue.Double(v)); c.Equal(VmcxValueType.Double, r.Type, "浮点类型"); c.Equal(v, double.Parse(r.Text, CultureInfo.InvariantCulture), "浮点原文"); } }, c => { foreach (var v in new[] { double.NaN, double.NegativeInfinity, double.PositiveInfinity }) c.Throws<ArgumentOutOfRangeException>(() => c.Call(() => VmcxValue.Double(v)), "拒绝非有限值"); }]);
        add("VmcxValue.GetDouble()", [c => { foreach (var v in finite) c.Equal(v, c.Call(() => VmcxValue.Double(v).GetDouble()), "读取浮点数"); }, c => c.Throws<InvalidOperationException>(() => c.Call(() => VmcxValue.String("NaN").GetDouble()), "拒绝其他存储类型")]);
        string[] strings = ["中文", "a\r\nb", " 空格 ", "", "表情😀", "尾随\0"];
        add("VmcxValue.String()", [c => { foreach (var v in strings) { var r = c.Call(() => VmcxValue.String(v)); c.Equal(VmcxValueType.String, r.Type, "字符串类型"); c.Equal(v, r.Text, "保留原文"); } }, c => { c.Throws<ArgumentNullException>(() => c.Call(() => VmcxValue.String(null!)), "null 参数"); c.Throws<ArgumentException>(() => c.Call(() => VmcxValue.String("\ud800")), "非法 UTF-16"); }]);
        add("VmcxValue.GetString()", [c => { foreach (var v in strings) c.Equal(v, c.Call(() => VmcxValue.String(v).GetString()), "读取字符串原文"); }, c => c.Throws<InvalidOperationException>(() => c.Call(() => VmcxValue.Integer(3).GetString()), "拒绝其他存储类型")]);
        add("VmcxValue.Boolean()", [c => { foreach (bool v in new[] { true, false }) { var r = c.Call(() => VmcxValue.Boolean(v)); c.Equal(VmcxValueType.Boolean, r.Type, "布尔类型"); c.Equal(v ? "True" : "False", r.Text, "布尔原文"); } }]);
        add("VmcxValue.GetBoolean()", [c => { foreach (bool v in new[] { true, false }) c.Equal(v, c.Call(() => VmcxValue.Boolean(v).GetBoolean()), "读取布尔值"); c.Throws<InvalidOperationException>(() => c.Call(() => VmcxValue.Integer(1).GetBoolean()), "类型校验"); }]);
        add("VmcxValue.Raw()", [c => { byte[] bytes = [0, 1, 255]; var r = c.Call(() => VmcxValue.Raw(bytes)); bytes[0] = 42; c.Equal("AAH/", r.Text, "源缓冲修改后原文保持"); c.Equal("", c.Call(() => VmcxValue.Raw([])).Text, "空数据"); }]);
        add("VmcxValue.GetRaw()", [c => { var value = VmcxValue.Raw([0, 1, 255]); var first = c.Call(value.GetRaw); c.Sequence(new byte[] { 0, 1, 255 }, first, "原始字节"); first[0] = 42; c.Sequence(new byte[] { 0, 1, 255 }, c.Call(value.GetRaw), "每次取得独立数组"); c.Equal(0, c.Call(() => VmcxValue.Raw([]).GetRaw()).Length, "空数组"); }]);
    }
}