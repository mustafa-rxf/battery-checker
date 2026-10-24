using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;

namespace PilKontrol.Core;

public record BatteryInfo(int? Percent, bool OnAc, int? SecondsLeft);

public static class Telemetry
{
    public static BatteryInfo GetBattery()
    {
        Native.GetSystemPowerStatus(out var s);
        int? pct = s.BatteryLifePercent == 255 ? null : s.BatteryLifePercent;
        int? left = s.BatteryLifeTime == uint.MaxValue ? null : (int)s.BatteryLifeTime;
        return new BatteryInfo(pct, s.ACLineStatus == 1, left);
    }

    /// <summary>(çekilen watt, kalan Wh, tam dolu Wh). Okunamayan değer null.</summary>
    public static (double? Watts, double? RemainingWh, double? FullWh) ReadWmi()
    {
        double? watts = null, rem = null, full = null;
        try
        {
            using var s = new ManagementObjectSearcher(@"root\wmi", "SELECT DischargeRate, RemainingCapacity FROM BatteryStatus");
            foreach (ManagementBaseObject o in s.Get())
            {
                var rate = Convert.ToDouble(o["DischargeRate"]);
                watts = rate > 0 ? rate / 1000 : null;
                rem = Convert.ToDouble(o["RemainingCapacity"]) / 1000;
            }
            using var f = new ManagementObjectSearcher(@"root\wmi", "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity");
            foreach (ManagementBaseObject o in f.Get())
                full = Convert.ToDouble(o["FullChargedCapacity"]) / 1000;
        }
        catch { /* WMI okunamadı */ }
        return (watts, rem, full);
    }

    static readonly HashSet<string> Ignore = new(StringComparer.OrdinalIgnoreCase)
    {
        "idle", "_total", "system", "registry", "svchost", "dwm", "csrss", "wininit", "winlogon",
        "services", "lsass", "explorer", "pilkontrol", "python", "pythonw", "wmiprvse", "msmpeng",
        "searchhost", "sihost", "fontdrvhost", "ctfmon", "taskhostw", "runtimebroker", "audiodg",
        "memory compression", "smss", "conhost", "startmenuexperiencehost", "shellexperiencehost",
        "textinputhost", "securityhealthsystray", "claude", "code", "srtasks", "wmiapsrv",
        "sqlservr", "aggregatorhost", "appactions", "system interrupts", "secure system", "msedgewebview2",
    };
    static readonly string[] IgnorePrefix = { "codesetup", "crossdevice", "microsoft.", "nvidia", "intel", "realtek" };

    /// <summary>CPU kullanan kullanıcı uygulamaları: (ad, toplam CPU %). En çok kullanandan başlar.</summary>
    public static List<(string Name, double Cpu)> RunningApps(int top = 8)
    {
        var total = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name, PercentProcessorTime FROM Win32_PerfFormattedData_PerfProc_Process");
            foreach (ManagementBaseObject o in s.Get())
            {
                var name = Regex.Replace((string)o["Name"], @"#\d+$", "");
                if (Ignore.Contains(name) || IgnorePrefix.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                    continue;
                var cpu = Convert.ToDouble(o["PercentProcessorTime"]) / Environment.ProcessorCount;
                total[name] = total.GetValueOrDefault(name) + cpu;
            }
        }
        catch { return new(); }
        return total.Where(kv => kv.Value >= 0.1)
                    .OrderByDescending(kv => kv.Value).Take(top)
                    .Select(kv => (kv.Key, kv.Value)).ToList();
    }
}
