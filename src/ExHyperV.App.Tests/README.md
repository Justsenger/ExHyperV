# App.Tests 桌面截图

在已登录的 Windows 桌面中运行 PowerShell 7，使用与 App 相同的权限级别。传入已解包发布目录或构建目录中的桌面程序。

```powershell
pwsh -File src/ExHyperV.App.Tests/Test-App.ps1 -AppPath "C:\path\to\publish\ExHyperV.exe"
```

执行器启动独立 App 实例，依次通过 UI Automation 操作，并将每个检查点的窗口原图保存到 `.cache/app-tests/<轮次>/`。打开 `index.html` 浏览，或将 PNG 和 `summary.json` 交给 AI。汇总列出程序哈希、场景、预期画面、截图尺寸、DPI、图片数量和 PNG 总字节数。可用 `-OutputRoot` 指定其他位置。

在 `Scenes.ps1` 中用 `Record-Scene` 登记稳定的场景 ID、预期画面和操作。等待对应控件或状态后再截图。失败时尝试留下现场图并结束本轮，错误随索引保存。当前捕获主窗口及其中的 XAML 浮层，独立系统对话框需要为其窗口单独登记截图点。
