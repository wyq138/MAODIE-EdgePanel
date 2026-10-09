using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace EdgePanel.Services;

/// <summary>
/// 局域网坐标回传服务：在本机开一个极简 HTTP 服务，手机扫码打开网页、点选地图后
/// 把经纬度 POST 回本机。用 TcpListener 手写 HTTP 应答，避免 HttpListener 的 URL ACL 权限要求。
/// </summary>
public sealed class CoordinatePickServer : IDisposable
{
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    /// <summary>手机回传坐标后触发（后台线程，UI 侧需自行切回）。</summary>
    public event Action<double, double>? Picked;

    public string LanIp { get; private set; } = "127.0.0.1";
    public int Port { get; private set; }
    public string Url => $"http://{LanIp}:{Port}/";
    public bool IsRunning => _listener is not null;

    public void Start()
    {
        if (_listener is not null)
        {
            return;
        }

        LanIp = FindLanIp();
        _listener = new TcpListener(IPAddress.Any, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _cts = new CancellationTokenSource();

        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
        App.Log($"坐标回传服务已启动：{Url}");
    }

    public void Stop()
    {
        try
        {
            _cts?.Cancel();
        }
        catch
        {
            // 忽略
        }

        try
        {
            _listener?.Stop();
        }
        catch
        {
            // 忽略
        }

        _listener = null;
        _cts = null;
    }

    public void Dispose() => Stop();

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(token);
            }
            catch
            {
                break;
            }

            _ = Task.Run(() => HandleAsync(client), token);
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        try
        {
            using (client)
            {
                var stream = client.GetStream();
                stream.ReadTimeout = 5000;
                stream.WriteTimeout = 5000;

                var data = new MemoryStream();
                var buffer = new byte[8192];
                var total = 0;

                while (total < 64 * 1024)
                {
                    int n;
                    try
                    {
                        n = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length));
                    }
                    catch
                    {
                        break;
                    }

                    if (n <= 0)
                    {
                        break;
                    }

                    data.Write(buffer, 0, n);
                    total += n;

                    var text = Encoding.UTF8.GetString(data.GetBuffer(), 0, total);
                    var headEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    if (headEnd < 0)
                    {
                        continue;
                    }

                    var head = text[..headEnd];
                    var firstLine = head.Split("\r\n")[0];
                    var parts = firstLine.Split(' ');
                    if (parts.Length < 2)
                    {
                        return;
                    }

                    var method = parts[0];
                    var path = parts[1];
                    var contentLength = GetHeader(head, "Content-Length");
                    var needBody = int.TryParse(contentLength, out var cl) ? cl : 0;
                    var haveBody = total - (headEnd + 4);

                    if (haveBody < needBody)
                    {
                        continue; // body 还没读完，继续读
                    }

                    var body = needBody > 0
                        ? Encoding.UTF8.GetString(data.GetBuffer(), headEnd + 4, needBody)
                        : "";

                    await RespondAsync(stream, method, path, body);
                    return;
                }
            }
        }
        catch
        {
            // 连接中途断开，忽略
        }
    }

    private async Task RespondAsync(NetworkStream stream, string method, string path, string body)
    {
        if (method == "POST" && path == "/set")
        {
            var lat = 0.0;
            var lng = 0.0;
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.TryGetProperty("lat", out var latNode))
                {
                    lat = latNode.GetDouble();
                }

                if (root.TryGetProperty("lng", out var lngNode))
                {
                    lng = lngNode.GetDouble();
                }
            }
            catch
            {
                // 解析失败则忽略该坐标
            }

            if (lat is >= -90 and <= 90 && lng is >= -180 and <= 180)
            {
                Picked?.Invoke(lat, lng);
            }

            await WriteAsync(stream, 200, "application/json", "{\"ok\":true}");
            return;
        }

        await WriteAsync(stream, 200, "text/html; charset=utf-8", HtmlPage);
    }

    private static async Task WriteAsync(NetworkStream stream, int status, string contentType, string content)
    {
        var body = Encoding.UTF8.GetBytes(content);
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} OK\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Connection: close\r\n\r\n");

        await stream.WriteAsync(header.AsMemory());
        await stream.WriteAsync(body.AsMemory());
        await stream.FlushAsync();
    }

    private static string GetHeader(string head, string name)
    {
        foreach (var line in head.Split("\r\n"))
        {
            var idx = line.IndexOf(':');
            if (idx < 0)
            {
                continue;
            }

            if (line[..idx].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return line[(idx + 1)..].Trim();
            }
        }

        return "";
    }

    private static string FindLanIp()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork)
                    {
                        continue;
                    }

                    var ip = addr.Address.ToString();
                    if (ip.StartsWith("169.254", StringComparison.Ordinal))
                    {
                        continue; // 链路本地地址，没有有效 DHCP
                    }

                    return ip;
                }
            }
        }
        catch
        {
            // 取不到就回退
        }

        return "127.0.0.1";
    }

    private const string HtmlPage = """
        <!doctype html>
        <html lang="zh">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1, maximum-scale=1, user-scalable=no">
        <link rel="stylesheet" href="https://unpkg.com/leaflet@1.9.4/dist/leaflet.css"/>
        <script src="https://unpkg.com/leaflet@1.9.4/dist/leaflet.js"></script>
        <style>
          html,body{margin:0;height:100%;font-family:sans-serif}
          #map{height:70vh;width:100%}
          #bar{padding:10px;background:#fff;border-top:1px solid #ddd}
          #info{font-size:14px;margin-bottom:8px;color:#333}
          button{width:100%;padding:12px;font-size:16px;background:#2f7cf6;color:#fff;border:none;border-radius:6px}
        </style>
        </head>
        <body>
        <div id="map"></div>
        <div id="bar">
          <div id="info">点击地图选择位置</div>
          <div style="display:flex;gap:8px">
            <button id="loc" style="background:#666">定位到当前位置</button>
            <button id="ok">确认并回传坐标</button>
          </div>
        </div>
        <script>
          var map=L.map('map').setView([35,105],5);
          L.tileLayer('https://webrd0{s}.is.autonavi.com/appmaptile?lang=zh_cn&size=1&scale=1&style=8&x={x}&y={y}&z={z}',{subdomains:['1','2','3','4'],maxZoom:18}).addTo(map);
          var marker=null;
          map.on('click',function(e){
            if(marker){marker.setLatLng(e.latlng);}else{marker=L.marker(e.latlng).addTo(map);}
            document.getElementById('info').textContent=e.latlng.lat.toFixed(6)+' , '+e.latlng.lng.toFixed(6);
          });
          document.getElementById('loc').onclick=function(){
            if(!navigator.geolocation){alert('浏览器不支持定位');return;}
            navigator.geolocation.getCurrentPosition(function(p){
              var ll=[p.coords.latitude,p.coords.longitude];
              map.setView(ll,15);
              if(marker){marker.setLatLng(ll);}else{marker=L.marker(ll).addTo(map);}
              document.getElementById('info').textContent=ll[0].toFixed(6)+' , '+ll[1].toFixed(6);
            },function(){alert('定位失败，请手动点击地图');});
          };
          document.getElementById('ok').onclick=function(){
            if(!marker){alert('请先点击地图选择位置');return;}
            var lat=marker.getLatLng().lat,lng=marker.getLatLng().lng;
            document.getElementById('info').textContent='正在回传…';
            fetch('/set',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({lat:lat,lng:lng})})
              .then(function(r){return r.json();})
              .then(function(){document.getElementById('info').textContent='已回传，可返回电脑查看天气';})
              .catch(function(){document.getElementById('info').textContent='回传失败，请确认手机与电脑在同一 WiFi';});
          };
        </script>
        </body>
        </html>
        """;
}
