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
            var json = await http.GetStringAsync(url);

            using var document = JsonDocument.Parse(json);
            var latest = document.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;
            var link = document.RootElement.TryGetProperty("url", out var u) ? u.GetString() : null;

            if (string.IsNullOrWhiteSpace(latest))
            {
                return "更新信息格式不正确：缺少 version 字段。";
            }

            if (string.Equals(latest, CurrentVersion, StringComparison.OrdinalIgnoreCase))
            {
                return $"已是最新版本 {CurrentVersion}。";
            }

            if (!string.IsNullOrWhiteSpace(link))
            {
                if (link.TrimStart().StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    SystemActions.OpenPath(link);
                    return $"发现新版本 {latest}（当前 {CurrentVersion}），已打开下载页面。";
                }

                return $"发现新版本 {latest}（当前 {CurrentVersion}），但下载地址不是 https 链接，已忽略。";
            }

            return $"发现新版本 {latest}（当前 {CurrentVersion}），但信息里没有下载地址。";
        }
        catch (Exception ex)
        {
            App.Log("检查更新失败：" + ex.Message);
            return $"检查更新失败：{ex.Message}";
        }
    }
}