using Windows.Media.Control;
using Windows.Storage.Streams;
using WindowsMediaController;

namespace EdgePanel.Services;

/// <summary>当前媒体会话的快照。</summary>
public sealed class MediaInfo
{
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public string SourceApp { get; set; } = "";
    public bool IsPlaying { get; set; }
    public bool HasSession { get; set; }
    public TimeSpan Position { get; set; }
    public TimeSpan Duration { get; set; }
    public byte[]? Thumbnail { get; set; }
}

/// <summary>
/// 媒体服务：基于 Dubya.WindowsMediaController.Jayfunc（SMTC 封装）。
/// 可读取任意播放器（汽水音乐 / 网易云 / QQ音乐 / Spotify / 浏览器…）的曲目信息并远程控制。
/// </summary>
public sealed class MediaService : IDisposable
{
    public static MediaService Instance { get; } = new();

    private readonly MediaManager _manager = new();

    private MediaManager.MediaSession? _session;
    private Timer? _timer;
    private bool _initialized;
    private int _refreshing;
    private string _loggedTitle = "";
    private string _thumbTitle = "";
    private byte[]? _thumbBytes;
    private byte[]? _prevThumbBytes;
    private DateTime _thumbStaleSince;

    // SMTC 的播放进度只在状态变化时更新，两次之间是"死"的，直接用它会让进度条一跳一跳。
    // 这里记下最后一次进度与它到达的时刻，中间用本地时钟外推。
    private TimeSpan _lastPosition;
    private DateTime _lastPositionAt;

    /// <summary>媒体信息刷新（可能来自后台线程，UI 侧自行 DispatcherQueue 切回）。</summary>
    public event Action<MediaInfo>? Updated;

    public MediaInfo Current { get; private set; } = new();

    private MediaService()
    {
    }

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        try
        {
            // 会话开/关、焦点切换都会触发重算，第一时间捕获"汽水音乐"这类播放器
            _manager.OnFocusedSessionChanged += OnSessionChanged;
            _manager.OnAnySessionOpened += OnSessionChanged;
            _manager.OnAnySessionClosed += OnSessionChanged;

            await _manager.StartAsync();

            _session = PickSession();
            LogSession();

            // 100ms 轮询：播放/暂停/切歌与歌词进度更跟手，主界面/胶囊延迟更低
            _timer = new Timer(_ => _ = RefreshAsync(), null, 0, 100);
            _initialized = true;
            App.Log("SMTC 媒体服务已启动（Dubya.WindowsMediaController.Jayfunc）");
        }
        catch (Exception ex)
        {
            App.Log("SMTC 初始化失败：" + ex.Message);
        }
    }

    /// <summary>媒体会话发生变化（新开/关闭/切焦点）→ 立即重算当前会话。</summary>
    private void OnSessionChanged(MediaManager.MediaSession session) => _ = RefreshAsync();

    /// <summary>
    /// 挑当前该显示的会话：优先"正在播放"的会话，其次系统焦点会话。
    /// 只认焦点会话会出现"汽水在放歌但焦点在别的 App 上就抓不到"的问题。
    /// </summary>
    private MediaManager.MediaSession? PickSession()
    {
        try
        {
            var focused = _manager.GetFocusedSession();

            if (focused is not null && IsPlaying(focused))
            {
                return focused;
            }

            var playing = _manager.CurrentMediaSessions.Values.FirstOrDefault(IsPlaying);
            return playing ?? focused;
        }
        catch
        {
            return _session;
        }
    }

    private static bool IsPlaying(MediaManager.MediaSession session)
    {
        try
        {
            return session.ControlSession?.GetPlaybackInfo()?.PlaybackStatus ==
                   GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        }
        catch
        {
            return false;
        }
    }

    private void LogSession()
    {
        if (_session is not null)
        {
            App.Log($"已连接媒体会话：{_session.Id}");
        }
        else
        {
            App.Log("当前没有正在播放的媒体会话");
        }
    }

    private async Task RefreshAsync()
    {
        // 会话切换事件很密集，20ms 一轮的定时器也可能把上一轮挤在 await 里，
        // 不加锁会出现旧会话的结果后写入、把新歌信息覆盖回去。
        if (Interlocked.Exchange(ref _refreshing, 1) == 1)
        {
            return;
        }

        try
        {
            var session = PickSession();
            if (session is null)
            {
                _session = null;
                Publish(new MediaInfo { HasSession = false });
                return;
            }

            if (!ReferenceEquals(session, _session))
            {
                _session = session;
                _lastPosition = TimeSpan.Zero;
                _lastPositionAt = default;
                LogSession();
            }

            var control = session.ControlSession;
            if (control is null)
            {
                Publish(new MediaInfo { HasSession = false });
                return;
            }

            var props = await control.TryGetMediaPropertiesAsync();
            var playback = control.GetPlaybackInfo();
            var timeline = control.GetTimelineProperties();

            var isPlaying = playback?.PlaybackStatus ==
                            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            var duration = timeline.EndTime - timeline.StartTime;

            var title = string.IsNullOrWhiteSpace(props.Title) ? "未知曲目" : props.Title;

            var snapshot = new MediaInfo
            {
                HasSession = true,
                Title = title,
                Artist = props.Artist ?? "",
                Album = props.AlbumTitle ?? "",
                SourceApp = session.Id ?? "",
                IsPlaying = isPlaying,
                Position = Extrapolate(timeline.Position, duration, isPlaying),
                Duration = duration,
                Thumbnail = await ResolveThumbnailAsync(title, props),
            };

            // 曲目变化时留一条日志，便于确认"抓本机正在播放的歌曲"确实生效
            if (!string.IsNullOrWhiteSpace(snapshot.Title) && snapshot.Title != _loggedTitle)
            {
                _loggedTitle = snapshot.Title;
                App.Log($"正在播放：{snapshot.Artist} - {snapshot.Title}（{snapshot.SourceApp}）");
            }

            Publish(snapshot);
        }
        catch (Exception ex)
        {
            App.Log("刷新媒体信息失败：" + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    private void Publish(MediaInfo info)
    {
        Current = info;
        Updated?.Invoke(info);
    }

    /// <summary>
    /// SMTC 的时间轴只在播放状态变化时才刷新，两次刷新之间 Position 是静止的，
    /// 直接显示会让进度条先卡住再跳一下。这里以播放器给出的位置为锚点，
    /// 用本地时钟把中间的空档补上；一旦锚点变了（切歌/拖动/暂停）就重新对齐。
    /// </summary>
    private TimeSpan Extrapolate(TimeSpan raw, TimeSpan duration, bool isPlaying)
    {
        var now = DateTime.UtcNow;

        if (!isPlaying || _lastPositionAt == default || raw != _lastPosition)
        {
            _lastPosition = raw;
            _lastPositionAt = now;
        }

        var value = isPlaying ? _lastPosition + (now - _lastPositionAt) : raw;

        if (value < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return duration > TimeSpan.Zero && value > duration ? duration : value;
    }

    /// <summary>
    /// 读取封面并确保与当前曲目一致。SMTC 切歌后前几轮可能仍返回上一首的缩略图，
    /// 这里在曲目变化时清空并持续重试，直到拿到与旧封面不同的新字节。
    /// </summary>
    private async Task<byte[]?> ResolveThumbnailAsync(string title, GlobalSystemMediaTransportControlsSessionMediaProperties props)
    {
        if (title != _thumbTitle)
        {
            _prevThumbBytes = _thumbBytes;
            _thumbBytes = null;
            _thumbTitle = title;
            _thumbStaleSince = default;
        }

        if (_thumbBytes is not null)
        {
            return _thumbBytes;
        }

        var read = await ReadThumbnailAsync(props);
        if (read is null or { Length: 0 })
        {
            // 播放器还没给出缩略图（props.Thumbnail 为 null 时 ReadThumbnailAsync 几乎零开销），下一轮继续
            return null;
        }

        if (!BytesEqual(read, _prevThumbBytes))
        {
            _thumbBytes = read;
            return read;
        }

        // 读到的还是上一首的图：SMTC 尚未更新。若长时间仍如此则不再死等，直接采用。
        if (_thumbStaleSince == default)
        {
            _thumbStaleSince = DateTime.UtcNow;
        }
        else if (DateTime.UtcNow - _thumbStaleSince > TimeSpan.FromSeconds(3))
        {
            _thumbBytes = read;
            return read;
        }

        return null;
    }

    private static bool BytesEqual(byte[]? a, byte[]? b)
    {
        if (a is null || b is null || a.Length != b.Length)
        {
            return false;
        }

        for (var i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
            {
                return false;
            }
        }

        return true;
    }

    private static async Task<byte[]?> ReadThumbnailAsync(GlobalSystemMediaTransportControlsSessionMediaProperties props)
    {
        try
        {
            if (props.Thumbnail is null)
            {
                return null;
            }

            using IRandomAccessStreamWithContentType stream = await props.Thumbnail.OpenReadAsync();
            var bytes = new byte[stream.Size];
            using var reader = new DataReader(stream);
            await reader.LoadAsync((uint)stream.Size);
            reader.ReadBytes(bytes);
            return bytes;
        }
        catch
        {
            return null;
        }
    }

    public async Task PlayPauseAsync()
    {
        try
        {
            if (_session is null)
            {
                return;
            }

            if (Current.IsPlaying)
            {
                await _session.ControlSession.TryPauseAsync();
            }
            else
            {
                await _session.ControlSession.TryPlayAsync();
            }
        }
        catch (Exception ex)
        {
            App.Log("播放/暂停失败：" + ex.Message);
        }
    }

    public async Task NextAsync()
    {
        try
        {
            await _session?.ControlSession.TrySkipNextAsync();
        }
        catch (Exception ex)
        {
            App.Log("下一首失败：" + ex.Message);
        }
    }

    public async Task PreviousAsync()
    {
        try
        {
            await _session?.ControlSession.TrySkipPreviousAsync();
        }
        catch (Exception ex)
        {
            App.Log("上一首失败：" + ex.Message);
        }
    }

    public async Task SeekAsync(TimeSpan position)
    {
        try
        {
            await _session?.ControlSession.TryChangePlaybackPositionAsync(position.Ticks);
        }
        catch (Exception ex)
        {
            App.Log("跳转播放位置失败：" + ex.Message);
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
        _manager.Dispose();
    }
}
