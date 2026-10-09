using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdgePanel.Core;

namespace EdgePanel.Services;

/// <summary>一条对话记录（用于界面展示与作为下一轮的上下文）。</summary>
public sealed class ChatEntry
{
    public string Role { get; init; } = "user";
    public string Text { get; set; } = "";
    public bool IsError { get; set; }
    public IReadOnlyList<string> Steps { get; set; } = Array.Empty<string>();
}

/// <summary>助手一轮回复：最终文本 + 过程中实际执行的工具步骤。</summary>
public sealed record AssistantReply(string Text, IReadOnlyList<string> Steps);

/// <summary>
/// 内置 AI 助手：调用用户自行配置的 OpenAI 兼容接口，通过 function calling 循环
/// 驱动 <see cref="PcTools"/> 里的电脑控制工具，直到模型给出最终答复。
/// </summary>
public sealed class AssistantService
{
    public static AssistantService Instance { get; } = new();

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(300) };

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>对话历史（跨页面切换保留；可在界面上清空）。</summary>
    public List<ChatEntry> History { get; } = new();

    private AssistantService()
    {
    }

    private static AppSettings S => SettingsStore.Current;

    public static bool IsConfigured =>
        !string.IsNullOrWhiteSpace(S.AssistantApiKey) && !string.IsNullOrWhiteSpace(S.AssistantModel);

    /// <summary>把用户输入交给模型，执行其请求的工具，返回最终答复。</summary>
    public async Task<AssistantReply> AskAsync(string userText, IProgress<string>? status, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userText))
        {
            return new AssistantReply("", Array.Empty<string>());
        }

        if (!IsConfigured)
        {
            return new AssistantReply("尚未配置模型：请到「设置 → 电脑助手」填写 Base URL、API Key 和模型名。", Array.Empty<string>());
        }

        await _gate.WaitAsync(ct);
        try
        {
            PcTools.Instance.TakeScreenshotPng();

            var settings = S;
            var messages = new JsonArray();

            if (!string.IsNullOrWhiteSpace(settings.AssistantSystemPrompt))
            {
                messages.Add(new JsonObject { ["role"] = "system", ["content"] = settings.AssistantSystemPrompt });
            }

            // 只回放纯文本历史；工具调用细节与错误提示不重放，避免上下文膨胀 / 非法 role
            var limit = Math.Clamp(settings.AssistantHistoryLimit, 2, 100);
            foreach (var entry in History.TakeLast(limit))
            {
                if (entry.Role is not ("user" or "assistant") || string.IsNullOrWhiteSpace(entry.Text))
                {
                    continue;
                }

                messages.Add(new JsonObject { ["role"] = entry.Role, ["content"] = entry.Text });
            }

            var tools = BuildTools(settings);
            var steps = new List<string>();
            var maxSteps = Math.Clamp(settings.AssistantMaxSteps, 1, 50);

            for (var step = 0; step < maxSteps; step++)
            {
                ct.ThrowIfCancellationRequested();
                status?.Report(step == 0 ? "正在思考…" : "正在继续处理…");

                var (content, calls, error) = await CallAsync(settings, messages, tools, ct);
                if (error is not null)
                {
                    return new AssistantReply(error!, steps);
                }

                if (calls.Count == 0)
                {
                    var text = string.IsNullOrWhiteSpace(content) ? "（模型没有返回内容）" : content!;
                    return new AssistantReply(text, steps);
                }

                // 把模型的工具调用请求记回上下文，否则下一轮会缺 assistant 消息而报错
                messages.Add(BuildAssistantToolCallMessage(content, calls));

                foreach (var call in calls)
                {
                    var label = string.IsNullOrWhiteSpace(call.Arguments) || call.Arguments == "{}"
                        ? call.Name
                        : $"{call.Name} {Shorten(call.Arguments, 120)}";
                    steps.Add(label);
                    status?.Report("正在执行：" + label);

                    string result;
                    try
                    {
                        result = await PcTools.Instance.InvokeAsync(call.Name, call.Arguments);
                    }
                    catch (Exception ex)
                    {
                        result = "工具执行失败：" + ex.Message;
                    }

                    messages.Add(new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = call.Id,
                        ["content"] = result,
                    });

                    // 截屏结果额外作为一张图片发给模型（需要模型支持视觉）
                    var png = settings.AssistantVision ? PcTools.Instance.TakeScreenshotPng() : null;
                    if (png is { Length: > 0 })
                    {
                        var dataUrl = "data:image/png;base64," + Convert.ToBase64String(png);
                        messages.Add(new JsonObject
                        {
                            ["role"] = "user",
                            ["content"] = new JsonArray
                            {
                                new JsonObject { ["type"] = "text", ["text"] = "这是刚才 screenshot 截到的屏幕画面：" },
                                new JsonObject
                                {
                                    ["type"] = "image_url",
                                    ["image_url"] = new JsonObject { ["url"] = dataUrl },
                                },
                            },
                        });
                    }
                }
            }

            return new AssistantReply($"已达到最大工具调用步数（{maxSteps}），请把任务拆小一些再试。", steps);
        }
        catch (OperationCanceledException)
        {
            return new AssistantReply("已取消。", Array.Empty<string>());
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed record ToolCall(string Id, string Name, string Arguments);

    private static JsonObject BuildAssistantToolCallMessage(string? content, IReadOnlyList<ToolCall> calls)
    {
        var array = new JsonArray();
        foreach (var call in calls)
        {
            array.Add(new JsonObject
            {
                ["id"] = call.Id,
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = call.Name,
                    ["arguments"] = string.IsNullOrWhiteSpace(call.Arguments) ? "{}" : call.Arguments,
                },
            });
        }

        return new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = content ?? "",
            ["tool_calls"] = array,
        };
    }

    private static JsonArray BuildTools(AppSettings settings)
    {
        var array = new JsonArray();
        foreach (var tool in PcTools.Instance.Tools)
        {
            if (!settings.AssistantAllowCommand && tool.Name == "run_command")
            {
                continue;
            }

            if (!settings.AssistantAllowFile && tool.Name is "read_file" or "write_file" or "list_dir")
            {
                continue;
            }

            array.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = JsonNode.Parse(tool.ParametersJson)!,
                },
            });
        }

        return array;
    }

    private static async Task<(string? Content, List<ToolCall> Calls, string? Error)> CallAsync(
        AppSettings settings, JsonArray messages, JsonArray tools, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = settings.AssistantModel,
            ["messages"] = messages,
        };

        if (tools.Count > 0)
        {
            body["tools"] = tools;
            body["tool_choice"] = "auto";
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, BuildEndpoint(settings.AssistantBaseUrl));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.AssistantApiKey);
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, ct);
        }
        catch (Exception ex)
        {
            return (null, new(), "请求模型失败：" + ex.Message);
        }

        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            return (null, new(), $"模型接口返回 {(int)response.StatusCode}：{Shorten(text, 600)}");
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            {
                return (null, new(), "模型返回里没有 choices：" + Shorten(text, 400));
            }

            var message = choices[0].GetProperty("message");
            var content = message.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString()
                : null;

            var calls = new List<ToolCall>();
            if (message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in toolCalls.EnumerateArray())
                {
                    var id = item.TryGetProperty("id", out var idNode) ? idNode.GetString() ?? "" : "";
                    if (!item.TryGetProperty("function", out var fn))
                    {
                        continue;
                    }

                    var name = fn.TryGetProperty("name", out var nameNode) ? nameNode.GetString() ?? "" : "";
                    var args = fn.TryGetProperty("arguments", out var argsNode) ? argsNode.GetString() ?? "{}" : "{}";
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        calls.Add(new ToolCall(id, name, args));
                    }
                }
            }

            return (content, calls, null);
        }
        catch (Exception ex)
        {
            return (null, new(), "解析模型返回失败：" + ex.Message + "\n" + Shorten(text, 400));
        }
    }

    private static string BuildEndpoint(string baseUrl)
    {
        var url = (baseUrl ?? "").Trim().TrimEnd('/');
        if (url.Length == 0)
        {
            url = "https://api.openai.com/v1";
        }

        return url.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? url
            : url + "/chat/completions";
    }

    private static string Shorten(string text, int max)
        => string.IsNullOrEmpty(text) || text.Length <= max ? text : text[..max] + "…";
}
