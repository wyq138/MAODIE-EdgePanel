using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace EdgePanel.Services;

public sealed class DiskInfo
{
    public string Name { get; init; } = "";
    public string Label { get; init; } = "";
    public double TotalGb { get; init; }
    public double FreeGb { get; init; }
    public double UsedPercent { get; init; }
}

public sealed class ProcessInfo
{
    public string Name { get; init; } = "";
    public double MemoryMb { get; init; }
    public int Pid { get; init; }
}

public sealed class SystemSnapshot
{
    public double CpuPercent { get; set; }
    public double MemoryUsedGb { get; set; }
    public double MemoryTotalGb { get; set; }
    public double MemoryPercent { get; set; }
    public double NetDownKbps { get; set; }
    public double NetUpKbps { get; set; }
    public string OsVersion { get; set; } = "";
    public string CpuName { get; set; } = "";
    public int UptimeMinutes { get; set; }
    public List<DiskInfo> Disks { get; set; } = new();
    public List<ProcessInfo> TopProcesses { get; set; } = new();
}

/// <summary>
/// 系统监控：CPU / 内存 / 磁盘 / 网络 / 进程。全部使用 Win32 + BCL，无第三方依赖。
/// </summary>
public sealed class SystemMonitorService
{
    public static SystemMonitorService Instance { get; } = new();

    private readonly Timer _timer;
    private ulong _prevIdle, _prevKernel, _prevUser;
    private long _prevNetRecv, _prevNetSent;
    private DateTime _prevNetTime = DateTime.UtcNow;
    private int _tick;

    public SystemSnapshot Current { get; private set; } = new();

    public event Action<SystemSnapshot>? Updated;

    private SystemMonitorService()
    {
        Current.OsVersion = Environment.OSVersion.VersionString;
        Current.CpuName = ReadCpuName();
        _timer = new Timer(_ => Sample(), null, 1000, 1000);
    }

    private void Sample()
    {
        try
        {
            var snap = Current;

            snap.CpuPercent = ReadCpuPercent();
            ReadMemory(snap);
            ReadNetwork(snap);
            snap.UptimeMinutes = Environment.TickCount64 > 0
                ? (int)(Environment.TickCount64 / 60000)
                : 0;

            // 磁盘与进程较重，每 3 秒刷新一次
            if (_tick++ % 3 == 0)
            {
                snap.Disks = ReadDisks();
                snap.TopProcesses = ReadTopProcesses();
            }

            Current = snap;
            Updated?.Invoke(snap);
        }
        catch (Exception ex)
        {
            App.Log("系统监控采样失败：" + ex.Message);
        }
    }

    private double ReadCpuPercent()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
        {
            return 0;
        }

        var idleTicks = ToUlong(idle);
        var kernelTicks = ToUlong(kernel);
        var userTicks = ToUlong(user);

        if (_prevIdle == 0 && _prevKernel == 0 && _prevUser == 0)
        {
            _prevIdle = idleTicks;
            _prevKernel = kernelTicks;
            _prevUser = userTicks;
            return 0;
        }

        var idleDelta = idleTicks - _prevIdle;
        var kernelDelta = kernelTicks - _prevKernel;
        var userDelta = userTicks - _prevUser;
        var total = kernelDelta + userDelta;

        _prevIdle = idleTicks;
        _prevKernel = kernelTicks;
        _prevUser = userTicks;

        if (total == 0)
        {
            return 0;
        }

        return Math.Clamp((1.0 - (double)idleDelta / total) * 100.0, 0, 100);
    }

    private static ulong ToUlong(FILETIME ft) => ((ulong)ft.High << 32) | ft.Low;

    private static void ReadMemory(SystemSnapshot snap)
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(status))
        {
            return;
        }

        const double gb = 1024.0 * 1024 * 1024;
        snap.MemoryTotalGb = Math.Round(status.ullTotalPhys / gb, 1);
        snap.MemoryUsedGb = Math.Round((status.ullTotalPhys - status.ullAvailPhys) / gb, 1);
        snap.MemoryPercent = status.dwMemoryLoad;
    }

    private void ReadNetwork(SystemSnapshot snap)
    {
        long recv = 0;
        long sent = 0;

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                var stats = nic.GetIPv4Statistics();
                recv += stats.BytesReceived;
                sent += stats.BytesSent;
            }
        }
        catch
        {
            return;
        }

        var now = DateTime.UtcNow;
        var seconds = (now - _prevNetTime).TotalSeconds;

        if (_prevNetTime != default && seconds > 0.2)
        {
            snap.NetDownKbps = Math.Max(0, (recv - _prevNetRecv) / 1024.0 / seconds);
            snap.NetUpKbps = Math.Max(0, (sent - _prevNetSent) / 1024.0 / seconds);
        }

        _prevNetRecv = recv;
        _prevNetSent = sent;
        _prevNetTime = now;
    }

    private static List<DiskInfo> ReadDisks()
    {
        var list = new List<DiskInfo>();

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
                {
                    continue;
                }

                const double gb = 1024.0 * 1024 * 1024;
                var total = drive.TotalSize / gb;
                var free = drive.TotalFreeSpace / gb;
                var used = total <= 0 ? 0 : (total - free) / total * 100;

                list.Add(new DiskInfo
                {
                    Name = drive.Name.TrimEnd('\\'),
                    Label = string.IsNullOrEmpty(drive.VolumeLabel) ? "本地磁盘" : drive.VolumeLabel,
                    TotalGb = Math.Round(total, 1),
                    FreeGb = Math.Round(free, 1),
                    UsedPercent = Math.Round(used, 1),
                });
            }
            catch
            {
                // 跳过不可访问的盘
            }
        }

        return list;
    }

    private static List<ProcessInfo> ReadTopProcesses()
    {
        var list = new List<ProcessInfo>();

        foreach (var p in Process.GetProcesses())
        {
            try
            {
                list.Add(new ProcessInfo
                {
                    Name = p.ProcessName,
                    MemoryMb = Math.Round(p.WorkingSet64 / 1024.0 / 1024.0, 1),
                    Pid = p.Id,
                });
            }
            catch
            {
                // 部分系统进程不可访问
            }
            finally
            {
                p.Dispose();
            }
        }

        return list.OrderByDescending(x => x.MemoryMb).Take(12).ToList();
    }

    private static string ReadCpuName()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return key?.GetValue("ProcessorNameString")?.ToString()?.Trim() ?? "未知处理器";
        }
        catch
        {
            return "未知处理器";
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint Low;
        public uint High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private sealed class MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FILETIME idleTime, out FILETIME kernelTime, out FILETIME userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);
}