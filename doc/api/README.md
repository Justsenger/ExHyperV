# API 文档维护

公开接口签名、参数、返回类型和枚举从 Core 源码生成。行为、使用条件、错误处理和示例与代码一起维护。

| 内容 | 源文件 |
| --- | --- |
| 接口含义、参数约束、结果和字段说明 | `src/ExHyperV.Core/` 的 XML 注释 |
| 开发者任务指南 | `doc/api/guide.md` |
| 编译检查的完整示例 | `doc/api/UsageExamples.cs` |
| 中文标题、分类、工具分组、只读策略、说明关联 | `doc/api/catalog.json` |
| 设计说明、配置版本 | `doc/api/DesignPages.xml`、`ConfigurationVersions.xml` |
| 分析、校验和生成工具 | `tools/api-docs/` |
| 网页模板、样式和字体 | `website/src/api/` |

生成结果为 `doc/api/API.md`、`API.json`、`FieldHelp.json`。网页和 MCP 使用 API.json 的稳定 `uid`；测试目录使用其中的分类；WinUI 使用 FieldHelp.json 的属性说明。目录标题和指南文字独立于 MCP 的调用分组与只读权限。

## 更新

在仓库根目录执行：

```powershell
dotnet run --project tools/api-docs --configuration Release -- --write
dotnet run --project tools/api-docs --configuration Release -- --check
dotnet run --project tools/api-docs --configuration Release -- --verify-examples <Core.dll的路径>
node tools/api-docs/export.mjs <输出目录>
```

新增公开类型和成员时，在 catalog.json 登记完整 XML 文档 ID、中文标题和分类。方法的 `readOnly` 描述执行策略；字段的“只读／读写”展示由 XML `modification` 记录生成。两者分别服务于方法调用权限和字段编辑说明。

`helpSource` 指向同一语义的字段文档 ID。处理器与内存配置属性由此复用查询字段说明。Core 模型保存数据；界面通知、编辑状态和选项标签由 WinUI 持有。

`--check` 检查生成结果是否与当前源码一致；`--verify-examples` 编译指南和 XML 中的示例。导出包包含 HTML、Markdown、API.json 和校验清单。网站同步先校验完整包，再替换已安装文档。

## 内容要求

直接说明用途、取值、单位、适用条件及结果。参数和返回值应足以让调用方构造请求、识别失败并采取下一步操作。未知机制统一写“相关机制暂不明确。”

方法标题写明操作；读取与设置分别命名，重载注明参数或用途差异，构造函数注明对象类型。生成器检查同一类型内的重名，网页检查覆盖全部文档入口中的实际方法标题。

能力介绍、使用流程和职责说明归入“功能说明”；类型摘要放在“描述”行。同一页面的相同说明只展示一次。

网页首页只展示两大层面的介绍与分类入口，不展开类型或成员总表。网页不设独立“使用说明”栏目；catalog.json 的成员 `examples` 引用 UsageExamples.cs 中的示例名称，将已编译检查的示例展示在对应方法下。类型 XML 示例随对应功能页展示。

字段使用 `fielddoc` 组织含义、取值、适用条件、缺值和修改入口。固定选项写在 `values kind="options"`；格式与范围写在 `values kind="format"`。枚举取值来自声明。操作示例通过 `example:<名称>:start/end` 标记引用到指南。

## 本地网站

运行仓库的 `启动官网.cmd`，或在 website 执行 `pnpm dev`。默认地址为 `http://localhost:5189/api/`。开发服务监听 Core、doc/api、tools/api-docs 与 website/src/api 的输入变化并重新生成。

```powershell
cd website
node --test scripts/test-api-navigation.mjs scripts/test-core-api.mjs
pnpm build
```

构建与发布说明见 [网站同步](../../website/docs/core-api-sync.md)。
