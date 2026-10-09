using EdgePanel.Controls;
using EdgePanel.Core;
using EdgePanel.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Streams;

namespace EdgePanel.Pages;

public sealed partial class MusicPage : Page
{
    private readonly List<MarqueeText> _lyricTexts = new();
    private int _currentLyricIndex = -2;
    private string _loadedFor = "";
    private string _coverFor = "";
    private bool _coverLoading;
    private bool _seeking;
    private bool _hooked;
    private double _durationSeconds;

    public MusicPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        // 拖动进度条即跳转播放位置
        ProgressSlider.PointerPressed += (_, _) => _seeking = true;
        ProgressSlider.PointerReleased += OnSeekReleased;
        ProgressSlider.PointerCaptureLost += (_, _) => _seeking = false;
    }

    private async void OnSeekReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_seeking)
        {
            return;
        }

        _seeking = false;
        await MediaService.Instance.SeekAsync(TimeSpan.FromSeconds(ProgressSlider.Value));
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_hooked)
        {
            _hooked = true;
            MediaService.Instance.Updated += OnMedia;
            LyricsService.Instance.Changed += OnLyricsChanged;
            SodaLyrics.Instance.Changed += OnSodaLyricChanged;
        }

        _ = MediaService.Instance.InitializeAsync();
        OnMedia(MediaService.Instance.Current);

        // 歌词/OCR 兜底歌词可能在页面加载前就已经就绪，Changed 事件不会再补发，
        // 这里主动构建一次，否则会出现"胶囊有歌词、主界面空白"。
        BuildLyrics();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        MediaService.Instance.Updated -= OnMedia;
        LyricsService.Instance.Changed -= OnLyricsChanged;
        SodaLyrics.Instance.Changed -= OnSodaLyricChanged;
        _hooked = false;
    }

    // ————————————————————————— 媒体 —————————————————————————

    private void OnMedia(MediaInfo info)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (!info.HasSession)
            {
                TitleMarquee.Text = "无播放中的媒体";
                ArtistText.Text = "打开任意播放器播放音乐即可联动";
                PlayButton.Content = new FontIcon { FontSize = 18, Glyph = "\uE768" };
                return;
            }

            TitleMarquee.Text = $"{info.Title} - {info.Artist}";

            var album = string.IsNullOrWhiteSpace(info.Album) ? "" : $" · {info.Album}";
            ArtistText.Text = $"{info.Artist}{album}";

            PlayButton.Content = new FontIcon
            {
                FontSize = 18,
                Glyph = info.IsPlaying ? "\uE769" : "\uE768",
            };

            _durationSeconds = Math.Max(1, info.Duration.TotalSeconds);
            DurationText.Text = Format(info.Duration);

            if (!_seeking)
            {
                ProgressSlider.Maximum = _durationSeconds;
                ProgressSlider.Value = Math.Clamp(info.Position.TotalSeconds, 0, _durationSeconds);
                PositionText.Text = Format(info.Position);
            }

            // 换歌先把上一首的封面清掉，再按曲目重新加载。
            // 原来用 `Source ??=` 只在首次赋值，导致切到下一首封面一直是上一首的。
            // SMTC 首发常常还没有缩略图，所以没加载成功时要持续重试。
            if (info.Title != _coverFor)
            {
                _coverFor = info.Title;
                _coverLoading = false;
                CoverImage.Source = null;
            }

            if (!_coverLoading && CoverImage.Source is null && info.Thumbnail is { Length: > 0 } bytes)
            {
                _coverLoading = true;
                try
                {
                    CoverImage.Source = await CreateBitmapAsync(bytes);
                }
                finally
                {
                    _coverLoading = false;
                }
            }

            // 曲目变化时重新匹配歌词
            if (info.Title != _loadedFor && LyricsService.Instance.MatchedTitle != info.Title)
            {
                _loadedFor = info.Title;
                _ = LyricsService.Instance.LoadForAsync(info.Title, info.Artist);
            }

            UpdateLyricHighlight(LyricsService.Instance.IndexAt(info.Position));
        });
    }

    private static string Format(TimeSpan time)
        => time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"mm\:ss");

    private static async Task<BitmapImage?> CreateBitmapAsync(byte[] bytes)
    {
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            var writer = new DataWriter(stream);
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
            stream.Seek(0);

            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private async void Play_Click(object sender, RoutedEventArgs e) => await MediaService.Instance.PlayPauseAsync();

    private async void Next_Click(object sender, RoutedEventArgs e) => await MediaService.Instance.NextAsync();

    private async void Prev_Click(object sender, RoutedEventArgs e) => await MediaService.Instance.PreviousAsync();

    private void ProgressSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_seeking || _durationSeconds <= 0)
        {
            return;
        }

        PositionText.Text = Format(TimeSpan.FromSeconds(e.NewValue));
    }

    // ————————————————————————— 歌词 —————————————————————————

    private void OnLyricsChanged()
    {
        DispatcherQueue.TryEnqueue(BuildLyrics);
    }

    /// <summary>汽水迷你条 OCR 兜底歌词变化；只有没抓到正常歌词时才需要同步到这里。</summary>
    private void OnSodaLyricChanged()
    {
        if (!LyricsService.Instance.HasLyrics)
        {
            DispatcherQueue.TryEnqueue(BuildLyrics);
        }
    }

    private void BuildLyrics()
    {
        LyricPanel.Children.Clear();
        _lyricTexts.Clear();
        _currentLyricIndex = -2;

        var lines = LyricsService.Instance.Lines;

        if (lines.Count == 0)
        {
            // 兜底：胶囊（收起态）显示的正是汽水迷你条 OCR 出来的这一行，
            // 主界面之前只看 LyricsService，所以出现"胶囊有歌词、这里没有"。
            var ocr = SodaLyrics.Instance.CurrentLine;
            if (!string.IsNullOrWhiteSpace(ocr))
            {
                AddLyricLine(ocr);
                LyricEmpty.Visibility = Visibility.Collapsed;
                UpdateLyricHighlight(0);
                return;
            }

            LyricEmpty.Visibility = Visibility.Visible;
            return;
        }

        LyricEmpty.Visibility = Visibility.Collapsed;

        foreach (var line in lines)
        {
            AddLyricLine(string.IsNullOrWhiteSpace(line.Text) ? "♪" : line.Text);
        }

        UpdateLyricHighlight(LyricsService.Instance.IndexAt(MediaService.Instance.Current.Position));
    }

    /// <summary>一行歌词用跑马灯呈现：超出宽度自动横向滚动，短句静止显示。</summary>
    private void AddLyricLine(string text)
    {
        var block = new MarqueeText
        {
            Text = text,
            FontSize = 14,
            Speed = 45,
            Opacity = 0.45,
        };

        LyricPanel.Children.Add(block);
        _lyricTexts.Add(block);
    }

    private void UpdateLyricHighlight(int index)
    {
        if (index == _currentLyricIndex)
        {
            return;
        }

        if (_currentLyricIndex >= 0 && _currentLyricIndex < _lyricTexts.Count)
        {
            var old = _lyricTexts[_currentLyricIndex];
            old.Opacity = 0.45;
            old.FontWeight = Microsoft.UI.Text.FontWeights.Normal;
            // 之前只重置了透明度与字重，蓝色一直留着，旧行会越积越多
            old.ClearValue(MarqueeText.ForegroundProperty);
        }

        if (index >= 0 && index < _lyricTexts.Count)
        {
            var current = _lyricTexts[index];
            current.Opacity = 1;
            current.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            current.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 130, 200, 255));

            try
            {
                var offset = current.TransformToVisual(LyricPanel).TransformPoint(new Point(0, 0)).Y;
                LyricScroll.ChangeView(null, Math.Max(0, offset - LyricScroll.ViewportHeight / 2 + current.ActualHeight / 2), null, true);
            }
            catch
            {
                // 布局未就绪时忽略
            }
        }

        _currentLyricIndex = index;
    }

    private void ReloadLyrics_Click(object sender, RoutedEventArgs e)
    {
        var info = MediaService.Instance.Current;
        _ = LyricsService.Instance.LoadForAsync(info.Title, info.Artist);
    }

    private async void PickFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.MusicLibrary;
            picker.FileTypeFilter.Add("*");

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.Panel!);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            StorageFolder? folder = await picker.PickSingleFolderAsync();
            if (folder is null)
            {
                return;
            }

            var s = SettingsStore.Current;
            s.LyricsFolder = string.IsNullOrWhiteSpace(s.LyricsFolder)
                ? folder.Path
                : s.LyricsFolder + ";" + folder.Path;
            SettingsStore.Save();

            var info = MediaService.Instance.Current;
            _ = LyricsService.Instance.LoadForAsync(info.Title, info.Artist);
        }
        catch (Exception ex)
        {
            App.Log("选择歌词目录失败：" + ex.Message);
        }
    }
}