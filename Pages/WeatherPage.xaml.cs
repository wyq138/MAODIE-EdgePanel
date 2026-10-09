using EdgePanel.Core;
using EdgePanel.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace EdgePanel.Pages;

public sealed partial class WeatherPage : Page
{
    private bool _loading = true;
    private bool _hooked;
    private CoordinatePickServer? _pickServer;

    public WeatherPage()
    {
        InitializeComponent();

        var s = SettingsStore.Current;
        PlaceBox.Text = s.WeatherPlace;
        LatBox.Value = s.WeatherLat;
        LonBox.Value = s.WeatherLon;
        _loading = false;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_hooked)
        {
            _hooked = true;
            WeatherService.Instance.Updated += OnWeather;
        }

        if (WeatherService.Instance.Current is { } current)
        {
            OnWeather(current);
        }
        else
        {
            _ = RefreshAsync();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        WeatherService.Instance.Updated -= OnWeather;
        _hooked = false;

        _pickServer?.Dispose();
        _pickServer = null;
    }

    private async Task RefreshAsync()
    {
        var s = SettingsStore.Current;
        await WeatherService.Instance.RefreshAsync(s.WeatherLat, s.WeatherLon, s.WeatherPlace);
    }

    private void OnWeather(WeatherNow? weather)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (weather is null)
            {
                NowDesc.Text = "获取失败，请检查网络后重试";
                return;
            }

            NowGlyph.Text = weather.Emoji;
            NowTemp.Text = $"{weather.Temp:0}°";
            NowDesc.Text = $"{weather.Place} · {weather.Describe}";
            NowDetail.Text =
                $"体感 {weather.FeelsLike:0}°   湿度 {weather.Humidity:0}%   风速 {weather.Wind:0} km/h\n" +
                $"更新于 {weather.UpdatedAt:HH:mm:ss}";

            ForecastPanel.Children.Clear();

            foreach (var day in weather.Days)
            {
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var weekday = new TextBlock
                {
                    Text = day.Weekday,
                    FontSize = 13,
                    VerticalAlignment = VerticalAlignment.Center,
                };

                var glyph = new TextBlock
                {
                    Text = WeatherService.Emoji(day.Code),
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe UI Emoji"),
                    FontSize = 15,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(glyph, 1);

                var desc = new TextBlock
                {
                    Text = WeatherService.Describe(day.Code),
                    FontSize = 12,
                    Opacity = 0.7,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(desc, 2);

                var temp = new TextBlock
                {
                    Text = $"{day.Min:0}° / {day.Max:0}°",
                    FontSize = 13,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(temp, 3);

                grid.Children.Add(weekday);
                grid.Children.Add(glyph);
                grid.Children.Add(desc);
                grid.Children.Add(temp);

                ForecastPanel.Children.Add(new Border
                {
                    Style = (Style)Application.Current.Resources["Card"],
                    Padding = new Thickness(10, 7, 10, 7),
                    Child = grid,
                });
            }
        });
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void SaveLocation_Click(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var s = SettingsStore.Current;
        s.WeatherPlace = string.IsNullOrWhiteSpace(PlaceBox.Text) ? "未知地点" : PlaceBox.Text.Trim();
        s.WeatherLat = double.IsNaN(LatBox.Value) ? s.WeatherLat : LatBox.Value;
        s.WeatherLon = double.IsNaN(LonBox.Value) ? s.WeatherLon : LonBox.Value;
        SettingsStore.Save();

        await RefreshAsync();
    }

    // ————————————————————————— 手机扫码选点 —————————————————————————

    private void ScanPick_Click(object sender, RoutedEventArgs e)
    {
        _pickServer?.Dispose();
        _pickServer = new CoordinatePickServer();
        _pickServer.Picked += OnCoordinatePicked;
        _pickServer.Start();

        QrImage.Source = new BitmapImage(new Uri(
            "https://api.qrserver.com/v1/create-qr-code/?size=220x220&margin=0&data=" +
            Uri.EscapeDataString(_pickServer.Url)));

        QrUrlText.Text = _pickServer.Url;
        QrStatus.Text = "手机连接同一 WiFi 后扫码，在地图上点选位置，点「确认并回传坐标」。";
        QrPanel.Visibility = Visibility.Visible;
    }

    private void StopScanPick_Click(object sender, RoutedEventArgs e)
    {
        QrPanel.Visibility = Visibility.Collapsed;
        _pickServer?.Dispose();
        _pickServer = null;
    }

    private void OnCoordinatePicked(double lat, double lon)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            QrStatus.Text = $"已收到坐标 {lat:F6}, {lon:F6}，正在获取天气…";

            var s = SettingsStore.Current;
            s.WeatherLat = lat;
            s.WeatherLon = lon;
            LatBox.Value = lat;
            LonBox.Value = lon;

            // 反查中文地名，失败则保留坐标字符串作为名称
            var name = await WeatherService.Instance.ResolvePlaceAsync(lat, lon);
            s.WeatherPlace = string.IsNullOrWhiteSpace(name) ? $"{lat:F4}, {lon:F4}" : name;
            PlaceBox.Text = s.WeatherPlace;
            SettingsStore.Save();

            QrPanel.Visibility = Visibility.Collapsed;
            _pickServer?.Dispose();
            _pickServer = null;

            await RefreshAsync();
        });
    }
}