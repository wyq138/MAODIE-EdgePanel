using System.Diagnostics;
using System.Text.Json;

namespace EdgePanel.Services;

/// <summary>单个 AI 工具的本地用量统计。</summary>
public sealed class AgentUsage
{
    public string Name { get; init; } = "";
    public bool Found { get; set; }
    public int ProcessCount { get; set; }
    public double MemoryMb { get; set; }
    public int SessionsToday { get; set; }
    public int SessionsTotal { get; set; }
    public long TokensToday { get; set; }
    public long TokensTotal { get; set; }
}

/// <summary>Agent 工作区（项目目录）及其会话数量、最近活动时间。</summary>
public sealed record AgentWorkspace(string Agent, string Name, string Path, int SessionCount, DateTime LastActive);

/// <summary>AI 助手一次性快照。</summary>
public sealed class AiAgentSnapshot
{
    public List<AgentUsage> Usages { get; } = new();
    public List<AgentWorkspace> Workspaces { get; } = new();
}

/// <summary>
/// 监控本机多种 AI 工具的本地活动：进程状态、会话/使用记录数量，以及可解析的 token 用量。
/// 多数工具的 token 额度在账号侧，本地只能尽力从会话 JSONL 里统计。
/// </summary>
public static class AiAgentService
{
    private static readonly string Home =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private sealed record ToolDef(
        string Name,
        string[] Keywords,
        string[] SessionRoots,
        bool TokenBased);

    private static readonly ToolDef[] Tools =
    {
        new("Claude Code", new[] { "claude" }, new[] { P(".claude", "projects") }, true),
        new("Codex", new[] { "codex" }, new[] { P(".codex", "sessions") }, true),
        new("Gemini CLI", new[] { "gemini" }, new[] { P(".gemini") }, true),
        new("Cursor", new[] { "cursor" }, new[] { P(".cursor") }, false),
        new("Windsurf", new[] { "windsurf" }, new[] { P(".windsurf") }, false),
        new("Cline", new[] { "cline" }, new[] { P(".vscode") }, false),
        new("Aider", new[] { "aider" }, new[] { P(".aider") }, true),
        new("Copilot", new[] { "copilot" }, new[] { P(".vscode") }, false),
        new("Trae", new[] { "trae" }, new[] { P(".trae-cn"), P(".trae") }, true),
        new("CodeBuddy", new[] { "codebuddy" }, new[] { P(".codebuddy") }, true),
        new("Comate", new[] { "comate", "baiducomate" }, new[] { P(".comate") }, true),
        new("通义灵码", new[] { "lingma", "tongyi" }, new[] { P(".lingma") }, true),
        new("豆包 MarsCode", new[] { "marscode", "doubao" }, new[] { P(".marscode") }, true),
    };

    private static string P(params string[] segs)
        => segs.Aggregate(Home, Path.Combine);

    public static AiAgentSnapshot Scan()
    {
        var snapshot = new AiAgentSnapshot();

        foreach (var tool in Tools)
        {
            snapshot.Usages.Add(ScanTool(tool));
        }

        // 工作区：Claude projects / Codex sessions 依然列出来方便打开
        ScanWorkspaces(snapshot);
        return snapshot;
    }

    private static AgentUsage ScanTool(ToolDef tool)
    {
        var usage = new AgentUsage { Name = tool.Name };

        foreach (var root in tool.SessionRoots)
        {
            if (Directory.Exists(root))
            {
                usage.Found = true;
                break;
            }
        }

        ScanProcess(tool, usage);

        if (tool.TokenBased)
        {
            foreach (var root in tool.SessionRoots)
            {
                if (Directory.Exists(root))
                {
                    ScanJsonl(root, usage);
                }
            }
        }

        return usage;
    }

    private static void ScanProcess(ToolDef tool, AgentUsage usage)
    {
        var count = 0;
        long memory = 0;
        var cpu = TimeSpan.Zero;

        try
        {
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    var name = process.ProcessName;
                    if (tool.Keywords.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase)))
                    {
                        count++;
                        memory += process.WorkingSet64;
                        cpu += process.TotalProcessorTime;
                    }
                }
                catch
                {
                    // 个别进程取不到信息，跳过
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            App.Log($"扫描 {tool.Name} 进程失败：" + ex.Message);
        }

        usage.ProcessCount = count;
        usage.MemoryMb = memory / 1024.0 / 1024.0;
    }

    private static void ScanJsonl(string root, AgentUsage usage)
    {
        try
        {
            foreach (var file in SafeJsonlFiles(root))
            {
                usage.SessionsTotal++;
                var isToday = file.LastWriteTime.Date == DateTime.Today;
                if (isToday)
                {
                    usage.SessionsToday++;
                }

                var tokens = SumTokens(file.FullName);
                usage.TokensTotal += tokens;
                if (isToday)
                {
                    usage.TokensToday += tokens;
                }
            }
        }
        catch (Exception ex)
        {
            App.Log($"扫描 {usage.Name} 记录失败：" + ex.Message);
        }
    }

    /// <summary>递归收集 JSONL 会话文件，跳过 node_modules/.git/bin/obj 等无关目录并限深限量。</summary>
    private static List<FileInfo> SafeJsonlFiles(string root)
    {
        var result = new List<FileInfo>();
        Walk(root, 0, result);
        return result;
    }

    private static void Walk(string dir, int depth, List<FileInfo> result)
    {
        if (depth > 6 || result.Count > 5000)
        {
            return;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.jsonl"))
            {
                try
                {
                    result.Add(new FileInfo(file));
                }
                catch
                {
                    // 忽略单个文件
                }
            }

            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                var name = Path.GetFileName(sub);
                if (name is "node_modules" or ".git" or "bin" or "obj" or "logs" or "Cache" or "CachedData")
                {
                    continue;
                }

                Walk(sub, depth + 1, result);
            }
        }
        catch
        {
            // 无权限目录忽略
        }
    }

    /// <summary>从一行 JSONL 里尽可能多地提取 token 数字，兼容 Claude Code / Codex 等不同字段。</summary>
    private static long SumTokens(string path)
    {
        long total = 0;

        try
        {
            foreach (var line in File.ReadLines(path))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed[0] != '{')
                {
                    continue;
                }

                try
                {
                    using var doc = JsonDocument.Parse(trimmed);
                    total += ExtractTokens(doc.RootElement);
                }
                catch
                {
                    // 非 JSON 行忽略
                }
            }
        }
        catch
        {
            // 文件读不了忽略
        }

        return total;
    }

    private static long ExtractTokens(JsonElement root)
    {
        long sum = 0;

        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            sum += ReadLong(usage, "input_tokens");
            sum += ReadLong(usage, "output_tokens");
            sum += ReadLong(usage, "total_tokens");
        }

        if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object &&
            message.TryGetProperty("usage", out var mUsage) && mUsage.ValueKind == JsonValueKind.Object)
        {
            sum += ReadLong(mUsage, "input_tokens");
            sum += ReadLong(mUsage, "output_tokens");
        }

        sum += ReadLong(root, "input_tokens");
        sum += ReadLong(root, "output_tokens");
        sum += ReadLong(root, "num_tokens");
        sum += ReadLong(root, "tokens");
        sum += ReadLong(root, "total_tokens");

        return sum;
    }

    private static long ReadLong(JsonElement obj, string name)
    {
        if (obj.TryGetProperty(name, out var node) &&
            node.ValueKind == JsonValueKind.Number &&
            node.TryGetInt64(out var value))
        {
            return value;
        }

        return 0;
    }

    private static void ScanWorkspaces(AiAgentSnapshot snapshot)
    {
        ScanClaudeProjects(P(".claude", "projects"), snapshot);
        ScanCodexSessions(P(".codex", "sessions"), snapshot);
    }

    /// <summary>~/.claude/projects 下每个子目录是一个工作区。</summary>
    private static void ScanClaudeProjects(string root, AiAgentSnapshot snapshot)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        try
        {
            foreach (var dir in Directory.GetDirectories(root))
            {
                var files = SafeFiles(dir);
                var last = files.Count == 0 ? Directory.GetLastWriteTime(dir) : files.Max(f => f.LastWriteTime);

                snapshot.Workspaces.Add(new AgentWorkspace(
                    "Claude",
                    Path.GetFileName(dir),
                    dir,
                    files.Count,
                    last));
            }
        }
        catch (Exception ex)
        {
            App.Log("扫描 Claude 工作区失败：" + ex.Message);
        }
    }

    /// <summary>~/.codex/sessions 按 年/月/日 分层存放 jsonl 会话文件。</summary>
    private static void ScanCodexSessions(string root, AiAgentSnapshot snapshot)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        var all = SafeFiles(root, recursive: true);
        var last = all.Count == 0 ? Directory.GetLastWriteTime(root) : all.Max(f => f.LastWriteTime);
        snapshot.Workspaces.Add(new AgentWorkspace("Codex", "sessions", root, all.Count, last));
    }

    private static List<FileInfo> SafeFiles(string dir, bool recursive = false)
    {
        try
        {
            var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            return new DirectoryInfo(dir).EnumerateFiles("*", option).ToList();
        }
        catch
        {
            return new List<FileInfo>();
        }
    }
}
