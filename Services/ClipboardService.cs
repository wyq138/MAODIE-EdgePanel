using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;

namespace EdgePanel.Services;

public sealed class ClipboardItem
{
    public string Text { get; init; } = "";
    public DateTime Time { get; init; } = DateTime.Now;
}

/// <summary>剪贴板历史：监听系统剪贴板变化，保留最近 N 条文本记录。</summary>
public sealed class ClipboardService
{
    public static ClipboardService Instance { get; } = new();

    private const int MaxItems = 60;
    private DispatcherQueue? _dispatcher;
    private bool _started;
    private string _last = "";

    public List<ClipboardItem> History { get; } = new();

    public event Action? Changed;

    private ClipboardService()
    {
    }

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _dispatcher = DispatcherQueue.GetForCurrentThread();
        Clipboard.ContentChanged += OnContentChanged;
        _started = true;
        App.Log("剪贴板监听已启动");
        _ = CaptureAsync();
    }

    private void OnContentChanged(object? sender, object e)
    {
        if (_dispatcher is null || _dispatcher.HasThreadAccess)
        {
            _ = CaptureAsync();
        }
        else
        {
            _dispatcher.TryEnqueue(() => _ = CaptureAsync());
        }
    }

    private async Task CaptureAsync()
    {
        try
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Text))
            {
                return;
            }

            var text = await content.GetTextAsync();
            if (string.IsNullOrWhiteSpace(text) || text == _last)
            {
                return;
            }

            _last = text;
            History.Insert(0, new ClipboardItem { Text = text });

            while (History.Count > MaxItems)
            {
                History.RemoveAt(History.Count - 1);
            }

            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            App.Log("读取剪贴板失败：" + ex.Message);
        }
    }

    public void Copy(string text)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
            _last = text;
        }
        catch (Exception ex)
        {
            App.Log("写入剪贴板失败：" + ex.Message);
        }
    }

    public void Clear()
    {
        History.Clear();
        Changed?.Invoke();
    }

    public void Remove(ClipboardItem item)
    {
        History.Remove(item);
        Changed?.Invoke();
    }
}