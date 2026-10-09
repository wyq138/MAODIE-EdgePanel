using EdgePanel.Core;
using EdgePanel.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EdgePanel.Pages;

public sealed partial class VaultPage : Page
{
    private bool _ready;

    public VaultPage()
    {
        InitializeComponent();

        Loaded += (_, _) =>
        {
            _ready = true;
            RefreshState();
        };

        VaultService.Instance.StateChanged += OnVaultStateChanged;
        Unloaded += (_, _) => VaultService.Instance.StateChanged -= OnVaultStateChanged;
    }

    private void OnVaultStateChanged()
    {
        // VaultService 的事件可能在 UI 线程外触发，统一调度回 UI 线程
        DispatcherQueue?.TryEnqueue(RefreshState);
    }

    /// <summary>按保险箱当前状态（没有 / 锁定 / 已解锁）切换界面。</summary>
    private void RefreshState()
    {
        if (!_ready)
        {
            return;
        }

        var vault = VaultService.Instance;
        var noVault = !vault.HasVault;
        NoVaultPanel.Visibility = noVault ? Visibility.Visible : Visibility.Collapsed;
        LockedPanel.Visibility = !noVault && !vault.IsUnlocked ? Visibility.Visible : Visibility.Collapsed;
        UnlockedPanel.Visibility = vault.IsUnlocked ? Visibility.Visible : Visibility.Collapsed;

        if (vault.IsUnlocked)
        {
            CountText.Text = $"共 {vault.Count} 条凭据";
            RenderItems();
        }

        // 每次状态刷新后清空各输入框，避免残留
        CreatePassBox.Password = "";
        CreatePass2Box.Password = "";
        UnlockPassBox.Password = "";
        ChangeOldBox.Password = "";
        ChangeNewBox.Password = "";
        ChangeNew2Box.Password = "";
        NoVaultErrorText.Text = "";
        NoVaultErrorText.Visibility = Visibility.Collapsed;
        LockedErrorText.Text = "";
        LockedErrorText.Visibility = Visibility.Collapsed;
        AddErrorText.Text = "";
        AddErrorText.Visibility = Visibility.Collapsed;
        ChangeErrorText.Text = "";
    }

    // ————————————————————————— 创建 / 解锁 / 锁定 —————————————————————————

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        var pass = CreatePassBox.Password;
        var pass2 = CreatePass2Box.Password;

        try
        {
            if (pass != pass2)
            {
                ShowNoVaultError("两次输入的主密码不一致");
                return;
            }

            VaultService.Instance.CreateVault(pass);
        }
        catch (Exception ex)
        {
            ShowNoVaultError(ex.Message);
        }
    }

    private void Unlock_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            VaultService.Instance.Unlock(UnlockPassBox.Password);
        }
        catch (Exception ex)
        {
            LockedErrorText.Text = ex.Message;
            LockedErrorText.Visibility = Visibility.Visible;
        }
    }

    private void Lock_Click(object sender, RoutedEventArgs e)
    {
        VaultService.Instance.Lock();
    }

    /// <summary>忘记主密码：主密码不可找回，只能二次确认后重置（清空全部凭据）。</summary>
    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (!string.Equals(ResetConfirmBox.Text?.Trim(), "重置", StringComparison.Ordinal))
        {
            LockedErrorText.Text = "请先在上方输入「重置」二字确认";
            LockedErrorText.Visibility = Visibility.Visible;
            return;
        }

        try
        {
            VaultService.Instance.ResetVault();
            ResetConfirmBox.Text = "";
            App.Log("保险箱已重置（忘记主密码）");
        }
        catch (Exception ex)
        {
            LockedErrorText.Text = ex.Message;
            LockedErrorText.Visibility = Visibility.Visible;
        }
    }

    private void ShowNoVaultError(string message)
    {
        NoVaultErrorText.Text = message;
        NoVaultErrorText.Visibility = Visibility.Visible;
    }

    // ————————————————————————— 添加 / 删除凭据 —————————————————————————

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            VaultService.Instance.AddItem(
                AddAppBox.Text ?? "",
                AddAccountBox.Text ?? "",
                AddPassBox.Password,
                AddNoteBox.Text ?? "");

            AddAppBox.Text = "";
            AddAccountBox.Text = "";
            AddPassBox.Password = "";
            AddNoteBox.Text = "";
            AddErrorText.Visibility = Visibility.Collapsed;
            RefreshState();
        }
        catch (Exception ex)
        {
            AddErrorText.Text = ex.Message;
            AddErrorText.Visibility = Visibility.Visible;
        }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id })
        {
            return;
        }

        try
        {
            VaultService.Instance.DeleteItem(id);
            RefreshState();
        }
        catch (Exception ex)
        {
            App.Log("删除保险箱条目失败：" + ex.Message);
        }
    }

    private void CopyPassword_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: (string id, string password) })
        {
            return;
        }

        try
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(password);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            App.Log($"保险箱已复制密码（{id}）到剪贴板");
        }
        catch (Exception ex)
        {
            App.Log("复制密码失败：" + ex.Message);
        }
    }

    private void RenderItems()
    {
        ItemsPanel.Children.Clear();

        foreach (var item in VaultService.Instance.GetItems())
        {
            var card = new Border
            {
                Background = GetThemeBrush("CardBackgroundBrush"),
                BorderBrush = GetThemeBrush("CardBorderBrush"),
                BorderThickness = new Microsoft.UI.Xaml.Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Microsoft.UI.Xaml.Thickness(12),
                Tag = item.Id,
            };

            var stack = new StackPanel { Spacing = 6 };

            var header = new Grid { ColumnSpacing = 8 };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var name = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(item.Account) ? item.App : $"{item.App} · {item.Account}",
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(name, 0);
            header.Children.Add(name);

            var copyButton = new Button
            {
                Content = "复制",
                Tag = (item.Id, item.Password),
            };
            copyButton.Click += CopyPassword_Click;
            Grid.SetColumn(copyButton, 1);
            header.Children.Add(copyButton);

            stack.Children.Add(header);

            var meta = new TextBlock
            {
                Text = $"密码：{MaskPassword(item.Password)}    更新于 {item.UpdatedAt:yyyy-MM-dd HH:mm}",
                FontSize = 11,
                Opacity = 0.75,
                TextWrapping = TextWrapping.Wrap,
            };
            stack.Children.Add(meta);

            if (!string.IsNullOrWhiteSpace(item.Note))
            {
                stack.Children.Add(new TextBlock
                {
                    Text = item.Note,
                    FontSize = 11,
                    Opacity = 0.6,
                    TextWrapping = TextWrapping.Wrap,
                });
            }

            var deleteButton = new Button
            {
                Content = "删除",
                HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Left,
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Windows.UI.Color.FromArgb(0xFF, 0xFF, 0x7B, 0x7B)),
                Tag = item.Id,
            };
            deleteButton.Click += Delete_Click;
            stack.Children.Add(deleteButton);

            card.Child = stack;
            ItemsPanel.Children.Add(card);
        }

        if (ItemsPanel.Children.Count == 0)
        {
            ItemsPanel.Children.Add(new TextBlock
            {
                Text = "还没有凭据，先在上面添加一条。检测到登录窗口时也会询问是否保存。",
                FontSize = 12,
                Opacity = 0.6,
                TextWrapping = TextWrapping.Wrap,
            });
        }
    }

    private static string MaskPassword(string password)
    {
        if (password.Length <= 3)
        {
            return new string('•', password.Length);
        }

        return password[..1] + new string('•', Math.Min(8, password.Length - 2)) + password[^1];
    }

    /// <summary>取主题画刷（Card 底色/描边），找不到时退回 null（无填充）。</summary>
    private Microsoft.UI.Xaml.Media.Brush? GetThemeBrush(string key)
    {
        try
        {
            return Application.Current.Resources[key] as Microsoft.UI.Xaml.Media.Brush;
        }
        catch
        {
            return null;
        }
    }

    // ————————————————————————— 更换主密码 —————————————————————————

    private void Change_Click(object sender, RoutedEventArgs e)
    {
        var current = ChangeOldBox.Password;
        var next = ChangeNewBox.Password;
        var next2 = ChangeNew2Box.Password;

        if (next != next2)
        {
            ChangeErrorText.Text = "两次输入的新密码不一致";
            return;
        }

        try
        {
            VaultService.Instance.ChangeMasterPassword(current, next);
            ChangeOldBox.Password = "";
            ChangeNewBox.Password = "";
            ChangeNew2Box.Password = "";
            ChangeErrorText.Text = "已更换 ✓";
        }
        catch (Exception ex)
        {
            ChangeErrorText.Text = ex.Message;
        }
    }
}
