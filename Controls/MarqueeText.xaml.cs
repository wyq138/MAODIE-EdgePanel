using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace EdgePanel.Controls;

/// <summary>
/// 跑马灯文本：文本超出可视宽度时横向无限滚动，未超出时静止显示。
/// 通过两个相邻副本实现无缝衔接。
/// </summary>
public sealed partial class MarqueeText : UserControl
{
    private const double Gap = 64;

    private Storyboard? _storyboard;
    private double _textWidth;
    private bool _ready;
    private string _lastText = "";

    public MarqueeText()
    {
        InitializeComponent();

        // 字号/颜色/字重设在控件上时，同步给内部两个文本副本
        // （UserControl 本身的这些属性不会自动继承到 XAML 里的子 TextBlock）
        RegisterPropertyChangedCallback(FontSizeProperty, (_, _) => ApplyTextStyle());
        RegisterPropertyChangedCallback(ForegroundProperty, (_, _) => ApplyTextStyle());
        RegisterPropertyChangedCallback(FontWeightProperty, (_, _) => ApplyTextStyle());
        ApplyTextStyle();

        Loaded += (_, _) =>
        {
            _ready = true;
            // 进入可视化树后 Foreground 才是继承后的最终值，这里再同步一次
            ApplyTextStyle();
            Restart();
        };
        Unloaded += (_, _) => Stop();
    }

    private void ApplyTextStyle()
    {
        if (Primary is null || Secondary is null)
        {
            return;
        }

        Primary.FontSize = FontSize;
        Secondary.FontSize = FontSize;
        Primary.FontWeight = FontWeight;
        Secondary.FontWeight = FontWeight;

        // Foreground 被 ClearValue 复位时值可能是 null，此时交回默认主题色
        Primary.Foreground = Foreground;
        Secondary.Foreground = Foreground;
    }

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(MarqueeText),
        new PropertyMetadata(string.Empty, OnTextChanged));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public static readonly DependencyProperty SpeedProperty = DependencyProperty.Register(
        nameof(Speed), typeof(double), typeof(MarqueeText),
        new PropertyMetadata(70d, OnSpeedChanged));

    /// <summary>滚动速度，像素/秒。</summary>
    public double Speed
    {
        get => (double)GetValue(SpeedProperty);
        set => SetValue(SpeedProperty, value);
    }

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((MarqueeText)d).Restart();

    private static void OnSpeedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((MarqueeText)d).Restart(true);

    private void Host_SizeChanged(object sender, SizeChangedEventArgs e) => Restart(true);

    private void Stop()
    {
        _storyboard?.Stop();
        _storyboard = null;
    }

    private void Restart() => Restart(false);

    private void Restart(bool force)
    {
        if (!_ready)
        {
            return;
        }

        var text = Text ?? string.Empty;

        // 歌词每 200ms 会被重新赋一次同样的值，若每次都重建动画，
        // 滚动位置会被不断重置回开头，看起来就是"根本不滚"。内容没变就不打断。
        if (!force && text == _lastText && _storyboard is not null)
        {
            return;
        }

        _lastText = text;
        Stop();

        Primary.Text = text;
        Secondary.Text = text;

        // 裁剪到控件可视区域，避免滚动时文字溢出到面板外
        Host.Clip = new RectangleGeometry
        {
            Rect = new Windows.Foundation.Rect(0, 0, Math.Max(0, Host.ActualWidth), Math.Max(0, Host.ActualHeight)),
        };

        Primary.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        _textWidth = Primary.DesiredSize.Width;

        var view = Host.ActualWidth;
        if (view <= 0 || _textWidth <= view + 1)
        {
            // 文字没超出：静止显示，不需要副本
            Secondary.Visibility = Visibility.Collapsed;
            Shift.X = 0;
            return;
        }

        Secondary.Visibility = Visibility.Visible;
        var distance = _textWidth + Gap;
        var speed = Math.Max(10, Speed);
        var duration = TimeSpan.FromSeconds(distance / speed);

        var anim = new DoubleAnimation
        {
            From = 0,
            To = -distance,
            Duration = new Duration(duration),
            RepeatBehavior = RepeatBehavior.Forever,
        };

        Storyboard.SetTarget(anim, Shift);
        Storyboard.SetTargetProperty(anim, "(TranslateTransform.X)");

        _storyboard = new Storyboard();
        _storyboard.Children.Add(anim);
        _storyboard.Begin();
    }
}