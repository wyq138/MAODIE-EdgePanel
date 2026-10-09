using EdgePanel.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EdgePanel.Pages;

public sealed partial class SystemPage : Page
{
    private bool _hooked;

    public SystemPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_hooked)
        {
            _hooked = true;
            SystemMonitorService.Instance.Updated += OnUpdated;
        }

        OnUpdated(SystemMonitorService.Instance.Current);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        SystemMonitorService.Instance.Updated -= OnUpdated;
        _hooked = false;
    }

    private void OnUpdated(SystemSnapshot snap)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            CpuText.Text = $"{snap.CpuPercent:0}%";
            CpuBar.Value = snap.CpuPercent;
            MemText.Text = $"{snap.MemoryUsedGb:0.0}G";
            MemBar.Value = snap.MemoryPercent;
            NetText.Text = $"{snap.NetDownKbps:0}";

            InfoText.Text =
                $"内存 {snap.MemoryUsedGb:0.0} / {snap.MemoryTotalGb:0.0} GB（{snap.MemoryPercent:0}%）" +
                $"   ·   上传 {snap.NetUpKbps:0} KB/s" +
                $"   ·   开机 {snap.UptimeMinutes / 60} 小时 {snap.UptimeMinutes % 60} 分\n" +
                $"CPU：{snap.CpuName}";

            RebuildDisks(snap);
            RebuildProcesses(snap);
        });
    }

    private void RebuildDisks(SystemSnapshot snap)
    {
        DiskPanel.Children.Clear();

        foreach (var disk in snap.Disks)
        {
            var title = new TextBlock
            {
                Text = $"{disk.Name}  {disk.Label}",
                FontSize = 13,
            };

            var detail = new TextBlock
            {
                Text = $"剩余 {disk.FreeGb:0.0} GB / 共 {disk.TotalGb:0.0} GB",
                FontSize = 11,
                Opacity = 0.65,
            };

            var bar = new ProgressBar
            {
                Value = disk.UsedPercent,
                Maximum = 100,
                Margin = new Thickness(0, 4, 0, 0),
            };

            var stack = new StackPanel();
            stack.Children.Add(title);
            stack.Children.Add(detail);
            stack.Children.Add(bar);

            DiskPanel.Children.Add(new Border
            {
                Style = (Style)Application.Current.Resources["Card"],
                Child = stack,
            });
        }
    }

    private void RebuildProcesses(SystemSnapshot snap)
    {
        ProcessPanel.Children.Clear();

        foreach (var process in snap.TopProcesses)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var name = new TextBlock
            {
                Text = process.Name,
                FontSize = 13,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            var memory = new TextBlock
            {
                Text = $"{process.MemoryMb:0.0} MB",
                FontSize = 12,
                Opacity = 0.75,
            };

            Grid.SetColumn(memory, 1);
            grid.Children.Add(name);
            grid.Children.Add(memory);

            ProcessPanel.Children.Add(new Border
            {
                Style = (Style)Application.Current.Resources["Card"],
                Padding = new Thickness(10, 7, 10, 7),
                Child = grid,
            });
        }
    }
}