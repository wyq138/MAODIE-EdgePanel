using EdgePanel.Core;
using EdgePanel.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace EdgePanel.Pages;

/// <summary>已安装应用列表的一行：图标 + 名称（供 x:Bind 使用，构造后不再变化）。</summary>
public sealed class AppRow
{
    public AppRow(InstalledApp app)
    {
        App = app;
        Name = app.Name;
        Icon = AppIconService.GetIcon(app.IconSource);
    }

    public InstalledApp App { get; }

    public string Name { get; }

    public WriteableBitmap? Icon { get; }
}

public sealed partial class LauncherPage : Page
{
    private sealed record CommonApp(string Label, string Glyph, string Target);

    /// <summary>Windows 自带应用，直接用 shell 执行，无需用户配置。</summary>
    private static readonly CommonApp[] CommonApps =
    {
        new("文件资源管理器", "\uEC50", "explorer.exe"),
        new("记事本", "\uE70B", "notepad.exe"),
        new("计算器", "\uE8EF", "calc.exe"),
        new("画图", "\uE790", "mspaint.exe"),
        new("截图工具", "\uE722", "ms-screenclip:"),
        new("命令提示符", "\uE756", "cmd.exe"),
        new("PowerShell", "\uE756", "powershell.exe"),
        new("任务管理器", "\uE9D9", "taskmgr.exe"),
        new("控制面板", "\uE770", "control.exe"),
        new("系统设置", "\uE713", "ms-settings:"),
        new("设备管理器", "\uE964", "devmgmt.msc"),
        new("服务", "\uE9F5", "services.msc"),
        new("磁盘管理", "\uEDA2", "diskmgmt.msc"),
        new("系统信息", "\uE946", "msinfo32.exe"),
        new("注册表编辑器", "\uE71D", "regedit.exe"),
        new("资源监视器", "\uE9D2", "resmon.exe"),
        new("任务计划程序", "\uE787", "taskschd.msc"),
        new("回收站", "\uE74D", "shell:RecycleBinFolder"),
        new("默认浏览器", "\uE774", "https://www.bing.com"),
        new("微软商店", "\uE719", "ms-windows-store:"),
    };

    public LauncherPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            BuildCommonApps();
            Rebuild();
            _ = LoadAppsAsync();
        };
    }

    // ————————————————————————— 本机已安装应用 —————————————————————————

    private List<AppRow> _allApps = new();

    private async Task LoadAppsAsync()
    {
        AppScanRing.IsActive = true;
        AppScanRing.Visibility = Visibility.Visible;
        AppCountText.Text = "正在扫描本机应用…";

        try
        {
            var apps = await InstalledAppService.LoadAsync();
            _allApps = apps.Select(app => new AppRow(app)).ToList();
            ApplyFilter();
        }
        catch (Exception ex)
        {
            App.Log("加载已安装应用失败：" + ex.Message);
            AppCountText.Text = "扫描失败，请稍后重试";
        }
        finally
        {
            AppScanRing.IsActive = false;
            AppScanRing.Visibility = Visibility.Collapsed;
        }
    }

    private void ApplyFilter()
    {
        var keyword = AppSearchBox.Text?.Trim() ?? "";
        var rows = _allApps.Where(row =>
            keyword.Length == 0 ||
            row.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase));

        var list = rows.ToList();
        AppList.ItemsSource = list;
        AppCountText.Text = keyword.Length == 0
            ? $"共扫描到 {list.Count} 个应用"
            : $"匹配 {list.Count} 个应用";
    }

    private void AppSearch_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void AppList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is AppRow row)
        {
            InstalledAppService.Launch(row.App.Target);
        }
    }

    private void BuildCommonApps()
    {
        if (CommonGrid.Children.Count > 0)
        {
            return;
        }

        for (var i = 0; i < CommonApps.Length; i++)
        {
            var app = CommonApps[i];

            if (i / 2 >= CommonGrid.RowDefinitions.Count)
            {
                CommonGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }

            var content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
            };
            content.Children.Add(new FontIcon { FontSize = 15, Glyph = app.Glyph });
            content.Children.Add(new TextBlock
            {
                Text = app.Label,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
            });

            var button = new Button
            {
                Content = content,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(10, 7, 10, 7),
            };
            var target = app.Target;
            button.Click += (_, _) => SystemActions.OpenPath(target);

            Grid.SetRow(button, i / 2);
            Grid.SetColumn(button, i % 2);
            CommonGrid.Children.Add(button);
        }
    }

    private void Rebuild()
    {
        ListPanel.Children.Clear();

        foreach (var raw in SettingsStore.Current.Launchers.ToList())
        {
            var parts = raw.Split('|', 2);
            var name = parts.Length > 1 ? parts[0] : Path.GetFileName(parts[0]);
            var target = parts.Length > 1 ? parts[1] : parts[0];

            var title = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(name) ? target : name,
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            };

            var sub = new TextBlock
            {
                Text = target,
                FontSize = 11,
                Opacity = 0.6,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            var openButton = new Button
            {
                Content = new FontIcon { FontSize = 13, Glyph = "\uE8A7" },
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(0, 0, 6, 0),
            };
            ToolTipService.SetToolTip(openButton, "打开");
            var captured = target;
            openButton.Click += (_, _) => Open(captured, raw);

            var removeButton = new Button
            {
                Content = new FontIcon { FontSize = 13, Glyph = "\uE74D" },
                Padding = new Thickness(8, 4, 8, 4),
            };
            ToolTipService.SetToolTip(removeButton, "删除");
            var capturedRaw = raw;
            removeButton.Click += (_, _) =>
            {
                SettingsStore.Current.Launchers.Remove(capturedRaw);
                SettingsStore.Save();
                Rebuild();
            };

            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            actions.Children.Add(openButton);
            actions.Children.Add(removeButton);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var texts = new StackPanel();
            texts.Children.Add(title);
            texts.Children.Add(sub);

            Grid.SetColumn(actions, 1);
            grid.Children.Add(texts);
            grid.Children.Add(actions);

            ListPanel.Children.Add(new Border
            {
                Style = (Style)Application.Current.Resources["Card"],
                Padding = new Thickness(10, 8, 10, 8),
                Child = grid,
            });
        }
    }

    private static void Open(string target, string raw)
    {
        if (raw.StartsWith("cmd:", StringComparison.OrdinalIgnoreCase))
        {
            // 去掉 cmd: 前缀，否则会把前缀当命令内容传给 cmd /c
            SystemActions.RunCommand(raw[4..].Trim());
            return;
        }

        if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            target.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            SystemActions.OpenPath(target);
            return;
        }

        SystemActions.OpenPath(target);
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var target = TargetBox.Text?.Trim() ?? "";
        if (target.Length == 0)
        {
            return;
        }

        var name = NameBox.Text?.Trim() ?? "";
        var entry = string.IsNullOrWhiteSpace(name) ? target : $"{name}|{target}";

        SettingsStore.Current.Launchers.Insert(0, entry);
        SettingsStore.Save();

        TargetBox.Text = "";
        NameBox.Text = "";
        Rebuild();
    }

    private async void PickFile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.FileTypeFilter.Add("*");

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.Panel!);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                return;
            }

            TargetBox.Text = file.Path;
            if (string.IsNullOrWhiteSpace(NameBox.Text))
            {
                NameBox.Text = file.DisplayName;
            }
        }
        catch (Exception ex)
        {
            App.Log("选择文件失败：" + ex.Message);
        }
    }

    private async void PickFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            picker.FileTypeFilter.Add("*");

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.Panel!);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var folder = await picker.PickSingleFolderAsync();
            if (folder is null)
            {
                return;
            }

            TargetBox.Text = folder.Path;
            if (string.IsNullOrWhiteSpace(NameBox.Text))
            {
                NameBox.Text = folder.Name;
            }
        }
        catch (Exception ex)
        {
            App.Log("选择文件夹失败：" + ex.Message);
        }
    }
}