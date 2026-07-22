using System.Collections;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ListaryOpen.Core.Settings;

namespace ListaryOpen.App;

internal static class LocalizationManager
{
    private static readonly ConditionalWeakTable<DependencyObject, OriginalValues> Originals = new();
    private static readonly IReadOnlyDictionary<string, string> SimplifiedChinese =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ListaryOpen Options"] = "ListaryOpen 选项",
            ["General"] = "常规",
            ["Indexing activity"] = "索引状态",
            ["Software updates"] = "软件更新",
            ["Check GitHub Releases automatically"] = "自动检查 GitHub Releases",
            ["Changes to this option are saved immediately."] = "此选项的更改会立即保存。",
            ["Check now"] = "立即检查",
            ["View release"] = "查看发布页面",
            ["Appearance"] = "外观",
            ["Theme"] = "主题",
            ["System follows the Windows app theme. Geek uses a high-contrast terminal palette."] = "“系统”跟随 Windows 应用主题；Geek 使用高对比度终端配色。",
            ["Save appearance"] = "保存外观",
            ["Hotkeys"] = "快捷键",
            ["Click a field, then press the shortcut you want."] = "点击输入框，然后按下所需组合键。",
            ["Click a field, then press the desired key combination."] = "点击输入框，然后按下所需组合键。",
            ["File search"] = "文件搜索",
            ["Dialog jump"] = "对话框跳转",
            ["Apply hotkeys"] = "应用快捷键",
            ["File Search"] = "文件搜索",
            ["Full-disk indexing"] = "全盘索引",
            ["Check a disk to index the entire disk. An unchecked disk can still contain folders listed below."] = "选中磁盘以索引整个磁盘。未选中的磁盘仍可包含下方列出的文件夹。",
            ["Additional indexed folders (one per line)"] = "其他索引文件夹（每行一个）",
            ["Windows Start Menu shortcuts are always included as an application source."] = "Windows 开始菜单快捷方式始终作为应用来源。",
            ["Exclusions"] = "排除项",
            ["One full path, folder name, or extension pattern per line, for example *.tmp."] = "每行输入一个完整路径、文件夹名称或扩展名模式，例如 *.tmp。",
            ["One full path, folder name, or extension pattern such as *.tmp per line."] = "每行输入一个完整路径、文件夹名称或扩展名模式，例如 *.tmp。",
            ["Background consistency check"] = "后台一致性检查",
            ["File changes are monitored continuously. This controls only fallback checks for missed changes."] = "文件变化会被持续监控。此选项只控制遗漏变化的兜底检查。",
            ["Apply and rebuild index"] = "应用并重建索引",
            ["Language"] = "语言",
            ["Display Language"] = "界面语言",
            ["Choose the language used by the ListaryOpen interface. Click Save language to apply it."] = "选择 ListaryOpen 的界面语言，然后点击“保存语言设置”应用。",
            ["Interface language"] = "界面语言",
            ["Save language"] = "保存语言设置",
            ["Follow Windows (recommended)"] = "跟随 Windows（推荐）",
            ["When needed (recommended)"] = "需要时（推荐）",
            ["Every 15 minutes"] = "每 15 分钟",
            ["Every hour"] = "每小时",
            ["Every 6 hours"] = "每 6 小时",
            ["Every day"] = "每天",
            ["All types"] = "所有类型",
            ["Folders"] = "文件夹",
            ["Files"] = "文件",
            ["Documents"] = "文档",
            ["Images"] = "图片",
            ["Videos"] = "视频",
            ["Any time"] = "任意时间",
            ["Today"] = "今天",
            ["Last 7 days"] = "最近 7 天",
            ["Last 30 days"] = "最近 30 天",
            ["Last year"] = "最近一年",
            ["Actions"] = "操作",
            ["Actions & Integration"] = "操作与集成",
            ["NTFS fast indexing"] = "NTFS 快速索引",
            ["Hook Quick Switch"] = "Hook 快速切换",
            ["Quick Launch"] = "快速启动",
            ["An enabled rule runs only when the global-search query exactly matches its keyword."] = "启用的规则仅在全局搜索内容与关键词完全一致时运行。",
            ["Enabled"] = "已启用",
            ["Exact keyword"] = "精确关键词",
            ["Title"] = "标题",
            ["Program or file"] = "程序或文件",
            ["Arguments"] = "参数",
            ["Working directory"] = "工作目录",
            ["Silent"] = "静默运行",
            ["Run as administrator"] = "以管理员身份运行",
            ["Save quick launch rules"] = "保存快速启动规则",
            ["Menu"] = "菜单",
            ["Explorer Quick Menu"] = "Explorer 快捷菜单",
            ["Action"] = "操作",
            ["Path or program"] = "路径或程序",
            ["Supports environment variables and %CURRENT_FOLDER%. Command-only fields apply to RunCommand."] = "支持环境变量和 %CURRENT_FOLDER%。命令专用字段仅适用于 RunCommand。",
            ["Save menu"] = "保存菜单",
            ["About"] = "关于",
            ["About ListaryOpen"] = "关于 ListaryOpen",
            ["Local-first Windows file search and dialog navigation."] = "本地优先的 Windows 文件搜索与对话框导航工具。",
            ["Check GitHub Releases"] = "检查 GitHub Releases",
            ["ListaryOpen Search"] = "ListaryOpen 搜索",
            ["Search"] = "搜索",
            ["Dialog Jump"] = "对话框跳转",
            ["Search files and folders"] = "搜索文件和文件夹",
            ["Jump dialog to folder"] = "将对话框跳转到文件夹",
            ["Quick switch to folder"] = "快速切换到文件夹",
            ["Search files and folders."] = "搜索文件和文件夹。",
            ["Searching files and folders."] = "正在搜索文件和文件夹。",
            ["Type to search."] = "输入内容以搜索。",
            ["Select a folder to jump the dialog."] = "选择文件夹以跳转对话框。",
            ["Select a folder result to jump the dialog."] = "选择文件夹结果以跳转对话框。",
            ["Searching..."] = "正在搜索…",
            ["Select a result first."] = "请先选择一个结果。",
            ["Reading visible Task Manager items…"] = "正在读取任务管理器中的可见项目…",
            ["Changes are applied without restarting."] = "更改无需重启即可生效。",
            ["Settings saved and applied."] = "设置已保存并应用。",
            ["Update preference saved."] = "更新偏好设置已保存。",
            ["Updates have not been checked."] = "尚未检查更新。",
            ["Use modifiers plus a letter, number, Space, or F1-F24."] = "请使用修饰键加字母、数字、空格或 F1-F24。",
            ["Hotkeys saved and active."] = "快捷键已保存并生效。",
            ["Indexing: idle"] = "索引：空闲",
            ["Idle"] = "空闲",
            ["Indexing"] = "正在索引",
            ["Completed"] = "已完成",
            ["Canceled"] = "已取消",
            ["Failed"] = "失败",
            ["Disabled"] = "已禁用",
            ["Enable"] = "启用",
            ["Ready"] = "就绪",
            ["Degraded"] = "部分可用",
            ["Enabling"] = "正在启用",
            ["Retry"] = "重试",
            ["NTFS fast indexing: enabled for this session"] = "NTFS 快速索引：本次会话已启用",
            ["NTFS fast indexing: unavailable (elevated helper bundle is missing)."] = "NTFS 快速索引：不可用（缺少提权辅助组件）。",
            ["NTFS fast indexing: disabled"] = "NTFS 快速索引：已禁用",
            ["Hook quick switch: enabled for x64 and x86"] = "Hook 快速切换：x64 和 x86 均已启用",
            ["Open / switch"] = "打开 / 切换",
            ["Show in File Explorer"] = "在文件资源管理器中显示",
            ["Copy full path"] = "复制完整路径",
            ["Select a result to preview"] = "选择结果以预览",
            ["Folder"] = "文件夹",
            ["Preview is not available"] = "没有可用的预览",
            ["Could not load preview"] = "无法加载预览",
            ["Match"] = "匹配",
            ["Usage"] = "使用频率",
            ["Name"] = "名称",
            ["Path"] = "路径",
            ["Pinyin"] = "拼音",
            ["Recent"] = "最近使用",
            ["Pinned"] = "已固定",
            ["Exact Name"] = "名称精确匹配",
            ["Name Prefix"] = "名称前缀",
            ["Name Substring"] = "名称包含",
            ["Size:\nCreated:\nModified:"] = "大小:\n创建时间:\n修改时间:",
            ["Task Manager Search"] = "任务管理器搜索",
            ["Task Manager"] = "任务管理器",
            ["↑/↓ select · Enter confirm · Esc close"] = "↑/↓ 选择 · Enter 确认 · Esc 关闭",
            ["Quick Switch"] = "快速切换",
            ["Search folders or enter a path…"] = "搜索文件夹或输入路径…",
            ["Switch to this folder"] = "切换到此文件夹"
        };

    private static bool _initialized;
    private static AppLanguage _effectiveLanguage = AppLanguage.English;

    public static event EventHandler? LanguageChanged;

    public static AppLanguage EffectiveLanguage => _effectiveLanguage;

    public static void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        EventManager.RegisterClassHandler(
            typeof(FrameworkElement),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnElementLoaded));
    }

    public static void Apply(AppLanguage language)
    {
        Initialize();
        _effectiveLanguage = Resolve(language);
        if (Application.Current is not null)
        {
            foreach (Window window in Application.Current.Windows)
            {
                LocalizeTree(window);
            }
        }

        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    public static string Translate(string? text)
    {
        if (string.IsNullOrEmpty(text) || _effectiveLanguage != AppLanguage.SimplifiedChinese)
        {
            return text ?? string.Empty;
        }

        return SimplifiedChinese.TryGetValue(text, out var translated) ? translated : text;
    }

    private static AppLanguage Resolve(AppLanguage language)
    {
        if (language != AppLanguage.System)
        {
            return language;
        }

        return CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? AppLanguage.SimplifiedChinese
            : AppLanguage.English;
    }

    private static void OnElementLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is DependencyObject element)
        {
            LocalizeElement(element);
        }
    }

    private static void LocalizeTree(DependencyObject root)
    {
        var visited = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<DependencyObject>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!visited.Add(current))
            {
                continue;
            }

            LocalizeElement(current);
            try
            {
                for (var index = 0; index < VisualTreeHelper.GetChildrenCount(current); index++)
                {
                    pending.Push(VisualTreeHelper.GetChild(current, index));
                }
            }
            catch (InvalidOperationException)
            {
            }

            if (current is FrameworkElement or FrameworkContentElement)
            {
                foreach (var child in LogicalTreeHelper.GetChildren(current).OfType<DependencyObject>())
                {
                    pending.Push(child);
                }
            }
        }
    }

    private static void LocalizeElement(DependencyObject element)
    {
        var originals = Originals.GetOrCreateValue(element);

        if (element is Window window && HasLocalStringValue(window, Window.TitleProperty))
        {
            originals.Title ??= window.Title;
            window.Title = Translate(originals.Title);
        }

        if (element is TextBlock textBlock && HasLocalStringValue(textBlock, TextBlock.TextProperty))
        {
            originals.Text ??= textBlock.Text;
            textBlock.Text = Translate(originals.Text);
        }

        if (element is HeaderedContentControl headered &&
            headered.Header is string header &&
            HasLocalStringValue(headered, HeaderedContentControl.HeaderProperty))
        {
            originals.Header ??= header;
            headered.Header = Translate(originals.Header);
        }

        if (element is ContentControl contentControl &&
            contentControl.Content is string content &&
            HasLocalStringValue(contentControl, ContentControl.ContentProperty))
        {
            originals.Content ??= content;
            contentControl.Content = Translate(originals.Content);
        }

        if (element is FrameworkElement frameworkElement &&
            frameworkElement.ToolTip is string toolTip &&
            HasLocalStringValue(frameworkElement, FrameworkElement.ToolTipProperty))
        {
            originals.ToolTip ??= toolTip;
            frameworkElement.ToolTip = Translate(originals.ToolTip);
        }

        // Item templates frequently use converters for localized enum values and
        // result annotations. Refresh the live view so changing language updates
        // already-realized rows rather than only future rows.
        if (element is ItemsControl itemsControl)
        {
            try
            {
                itemsControl.Items.Refresh();
            }
            catch (InvalidOperationException)
            {
                // A view may be in the middle of a deferred refresh while the
                // visual tree is being localized. Its next normal refresh will
                // still use the newly selected language.
            }


            if (itemsControl is ComboBox comboBox && comboBox.ItemTemplate is { } itemTemplate)
            {
                // ComboBox caches a separate presenter for the selected value;
                // refreshing its collection view alone does not re-run the item
                // converter for that presenter.
                comboBox.ItemTemplate = null;
                comboBox.ItemTemplate = itemTemplate;
                var selectedItem = comboBox.SelectedItem;
                if (selectedItem is not null)
                {
                    comboBox.SelectedItem = null;
                    comboBox.SelectedItem = selectedItem;
                }
            }
        }
    }

    private static bool HasLocalStringValue(DependencyObject element, DependencyProperty property) =>
        element.ReadLocalValue(property) is string;

    private sealed class OriginalValues
    {
        public string? Title { get; set; }
        public string? Text { get; set; }
        public string? Header { get; set; }
        public string? Content { get; set; }
        public string? ToolTip { get; set; }
    }
}
