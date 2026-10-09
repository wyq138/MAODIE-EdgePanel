using EdgePanel.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EdgePanel.Pages;

public sealed partial class TimerPage : Page
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(50) };

    private TimeSpan _elapsed;
    private DateTime _stopwatchTick;
    private bool _stopwatchRunning;

    private TimeSpan _countdownRemaining;
    private DateTime _countdownTick;
    private bool _countdownRunning;
    private int _lapIndex;

    public TimerPage()
    {
        InitializeComponent();
        _countdownRemaining = TimeSpan.FromMinutes(5);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _timer.Tick += OnTick;
        _timer.Start();
        RenderStopwatch();
        RenderCountdown();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        _timer.Tick -= OnTick;
    }

    private void OnTick(object? sender, object? e)
    {
        // 秒表与倒计时各自使用独立的时间基准，避免同时运行时互相抢时钟
        if (_stopwatchRunning)
        {
            var now = DateTime.Now;
            _elapsed += now - _stopwatchTick;
            _stopwatchTick = now;
            RenderStopwatch();
        }

        if (_countdownRunning)
        {
            var now = DateTime.Now;
            _countdownRemaining -= now - _countdownTick;
            _countdownTick = now;

            if (_countdownRemaining <= TimeSpan.Zero)
            {
                _countdownRemaining = TimeSpan.Zero;
                _countdownRunning = false;
                CdStartButton.Content = "开始";
                SystemActions.Beep();
                App.Log("倒计时结束");
            }

            RenderCountdown();
        }
    }

    // ————————————————————————— 秒表 —————————————————————————

    private void RenderStopwatch()
        => StopwatchText.Text = $"{(int)_elapsed.TotalMinutes:00}:{_elapsed.Seconds:00}.{_elapsed.Milliseconds / 10:00}";

    private void SwToggle_Click(object sender, RoutedEventArgs e)
    {
        _stopwatchRunning = !_stopwatchRunning;
        SwStartButton.Content = _stopwatchRunning ? "暂停" : "继续";

        if (_stopwatchRunning)
        {
            _stopwatchTick = DateTime.Now;
        }
    }

    private void SwReset_Click(object sender, RoutedEventArgs e)
    {
        _stopwatchRunning = false;
        _elapsed = TimeSpan.Zero;
        _lapIndex = 0;
        SwStartButton.Content = "开始";
        LapPanel.Children.Clear();
        RenderStopwatch();
    }

    private void Lap_Click(object sender, RoutedEventArgs e)
    {
        _lapIndex++;
        var row = new TextBlock
        {
            Text = $"第 {_lapIndex} 次  ·  {StopwatchText.Text}",
            FontSize = 12,
            Opacity = 0.8,
        };
        LapPanel.Children.Insert(0, row);
    }

    // ————————————————————————— 倒计时 —————————————————————————

    private void RenderCountdown()
        => CountdownText.Text = $"{(int)_countdownRemaining.TotalMinutes:00}:{_countdownRemaining.Seconds:00}";

    private void CdToggle_Click(object sender, RoutedEventArgs e)
    {
        // 运行中 → 暂停
        if (_countdownRunning)
        {
            _countdownRunning = false;
            CdStartButton.Content = "继续";
            RenderCountdown();
            return;
        }

        // 已归零（或首次点击）→ 按输入分钟数重新装载
        if (_countdownRemaining <= TimeSpan.Zero)
        {
            _countdownRemaining = TimeSpan.FromMinutes(Math.Max(1, MinutesBox.Value));
        }

        _countdownRunning = true;
        _countdownTick = DateTime.Now;
        CdStartButton.Content = "暂停";
        RenderCountdown();
    }

    private void CdReset_Click(object sender, RoutedEventArgs e)
    {
        _countdownRunning = false;
        _countdownRemaining = TimeSpan.FromMinutes(Math.Max(1, MinutesBox.Value));
        CdStartButton.Content = "开始";
        RenderCountdown();
    }
}