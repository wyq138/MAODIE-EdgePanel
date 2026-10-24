using EdgePanel.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EdgePanel.Pages;

public sealed partial class AiPage : Page
{
    private AiAgentSnapshot _snapshot = new();

    public AiPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    private async void Refresh()
    {
        try
        {
            UsagePanel.Children.Clear();
            WorkspacePanel.Children.Clear();
        QuotaText.Text = "正在扫描本机 AI 工具活动…";

        _snapshot = await Task.Run(AiAgentService.Scan);

        foreach (var usage in _snapshot.Usages.OrderByDescending(u => u.ProcessCount).ThenByDescending(u => u.SessionsTotal))
        {
            var running = usage.ProcessCount > 0;
            var state = running ? "运行中" : (usage.Found ? "已安装" : "未检测到");

            var card = new Border
            {
                Style = (Style)Application.Current.Resources["Card"],
                Padding = new Thickness(10, 8, 10, 8),
            };

            var grid = new Grid { ColumnSpacing = 8 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel { Spacing = 1 };
            left.Children.Add(new TextBlock
            {
                Text = $"{usage.Name} · {state}",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                FontSize = 13,
            });

            var detail = usage.ProcessCount > 0
                ? $"进程 {usage.ProcessCount} · 内存 {usage.MemoryMb:0} MB"
                : "未检测到运行进程";

            if (usage.SessionsTotal > 0)
            {
                detail += $" · 记录 今日 {usage.SessionsToday} / 累计 {usage.SessionsTotal}";
            }

            if (usage.TokensTotal > 0)
            {
                detail += $" · Token 今日 {FormatTokens(usage.TokensToday)} / 累计 {FormatTokens(usage.TokensTotal)}";
            }

            left.Children.Add(new TextBlock
            {
                Text = detail,
                FontSize = 11,
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap,
            });

            Grid.SetColumn(left, 0);

            var dot = new FontIcon
            {
                Glyph = running ? "\uE768" : "\uE894",
                FontSize = 14,
                Opacity = running ? 1 : 0.35,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(dot, 1);

            grid.Children.Add(left);
            grid.Children.Add(dot);
            card.Child = grid;
            UsagePanel.Children.Add(card);
        }

        if (_snapshot.Workspaces.Count == 0)
        {
            WorkspacePanel.Children.Add(new TextBlock
            {
                Text = "未找到 ~/.claude/projects 或 ~/.codex/sessions 目录",
                FontSize = 12,
                Opacity = 0.65,
                TextWrapping = TextWrapping.Wrap,
            });
        }
        else
        {
            foreach (var workspace in _snapshot.Workspaces
                         .OrderByDescending(w => w.LastActive)
                         .Take(40))
            {
                var button = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(10, 6, 10, 6),
                };

                var content = new StackPanel { Spacing = 2 };
                content.Children.Add(new TextBlock
                {
                    Text = $"{workspace.Agent} · {workspace.Name}",
                    FontSize = 12,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
                content.Children.Add(new TextBlock
                {
                    Text = $"{workspace.SessionCount} 个会话 · 最近 {Format(workspace.LastActive)}",
                    FontSize = 11,
                    Opacity = 0.7,
                });
                button.Content = content;

                var path = workspace.Path;
                button.Click += (_, _) => SystemActions.OpenPath(path);

                WorkspacePanel.Children.Add(button);
            }
        }

        var runningCount = _snapshot.Usages.Count(u => u.ProcessCount > 0);
        var todaySessions = _snapshot.Usages.Sum(u => u.SessionsToday);
        var todayTokens = _snapshot.Usages.Sum(u => u.TokensToday);
        var totalTokens = _snapshot.Usages.Sum(u => u.TokensTotal);

        RunningCount.Text = runningCount.ToString();
        TodaySessions.Text = todaySessions.ToString();
        TodayTokens.Text = FormatTokens(todayTokens);
        TotalTokens.Text = FormatTokens(totalTokens);

        var foundCount = _snapshot.Usages.Count(u => u.Found);
            QuotaText.Text =
                $"共识别 {_snapshot.Usages.Count} 种 AI 工具，其中 {foundCount} 种在本机有痕迹。\n" +
                $"今日合计 {todaySessions} 条记录、{FormatTokens(todayTokens)} Token；累计 {FormatTokens(totalTokens)} Token。\n" +
                "官方限额需登录各工具账号查看，本地统计为尽力解析。";
        }
        catch (Exception ex)
        {
            App.Log("AI 页刷新失败：" + ex.Message);
            QuotaText.Text = "扫描失败：" + ex.Message;
        }
    }

    private static string FormatTokens(long tokens)
    {
        if (tokens >= 1_000_000)
        {
            return $"{tokens / 1_000_000.0:0.0}M";
        }

        if (tokens >= 1_000)
        {
            return $"{tokens / 1_000.0:0.0}K";
        }

        return tokens.ToString();
    }

    private static string Format(DateTime time)
    {
        var span = DateTime.Now - time;

        if (span.TotalMinutes < 1)
        {
            return "刚刚";
        }

        if (span.TotalHours < 1)
        {
            return $"{(int)span.TotalMinutes} 分钟前";
        }

        if (span.TotalDays < 1)
        {
            return $"{(int)span.TotalHours} 小时前";
        }

        return time.ToString("MM-dd HH:mm");
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();
}
