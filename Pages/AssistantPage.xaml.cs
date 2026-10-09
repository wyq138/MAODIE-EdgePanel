using EdgePanel.Core;
using EdgePanel.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI;

namespace EdgePanel.Pages;

/// <summary>内置电脑助手：在面板里对话，助手通过工具直接操作本机。</summary>
public sealed partial class AssistantPage : Page
{
    private static readonly Color UserColor = Color.FromArgb(0xFF, 0x2F, 0x7C, 0xF6);
    private static readonly Color AssistantColor = Color.FromArgb(0x22, 0x80, 0x80, 0x80);
    private static readonly Color ErrorColor = Color.FromArgb(0x33, 0xFF, 0x6B, 0x6B);

    private bool _busy;

    public AssistantPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Reload();
    }

    private void Reload()
    {
        RefreshModel();
        ChatPanel.Children.Clear();
        foreach (var entry in AssistantService.Instance.History)
        {
            Render(entry);
        }

        if (AssistantService.Instance.History.Count == 0)
        {
            AddBubble("assistant", "你好，我可以直接操作这台电脑：打开程序、点击输入、执行命令、读写文件、看屏幕等你。先到「设置 → 电脑助手」配置模型即可开始。", isError: false);
        }

        ScrollToEnd();
    }

    private void RefreshModel()
    {
        var s = SettingsStore.Current;
        ModelHint.Text = AssistantService.IsConfigured
            ? $"模型：{s.AssistantModel}  ·  {s.AssistantBaseUrl}"
            : "尚未配置模型：请到「设置 → 电脑助手」填写 Base URL、API Key 和模型名。";
    }

    private void Input_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
        {
            return;
        }

        // Enter 发送，Shift+Enter 换行
        var shift = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
                     & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
        if (shift)
        {
            return;
        }

        e.Handled = true;
        _ = SendAsync();
    }

    private void Send_Click(object sender, RoutedEventArgs e) => _ = SendAsync();

    private async Task SendAsync()
    {
        if (_busy)
        {
            return;
        }

        var text = InputBox.Text?.Trim() ?? "";
        if (text.Length == 0)
        {
            return;
        }

        if (!AssistantService.IsConfigured)
        {
            StatusText.Text = "尚未配置模型，请先到「设置 → 电脑助手」填写。";
            return;
        }

        _busy = true;
        SendButton.IsEnabled = false;
        InputBox.Text = "";

        var userEntry = new ChatEntry { Role = "user", Text = text };
        AssistantService.Instance.History.Add(userEntry);
        Render(userEntry);
        ScrollToEnd();

        var progress = new Progress<string>(message => StatusText.Text = message);

        try
        {
            var reply = await AssistantService.Instance.AskAsync(text, progress, CancellationToken.None);

            var entry = new ChatEntry
            {
                Role = reply.Text.StartsWith("模型接口返回", StringComparison.Ordinal) ||
                       reply.Text.StartsWith("请求模型失败", StringComparison.Ordinal) ||
                       reply.Text.StartsWith("解析模型返回失败", StringComparison.Ordinal) ||
                       reply.Text.StartsWith("尚未配置", StringComparison.Ordinal)
                    ? "error"
                    : "assistant",
                Text = reply.Text,
                IsError = reply.Text.StartsWith("模型接口返回", StringComparison.Ordinal) ||
                          reply.Text.StartsWith("请求模型失败", StringComparison.Ordinal),
                Steps = reply.Steps,
            };

            AssistantService.Instance.History.Add(entry);
            Render(entry);
            StatusText.Text = "";
        }
        catch (Exception ex)
        {
            App.Log("助手请求失败：" + ex.Message);
            var entry = new ChatEntry { Role = "error", Text = "请求失败：" + ex.Message, IsError = true };
            AssistantService.Instance.History.Add(entry);
            Render(entry);
            StatusText.Text = "";
        }
        finally
        {
            _busy = false;
            SendButton.IsEnabled = true;
            ScrollToEnd();
        }
    }

    private void Render(ChatEntry entry)
    {
        if (entry.Steps.Count > 0)
        {
            ChatPanel.Children.Add(new TextBlock
            {
                Text = "已执行：" + string.Join("、", entry.Steps),
                FontSize = 11,
                Opacity = 0.6,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4, 0, 4, 0),
            });
        }

        AddBubble(entry.Role, entry.Text, entry.IsError);
    }

    private void AddBubble(string role, string text, bool isError)
    {
        var isUser = role == "user";

        var block = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            FontSize = 13,
        };

        var border = new Border
        {
            Padding = new Thickness(10, 8, 10, 8),
            CornerRadius = new CornerRadius(10),
            MaxWidth = 300,
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Background = new SolidColorBrush(isUser ? UserColor : isError ? ErrorColor : AssistantColor),
            Child = block,
        };

        if (isUser)
        {
            block.Foreground = new SolidColorBrush(Microsoft.UI.Colors.White);
        }

        ChatPanel.Children.Add(border);
        ScrollToEnd();
    }

    private void ScrollToEnd()
    {
        DispatcherQueue.TryEnqueue(() => ChatScroll.ChangeView(null, ChatScroll.ScrollableHeight, null));
    }
}
