using System.Net.Http;
using System.Text.Json;

namespace EdgePanel.Services;

public sealed class DailyForecast
{
    public DateTime Date { get; init; }
    public string Weekday { get; init; } = "";
    public int Code { get; init; }
    public double Min { get; init; }
    public double Max { get; init; }
}

public sealed class WeatherNow
{
    public string Place { get; set; } = "";
    public double Temp { get; set; }
    public double FeelsLike { get; set; }
    public double Humidity { get; set; }
    public double Wind { get; set; }
    public int Code { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    public List<DailyForecast> Days { get; set; } = new();

    public string Describe => WeatherService.Describe(Code);
    public string Emoji => WeatherService.Emoji(Code);
}

/// <summary>天气服务：Open-Meteo 免密钥接口，支持当前天气 + 7 天预报。</summary>
public sealed class WeatherService
{
    public static WeatherService Instance { get; } = new();

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };

    public WeatherNow? Current { get; private set; }
    public event Action<WeatherNow?>? Updated;

    private WeatherService()
    {
    }

    public async Task RefreshAsync(double lat, double lon, string place)
    {
        try
        {
            var url =
                "https://api.open-meteo.com/v1/forecast" +
                $"?latitude={lat.ToString(System.Globalization.CultureInfo.InvariantCulture)}" +
                $"&longitude={lon.ToString(System.Globalization.CultureInfo.InvariantCulture)}" +
                "&current=temperature_2m,relative_humidity_2m,apparent_temperature,weather_code,wind_speed_10m" +
                "&daily=weather_code,temperature_2m_max,temperature_2m_min" +
                "&timezone=auto&forecast_days=7";

            var json = await Http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var current = root.GetProperty("current");
            var result = new WeatherNow
            {
                Place = place,
                Temp = current.GetProperty("temperature_2m").GetDouble(),
                Humidity = current.GetProperty("relative_humidity_2m").GetDouble(),
                FeelsLike = current.GetProperty("apparent_temperature").GetDouble(),
                Wind = current.GetProperty("wind_speed_10m").GetDouble(),
                Code = current.GetProperty("weather_code").GetInt32(),
                UpdatedAt = DateTime.Now,
            };

            if (root.TryGetProperty("daily", out var daily))
            {
                var times = daily.GetProperty("time");
                var codes = daily.GetProperty("weather_code");
                var maxs = daily.GetProperty("temperature_2m_max");
                var mins = daily.GetProperty("temperature_2m_min");

                for (var i = 0; i < times.GetArrayLength(); i++)
                {
                    var date = DateTime.Parse(times[i].GetString() ?? "");
                    result.Days.Add(new DailyForecast
                    {
                        Date = date,
                        Weekday = i == 0 ? "今天" : i == 1 ? "明天" : date.ToString("ddd"),
                        Code = codes[i].GetInt32(),
                        Max = maxs[i].GetDouble(),
                        Min = mins[i].GetDouble(),
                    });
                }
            }

            Current = result;
            Updated?.Invoke(result);
            App.Log($"天气已更新：{place} {result.Temp}℃ {result.Describe}");
        }
        catch (Exception ex)
        {
            App.Log("获取天气失败：" + ex.Message);
            Updated?.Invoke(null);
        }
    }

    /// <summary>用 OSM Nominatim 反查坐标对应的中文地名（手机扫码选点后自动填名称）。</summary>
    public async Task<string> ResolvePlaceAsync(double lat, double lon)
    {
        try
        {
            var url = "https://nominatim.openstreetmap.org/reverse?format=jsonv2" +
                      $"&lat={lat.ToString(System.Globalization.CultureInfo.InvariantCulture)}" +
                      $"&lon={lon.ToString(System.Globalization.CultureInfo.InvariantCulture)}" +
                      "&accept-language=zh";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", "EdgePanel/1.0 (weather location picker)");

            using var response = await Http.SendAsync(request);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("display_name", out var name))
            {
                return name.GetString() ?? "";
            }
        }
        catch (Exception ex)
        {
            App.Log("反查地点失败：" + ex.Message);
        }

        return "";
    }

    public static string Describe(int code) => code switch
    {
        0 => "晴",
        1 => "晴间多云",
        2 => "多云",
        3 => "阴",
        45 or 48 => "雾",
        51 or 53 or 55 => "毛毛雨",
        56 or 57 => "冻雨",
        61 => "小雨",
        63 => "中雨",
        65 => "大雨",
        66 or 67 => "冻雨",
        71 => "小雪",
        73 => "中雪",
        75 => "大雪",
        77 => "米雪",
        80 => "阵雨",
        81 => "强阵雨",
        82 => "暴雨",
        85 or 86 => "阵雪",
        95 => "雷阵雨",
        96 or 99 => "雷暴伴冰雹",
        _ => "未知",
    };

    /// <summary>
    /// 天气对应的 emoji，用 Segoe UI Emoji 呈现（彩色）。
    /// 之前用的是 Segoe Fluent Icons / MDL2 的 E9C4 等字形，但雨、雪、雷这三个字形
    /// 在两套图标字体里都不存在，一到下雨下雪图标位置就是一片空白，所以改用 emoji。
    /// 带 \uFE0F 的强制走彩色字形（否则 ☀ 这类字符会渲染成单色）。
    /// </summary>
    public static string Emoji(int code) => code switch
    {
        0 => "\u2600\uFE0F",                  // ☀️ 晴
        1 => "\uD83C\uDF24\uFE0F",            // 🌤️ 晴间多云
        2 => "\u26C5",                        // ⛅ 多云
        3 => "\u2601\uFE0F",                  // ☁️ 阴
        45 or 48 => "\uD83C\uDF2B\uFE0F",     // 🌫️ 雾
        51 or 53 or 55 => "\uD83C\uDF26\uFE0F", // 🌦️ 毛毛雨
        56 or 57 or 61 or 63 or 65 or 66 or 67 or 80 or 81 or 82 => "\uD83C\uDF27\uFE0F", // 🌧️ 雨
        71 or 73 or 75 or 77 or 85 or 86 => "\uD83C\uDF28\uFE0F", // 🌨️ 雪
        95 or 96 or 99 => "\u26C8\uFE0F",     // ⛈️ 雷
        _ => "\u2601\uFE0F",
    };
}