using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using RapidOcrNet;
using SkiaSharp;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using RapidResult = RapidOcrNet.OcrResult;
using WinResult = Windows.Media.Ocr.OcrResult;

namespace EdgePanel.Services;

/// <summary>
/// 汽水音乐桌面左下角那条迷你播放条会同步显示"当前歌词行"，但它是 Chromium 窗口、
/// 无障碍树默认关闭（UIA 只能拿到 Chrome Legacy Window 两个节点），读不到里面的文字。
/// 这里走另一条路：截图该窗口 → OCR 识别 → 取字号最大的那行作为当前歌词。
/// 仅在本地面/在线歌词都抓不到时兜底，避免无谓的 OCR 开销。
///
/// OCR 引擎分两层：优先用系统自带的 Windows.Media.Ocr（有对应语言包时快且省内存）；
/// 日语/韩语在没有系统语言包时回退到随包分发的 PP-OCRv5 离线模型（RapidOcrNet），
/// 这样客户机不需要额外安装任何 OCR 语言包也能用。
/// </summary>
public sealed partial class SodaLyrics
{
    public static SodaLyrics Instance { get; } = new();

    /// <summary>随包分发的内置离线 OCR 模型目录（输出目录下的 Assets\Ocr）。</summary>
    private static string OfflineDir =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "Ocr");

    private readonly List<OcrEngine> _engines = new();

    /// <summary>内置离线引擎按语言缓存（"ja"/"ko"）：首次用到才加载，避免白白吃内存。</summary>
    private readonly Dictionary<string, RapidOcr?> _offline = new();
    private readonly object _offlineGate = new();

    private Timer? _timer;
    private int _busy;
    private string _lastLogged = "";

    /// <summary>从迷你条识别出的当前歌词行；读不到时为空串。</summary>
    public string CurrentLine { get; private set; } = "";

    public event Action? Changed;

    private SodaLyrics()
    {
        // 一个 OcrEngine 只认一种语言：中文引擎能兼顾英文/数字，但认不出韩文。
        // 韩文必须系统装了 ko-KR 的 OCR 语言包，否则 TryCreateFromLanguage 返回 null。
        foreach (var tag in new[] { "zh-CN", "ja-JP", "ko-KR", "en-US" })
        {
            var engine = TryCreateEngine(tag);
            if (engine is not null)
            {
                _engines.Add(engine);
            }
        }

        if (_engines.Count == 0)
        {
            try
            {
                var fallback = OcrEngine.TryCreateFromUserProfileLanguages();
                if (fallback is not null)
                {
                    _engines.Add(fallback);
                }
            }
            catch (Exception ex)
            {
                App.Log("OCR 引擎初始化失败：" + ex.Message);
            }
        }

        App.Log(_engines.Count == 0
            ? "系统未安装任何 OCR 语言包，迷你条歌词将只依赖内置离线 OCR"
            : "迷你条 OCR 已就绪，可用语言：" +
              string.Join(", ", _engines.Select(e => e.RecognizerLanguage.LanguageTag)));

        CheckOfflineModels();
    }

    /// <summary>
    /// 检查随包分发的内置 OCR 模型是否齐全——这是"日语/韩语歌词开箱即用"的保证：
    /// 客户机没装 ja/ko 的 Windows OCR 语言包时走这里的模型兜底，不需要任何额外安装。
    /// </summary>
    private static void CheckOfflineModels()
    {
        // 这只是一次自检，任何意外（路径取不到、目录被占等）都不该把整个类型的
        // 静态初始化搞挂——否则一次自检失败会连带歌词、媒体等所有功能一起崩。
        try
        {
            foreach (var (name, rec, dict) in new[]
                     {
                         ("中日文", "ch_PP-OCRv5_rec_mobile.onnx", "ch_PP-OCRv5_dict.txt"),
                         ("韩语", "korean_PP-OCRv5_rec_mobile.onnx", "korean_PP-OCRv5_dict.txt"),
                     })
            {
                var ok = File.Exists(Path.Combine(OfflineDir, rec)) &&
                         File.Exists(Path.Combine(OfflineDir, dict));

                App.Log(ok
                    ? $"内置离线 OCR（{name}）模型已就绪：{rec}"
                    : $"内置离线 OCR（{name}）模型缺失（{OfflineDir}），{name}歌词将无法识别");
            }
        }
        catch (Exception ex)
        {
            App.Log("内置离线 OCR 模型自检失败：" + ex.Message);
        }
    }

    private static OcrEngine? TryCreateEngine(string tag)
    {
        try
        {
            return OcrEngine.TryCreateFromLanguage(new Language(tag));
        }
        catch
        {
            return null;
        }
    }

    public void Start()
    {
        if (_timer is not null)
        {
            return;
        }

        // 900ms 一次足够跟住歌词切换，又不至于让 OCR 长时间占 CPU
        _timer = new Timer(_ => _ = TickAsync(), null, 0, 900);
        App.Log("汽水迷你条 OCR 歌词读取已启动");
    }

    private async Task TickAsync()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1)
        {
            return;
        }

        try
        {
            // 只有"正常歌词没拿到"才需要兜底，否则纯属浪费
            if (LyricsService.Instance.HasLyrics || !MediaService.Instance.Current.IsPlaying)
            {
                SetLine("");
                return;
            }

            if (!TryFindBar(out var rect))
            {
                SetLine("");
                return;
            }

            var frame = Capture(rect);
            if (frame is null)
            {
                return;
            }

            SetLine(await RecognizeAsync(frame.Value));
        }
        catch (Exception ex)
        {
            App.Log("迷你条歌词 OCR 失败：" + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    /// <summary>
    /// 按正在播放歌曲的语言挑选优先 OCR 引擎：日语歌用 ja-JP、韩语歌用 ko-KR、
    /// 中文歌用 zh-CN、其它用 en-US。
    /// 日语/韩语：系统装了对应语言包就优先用系统的，没装则回退内置离线模型；
    /// 但绝不能退回中文引擎硬认（中文引擎认不出假名/谚文，会显示一段错误的中文歌词）。
    /// 中/英：先用系统引擎逐个试；一个系统语言包都没有时也回退内置离线模型
    /// （ch 模型同时覆盖中英文），保证客户机不装任何 OCR 语言包也能用。
    /// </summary>
    private async Task<string> RecognizeAsync(Frame frame)
    {
        var preferred = DetectLanguageTag();
        var family = preferred[..2];

        if (family is "ja" or "ko")
        {
            var system = _engines.FirstOrDefault(e => e.RecognizerLanguage.LanguageTag
                .StartsWith(family, StringComparison.OrdinalIgnoreCase));

            if (system is not null)
            {
                using var bitmap = ToSoftwareBitmap(frame);
                var line = PickLyricLine(ToLines(await system.RecognizeAsync(bitmap)));
                if (line.Length > 0)
                {
                    return line;
                }
            }

            // 系统没有该语言引擎（或没读出内容）时走随包分发的内置离线模型
            return await OfflineAsync(family, frame);
        }

        var ordered = _engines
            .OrderByDescending(e => e.RecognizerLanguage.LanguageTag
                .StartsWith(family, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (ordered.Count == 0)
        {
            // 客户机一个系统 OCR 语言包都没装：退回内置离线模型，别显示空白歌词
            return await OfflineAsync(family, frame);
        }

        using var sbmp = ToSoftwareBitmap(frame);
        var result = "";
        foreach (var engine in ordered)
        {
            result = PickLyricLine(ToLines(await engine.RecognizeAsync(sbmp)));
            if (result.Length > 0)
            {
                break;
            }
        }

        return result;
    }

    /// <summary>
    /// 用内置离线引擎识别。日文、中文、英文都用 PP-OCRv5 中文识别模型（该模型同时覆盖
    /// 中日文与英文），韩文用韩文识别模型；检测/方向模型沿用包内自带的那一套。
    /// </summary>
    private async Task<string> OfflineAsync(string family, Frame frame)
    {
        var ocr = GetOfflineEngine(family);
        if (ocr is null)
        {
            return "";
        }

        using var bitmap = ToSkBitmap(frame);
        RapidResult result;
        try
        {
            // ONNX 推理是纯 CPU 计算，挪到线程池，别占着定时器线程
            result = await Task.Run(() => ocr.Detect(bitmap, RapidOcrOptions.Default));
        }
        catch (Exception ex)
        {
            App.Log($"内置离线 OCR（{family}）识别失败：{ex.Message}");
            return "";
        }

        return PickLyricLine(ToLines(result));
    }

    /// <summary>
    /// 取（必要时创建）内置离线 OCR 引擎。首次用到才加载，避免只用中/英文的用户
    /// 白白多占几十 MB 内存；加载失败会缓存 null，不反复重试。
    /// </summary>
    private RapidOcr? GetOfflineEngine(string family)
    {
        lock (_offlineGate)
        {
            if (_offline.TryGetValue(family, out var cached))
            {
                return cached;
            }

            RapidOcr? ocr = null;
            try
            {
                var (rec, dict) = family == "ko"
                    ? ("korean_PP-OCRv5_rec_mobile.onnx", "korean_PP-OCRv5_dict.txt")
                    : ("ch_PP-OCRv5_rec_mobile.onnx", "ch_PP-OCRv5_dict.txt");

                var recPath = Path.Combine(OfflineDir, rec);
                var dictPath = Path.Combine(OfflineDir, dict);

                if (!File.Exists(recPath) || !File.Exists(dictPath))
                {
                    App.Log($"内置离线 OCR 模型缺失：{recPath}");
                }
                else
                {
                    // 检测/方向模型沿用包内自带的 PP-OCRv5 一套，只换识别模型与字典。
                    // 路径一律给绝对路径：库自带的模型路径是相对"当前工作目录"的，
                    // 从快捷方式或其它目录启动时工作目录未必是 exe 所在目录。
                    var latin = RapidOcrModelSet.PPOCRv5Latin;
                    ocr = new RapidOcr();
                    ocr.InitModels(latin with
                    {
                        DetModelPath = Path.Combine(AppContext.BaseDirectory, latin.DetModelPath),
                        ClsModelPath = Path.Combine(AppContext.BaseDirectory, latin.ClsModelPath),
                        RecModelPath = recPath,
                        KeysPath = dictPath,
                    });
                    App.Log($"内置离线 OCR 已加载（{family}）：{rec}");
                }
            }
            catch (Exception ex)
            {
                App.Log($"内置离线 OCR（{family}）初始化失败：{ex.Message}");
                ocr?.Dispose();
                ocr = null;
            }

            _offline[family] = ocr;
            return ocr;
        }
    }

    /// <summary>把抓屏得到的 BGRA 像素转成 Windows OCR 需要的 SoftwareBitmap。</summary>
    private static SoftwareBitmap ToSoftwareBitmap(Frame frame)
        => SoftwareBitmap.CreateCopyFromBuffer(
            frame.Bgra.AsBuffer(), BitmapPixelFormat.Bgra8, frame.Width, frame.Height,
            BitmapAlphaMode.Premultiplied);

    /// <summary>把抓屏得到的 BGRA 像素直接包成 SkiaSharp 位图，省掉一次 PNG 编解码。</summary>
    private static SKBitmap ToSkBitmap(Frame frame)
    {
        // 用 Opaque 而不是 Premul：GDI 抓屏出来的 DIB 里 alpha 通道是 0，
        // 若按预乘位图处理，Skia 反预乘时会被当成全黑，图片直接变纯黑。
        var info = new SKImageInfo(
            frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        var bitmap = new SKBitmap(info);
        Marshal.Copy(frame.Bgra, 0, bitmap.GetPixels(), frame.Bgra.Length);
        return bitmap;
    }

    /// <summary>把系统 OCR 的行结果统一成"文本 + 上下边界"（坐标是放大后截图的像素）。</summary>
    private static List<OcrLineBox> ToLines(WinResult result)
    {
        var lines = new List<OcrLineBox>(result.Lines.Count);
        foreach (var line in result.Lines)
        {
            var text = (line.Text ?? "").Trim();
            if (text.Length == 0 || line.Words.Count == 0)
            {
                continue;
            }

            double top = double.MaxValue;
            double bottom = double.MinValue;
            foreach (var word in line.Words)
            {
                top = Math.Min(top, word.BoundingRect.Top);
                bottom = Math.Max(bottom, word.BoundingRect.Bottom);
            }

            lines.Add(new OcrLineBox(text, top, bottom));
        }

        return lines;
    }

    /// <summary>
    /// 把内置 OCR 的行结果统一成"文本 + 上下边界"。
    /// BoxPoints 是顺时针的四个角点、源图像素坐标，取 Y 的最小/最大值即可。
    /// </summary>
    private static List<OcrLineBox> ToLines(RapidResult result)
    {
        var lines = new List<OcrLineBox>(result.TextBlocks.Length);
        foreach (var block in result.TextBlocks)
        {
            var text = (block.Text ?? "").Trim();
            if (text.Length == 0)
            {
                continue;
            }

            double top = double.MaxValue;
            double bottom = double.MinValue;
            foreach (var point in block.BoxPoints)
            {
                top = Math.Min(top, point.Y);
                bottom = Math.Max(bottom, point.Y);
            }

            lines.Add(new OcrLineBox(text, top, bottom));
        }

        return lines;
    }

    /// <summary>一行的识别文本与其上下边界，用来屏蔽两套 OCR 结果的差异。</summary>
    private readonly record struct OcrLineBox(string Text, double Top, double Bottom);

    /// <summary>根据歌名/歌手判断歌词语言，用于选择 OCR 引擎。</summary>
    private static string DetectLanguageTag()
    {
        var info = MediaService.Instance.Current;
        var text = info.Title + " " + info.Artist;

        foreach (var ch in text)
        {
            if (ch is >= '\u3040' and <= '\u30FF')
            {
                return "ja-JP"; // 平假名/片假名
            }

            if (ch is >= '\uAC00' and <= '\uD7AF')
            {
                return "ko-KR"; // 谚文
            }
        }

        foreach (var ch in text)
        {
            if (ch is >= '\u4E00' and <= '\u9FFF')
            {
                return "zh-CN"; // 汉字
            }
        }

        return "en-US";
    }

    private void SetLine(string line)
    {
        if (line == CurrentLine)
        {
            return;
        }

        CurrentLine = line;
        Changed?.Invoke();
    }

    /// <summary>
    /// 从 OCR 结果里挑歌词行：标题行显示的是正在播放的歌名，歌词行永远排在它下面。
    /// 所以先排除与歌名相同的行，再取其下"位置最低且字数够"的那行。
    /// 字数的判定不再限定汉字（中/韩/英/数字都算），否则韩语、英语歌词会被判为空。
    /// </summary>
    private string PickLyricLine(List<OcrLineBox> lines)
    {
        var title = MediaService.Instance.Current.Title ?? "";
        var tallest = "";
        var tallestHeight = 0d;

        var best = "";
        var bestBottom = double.MinValue;
        var bestHeight = 0d;

        foreach (var line in lines)
        {
            var text = line.Text;
            var height = line.Bottom - line.Top;

            if (height > tallestHeight)
            {
                tallestHeight = height;
                tallest = text;
            }

            // 歌名行不是歌词，跳过
            if (IsSameText(text, title))
            {
                continue;
            }

            if (LetterCount(text) < 2)
            {
                continue;
            }

            if (line.Bottom > bestBottom || (line.Bottom == bestBottom && height > bestHeight))
            {
                bestBottom = line.Bottom;
                bestHeight = height;
                best = text;
            }
        }

        // 底部优先挑不出来时退回字高最大的那行（同样排除歌名）
        if (best.Length == 0 && !IsSameText(tallest, title))
        {
            best = tallest;
        }

        if (best != _lastLogged)
        {
            _lastLogged = best;
            App.Log($"迷你条 OCR 识别到歌词：{best}（字高 {tallestHeight:0}）");
        }

        return Normalize(best);
    }

    /// <summary>判断 OCR 出来的这行是不是歌名（去掉空白/标点后比较，允许一两个字符的识别误差）。</summary>
    private static bool IsSameText(string a, string b)
    {
        var x = FoldText(a);
        var y = FoldText(b);
        if (x.Length == 0 || y.Length == 0)
        {
            return false;
        }

        if (x == y)
        {
            return true;
        }

        return Math.Min(x.Length, y.Length) >= 3 &&
               Math.Abs(x.Length - y.Length) <= 1 &&
               (x.Contains(y, StringComparison.Ordinal) || y.Contains(x, StringComparison.Ordinal));
    }

    /// <summary>只保留字母与数字并小写，用于忽略 OCR 标点/空格差异。</summary>
    private static string FoldText(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// OCR 会在汉字之间插入空格（"难道 非要耗尽"），中文歌词里这些空格不是原意，抹掉；
    /// 汉字与英文/数字之间、以及英文/韩文自身的空格都保留。
    /// </summary>
    private static string Normalize(string text)
        => CjkSpaceRegex().Replace(text.Trim(), "");

    [System.Text.RegularExpressions.GeneratedRegex(
        @"(?<=[\u4e00-\u9fff])\s+(?=[\u4e00-\u9fff])",
        System.Text.RegularExpressions.RegexOptions.Compiled)]
    private static partial System.Text.RegularExpressions.Regex CjkSpaceRegex();

    private static int LetterCount(string text)
    {
        var count = 0;
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>定位汽水迷你条：可见、在屏幕内、约 300~600 宽、40~100 高的 Chromium 窗口。</summary>
    private static bool TryFindBar(out Rect rect)
    {
        Rect found = default;
        var ok = false;

        // 存成局部变量，避免回调委托在枚举过程中被 GC 回收
        EnumWindowsProc callback = (hwnd, lparam) =>
        {
            if (!IsWindowVisible(hwnd))
            {
                return true;
            }

            if (!GetWindowRect(hwnd, out var r))
            {
                return true;
            }

            var w = r.Right - r.Left;
            var h = r.Bottom - r.Top;
            if (w is < 260 or > 640 || h is < 36 or > 110 || r.Left < 0 || r.Top < 0)
            {
                return true;
            }

            var cls = new StringBuilder(64);
            _ = GetClassName(hwnd, cls, cls.Capacity);
            if (!cls.ToString().StartsWith("Chrome_WidgetWin", StringComparison.Ordinal))
            {
                return true;
            }

            _ = GetWindowThreadProcessId(hwnd, out var pid);
            if (!IsSodaMusic(pid))
            {
                return true;
            }

            found = new Rect(r.Left, r.Top, w, h);
            ok = true;
            return false;
        };

        _ = EnumWindows(callback, IntPtr.Zero);

        if (!ok)
        {
            rect = default;
            return false;
        }

        // 迷你条是固定布局（388×64）：左边约 44px 是封面，右边约 185px 是
        // 上一首/播放/下一首/收藏四个按钮，中间才是"标题行 + 歌词行"。
        // 只截中间这块，OCR 就不会被封面和图标干扰。
        const int leftInset = 44;
        const int rightInset = 185;
        const int topInset = 2;
        const int bottomInset = 8;

        var x1 = found.X + leftInset;
        var y1 = found.Y + topInset;
        var x2 = found.X + found.W - rightInset;
        var y2 = found.Y + found.H - bottomInset;

        // 迷你条常常有一部分露在屏幕外（底边被任务栏/屏幕边界切掉），
        // 截到屏幕外会得到一条纯黑，OCR 会把它当噪声，所以先夹到虚拟屏幕内。
        var vx = GetSystemMetrics(76);
        var vy = GetSystemMetrics(77);
        var vw = GetSystemMetrics(78);
        var vh = GetSystemMetrics(79);

        x1 = Math.Max(x1, vx);
        y1 = Math.Max(y1, vy);
        x2 = Math.Min(x2, vx + vw);
        y2 = Math.Min(y2, vy + vh);

        rect = new Rect(x1, y1, x2 - x1, y2 - y1);
        return rect.W > 20 && rect.H > 8;
    }

    private static readonly Dictionary<uint, bool> SodaCache = new();

    private static bool IsSodaMusic(uint pid)
    {
        if (SodaCache.TryGetValue(pid, out var cached))
        {
            return cached;
        }

        var isSoda = false;
        try
        {
            isSoda = Process.GetProcessById((int)pid).ProcessName
                .Contains("SodaMusic", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // 进程刚好退出
        }

        SodaCache[pid] = isSoda;
        return isSoda;
    }

    /// <summary>
    /// 用 GDI 把屏幕上一块区域抓成 BGRA8 原始像素（不依赖 System.Drawing）。
    /// 返回原始像素而不是 SoftwareBitmap：这样可以按需喂给系统 OCR 或用 SkiaSharp 直接
    /// 构造位图给内置离线 OCR，省掉一次 PNG 编解码。
    /// </summary>
    private static Frame? Capture(Rect rect)
    {
        // 迷你条上的歌词只有十几像素高，直接 OCR 中文识别率很低；
        // 先用 HALFTONE 放大 2 倍再识别，字高超 20px 后准确率明显提升。
        const int scale = 2;

        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            return null;
        }

        var outW = rect.W * scale;
        var outH = rect.H * scale;
        var memDc = CreateCompatibleDC(screenDc);
        var hBitmap = CreateCompatibleBitmap(screenDc, outW, outH);
        var previous = SelectObject(memDc, hBitmap);

        try
        {
            _ = SetStretchBltMode(memDc, HALFTONE);
            _ = SetBrushOrgEx(memDc, 0, 0, IntPtr.Zero);

            if (!StretchBlt(
                    memDc, 0, 0, outW, outH, screenDc, rect.X, rect.Y, rect.W, rect.H, SRCCOPY))
            {
                return null;
            }

            var info = new BitmapInfoHeader
            {
                Size = 40,
                Width = outW,
                Height = -outH, // 负高度 = 自上而下，省一次翻转
                Planes = 1,
                BitCount = 32,
                Compression = 0,
            };

            var pixels = new byte[outW * outH * 4];
            var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                if (GetDIBits(memDc, hBitmap, 0, (uint)outH, handle.AddrOfPinnedObject(), ref info, 0) == 0)
                {
                    return null;
                }
            }
            finally
            {
                handle.Free();
            }

            return new Frame(pixels, outW, outH);
        }
        finally
        {
            _ = SelectObject(memDc, previous);
            _ = DeleteObject(hBitmap);
            _ = DeleteDC(memDc);
            _ = ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private readonly record struct Rect(int X, int Y, int W, int H);

    /// <summary>抓屏结果：BGRA8 原始像素 + 尺寸，供系统 OCR 和内置离线 OCR 共用。</summary>
    private readonly record struct Frame(byte[] Bgra, int Width, int Height);

    // ————————————————————————— Win32 —————————————————————————

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr param);

    [StructLayout(LayoutKind.Sequential)]
    private struct Win32Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    private const int SRCCOPY = 0x00CC0020;
    private const int HALFTONE = 4;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr param);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out Win32Rect rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int maxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern int SetStretchBltMode(IntPtr dc, int mode);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetBrushOrgEx(IntPtr dc, int x, int y, IntPtr previous);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StretchBlt(
        IntPtr dst, int x, int y, int width, int height,
        IntPtr src, int srcX, int srcY, int srcWidth, int srcHeight, int rop);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(
        IntPtr dc, IntPtr bitmap, uint start, uint lines, IntPtr bits, ref BitmapInfoHeader info, uint usage);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr dc);
}
