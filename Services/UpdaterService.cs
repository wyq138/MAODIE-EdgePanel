using System.Reflection;
using System.Text.Json;

namespace EdgePanel.Services;

/// <summary>极简更新检查：显示本地版本，可选从配置地址拉取最新版本信息。</summary>
public static class UpdaterService
{
    public static string CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    /// <summary>
    /// 期望的版本信息 JSON：{ "version": "1.2.0", "url": "https://下载地址" }
    /// 未配置地址时仅回报本地版本。
    /// </summary>
    public static async Task<string> CheckAsync(string url)
    {
        // 商店分发的包禁止自更新（Store 政策 10.8.2），检查更新功能只对商店外渠道开放
        try
        {
            if (Windows.ApplicationModel.Package.Current?.SignatureKind
                == Windows.ApplicationModel.PackageSignatureKind.Store)
            {
                return $"当前版本 {CurrentVersion}。商店版更新由 Microsoft Store 接管。";
            }
        }
        catch
        {
            // 无包身份（免安装）时继续走自托管更新检查
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            return $"当前版本 {CurrentVersion}。未配置更新地址：可在 settings.json 的 UpdateCheckUrl 填入版本信息 JSON 地址。";
        }

        if (!url.TrimStart().StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return "更新地址必须是 https:// 链接。";
        }

        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MAODIE-EdgePanel-Updater"); // api.github.com 强制要求 UA
            var json = await http.GetStringAsync(url);

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            // GitHub releases API 格式：{ "tag_name": "v1.0.0", "assets": [ { "browser_download_url": … } ] }
            // 自定义格式：{ "version": "1.2.0", "url": "https://下载地址" }
            string? latest;
            string? link = null;
            if (root.TryGetProperty("tag_name", out var tag))
            {
                latest = tag.GetString();
                if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                {
                    foreach (var asset in assets.EnumerateArray())
                    {
                        if (asset.TryGetProperty("name", out var name) &&
                            name.GetString()?.EndsWith(".msix", StringComparison.OrdinalIgnoreCase) == true &&
                            asset.TryGetProperty("browser_download_url", out var dl))
                        {
                            link = dl.GetString();
                            break;
                        }
                    }
                }
            }
            else
            {
                latest = root.TryGetProperty("version", out var v) ? v.GetString() : null;
                link = root.TryGetProperty("url", out var u) ? u.GetString() : null;
            }

            if (string.IsNullOrWhiteSpace(latest))
            {
                return "更新信息格式不正确：缺少 version 字段。";
            }

            // GitHub tag 通常是 "v1.0.0"，程序集版本是 "1.0.0"，比较时统一去掉 v 前缀
            latest = latest.TrimStart('v', 'V');

            // 读取发布时间（GitHub API 的 published_at；自定义格式可选 provided）
            DateTime? publishedUtc = null;
            if (root.TryGetProperty("published_at", out var pa) &&
                pa.ValueKind == JsonValueKind.String &&
                DateTime.TryParse(pa.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
            {
                publishedUtc = parsed.ToUniversalTime();
            }

            var needUpdate = false;
            if (Version.TryParse(latest, out var latestVer) &&
                Version.TryParse(CurrentVersion, out var currentVer))
            {
                needUpdate = latestVer > currentVer;
            }
            else
            {
                needUpdate = !string.Equals(latest, CurrentVersion, StringComparison.OrdinalIgnoreCase);
            }

            // 版本号没变但发布时间比本机构建时间新 —— 同版本重新交付，也要提示更新
            if (!needUpdate && publishedUtc.HasValue)
            {
                var buildUtc = GetBuildTimeUtc();
                needUpdate = publishedUtc.Value > buildUtc;
            }

            if (!needUpdate)
            {
                return $"已是最新版本 {CurrentVersion}。";
            }

            var publishedText = publishedUtc.HasValue
                ? $"，发布于 {publishedUtc.Value.ToLocalTime():yyyy-MM-dd HH:mm}"
                : "";
            if (!string.IsNullOrWhiteSpace(link))
            {
                if (link.TrimStart().StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    SystemActions.OpenPath(link);
                    return $"发现新版本 {latest}{publishedText}（当前 {CurrentVersion}），已打开下载页面。";
                }

                return $"发现新版本 {latest}{publishedText}（当前 {CurrentVersion}），但下载地址不是 https 链接，已忽略。";
            }

            return $"发现新版本 {latest}{publishedText}（当前 {CurrentVersion}），但信息里没有下载地址。";
        }
        catch (Exception ex)
        {
            App.Log("检查更新失败：" + ex.Message);
            return $"检查更新失败：{ex.Message}";
        }
    }

    /// <summary>本机构建时间（exe 文件的写入时间，UTC）。同版本重交付时靠它与 release 发布时间比较。</summary>
    private static DateTime GetBuildTimeUtc()
    {
        try
        {
            var location = Assembly.GetExecutingAssembly().Location;
            if (!string.IsNullOrEmpty(location))
            {
                return File.GetLastWriteTimeUtc(location);
            }
        }
        catch
        {
            // 取不到就用极小值，退化为只按版本号比较
        }

        return DateTime.MinValue;
    }
}