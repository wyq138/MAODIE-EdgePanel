using EdgePanel.Core;
using EdgePanel.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EdgePanel.Pages;

public sealed partial class NotesPage : Page
{
    private bool _ready;

    public NotesPage()
    {
        InitializeComponent();

        Loaded += (_, _) =>
        {
            Editor.Text = SettingsStore.Current.Notes ?? "";
            _ready = true;
            StatusText.Text = "已载入";
        };

        // 离开页面时落盘，避免切页丢失
        Unloaded += (_, _) => Save();
    }

    private void Editor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        StatusText.Text = "编辑中…";
    }

    private void Save_Click(object sender, RoutedEventArgs e) => Save();

    private void Save()
    {
        if (!_ready)
        {
            return;
        }

        SettingsStore.Current.Notes = Editor.Text ?? "";
        SettingsStore.Save();
        StatusText.Text = $"已保存 · {DateTime.Now:HH:mm:ss}";
    }

    private void InsertTime_Click(object sender, RoutedEventArgs e)
    {
        var stamp = $"{DateTime.Now:yyyy-MM-dd HH:mm} ";
        var caret = Editor.SelectionStart;
        var text = Editor.Text ?? "";
        Editor.Text = text.Insert(Math.Clamp(caret, 0, text.Length), stamp);
        Editor.SelectionStart = Math.Clamp(caret, 0, Editor.Text.Length) + stamp.Length;
        Editor.Focus(FocusState.Programmatic);
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        Editor.Text = "";
        Save();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => SystemActions.OpenPath(App.DataRoot);
}