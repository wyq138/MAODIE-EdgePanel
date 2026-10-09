using EdgePanel.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EdgePanel.Pages;

public sealed partial class ClipboardPage : Page
{
    private bool _hooked;

    public ClipboardPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ClipboardService.Instance.Start();

        if (!_hooked)
        {
            _hooked = true;
            ClipboardService.Instance.Changed += Rebuild;
        }

        Rebuild();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ClipboardService.Instance.Changed -= Rebuild;
        _hooked = false;
    }

    private void Rebuild()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            ListPanel.Children.Clear();
            var history = ClipboardService.Instance.History;
            EmptyHint.Visibility = history.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            foreach (var item in history)
            {
                ListPanel.Children.Add(BuildRow(item));
            }
        });
    }

    private FrameworkElement BuildRow(ClipboardItem item)
    {
        var text = new TextBlock
        {
            Text = item.Text,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 3,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var time = new TextBlock
        {
            Text = item.Time.ToString("MM-dd HH:mm:ss"),
            FontSize = 11,
            Opacity = 0.6,
            Margin = new Thickness(0, 4, 0, 0),
        };

        var copyButton = new Button
        {
            Content = new FontIcon { FontSize = 12, Glyph = "\uE8C8" },
            Padding = new Thickness(7, 3, 7, 3),
            Margin = new Thickness(0, 0, 6, 0),
        };
        ToolTipService.SetToolTip(copyButton, "复制");
        copyButton.Click += (_, _) => ClipboardService.Instance.Copy(item.Text);

        var removeButton = new Button
        {
            Content = new FontIcon { FontSize = 12, Glyph = "\uE74D" },
            Padding = new Thickness(7, 3, 7, 3),
        };
        ToolTipService.SetToolTip(removeButton, "删除");
        removeButton.Click += (_, _) => ClipboardService.Instance.Remove(item);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(8, 0, 0, 0),
        };
        actions.Children.Add(copyButton);
        actions.Children.Add(removeButton);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var content = new StackPanel();
        content.Children.Add(text);
        content.Children.Add(time);

        Grid.SetColumn(content, 0);
        Grid.SetColumn(actions, 1);
        grid.Children.Add(content);
        grid.Children.Add(actions);

        return new Border
        {
            Style = (Style)Application.Current.Resources["Card"],
            Child = grid,
        };
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => ClipboardService.Instance.Clear();
}