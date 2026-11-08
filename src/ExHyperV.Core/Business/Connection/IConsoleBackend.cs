namespace ExHyperV.Services;

// 连接方式在 Core 内实现适配并加入目录；公共调用方只接触 IConsoleConnection 和各能力接口。
internal interface IConsoleBackend
{
    ConsoleBackendInfo Info { get; }
    IConsoleConnection Create(ConsoleConnectionOptions options);
}
