using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.Json;
using EdgePanel.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Devices.Power;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace EdgePanel.Services;

/// <summary>一条可供模型调用的电脑控制工具：名字 + 说明 + JSON Schema 参数。</summary>
public sealed record PcTool(string Name, string Description, string ParametersJson);

/// <summary>
/// 电脑控制工具集：鼠标、键盘、截屏、启动/切换程序、命令行、文件读写、剪贴板、系统信息。
/// 通过 Win32 / .NET 直接操作本机，供内置 AI 助手以 function calling 方式调用。
/// </summary>
public sealed class PcTools
{
    public static PcTools Instance { get; } = new();

    /// <summary>最近一次截屏的 PNG 字节（助手可把它发给支持视觉的模型）。</summary>
    public byte[]? LastScreenshotPng { get; private set; }

    private PcTools()
    {
    }

    /// <summary>取出并清空最近一次截屏，避免同一张图被反复发给模型。</summary>
    public byte[]? TakeScreenshotPng()
    {
        var value = LastScreenshotPng;
        LastScreenshotPng = null;
        return value;
    }

    public IReadOnlyList<PcTool> Tools { get; } = new[]
    {
        new PcTool("screenshot", "截取整个屏幕并返回图片。需要看屏幕内容、判断界面状态、或操作后确认结果时先调用它。",
            """{"type":"object","properties":{},"required":[]}"""),
        new PcTool("mouse_move", "把鼠标移动到屏幕坐标 (x,y)。坐标为屏幕物理像素，左上角是 (0,0)。",
            """{"type":"object","properties":{"x":{"type":"integer"},"y":{"type":"integer"}},"required":["x","y"]}"""),
        new PcTool("mouse_click", "在屏幕坐标 (x,y) 点击鼠标。",
            """{"type":"object","properties":{"x":{"type":"integer"},"y":{"type":"integer"},"button":{"type":"string","enum":["left","right","middle"],"description":"哪个键，默认 left"},"double":{"type":"boolean","description":"是否双击"}},"required":["x","y"]}"""),
        new PcTool("mouse_scroll", "滚动鼠标滚轮。amount 正数向上、负数向下，1 约等于一格。",
            """{"type":"object","properties":{"amount":{"type":"integer"}},"required":["amount"]}"""),
        new PcTool("mouse_drag", "按住左键把鼠标从 (x1,y1) 拖到 (x2,y2)，用于拖拽窗口、拖选文本。",
            """{"type":"object","properties":{"x1":{"type":"integer"},"y1":{"type":"integer"},"x2":{"type":"integer"},"y2":{"type":"integer"}},"required":["x1","y1","x2","y2"]}"""),
        new PcTool("keyboard_type", "在当前焦点处输入一段文本（支持中文）。",
            """{"type":"object","properties":{"text":{"type":"string"}},"required":["text"]}"""),
        new PcTool("key_press", "按下按键或组合键，例如 \"enter\"、\"esc\"、\"ctrl+c\"、\"win+r\"、\"alt+tab\"、\"f5\"。",
            """{"type":"object","properties":{"keys":{"type":"string"}},"required":["keys"]}"""),
        new PcTool("launch_app", "启动程序、打开文件或网址，例如 path=\"notepad\"、path=\"C:\\\\a\\\\b.exe\"、path=\"https://example.com\"。",
            """{"type":"object","properties":{"path":{"type":"string"},"arguments":{"type":"string"}},"required":["path"]}"""),
        new PcTool("list_windows", "列出当前所有可见窗口（标题 + 进程号），用于找到要操作的程序。",
            """{"type":"object","properties":{},"required":[]}"""),
        new PcTool("focus_window", "把标题包含 match（不区分大小写）或进程号等于 match 的窗口切到前台。",
            """{"type":"object","properties":{"match":{"type":"string"}},"required":["match"]}"""),
        new PcTool("close_window", "关闭标题包含 match（或进程号等于 match）的窗口。",
            """{"type":"object","properties":{"match":{"type":"string"}},"required":["match"]}"""),
        new PcTool("run_command", "在 cmd 中执行一条命令并返回输出。可用于查看文件、执行脚本等，注意有副作用。",
            """{"type":"object","properties":{"command":{"type":"string"},"working_directory":{"type":"string"},"timeout_seconds":{"type":"integer","description":"超时秒数，默认 60"}},"required":["command"]}"""),
        new PcTool("read_file", "读取文本文件内容（最多 256KB）。",
            """{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}"""),
        new PcTool("write_file", "写入（或追加）文本内容到文件，父目录不存在会自动创建。用来创建文件、写代码。",
            """{"type":"object","properties":{"path":{"type":"string"},"content":{"type":"string"},"append":{"type":"boolean","description":"true 表示追加，默认覆盖"}},"required":["path","content"]}"""),
        new PcTool("list_dir", "列出目录下的文件和子目录。",
            """{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}"""),
        new PcTool("get_clipboard", "读取剪贴板文本。",
            """{"type":"object","properties":{},"required":[]}"""),
        new PcTool("set_clipboard", "写入文本到剪贴板。",
            """{"type":"object","properties":{"text":{"type":"string"}},"required":["text"]}"""),
        new PcTool("system_info", "获取系统信息：操作系统、内存、磁盘、电量等。",
            """{"type":"object","properties":{},"required":[]}"""),
    };

    public async Task<string> InvokeAsync(string name, string argsJson)
    {
        JsonElement a;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
            a = doc.RootElement.Clone();
        }
        catch
        {
            return "参数不是合法 JSON：" + argsJson;
        }

        try
        {
            switch (name)
            {
                case "screenshot":
                    return await ScreenshotAsync();

                case "mouse_move":
                {
                    var x = I(a, "x");
                    var y = I(a, "y");
                    MoveCursor(x, y);
                    return $"鼠标已移动到 ({x},{y})";
                }

                case "mouse_click":
                {
                    var x = I(a, "x");
                    var y = I(a, "y");
                    var button = S(a, "button", "left").ToLowerInvariant();
                    var dbl = B(a, "double");
                    MoveCursor(x, y);
                    Thread.Sleep(30);
                    ClickButton(button, dbl);
                    return $"已{(dbl ? "双击" : "点击")} ({x},{y}) {button}键";
                }

                case "mouse_scroll":
                {
                    var amount = I(a, "amount");
                    mouse_event(MOUSEEVENTF_WHEEL, 0, 0, amount * 120, UIntPtr.Zero);
                    return $"已滚动 {amount} 格";
                }

                case "mouse_drag":
                {
                    var x1 = I(a, "x1");
                    var y1 = I(a, "y1");
                    var x2 = I(a, "x2");
                    var y2 = I(a, "y2");
                    MoveCursor(x1, y1);
                    Thread.Sleep(60);
                    mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                    Thread.Sleep(60);
                    for (var i = 1; i <= 12; i++)
                    {
                        MoveCursor((int)(x1 + (x2 - x1) * i / 12.0), (int)(y1 + (y2 - y1) * i / 12.0));
                        Thread.Sleep(15);
                    }

                    mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
                    return $"已从 ({x1},{y1}) 拖拽到 ({x2},{y2})";
                }

                case "keyboard_type":
                {
                    var text = S(a, "text");
                    if (text.Length == 0)
                    {
                        return "没有要输入的文本";
                    }

                    if (text.Length > 4000)
                    {
                        text = text[..4000];
                    }

                    SendUnicode(text);
                    return $"已输入 {text.Length} 个字符";
                }

                case "key_press":
                {
                    var keys = S(a, "keys");
                    SendKeyCombo(keys);
                    return $"已按键：{keys}";
                }

                case "launch_app":
                {
                    var path = S(a, "path");
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        return "缺少 path";
                    }

                    var arguments = S(a, "arguments");
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = path,
                        Arguments = arguments,
                        UseShellExecute = true,
                    });
                    return $"已启动：{path} {arguments}".Trim();
                }

                case "list_windows":
                    return ListWindows();

                case "focus_window":
                {
                    var match = S(a, "match");
                    var hwnd = FindWindow(match);
                    if (hwnd == IntPtr.Zero)
                    {
                        return $"没找到匹配「{match}」的窗口";
                    }

                    ShowWindow(hwnd, SW_RESTORE);
                    SetForegroundWindow(hwnd);
                    return $"已切到窗口：{WindowTitle(hwnd)}";
                }

                case "close_window":
                {
                    var match = S(a, "match");
                    var hwnd = FindWindow(match);
                    if (hwnd == IntPtr.Zero)
                    {
                        return $"没找到匹配「{match}」的窗口";
                    }

                    var title = WindowTitle(hwnd);
                    PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                    return $"已请求关闭窗口：{title}";
                }

                case "run_command":
                    return await RunCommandAsync(S(a, "command"), S(a, "working_directory"), I(a, "timeout_seconds", 60));

                case "read_file":
                {
                    var path = S(a, "path");
                    var info = new FileInfo(path);
                    if (!info.Exists)
                    {
                        return $"文件不存在：{path}";
                    }

                    if (info.Length > 256 * 1024)
                    {
                        return $"文件较大（{info.Length} 字节），只读取前 256KB：\n" + await ReadHeadAsync(path, 256 * 1024);
                    }

                    return await File.ReadAllTextAsync(path);
                }

                case "write_file":
                {
                    var path = S(a, "path");
                    var content = S(a, "content");
                    var append = B(a, "append");
                    var dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrWhiteSpace(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    if (append)
                    {
                        await File.AppendAllTextAsync(path, content);
                    }
                    else
                    {
                        await File.WriteAllTextAsync(path, content);
                    }

                    return $"已{(append ? "追加" : "写入")} {content.Length} 个字符到 {path}";
                }

                case "list_dir":
                {
                    var path = S(a, "path");
                    if (!Directory.Exists(path))
                    {
                        return $"目录不存在：{path}";
                    }

                    var sb = new StringBuilder();
                    foreach (var d in Directory.GetDirectories(path))
                    {
                        sb.AppendLine("[目录] " + Path.GetFileName(d));
                    }

                    foreach (var f in Directory.GetFiles(path))
                    {
                        sb.AppendLine($"[文件] {Path.GetFileName(f)}  {new FileInfo(f).Length} 字节");
                    }

                    return sb.Length == 0 ? "（空目录）" : sb.ToString();
                }

                case "get_clipboard":
                {
                    var view = Clipboard.GetContent();
                    if (!view.Contains(StandardDataFormats.Text))
                    {
                        return "剪贴板里没有文本";
                    }

                    return await view.GetTextAsync();
                }

                case "set_clipboard":
                {
                    var data = new DataPackage();
                    data.SetText(S(a, "text"));
                    Clipboard.SetContent(data);
                    return "已写入剪贴板";
                }

                case "system_info":
                    return SystemInfo();

                default:
                    return $"未知工具：{name}";
            }
        }
        catch (Exception ex)
        {
            App.Log($"工具 {name} 执行失败：{ex.Message}");
            return $"工具 {name} 执行失败：{ex.Message}";
        }
    }

    // ————————————————————————— 参数读取 —————————————————————————

    private static string S(JsonElement e, string name, string fallback = "")
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;

    private static int I(JsonElement e, string name, int fallback = 0)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : fallback;

    private static bool B(JsonElement e, string name, bool fallback = false)
        => e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : fallback;

    private static async Task<string> ReadHeadAsync(string path, int maxBytes)
    {
        await using var stream = File.OpenRead(path);
        var buffer = new byte[maxBytes];
        var read = await stream.ReadAsync(buffer);
        return Encoding.UTF8.GetString(buffer, 0, read);
    }

    // ————————————————————————— 鼠标 / 键盘 —————————————————————————

    private static void MoveCursor(int x, int y) => SetCursorPos(x, y);

    private static void ClickButton(string button, bool dbl)
    {
        var (down, up) = button switch
        {
            "right" => (MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP),
            "middle" => (MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP),
            _ => (MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP),
        };

        var times = dbl ? 2 : 1;
        for (var i = 0; i < times; i++)
        {
            mouse_event(down, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(20);
            mouse_event(up, 0, 0, 0, UIntPtr.Zero);
            if (times > 1)
            {
                Thread.Sleep(60);
            }
        }
    }

    private static void SendUnicode(string text)
    {
        var inputs = new List<INPUT>(text.Length * 2);
        foreach (var ch in text)
        {
            inputs.Add(new INPUT
            {
                Type = INPUT_KEYBOARD,
                U = new InputUnion { Ki = new KEYBDINPUT { Scan = ch, Flags = KEYEVENTF_UNICODE } },
            });
            inputs.Add(new INPUT
            {
                Type = INPUT_KEYBOARD,
                U = new InputUnion { Ki = new KEYBDINPUT { Scan = ch, Flags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP } },
            });
        }

        if (inputs.Count > 0)
        {
            SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
        }
    }

    private static void SendKeyCombo(string combo)
    {
        var parts = combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return;
        }

        var mods = new List<ushort>();
        var vk = (ushort)0;
        foreach (var part in parts)
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    mods.Add(0x11);
                    break;
                case "shift":
                    mods.Add(0x10);
                    break;
                case "alt":
                    mods.Add(0x12);
                    break;
                case "win":
                    mods.Add(0x5B);
                    break;
                default:
                    vk = MapKey(part.ToLowerInvariant());
                    break;
            }
        }

        if (vk == 0)
        {
            return;
        }

        var inputs = new List<INPUT>();
        foreach (var m in mods)
        {
            inputs.Add(KeyInput(m, false));
        }

        inputs.Add(KeyInput(vk, false));
        inputs.Add(KeyInput(vk, true));
        for (var i = mods.Count - 1; i >= 0; i--)
        {
            inputs.Add(KeyInput(mods[i], true));
        }

        SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
    }

    private static INPUT KeyInput(ushort vk, bool up) => new()
    {
        Type = INPUT_KEYBOARD,
        U = new InputUnion { Ki = new KEYBDINPUT { Vk = vk, Flags = up ? KEYEVENTF_KEYUP : 0 } },
    };

    private static ushort MapKey(string key) => key switch
    {
        "enter" or "return" => 0x0D,
        "tab" => 0x09,
        "esc" or "escape" => 0x1B,
        "space" => 0x20,
        "backspace" => 0x08,
        "delete" or "del" => 0x2E,
        "insert" => 0x2D,
        "home" => 0x24,
        "end" => 0x23,
        "pageup" => 0x21,
        "pagedown" => 0x22,
        "up" => 0x26,
        "down" => 0x28,
        "left" => 0x25,
        "right" => 0x27,
        "capslock" => 0x14,
        "printscreen" => 0x2C,
        _ when key.Length >= 2 && key[0] == 'f' && int.TryParse(key[1..], out var n) && n is >= 1 and <= 12
            => (ushort)(0x6F + n),
        _ when key.Length == 1 => (ushort)char.ToUpperInvariant(key[0]),
        _ => 0,
    };

    // ————————————————————————— 截屏 —————————————————————————

    private async Task<string> ScreenshotAsync()
    {
        var (png, width, height) = await CaptureAsync();
        if (png is null)
        {
            return "截屏失败";
        }

        LastScreenshotPng = png;
        var dir = Path.Combine(App.DataRoot, "shots");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"shot_{DateTime.Now:yyyyMMdd_HHmmss}.png");
        await File.WriteAllBytesAsync(file, png);

        return $"已截屏：{width}x{height}，保存到 {file}";
    }

    private static async Task<(byte[]? Png, int Width, int Height)> CaptureAsync()
    {
        var x = GetSystemMetrics(SM_XVIRTUALSCREEN);
        var y = GetSystemMetrics(SM_YVIRTUALSCREEN);
        var w = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        var h = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        if (w <= 0 || h <= 0)
        {
            return (null, 0, 0);
        }

        var hdcScreen = GetDC(IntPtr.Zero);
        var hdcMem = CreateCompatibleDC(hdcScreen);
        var hBmp = CreateCompatibleBitmap(hdcScreen, w, h);
        var old = SelectObject(hdcMem, hBmp);

        try
        {
            if (!BitBlt(hdcMem, 0, 0, w, h, hdcScreen, x, y, SRCCOPY | CAPTUREBLT))
            {
                return (null, 0, 0);
            }

            var bmi = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = w,
                    biHeight = -h, // 负数表示自上而下，避免图像上下颠倒
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = BI_RGB,
                },
            };

            var pixels = new byte[w * h * 4];
            if (GetDIBits(hdcMem, hBmp, 0, (uint)h, pixels, ref bmi, DIB_RGB_COLORS) == 0)
            {
                return (null, 0, 0);
            }

            using var stream = new InMemoryRandomAccessStream();
            var bitmap = SoftwareBitmap.CreateCopyFromBuffer(
                pixels.AsBuffer(), BitmapPixelFormat.Bgra8, w, h, BitmapAlphaMode.Premultiplied);
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetSoftwareBitmap(bitmap);
            await encoder.FlushAsync();

            stream.Seek(0);
            var bytes = new byte[stream.Size];
            using (var reader = new DataReader(stream))
            {
                await reader.LoadAsync((uint)stream.Size);
                reader.ReadBytes(bytes);
            }

            return (bytes, w, h);
        }
        finally
        {
            SelectObject(hdcMem, old);
            DeleteObject(hBmp);
            DeleteDC(hdcMem);
            ReleaseDC(IntPtr.Zero, hdcScreen);
        }
    }

    // ————————————————————————— 窗口 —————————————————————————

    private static string ListWindows()
    {
        var sb = new StringBuilder();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd))
            {
                return true;
            }

            var title = WindowTitle(hwnd);
            if (string.IsNullOrWhiteSpace(title))
            {
                return true;
            }

            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            sb.AppendLine($"[{pid}] {title}");
            return true;
        }, IntPtr.Zero);

        return sb.Length == 0 ? "没有可见窗口" : sb.ToString();
    }

    private static IntPtr FindWindow(string match)
    {
        if (string.IsNullOrWhiteSpace(match))
        {
            return IntPtr.Zero;
        }

        var found = IntPtr.Zero;
        var isPid = uint.TryParse(match, out var pid);
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd))
            {
                return true;
            }

            NativeMethods.GetWindowThreadProcessId(hwnd, out var windowPid);
            var hit = isPid
                ? windowPid == pid
                : WindowTitle(hwnd).Contains(match, StringComparison.OrdinalIgnoreCase);

            if (!hit)
            {
                return true;
            }

            found = hwnd;
            return false; // 找到第一个就停
        }, IntPtr.Zero);

        return found;
    }

    private static string WindowTitle(IntPtr hwnd)
    {
        var length = GetWindowTextLength(hwnd);
        if (length <= 0)
        {
            return "";
        }

        var sb = new StringBuilder(length + 1);
        _ = GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    // ————————————————————————— 命令行 / 系统信息 —————————————————————————

    private static async Task<string> RunCommandAsync(string command, string workingDirectory, int timeoutSeconds)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return "缺少 command";
        }

        var timeout = Math.Clamp(timeoutSeconds <= 0 ? 60 : timeoutSeconds, 5, 600);

        // chcp 65001 让 cmd 以 UTF-8 输出，避免中文变成乱码
        var psi = new ProcessStartInfo("cmd.exe", $"/d /s /c \"chcp 65001>nul & {command}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        if (!string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory))
        {
            psi.WorkingDirectory = workingDirectory;
        }

        using var process = Process.Start(psi);
        if (process is null)
        {
            return "无法启动 cmd";
        }

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        var wait = process.WaitForExitAsync();

        var finished = await Task.WhenAny(wait, Task.Delay(TimeSpan.FromSeconds(timeout)));
        if (finished != wait)
        {
            try
            {
                process.Kill(true);
            }
            catch
            {
                // 忽略
            }

            return $"命令执行超过 {timeout} 秒已终止。";
        }

        var output = (await stdout).Trim();
        var error = (await stderr).Trim();
        var sb = new StringBuilder();
        sb.AppendLine($"退出码：{process.ExitCode}");
        if (output.Length > 0)
        {
            sb.AppendLine("--- 输出 ---");
            sb.AppendLine(Trim(output));
        }

        if (error.Length > 0)
        {
            sb.AppendLine("--- 错误 ---");
            sb.AppendLine(Trim(error));
        }

        return sb.ToString();
    }

    private static string SystemInfo()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"操作系统：{RuntimeInformation.OSDescription}");
        sb.AppendLine($"计算机名：{Environment.MachineName}    用户：{Environment.UserName}");
        sb.AppendLine($"CPU 逻辑核心：{Environment.ProcessorCount}");

        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref mem))
        {
            sb.AppendLine($"内存：已用 {Gb(mem.ullTotalPhys - mem.ullAvailPhys)} / 共 {Gb(mem.ullTotalPhys)}");
        }

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.IsReady)
                {
                    sb.AppendLine($"磁盘 {drive.Name}：可用 {Gb((ulong)drive.AvailableFreeSpace)} / 共 {Gb((ulong)drive.TotalSize)}");
                }
            }
            catch
            {
                // 忽略不可读的盘
            }
        }

        try
        {
            var report = Battery.AggregateBattery.GetReport();
            sb.AppendLine($"电源：{report.Status}，剩余 {report.RemainingCapacityInMilliwattHours} / {report.FullChargeCapacityInMilliwattHours} mWh");
        }
        catch
        {
            // 台式机可能没有电池
        }

        return sb.ToString().Trim();
    }

    private static string Gb(ulong bytes) => $"{bytes / 1024.0 / 1024 / 1024:0.0} GB";

    private static string Trim(string text) => text.Length <= 8000 ? text : text[..8000] + "\n…（输出过长已截断）";

    // ————————————————————————— Win32 互操作 —————————————————————————

    private const int INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;

    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;

    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    private const uint SRCCOPY = 0x00CC0020;
    private const uint CAPTUREBLT = 0x40000000;
    private const int BI_RGB = 0;
    private const uint DIB_RGB_COLORS = 0;

    private const int SW_RESTORE = 9;
    private const uint WM_CLOSE = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT Mi;
        [FieldOffset(0)] public KEYBDINPUT Ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort Vk;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, int dx, int dy, int data, UIntPtr extraInfo);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(
        IntPtr hdcDest, int xDest, int yDest, int width, int height, IntPtr hdcSrc, int xSrc, int ySrc, uint rop);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(
        IntPtr hdc, IntPtr hbm, uint start, uint lines, byte[] bits, ref BITMAPINFO info, uint usage);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hwnd, int cmd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);
}
