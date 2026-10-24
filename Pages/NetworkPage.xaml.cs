using System.Net.NetworkInformation;
using System.Net.Sockets;
using EdgePanel.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EdgePanel.Pages;

public sealed partial class NetworkPage : Page
{
    public NetworkPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    private void Refresh()
    {
        AdapterPanel.Children.Clear();

        List<NetworkInterface> adapters;
        try
        {
            adapters = NetworkInterface.GetAllNetworkInterfaces()
                .Where(a => a.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .OrderByDescending(a => a.OperationalStatus == OperationalStatus.Up)
                .ThenBy(a => a.Name)
                .ToList();
        }
        catch (Exception ex)
        {
            App.Log("读取网卡失败：" + ex.Message);
            AdapterPanel.Children.Add(new TextBlock { Text = "读取网卡信息失败：" + ex.Message, TextWrapping = TextWrapping.Wrap });
            return;
        }

        foreach (var adapter in adapters)
        {
            AdapterPanel.Children.Add(BuildCard(adapter));
        }
    }

    private static Border BuildCard(NetworkInterface adapter)
    {
        var lines = new StackPanel { Spacing = 3 };

        var up = adapter.OperationalStatus == OperationalStatus.Up;
        lines.Children.Add(new TextBlock
        {
            Text = $"{adapter.Name} · {(up ? "已连接" : "未连接")}",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        lines.Children.Add(new TextBlock
        {
            Text = $"{Describe(adapter.NetworkInterfaceType)} · {adapter.Description}",
            FontSize = 11,
            Opacity = 0.65,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        string ipv4 = "-";
        string gateway = "-";
        string dns = "-";
        var prefix = "";

        try
        {
            var props = adapter.GetIPProperties();

            var unicast = props.UnicastAddresses
                .FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork);
            if (unicast is not null)
            {
                ipv4 = unicast.Address.ToString();
                prefix = $"/{unicast.PrefixLength}";
            }

            gateway = props.GatewayAddresses
                .FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork)
                ?.Address.ToString() ?? "-";

            var servers = props.DnsAddresses
                .Where(d => d.AddressFamily == AddressFamily.InterNetwork)
                .Select(d => d.ToString())
                .ToList();
            dns = servers.Count > 0 ? string.Join(", ", servers) : "-";
        }
        catch
        {
            // 个别适配器取不到信息
        }

        lines.Children.Add(new TextBlock
        {
            Text = $"IP {ipv4}{prefix}   网关 {gateway}",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        });

        lines.Children.Add(new TextBlock
        {
            Text = $"DNS {dns}",
            FontSize = 12,
            Opacity = 0.8,
            TextWrapping = TextWrapping.Wrap,
        });

        lines.Children.Add(new TextBlock
        {
            Text = $"速率 {adapter.Speed / 1_000_000} Mbps · MAC {adapter.GetPhysicalAddress()}",
            FontSize = 11,
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap,
        });

        return new Border
        {
            Style = (Style)Application.Current.Resources["Card"],
            Child = lines,
        };
    }

    private static string Describe(NetworkInterfaceType type) => type switch
    {
        NetworkInterfaceType.Ethernet => "有线",
        NetworkInterfaceType.Wireless80211 => "无线",
        NetworkInterfaceType.Tunnel => "隧道",
        NetworkInterfaceType.Ppp => "拨号",
        _ => type.ToString(),
    };

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void OpenSettings_Click(object sender, RoutedEventArgs e) => SystemActions.OpenSettings();

    private void IpConfig_Click(object sender, RoutedEventArgs e) => SystemActions.RunCommand("ipconfig /all");

    private void OpenConnections_Click(object sender, RoutedEventArgs e) => SystemActions.RunCommand("ncpa.cpl");
}