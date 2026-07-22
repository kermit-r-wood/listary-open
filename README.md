# ListaryOpen

ListaryOpen 是一款面向 Windows 的本地文件搜索与文件对话框快速跳转工具。它常驻系统托盘，让你无需离开键盘，就能找到文件、打开目录，或让“打开 / 保存 / 上传”对话框跳到正在使用的文件夹。

> 项目仍在开发中。目前仅支持 Windows，部分高级功能可能触发 UAC 或“未知发布者”提示。

## 你可以用它做什么

- 按 `Ctrl+Space`，随时搜索本机文件和文件夹。
- 在文件资源管理器中直接输入文件名，自动弹出搜索并优先显示当前目录内容。
- 按 `Ctrl+G`，让打开、保存或上传对话框快速跳转目录。
- 使用文件名片段、多个关键词或拼音查找内容。
- 在本地建立索引；文件名和路径不会上传到云端。
- 可选启用 NTFS 快速索引和 Hook Quick Switch，获得更快的索引速度及更广的对话框兼容性。

## 下载与安装

目前可从本仓库的 **Actions** 页面获取构建版本：

1. 打开最近一次成功的 `Package` 工作流。
2. 在页面底部下载 `ListaryOpen-win-x64` 构建产物。
3. 解压到任意文件夹。
4. 运行 `ListaryOpen.App.exe`。

ListaryOpen 是绿色软件，当前无需安装。请保留压缩包内的所有文件，不要只复制主程序。首次运行后，它会出现在 Windows 系统托盘中，并在后台索引当前用户目录。

本地构建尚未进行代码签名，因此 Windows SmartScreen 或 UAC 可能显示“未知发布者”。请只使用你信任的来源提供的构建。

### 系统要求

- Windows 10 或 Windows 11（64 位）。
- NTFS 快速索引和 Hook Quick Switch 需要管理员授权。
- 从源码运行时需要 .NET 8 SDK；打包版本无需另外安装 SDK。

## 快速上手

### 搜索文件

按 `Ctrl+Space` 打开搜索面板，输入文件或文件夹名称的一部分，然后使用：

当前台窗口是文件资源管理器时，也可以直接开始输入。ListaryOpen 会在右下角打开搜索浮层，把当前目录的直接文件和文件夹排在索引结果之前，并默认选中第一项。按 `↑` / `↓` 只移动浮层结果（不会移动 Explorer 自身列表），单击结果或按 `Enter` 可打开该项目；在地址栏、资源管理器搜索框或重命名输入框中输入时不会触发。

| 操作 | 快捷键 |
| --- | --- |
| 打开选中项目 | `Enter` |
| 在文件资源管理器中定位 | `Ctrl+Enter` |
| 复制完整路径 | `Ctrl+C` |
| 关闭搜索面板 | `Esc` |

也可以右键任意搜索结果，选择打开（Quick Switch 模式下为切换目录）、在文件资源管理器中定位，或复制完整路径。

搜索刚启动时可能还在建立索引。若结果不完整，请等待片刻后重试；托盘设置窗口中可以查看索引状态。

### 在文件对话框中跳转

1. 在 Explorer 或 Directory Opus 中打开目标文件夹。
2. 切回应用的“打开”“保存”或“上传”对话框。
3. 确保该对话框位于最前方，然后按 `Ctrl+G`。

文件对话框出现后，ListaryOpen 会把 Quick Switch 搜索条持续贴附在窗口下方。按 `Ctrl+G` 会直接切换到首选目录；点击搜索条才会展开最近的 Explorer、Directory Opus 和 Total Commander 目录，输入路径或目录名筛选后按 `Enter` 切换。

部分应用使用自定义文件对话框，默认方式可能无法控制。此时可在设置中启用 **Hook Quick Switch**，以提高兼容性。

## 托盘与设置

双击系统托盘中的 ListaryOpen 图标可打开设置；右键图标还可重新建立索引或退出程序。

设置页面包括：

- **Indexing**：查看索引状态和索引目录；可启用 **NTFS Fast Indexing** 加快 NTFS 磁盘扫描。
- **Hotkeys**：查看全局搜索和对话框跳转快捷键。
- **Quick Switch**：查看对话框跳转状态；可启用 **Hook Quick Switch**。
- **General**：管理通用行为和 Quick Save/Open 状态。

Hotkeys 区域可直接编辑全局搜索和 Quick Switch 快捷键并点击 **Apply**。支持 Ctrl、Alt、Shift、Win 与字母、数字、Space 或 F1–F24 的组合；成功后写入 `data/settings.json`，重启后继续生效。

启用 NTFS Fast Indexing 或 Hook Quick Switch 时，Windows 可能请求管理员权限。

### Hook Quick Switch 状态

| 状态 | 含义 |
| --- | --- |
| `Disabled` | 功能未启用 |
| `Ready` | x64 和 x86 辅助进程均正常运行 |
| `Degraded` | 只有部分支持可用 |
| `Failed` | 已请求启用，但辅助进程无法运行 |

即使 Hook Quick Switch 不可用，普通搜索和部分标准文件对话框跳转仍可正常使用。

### 性能数据

每次搜索和 Quick Switch 打开都会记录总耗时及各阶段耗时。搜索面板底部会显示本次总耗时和索引搜索耗时；完整数据写入程序目录下的 `data/performance-metrics.jsonl`，每行一个 JSON 对象，可用于统计 P50/P95。主要阶段包括 debounce、数据库连接等待、候选读取、使用记录读取、排序、结果发布，以及 hook 对话框捕获和候选目录收集。

## 常见问题

### 搜不到某个文件

打开托盘设置，确认索引是否仍在进行。当前版本默认索引当前 Windows 用户的用户目录；该目录以外的文件不会出现在结果中。需要从头更新索引时，可右键托盘图标并选择 **Reindex**。

### `Ctrl+Space` 或 `Ctrl+G` 没有反应

快捷键可能已被其他软件占用。退出或修改冲突软件的快捷键，然后重启 ListaryOpen。使用 `Ctrl+G` 时，还应确认文件对话框正处于最前方。

### 某些管理员程序的对话框无法跳转

Windows 会限制普通权限程序控制管理员权限窗口。尽量让目标应用与 ListaryOpen 处于相同权限级别，或启用需要授权的 Quick Switch 支持。

### 启用高级功能后出现权限提示

这是 NTFS 快速索引器或 Hook 辅助进程请求权限。未签名的开发构建还可能显示“未知发布者”。如果构建来源不可信，请取消操作。

### 关闭窗口后程序仍在运行

ListaryOpen 是托盘程序。请在系统托盘中找到它，右键选择 **Exit** 完全退出。

## 从源码运行

以下内容供希望参与开发或自行构建的用户使用：

```powershell
dotnet build ListaryOpen.sln
dotnet run --project src\ListaryOpen.App\ListaryOpen.App.csproj
```

运行测试：

```powershell
dotnet test ListaryOpen.sln
```

原生 Hook 构建说明见 [`native/ListaryOpen.Hooks/README.md`](native/ListaryOpen.Hooks/README.md)，手动测试流程见 [`docs/manual-test-checklist.md`](docs/manual-test-checklist.md)。
