using EdgePanel.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace EdgePanel.Pages;

public sealed partial class ToolsPage : Page
{
    private sealed record ToolAction(string Label, string Glyph, Action Invoke);

    private static readonly ToolAction[] Actions =
    {
        new("锁定屏幕", "\uE72E", SystemActions.LockScreen),
        new("睡眠", "\uE708", SystemActions.Sleep),
        new("切换静音", "\uE74F", SystemActions.ToggleMute),
        new("清空回收站", "\uE74D", SystemActions.ClearRecycleBin),
        new("清理临时目录", "\uE894", SystemActions.EmptyTempFolder),
        new("任务管理器", "\uE9D9", SystemActions.OpenTaskManager),
        new("资源监视器", "\uE9D2", SystemActions.OpenResourceMonitor),
        new("文件资源管理器", "\uE8B7", SystemActions.OpenExplorer),
        new("控制面板", "\uE770", SystemActions.OpenControlPanel),
        new("系统设置", "\uE713", SystemActions.OpenSettings),
        new("数据目录", "\uE838", () => SystemActions.OpenPath(App.DataRoot)),
        new("日志目录", "\uE7C3", () => SystemActions.OpenPath(App.LogDir)),
        new("关机", "\uE7E8", SystemActions.Shutdown),
        new("重启", "\uE777", SystemActions.Restart),
    };

    public ToolsPage()
    {
        InitializeComponent();
        BuildButtons();
    }

    private void BuildButtons()
    {
        for (var i = 0; i < Actions.Length; i++)
        {
            var action = Actions[i];

            var content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
            };
            content.Children.Add(new FontIcon { FontSize = 15, Glyph = action.Glyph });
            content.Children.Add(new TextBlock
            {
                Text = action.Label,
                VerticalAlignment = VerticalAlignment.Center,
            });

            var button = new Button
            {
                Content = content,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
            };
            button.Click += (_, _) => Run(action);

            if (i / 2 >= ActionGrid.RowDefinitions.Count)
            {
                ActionGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }

            Grid.SetRow(button, i / 2);
            Grid.SetColumn(button, i % 2);
            ActionGrid.Children.Add(button);
        }
    }

    private static void Run(ToolAction action)
    {
        try
        {
            action.Invoke();
        }
        catch (Exception ex)
        {
            App.Log($"执行 {action.Label} 失败：{ex.Message}");
        }
    }

    private void Run_Click(object sender, RoutedEventArgs e) => RunCommand();

    private void CommandBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            RunCommand();
            e.Handled = true;
        }
    }

    private void RunCommand()
    {
        var command = CommandBox.Text?.Trim() ?? "";
        if (command.Length == 0)
        {
            return;
        }

        SystemActions.RunCommand(command);
        CommandBox.Text = "";
    }
}