# ListaryOpen

ListaryOpen 是一个 Windows 文件搜索和对话框快速跳转工具。它以托盘程序运行，在本地建立文件索引，提供键盘优先的搜索面板，并帮助打开、保存、上传等文件对话框快速跳转到你正在使用的文件夹。

## 功能亮点

- 使用 `Ctrl+Space` 打开全局文件和文件夹搜索。
- 使用 `Ctrl+G` 在打开、保存、上传对话框中快速跳转目录。
- 后台索引当前用户的用户目录。
- 可选启用 NTFS 快速索引，加快扫描速度。
- 可选启用 Hook Quick Switch，提升 x64 和 x86 应用对话框的覆盖范围。
- 通过系统托盘管理索引、热键、Quick Switch 和启动状态。

## 系统要求

- Windows。
- 如果从源码运行，需要 .NET 8 SDK。
- NTFS 快速索引和 Hook Quick Switch 可能需要管理员授权。

Hook Quick Switch 会使用原生辅助进程。本地未签名构建可能触发 Windows 或 UAC 的未知发布者提示；除非二进制文件已签名，否则这是正常现象。

## 从源码快速启动

也可以在 GitHub 的 **Actions** 页面运行或下载 `Package` 工作流产物。打包产物名为 `ListaryOpen-win-x64`，其中包含 WPF 主程序、提升权限索引器，以及 x64/x86 Hook Quick Switch 原生组件。

构建应用：

```powershell
dotnet build ListaryOpen.sln
```

运行 WPF 应用：

```powershell
dotnet run --project src\ListaryOpen.App\ListaryOpen.App.csproj
```

启动后，ListaryOpen 会出现在 Windows 系统托盘中，并开始在后台索引已配置的目录。默认索引目录是当前用户的用户目录。

## 基本用法

### 搜索

按 `Ctrl+Space` 打开或隐藏搜索面板。

输入文件或文件夹名称的一部分，选择结果后可以使用：

- `Enter` 打开选中的项目。
- `Ctrl+Enter` 在文件资源管理器中定位选中的项目。
- `Ctrl+C` 复制选中项目的路径。
- `Esc` 关闭搜索面板。

搜索结果会显示名称、父级路径、文件或文件夹类型，以及匹配原因。

### 对话框跳转

当打开、保存或上传对话框位于前台时，按 `Ctrl+G`。

ListaryOpen 会尝试把当前对话框跳转到 Explorer 或 Directory Opus 中最相关的文件夹。如果无法直接跳转，它会以 Quick Switch 或 Dialog Jump 模式打开搜索面板，让你手动选择目标文件夹。

为了获得更稳定的效果：

- 按 `Ctrl+G` 前，确保目标文件对话框处于前台。
- 保持目标文件夹在 Explorer 或 Directory Opus 中打开，或最近使用过。
- 如果某个应用的对话框无法被默认方式处理，可以启用 Hook Quick Switch。

## 设置

可以从系统托盘图标打开设置窗口。

设置窗口包含：

- **Indexing**：当前索引状态和索引目录。
- **Hotkeys**：已注册的全局快捷键。
- **Quick Switch**：对话框跳转和 Hook Quick Switch 状态。
- **General**：应用通用行为和 Quick Save/Open 状态。

点击 **Enable NTFS Fast Indexing** 可以启用提升权限的索引器，用于更快扫描 NTFS 卷。该操作可能触发 UAC 提示。

点击 **Enable Hook Quick Switch** 可以启动 x64 和 x86 的原生 hook host。设置窗口会显示每个 host 是否可用、降级、失败或已禁用。

## Hook Quick Switch

Hook Quick Switch 用于提升 `Ctrl+G` 对话框跳转能力，尤其适合普通 Windows 对话框自动化难以控制的应用。

典型目标包括浏览器上传对话框、编辑器打开文件对话框，以及同时存在 x64/x86 的桌面应用。启用后，ListaryOpen 会启动原生 hook host，并在设置中报告状态。

状态含义：

- **Disabled**：hook 支持未启用。
- **Ready**：x64 和 x86 hook host 都在运行。
- **Degraded**：只有部分 hook 支持可用。
- **Failed**：已请求启用 hook，但 host 不可用。

如果 Hook Quick Switch 被禁用或不可用，普通搜索和部分受支持的对话框跳转仍然可以工作，但覆盖范围会更有限。

## 常见问题

### 应用提示快捷键注册失败

可能有其它应用已经占用了 `Ctrl+Space` 或 `Ctrl+G`。关闭或重新配置冲突应用后，重启 ListaryOpen。

### 搜不到某个文件

先检查设置窗口里的索引状态。如果索引仍在进行中，等待它完成。当前版本固定索引当前用户的用户目录，尚不支持在设置中调整索引目录。

### 按 `Ctrl+G` 没有反应

确认打开、保存或上传对话框处于前台。如果目标应用以管理员权限运行，而 ListaryOpen 没有以相同权限运行，Windows 可能会阻止访问。可以让两者处于相同完整性级别，或在可用时启用相关提升权限支持。

### Hook Quick Switch 请求权限

Hook Quick Switch 可能需要 UAC 授权，以便启动原生辅助进程。未签名的本地构建也可能出现未知发布者提示。

### 某些对话框仍然无法跳转

部分自定义对话框不是标准 Windows 文件对话框。可以尝试启用 Hook Quick Switch，或使用搜索面板手动选择目标文件夹。也可以参考 `docs\manual-hook-quick-switch-test.md` 进行手动验证。

## 开发者入口

运行自动化测试：

```powershell
dotnet test ListaryOpen.sln
```

生成完整 Windows 打包产物可以使用 GitHub Actions 中的 `Package` 工作流。该工作流会在 Windows runner 上构建 .NET 应用、运行测试、编译 native hooks，并上传 `ListaryOpen-win-x64` artifact。

原生 hook 构建细节见 `native\ListaryOpen.Hooks\README.md`。

手动验证流程见：

- `docs\manual-test-checklist.md`
- `docs\manual-hook-quick-switch-test.md`
