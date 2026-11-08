# 发布官网 API 文档

`ExHyperV.com/api/` 直接使用本目录生成器的 HTML，内容仍以 Core 的 XML 注释、`API.md`、示例及 Generator 模板为准。

“配置版本”字段内的宿主 build 对照来自 `Generator/ConfigurationVersions.xml`，由同一份数据生成 Markdown 表和网页中的版本矩阵。数据源链接固定到参考库提交，Windows 发行名与 build 按微软发行记录校正；更新时修改此数据文件，重新生成即可。它只参与文档展示，不作为 Core 的运行时兼容性规则。文件字段可使用 `<fielddoc><format>` 提供单独的“文件格式”说明。

字段页按“成员、类型、取值、描述”排列。枚举字段的取值直接从枚举声明生成，包含成员、数值和说明；不用另写一份列表。其他固定选项使用 `<fielddoc><values kind="options">`，格式、范围和示例使用 `<fielddoc><values kind="format">`，都显示在“取值”中。普通 `<values>` 用于补充转换规则，合入描述。没有取值说明时不显示空行。“取值”只写格式、范围、枚举和示例，不放转换方法或调用教程；“描述”解释当前成员的用途，不补充无关概念的区别。

网页布局在 `Generator/WebTemplate.html`，样式在 `Generator/WebStyles.css`，以官网的黑色顶栏、Comfortaa 字标、浅／深色薄荷绿配色及圆角导航为基准。生成器将 CSS、字标所用的 Comfortaa Latin 字体及其完整 OFL 许可证嵌入 HTML，独立文件仍可使用。正文、标题和导航共用系统中文无衬线字体，中英文使用同一字体栈；正文为正常字重，标题和强调文字适度加粗，代码使用等宽字体。网站与独立预览采用相同的字体规则。主站导航使用 `/`、`/os/`、`/docs/`，始终保留当前站点的协议、主机和端口，不写死生产域名。仅在 HTTP(S) 的 `/api/` 路径下显示主站导航；独立预览或文件打开时隐藏这些入口，字标返回文档概览。

在仓库根目录执行（需要 Node.js 22+、.NET 10 SDK 和 Git）：

```powershell
node src/ExHyperV.Core/Documentation/export-website.mjs "$env:TEMP/ExHyperV-core-api"
```

输出 `index.html`、`API.md`、`manifest.json`。导出只分析源码，既不执行 Core，也不执行示例。API.md 过期时失败；先按下方“更新和检查”更新文档。`--require-clean` 禁止导出未提交的 Core 内容，供发布使用。

`.github/workflows/core-api-docs.yml` 在 `2.0.0` 的 Core 变更后自动导出，将产物发布至 `api-docs` 分支，然后请求 Cloudflare Workers Builds 重建官网。需在主仓库配置 Actions Secret `EXHYPERV_SITE_DEPLOY_HOOK`，内容为官网 Worker 的完整 Deploy Hook URL。无 Hook 时仍发布产物，但会明确警告官网自动更新未接通。

网站构建拉取 `api-docs` 的单个提交，验证完整性后原样提供 HTML。`manifest.json` 保存源提交和内容哈希，便于排查线上文档版本。官网构建环境无需 .NET，不引用本机路径。推送触发的更新有构建延迟；本地未提交内容仅用于预览。

首次上线必须一起提交当前生成器和文档源；只提交此脚本不能保证远程旧版生成器支持同样的网页输出。官网仓库 `docs/core-api-sync.md` 记录完整接入步骤。

## 维护内容和示例

这一节供修改 Core 的维护者使用，说明文字和示例应改在哪里，以及如何重新生成文档。

### 只维护一份材料

| 要修改的内容 | 修改位置 |
| --- | --- |
| 类型、方法、参数和字段的含义 | 对应 C# 声明旁的 XML 注释 |
| 接入步骤、共同约定和迁移说明 | `API.md` 的手写章节 |
| 使用说明中的调用示例 | `Documentation/UsageExamples.cs` |
| 页面样式和生成规则 | `Documentation/Generator` |

`api-reference` 和 `api-example-名称` 标记之间的内容由生成器更新。网页也由同一份材料生成，不直接编辑生成后的 HTML。

网页的“设计图”页签使用 `Documentation/Generator/DesignPages.xml` 中的说明和 SVG 图示，与 API 参考一起生成到同一个 HTML。规划草案与当前结构分别标记；调整设计时同步修改文字和图示。生成器检查页面身份、图示说明及相关类型引用；这些检查不能代替对设计与实际实现是否一致的审查。

正文统一使用中文，代码名称和协议名称保持原样。说明先讲用途，再讲参数、结果和必要的使用条件。未确定的行为留在审查记录中；公开文档明确当前已有能力和确知的限制。

### 编写字段说明

需要详细解释的字段，在 XML 注释中使用 `fielddoc`。每栏回答一个具体问题：

| 栏目 | 应写的内容 |
| --- | --- |
| `meaning` | 这个值表示什么 |
| `unit` | 单位、换算或编码 |
| `values` | 合法值、特殊值及默认值 |
| `source` | 从哪里读取，或如何计算 |
| `applicability` | 哪些版本、硬件或对象状态适用 |
| `absence` | 未查询、缺测、不支持或失败时怎样表示 |
| `verification` | 已验证的范围与尚未验证的部分 |
| `references` | 官方资料或相关实现 |

普通属性和字段写在自身声明上；位置式 record 的字段说明写在类型注释中对应的 `param` 内。生成属性的说明写在其源字段上。新增读取模型时，将类型登记到 `FieldDocumentationModels`；其他字段也可显式添加 `fielddoc`。

可组合查询字段还需要 `modification` 描述修改能力：`supported` 表示已有入口，`readonly` 表示只读，`unavailable` 表示尚无入口，`operation` 表示通过操作改变。已有入口要填写 `input`、`submission`，并用 `see` 引用具体方法。

添加设备等没有对应查询字段的修改操作，在公开方法上使用 `editentry`，填写 `label` 和 `category`。业务模块开头的说明使用所属类型注释中的 `businessdoc`。详细格式可参照相邻的现有声明。

### 更新和检查

在仓库根目录执行以下命令。生成器使用 .NET SDK 的编译器读取源码；不会加载 Core 或操作虚拟机。

```powershell
# 根据源码和示例更新 API.md。
dotnet run --project src/ExHyperV.Core/Documentation/Generator -- --write

# 检查说明是否齐全，以及文档是否与源码同步。
dotnet run --project src/ExHyperV.Core/Documentation/Generator -- --check

# 生成可以独立打开或部署的网页。
dotnet run --project src/ExHyperV.Core/Documentation/Generator -- --html "$env:TEMP/ExHyperV-Core-API.html"

# 编译全部调用示例和接口内示例，不执行它们。
dotnet run --project src/ExHyperV.Core/Documentation/Generator -- --verify-examples '<构建输出目录>/ExHyperV.Core.dll'
```

示例检查需要 Core DLL 及其托管依赖位于同一输出目录。可使用应用的完整构建输出，或构建 Core 时设置 `CopyLocalLockFileAssemblies=true`。

示例源码用 `example:名称:start/end` 标记可展示片段。片段开头的 `title` 和 `description` 注释会生成标题和说明，其余内容生成代码块，放入 `API.md` 中同名的 `api-example-名称:start/end` 块；每个示例恰好放置一次，整个文件参加编译检查。

### 提交前核对

1. 接口变化时，同步修改用途、参数、返回值和示例。
2. 执行生成、同步检查和示例编译。
3. 打开生成网页，检查链接、表格和代码是否容易阅读。
4. 将源码注释、示例和 API.md 一起保存。发布文档使用对应版本的材料。

编译检查能发现示例调用已经失效，但不能证明行为说明正确。行为仍需结合实现和相应测试核对。
