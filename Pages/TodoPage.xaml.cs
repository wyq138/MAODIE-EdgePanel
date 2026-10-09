using EdgePanel.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace EdgePanel.Pages;

public sealed partial class TodoPage : Page
{
    public TodoPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Rebuild();
    }

    private static (bool Done, string Text) Decode(string raw)
    {
        var parts = raw.Split('|', 2);
        return parts.Length == 2 && parts[0] == "1"
            ? (true, parts[1])
            : (false, parts.Length == 2 ? parts[1] : raw);
    }

    private void Rebuild()
    {
        ListPanel.Children.Clear();
        var todos = SettingsStore.Current.Todos;
        var done = 0;

        for (var i = 0; i < todos.Count; i++)
        {
            var (isDone, text) = Decode(todos[i]);
            if (isDone)
            {
                done++;
            }

            var index = i;
            var check = new CheckBox
            {
                IsChecked = isDone,
                MinWidth = 0,
                VerticalAlignment = VerticalAlignment.Center,
            };
            check.Checked += (_, _) => Toggle(index, true);
            check.Unchecked += (_, _) => Toggle(index, false);

            var label = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = isDone ? 0.5 : 1,
                TextDecorations = isDone
                    ? Windows.UI.Text.TextDecorations.Strikethrough
                    : Windows.UI.Text.TextDecorations.None,
            };

            var remove = new Button
            {
                Content = new FontIcon { FontSize = 12, Glyph = "\uE74D" },
                Padding = new Thickness(7, 3, 7, 3),
                VerticalAlignment = VerticalAlignment.Center,
            };
            remove.Click += (_, _) =>
            {
                SettingsStore.Current.Todos.RemoveAt(index);
                SettingsStore.Save();
                Rebuild();
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Grid.SetColumn(label, 1);
            Grid.SetColumn(remove, 2);
            grid.Children.Add(check);
            grid.Children.Add(label);
            grid.Children.Add(remove);

            ListPanel.Children.Add(new Border
            {
                Style = (Style)Application.Current.Resources["Card"],
                Padding = new Thickness(8, 6, 8, 6),
                Child = grid,
            });
        }

        SummaryText.Text = todos.Count == 0
            ? "还没有待办事项"
            : $"共 {todos.Count} 条 · 已完成 {done} 条";
    }

    private static void Toggle(int index, bool done)
    {
        var todos = SettingsStore.Current.Todos;
        if (index < 0 || index >= todos.Count)
        {
            return;
        }

        var (_, text) = Decode(todos[index]);
        todos[index] = $"{(done ? 1 : 0)}|{text}";
        SettingsStore.Save();
    }

    private void Add()
    {
        var text = InputBox.Text?.Trim() ?? "";
        if (text.Length == 0)
        {
            return;
        }

        SettingsStore.Current.Todos.Insert(0, "0|" + text);
        SettingsStore.Save();
        InputBox.Text = "";
        Rebuild();
    }

    private void Add_Click(object sender, RoutedEventArgs e) => Add();

    private void InputBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            Add();
            e.Handled = true;
        }
    }

    private void ClearDone_Click(object sender, RoutedEventArgs e)
    {
        var todos = SettingsStore.Current.Todos;
        todos.RemoveAll(t => Decode(t).Done);
        SettingsStore.Save();
        Rebuild();
    }
}