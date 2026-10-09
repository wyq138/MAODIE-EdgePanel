using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EdgePanel.Core;

namespace EdgePanel.Services;

public sealed class LyricLine
{
    public TimeSpan Time { get; init; }
    public string Text { get; init; } = "";
}

/// <summary>
/// 歌词服务：从本地目录检索 .lrc 文件、解析时间轴，并按播放进度定位当前行。
/// </summary>
public sealed partial class LyricsService
{
    public static LyricsService Instance { get; } = new();

    private static readonly HttpClient Http = CreateHttp();

    private readonly List<LyricLine> _lines = new();

    public IReadOnlyList<LyricLine> Lines => _lines;
    public string SourcePath { get; private set; } = "";
    public string MatchedTitle { get; private set; } = "";
    public bool HasLyrics => _lines.Count > 0;

    /// <summary>歌词内容变化（加载成功/失败）时触发。</summary>
    public event Action? Changed;

    private LyricsService()
    {
    }

    private static HttpClient CreateHttp()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        // LRCLIB 要求请求方声明 User-Agent
        client.DefaultRequestHeaders.UserAgent.ParseAdd("EdgePanel/1.0 (side-panel lyrics)");
        return client;
    }

    [GeneratedRegex(@"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]", RegexOptions.Compiled)]
    private static partial Regex TimeTagRegex();

    /// <summary>匹配标题里的括号后缀，如 (Live)、(Remix)、【…】、[feat. …]。</summary>
    [GeneratedRegex(@"[(（\[【][^)）\]】]*[)）\]】]")]
    private static partial Regex BracketSuffixRegex();

    /// <summary>为指定曲目加载歌词；title/artist 来自 SMTC。返回是否找到。</summary>
    public bool LoadFor(string title, string artist)
    {
        _lines.Clear();
        SourcePath = "";
        MatchedTitle = title;

        var dirs = GetSearchDirs();
        if (dirs.Count == 0 || string.IsNullOrWhiteSpace(title))
        {
            Changed?.Invoke();
            return false;
        }

        var candidates = new List<string>();
        foreach (var dir in dirs)
        {
            try
            {
                if (!Directory.Exists(dir))
                {
                    continue;
                }

                candidates.AddRange(Directory.EnumerateFiles(dir, "*.lrc", SearchOption.AllDirectories));
            }
            catch
            {
                // 忽略无权限目录
            }
        }

        // 先精确匹配 "歌手 - 歌名"，再退化到只匹配歌名
        var best = FindBest(candidates, artist, title) ?? FindBest(candidates, null, title);

        if (best is null)
        {
            App.Log($"未找到歌词：{artist} - {title}");
            Changed?.Invoke();
            return false;
        }

        try
        {
            var text = ReadLrcText(best);
            _lines.AddRange(ParseLrc(text));
            SourcePath = best;
            App.Log($"已加载歌词：{best}（{_lines.Count} 行）");
            Changed?.Invoke();
            return _lines.Count > 0;
        }
        catch (Exception ex)
        {
            App.Log("读取歌词失败：" + ex.Message);
            Changed?.Invoke();
            return false;
        }
    }

    /// <summary>
    /// 加载歌词：优先本地 .lrc；找不到时按 网易云 → LRCLIB → 汽水归一 顺序在线抓取同步歌词。
    /// </summary>
    public async Task<bool> LoadForAsync(string title, string artist)
    {
        if (LoadFor(title, artist))
        {
            return true;
        }

        // 空标题 / "未知曲目" 这类占位名拿去搜只会搜到牛头不对马嘴的歌，直接放弃
        if (!SettingsStore.Current.OnlineLyrics || IsPlaceholder(title))
        {
            return false;
        }

        // 中文歌网易云覆盖最好；日/韩/英等非中文曲目 LRCLIB 更准，先走它
        if (IsCjkPrimary(title, artist))
        {
            if (await LoadFromNeteaseAsync(title, artist))
            {
                return true;
            }

            if (await LoadFromLrclibAsync(title, artist))
            {
                return true;
            }
        }
        else
        {
            if (await LoadFromLrclibAsync(title, artist))
            {
                return true;
            }

            if (await LoadFromNeteaseAsync(title, artist))
            {
                return true;
            }
        }

        // 前两者都失败时，借汽水音乐公网搜索把曲名/歌手归一化后再试一次网易云
        return await LoadViaSodaThenNeteaseAsync(title, artist);
    }

    /// <summary>判断是否"中文优先"：歌名/歌手含汉字且不含假名/谚文时算中文。</summary>
    private static bool IsCjkPrimary(string title, string artist)
    {
        var text = title + artist;
        var hasCjk = false;

        foreach (var ch in text)
        {
            if (ch is >= '\u3040' and <= '\u30FF' || ch is >= '\uAC00' and <= '\uD7AF')
            {
                return false; // 日/韩
            }

            if (ch is >= '\u4E00' and <= '\u9FFF')
            {
                hasCjk = true;
            }
        }

        return hasCjk;
    }

    /// <summary>
    /// 把解析出的歌词落地并通知界面；解析出的行数少于 minLines 视为无效。
    /// （网易云会把"伴奏版"标成同名的另一首，解析出来只有作曲/编曲两三行，要按行数挡掉）
    /// </summary>
    private bool ApplyLines(string lrcText, string title, string source, int minLines = 1)
    {
        var parsed = ParseLrc(lrcText);
        if (parsed.Count < minLines)
        {
            return false;
        }

        _lines.Clear();
        _lines.AddRange(parsed);
        SourcePath = source;
        MatchedTitle = title;
        App.Log($"已加载歌词：{title}（{source}，{_lines.Count} 行）");
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// 网易云音乐：搜索匹配曲目后取官方同步歌词（lrc.lyric）。
    /// 用的是公开的 web 接口，免密钥、免登录，属于"野路子"。
    /// </summary>
    private async Task<bool> LoadFromNeteaseAsync(string title, string artist)
    {
        try
        {
            var key = CleanTitle(title);
            const string referer = "https://music.163.com/";
            var duration = MediaService.Instance.Current.Duration;
            var wanted = Normalize(title);

            // 先按清洗后的主标题搜，搜不到再拿原始标题（含 (Live)/(Remix) 后缀）搜一次
            var candidates = await SearchAsync(string.IsNullOrWhiteSpace(artist) ? key : $"{key} {artist}");

            if (candidates.Count == 0 && !string.Equals(key, title, StringComparison.Ordinal))
            {
                candidates = await SearchAsync(string.IsNullOrWhiteSpace(artist) ? title : $"{title} {artist}");
            }

            if (candidates.Count == 0)
            {
                return false;
            }

            // 从时长最接近的开始试：命中伴奏版时解析不出正文，会往后换一首
            foreach (var (id, by, _) in candidates.OrderBy(c => c.Score).Take(3))
            {
                var lyricJson = await GetAsync(
                    $"https://music.163.com/api/song/lyric?id={id}&lv=-1&kv=-1&tv=-1",
                    referer);

                using var lyricDoc = JsonDocument.Parse(lyricJson);
                if (!lyricDoc.RootElement.TryGetProperty("lrc", out var lrc) ||
                    !lrc.TryGetProperty("lyric", out var textNode) ||
                    textNode.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var sourceName = string.IsNullOrWhiteSpace(by) ? "网易云" : $"网易云 · {by}";
                if (ApplyLines(textNode.GetString() ?? "", title, sourceName, minLines: 5))
                {
                    return true;
                }
            }

            return false;

            async Task<List<(long Id, string Artist, double Score)>> SearchAsync(string q)
            {
                var found = new List<(long Id, string Artist, double Score)>();
                var url = "https://music.163.com/api/search/get/web?type=1&limit=10&offset=0&s=" +
                          Uri.EscapeDataString(q);
                var json = await GetAsync(url, referer);

                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("result", out var result) ||
                    !result.TryGetProperty("songs", out var songs) ||
                    songs.ValueKind != JsonValueKind.Array)
                {
                    return found;
                }

                foreach (var item in songs.EnumerateArray())
                {
                    if (!item.TryGetProperty("id", out var idNode) || idNode.ValueKind != JsonValueKind.Number)
                    {
                        continue;
                    }

                    var name = item.TryGetProperty("name", out var nameNode) ? nameNode.GetString() ?? "" : "";

                    // 歌名对不上的直接排除，避免搜到同歌手别的歌
                    var songTitle = Normalize(name);
                    if (songTitle.Length == 0 ||
                        (!songTitle.Contains(wanted) && !wanted.Contains(songTitle)))
                    {
                        continue;
                    }

                    // 伴奏 / 纯音乐没有正文歌词，换下一首
                    if (IsInstrumental(name))
                    {
                        continue;
                    }

                    var by = "";
                    if (item.TryGetProperty("artists", out var artists) && artists.ValueKind == JsonValueKind.Array)
                    {
                        by = string.Concat(artists.EnumerateArray()
                            .Select(a => a.TryGetProperty("name", out var n) ? n.GetString() : null));
                    }

                    var seconds = item.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number
                        ? d.GetDouble() / 1000.0
                        : 0;

                    // 播放器给了时长时按时长差择优，能有效避开翻唱/remix 错配
                    var score = duration > TimeSpan.Zero && seconds > 0
                        ? Math.Abs(seconds - duration.TotalSeconds)
                        : 0;

                    found.Add((idNode.GetInt64(), by, score));
                }

                return found;
            }
        }
        catch (Exception ex)
        {
            App.Log("网易云歌词获取失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>伴奏 / 纯音乐 / off vocal 这类版本没有正文歌词。</summary>
    private static bool IsInstrumental(string name)
    {
        var lower = name.ToLowerInvariant();
        return lower.Contains("伴奏") || lower.Contains("纯音乐") ||
               lower.Contains("instrumental") || lower.Contains("off vocal") ||
               lower.Contains("karaoke") || lower.Contains("演奏版");
    }

    /// <summary>SMTC 拿不到元数据时会给出"未知曲目"这类占位名，不能拿去搜。</summary>
    private static bool IsPlaceholder(string title)
    {
        var t = title.Trim();
        return t.Length == 0 || t == "未知曲目" || t == "未知" || t == "Unknown";
    }

    /// <summary>
    /// 借汽水音乐公网搜索接口（api.qishui.com/luna/search/track，免签名，已实测可用）拿回
    /// 权威的曲名/歌手，再交给网易云做最后一次匹配。
    /// 汽水自家的歌词详情接口带版本签名校验（直接访问返回"应用版本有风险"），拿不到歌词，
    /// 所以这里只用它做"别名归一"，歌词仍取网易云。
    /// </summary>
    private async Task<bool> LoadViaSodaThenNeteaseAsync(string title, string artist)
    {
        try
        {
            var query = string.IsNullOrWhiteSpace(artist) ? title : $"{title} {artist}";
            const string url = "https://api.qishui.com/luna/search/track?device_platform=android&os=android" +
                               "&aid=386088&app_name=luna&version_code=100198030&version_name=19.8.0" +
                               "&device_type=ABR-AL80&device_id=2204957404565290&iid=2204957404569386" +
                               "&cursor=0&count=10&q=";
            var json = await GetAsync(url + Uri.EscapeDataString(query), null, "LunaPC/3.4.0(388267242)");

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("result_groups", out var groups) ||
                groups.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var tried = 0;
            foreach (var group in groups.EnumerateArray())
            {
                if (!group.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var item in data.EnumerateArray())
                {
                    if (tried >= 3 ||
                        !item.TryGetProperty("entity", out var entity) ||
                        !entity.TryGetProperty("track", out var track))
                    {
                        continue;
                    }

                    var name = track.TryGetProperty("name", out var nameNode) ? nameNode.GetString() ?? "" : "";
                    if (string.IsNullOrWhiteSpace(name) || IsInstrumental(name))
                    {
                        continue;
                    }

                    var by = "";
                    if (track.TryGetProperty("artists", out var artists) && artists.ValueKind == JsonValueKind.Array)
                    {
                        by = string.Concat(artists.EnumerateArray()
                            .Select(a => a.TryGetProperty("name", out var n) ? n.GetString() : null));
                    }

                    tried++;
                    if (await LoadFromNeteaseAsync(name, by))
                    {
                        App.Log($"汽水归一后命中歌词：{name} - {by}");
                        return true;
                    }
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            App.Log("汽水音乐归一匹配失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>LRCLIB（https://lrclib.net，免密钥）搜索并解析 syncedLyrics。</summary>
    private async Task<bool> LoadFromLrclibAsync(string title, string artist)
    {
        try
        {
            // SMTC 给的标题常带 "(Live)"、"(Remix)"、"（…）" 等后缀，直接搜会一条都命中不了，
            // 所以先清洗出主标题；用主标题搜不到再退回原始标题、最后去掉歌手再试一次。
            var cleaned = CleanTitle(title);
            var duration = MediaService.Instance.Current.Duration;

            var attempts = new List<(string Track, string Artist)>();
            void Add(string track, string by)
            {
                if (string.IsNullOrWhiteSpace(track))
                {
                    return;
                }

                if (!attempts.Any(a => a.Track == track && a.Artist == by))
                {
                    attempts.Add((track, by));
                }
            }

            Add(cleaned, artist);
            Add(title, artist);
            Add(cleaned, "");
            Add(title, "");

            foreach (var (track, by) in attempts)
            {
                var synced = await SearchLrclibAsync(track, by, duration);
                if (synced is not null && ApplyLines(synced, title, "在线 · LRCLIB"))
                {
                    return true;
                }
            }

            App.Log($"在线歌词无结果：{artist} - {title}");
            Changed?.Invoke();
            return false;
        }
        catch (Exception ex)
        {
            App.Log("在线歌词获取失败：" + ex.Message);
            Changed?.Invoke();
            return false;
        }
    }

    /// <summary>带 Referer 的 GET：网易云接口要求来源页，缺失会返回空结果。</summary>
    private static async Task<string> GetAsync(string url, string? referer, string? userAgent = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (referer is not null)
        {
            request.Headers.Referrer = new Uri(referer);
        }

        if (userAgent is not null)
        {
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        }

        using var response = await Http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>去括号后缀、压缩空白的检索用标题。</summary>
    private static string CleanTitle(string title)
    {
        var cleaned = BracketSuffixRegex().Replace(title, " ");
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
        return cleaned.Length == 0 ? title.Trim() : cleaned;
    }

    /// <summary>
    /// 调 LRCLIB 搜索并在多个结果里挑最合适的同步歌词：
    /// 有播放时长时优先取时长最接近的，避免同名翻唱/不同版本错配。
    /// </summary>
    private static async Task<string?> SearchLrclibAsync(string track, string artist, TimeSpan duration)
    {
        var url = "https://lrclib.net/api/search?track_name=" + Uri.EscapeDataString(track);
        if (!string.IsNullOrWhiteSpace(artist))
        {
            url += "&artist_name=" + Uri.EscapeDataString(artist);
        }

        var json = await GetAsync(url, null);

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? best = null;
        var bestDiff = long.MaxValue;

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("syncedLyrics", out var node) ||
                node.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(node.GetString()))
            {
                continue;
            }

            var text = node.GetString()!;

            // 播放器没给时长就没法比对，直接取第一条带同步歌词的结果
            if (duration <= TimeSpan.Zero)
            {
                return text;
            }

            if (item.TryGetProperty("duration", out var dur) && dur.ValueKind == JsonValueKind.Number)
            {
                var diff = Math.Abs((long)(dur.GetDouble() - duration.TotalSeconds));
                if (diff < bestDiff)
                {
                    bestDiff = diff;
                    best = text;
                }
            }
            else
            {
                best ??= text;
            }
        }

        return best;
    }

    private static string? FindBest(List<string> candidates, string? artist, string title)
    {
        title = Normalize(title);
        artist = artist is null ? null : Normalize(artist);

        string? loose = null;
        foreach (var path in candidates)
        {
            var name = Normalize(Path.GetFileNameWithoutExtension(path));

            if (artist is not null && name.Contains(artist) && name.Contains(title))
            {
                return path;
            }

            if (name.Contains(title))
            {
                loose ??= path;
            }
        }

        return loose;
    }

    private static string Normalize(string value)
        => value.Trim().ToLowerInvariant().Replace(" ", "").Replace("　", "");

    private static string ReadLrcText(string path)
    {
        var bytes = File.ReadAllBytes(path);

        // 优先按严格 UTF-8 解码；失败再退回宽松 UTF-8（几乎所有现代 .lrc 都是 UTF-8）
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch
        {
            return Encoding.UTF8.GetString(bytes);
        }
    }

    /// <summary>解析 LRC 文本（支持一行多时间标签）。</summary>
    public static List<LyricLine> ParseLrc(string text)
    {
        var result = new List<LyricLine>();

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var matches = TimeTagRegex().Matches(line);
            if (matches.Count == 0)
            {
                continue;
            }

            var content = TimeTagRegex().Replace(line, "").Trim();
            foreach (Match m in matches)
            {
                var minutes = int.Parse(m.Groups[1].Value);
                var seconds = int.Parse(m.Groups[2].Value);
                var fraction = m.Groups[3].Success ? m.Groups[3].Value : "0";

                var ms = fraction.Length switch
                {
                    1 => int.Parse(fraction) * 100,
                    2 => int.Parse(fraction) * 10,
                    _ => int.Parse(fraction[..3]),
                };

                result.Add(new LyricLine
                {
                    Time = new TimeSpan(0, 0, minutes, seconds, ms),
                    Text = content,
                });
            }
        }

        return result.OrderBy(l => l.Time).ToList();
    }

    private static List<string> GetSearchDirs()
    {
        var dirs = new List<string>();
        var configured = SettingsStore.Current.LyricsFolder;

        if (!string.IsNullOrWhiteSpace(configured))
        {
            dirs.AddRange(configured.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        var music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        if (Directory.Exists(music))
        {
            dirs.Add(music);
        }

        return dirs.Distinct().ToList();
    }

    /// <summary>根据播放位置定位当前歌词行索引；返回 -1 表示还没到第一行。</summary>
    public int IndexAt(TimeSpan position)
    {
        if (_lines.Count == 0)
        {
            return -1;
        }

        var lo = 0;
        var hi = _lines.Count - 1;
        var found = -1;

        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (_lines[mid].Time <= position)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return found;
    }
}