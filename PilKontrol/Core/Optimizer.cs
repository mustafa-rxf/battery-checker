using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace PilKontrol.Core;

/// <summary>Pile geçince yapılan işler ve prize takınca geri alınmaları.</summary>
public static class Optimizer
{
    // Parlaklığa bilerek dokunulmaz.
    static readonly (string, string, int)[] SaverSettings =
    {
        ("SUB_PROCESSOR", "PROCTHROTTLEMAX", 99),   // %100 altı: Intel turbo kapanır
        ("SUB_PROCESSOR", "PERFBOOSTMODE", 0),      // turbo boost kapalı
        ("SUB_PROCESSOR", "PERFEPP", 80),           // verimlilik eğilimli
        ("SUB_PCIEXPRESS", "ASPM", 2),              // PCIe maksimum tasarruf
        ("SUB_VIDEO", "VIDEOIDLE", 90),             // 1,5 dk boşta ekranı kapat
        ("SUB_SLEEP", "STANDBYIDLE", 600),          // 10 dk boşta uyku
        ("SUB_DISK", "DISKIDLE", 120),              // 2 dk boşta diski durdur
        (WifiSub, WifiPower, 3),                    // Wi-Fi adaptörü maksimum tasarruf
        (UsbSub, UsbSelective, 1),                  // USB seçmeli askıya alma açık
        (UsbSub, Usb3LinkPower, 2),                 // USB 3 bağlantı gücü maksimum tasarruf
    };
    static readonly (string, string, int)[] UltraSettings =
    {
        ("SUB_PROCESSOR", "PROCTHROTTLEMAX", 60),
        ("SUB_PROCESSOR", "PERFBOOSTMODE", 0),
        ("SUB_PROCESSOR", "PERFEPP", 100),
        ("SUB_PCIEXPRESS", "ASPM", 2),
        ("SUB_VIDEO", "VIDEOIDLE", 30),
        ("SUB_SLEEP", "STANDBYIDLE", 180),
        ("SUB_DISK", "DISKIDLE", 60),
        (WifiSub, WifiPower, 3),
        (UsbSub, UsbSelective, 1),
        (UsbSub, Usb3LinkPower, 2),
    };

    // Takma adı olmayan ayarlar için GUID'ler
    const string WifiSub = "19cbb8fa-5279-450e-9fac-8a3d5fedd0c1", WifiPower = "12bbebe6-58d6-4636-95bb-3217ef867c1a";
    const string UsbSub = "2a737441-1930-4402-8d77-b2bebba308a3", UsbSelective = "48e6b7a6-50f5-4782-a5d4-53bb8f07e226",
        Usb3LinkPower = "d4e98f31-5ffe-4ce1-be31-1b38b384c009";

    const int LowRefreshHz = 60;
    /// <summary>Kısılan uygulamaların toplam CPU tavanı (1/100 yüzde; 300 = toplam CPU'nun %3'ü).</summary>
    const uint ThrottleCpuRate = 300;
    static readonly object ThrottleLock = new();
    static readonly Dictionary<int, ProcessPriorityClass> OriginalPriority = new();
    /// <summary>Şu an öndeki uygulama: kısılmaz, yoksa kullanırken donar.</summary>
    public static volatile string? Foreground;
    public const int UltraBelowPercent = 20;

    static Settings S => Settings.Current;

    public static void Enable(bool ultra)
    {
        var current = PowerPlans.Active();
        if (!PowerPlans.IsOurs(current.Name))
        {
            S.PreviousPlan = current.Guid;
            S.Save();
        }
        var guid = PowerPlans.Ensure(ultra ? PowerPlans.Ultra : PowerPlans.Saver, ultra ? UltraSettings : SaverSettings);
        PowerPlans.SetActive(guid); // aktif olsa bile yeniden uygula
        LowerRefresh(true);
        Throttle(true);
        Kill(S.Kill);
    }

    public static void Disable()
    {
        var plans = PowerPlans.List();
        var previous = plans.Any(p => p.Guid == S.PreviousPlan) ? S.PreviousPlan
            : plans.FirstOrDefault(p => !PowerPlans.IsOurs(p.Name)).Guid;
        if (previous != null && PowerPlans.Active().Guid != previous) PowerPlans.SetActive(previous);
        LowerRefresh(false);
        Throttle(false);
        Restore();
    }

    // ---- Ekran yenileme hızı ----

    static bool GetMode(out Native.DevMode dm)
    {
        dm = new Native.DevMode();
        dm.dmSize = (ushort)Marshal.SizeOf<Native.DevMode>();
        return Native.EnumDisplaySettingsW(null, -1, ref dm);
    }

    static bool SetRefresh(uint hz)
    {
        if (!GetMode(out var dm)) return false;
        if (dm.dmDisplayFrequency == hz) return true;
        dm.dmDisplayFrequency = hz;
        dm.dmFields = 0x400000; // DM_DISPLAYFREQUENCY
        if (Native.ChangeDisplaySettingsExW(null, ref dm, IntPtr.Zero, 2, IntPtr.Zero) != 0) return false; // CDS_TEST
        return Native.ChangeDisplaySettingsExW(null, ref dm, IntPtr.Zero, 0, IntPtr.Zero) == 0;
    }

    static void LowerRefresh(bool on)
    {
        if (on)
        {
            if (GetMode(out var dm) && dm.dmDisplayFrequency > LowRefreshHz)
            {
                S.RefreshOriginal = (int)dm.dmDisplayFrequency;
                S.Save();
                SetRefresh(LowRefreshHz);
            }
        }
        else if (S.RefreshOriginal > 0)
        {
            SetRefresh((uint)S.RefreshOriginal);
            S.RefreshOriginal = 0;
            S.Save();
        }
    }

    // ---- Arka plan uygulamaları ----

    /// <summary>Listedeki uygulamaları kapatmadan kısar (veya serbest bırakır).</summary>
    public static void Throttle(bool on)
    {
        var names = on ? S.Throttle.Except(S.Kill, StringComparer.OrdinalIgnoreCase)
                              .Where(n => !string.Equals(n, Foreground, StringComparison.OrdinalIgnoreCase))
                       : S.Throttle.AsEnumerable();
        foreach (var name in names.ToList()) ThrottleOne(name, on);
    }

    /// <summary>Düşük öncelik + EcoQoS (Görev Yöneticisi "Verimlilik modu") + sert CPU tavanı.</summary>
    public static void ThrottleOne(string name, bool on)
    {
        lock (ThrottleLock)
        {
            // Adlı job: uygulama yeniden açılsa bile aynı job'a ulaşıp sınırı kaldırabiliriz.
            var job = Native.CreateJobObjectW(IntPtr.Zero, @"Local\PilKontrol_" + name);
            if (job != IntPtr.Zero)
            {
                // Sınırı kapatmak için "kapat" bayrağı yok: tavanı %100'e çıkarırız.
                var cap = new Native.JobCpuRateControl { ControlFlags = 1u | 4u, CpuRate = on ? ThrottleCpuRate : 10000 };
                Native.SetInformationJobObject(job, 15, ref cap, Marshal.SizeOf(cap));
            }

            foreach (var p in Process.GetProcessesByName(name))
                using (p)
                {
                    try
                    {
                        if (on)
                        {
                            OriginalPriority.TryAdd(p.Id, p.PriorityClass);
                            SetEco(p, true);
                            p.PriorityClass = ProcessPriorityClass.Idle;
                            if (job != IntPtr.Zero) Native.AssignProcessToJobObject(job, p.Handle); // zaten üyeyse sessizce başarısız olur
                        }
                        else
                        {
                            SetEco(p, false);
                            if (OriginalPriority.Remove(p.Id, out var orig)) p.PriorityClass = orig;
                            else if (p.PriorityClass == ProcessPriorityClass.Idle) p.PriorityClass = ProcessPriorityClass.Normal;
                        }
                    }
                    catch { /* erişim yok: atla */ }
                }
            if (job != IntPtr.Zero) Native.CloseHandle(job); // sınır, içindeki süreçler yaşadıkça geçerli kalır
        }
    }

    static void SetEco(Process p, bool on)
    {
        var st = new Native.ProcessPowerThrottlingState { Version = 1, ControlMask = on ? 1u : 0u, StateMask = on ? 1u : 0u };
        var size = Marshal.SizeOf(st);
        if (Native.SetProcessInformation(p.Handle, 4, ref st, size) || on) return;
        // Sistem yönetimine bırakma kabul edilmediyse açıkça "kısma yok" de.
        st.ControlMask = 1; st.StateMask = 0;
        Native.SetProcessInformation(p.Handle, 4, ref st, size);
    }

    /// <summary>Şu an en öndeki pencerenin süreç adı (yoksa null).</summary>
    public static string? ForegroundProcessName()
    {
        try
        {
            Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out var pid);
            if (pid == 0) return null;
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch { return null; }
    }

    /// <summary>Önce nazikçe kapatır, 8 sn sonra açık kalanı zorla sonlandırır.</summary>
    public static void Kill(IEnumerable<string> names)
    {
        var waiting = new List<Process>();
        foreach (var name in names.ToList())
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    var path = p.MainModule?.FileName;
                    if (path != null && !path.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase))
                    {
                        S.Killed[name] = path;
                        S.Save();
                    }
                    if (p.CloseMainWindow()) waiting.Add(p);
                    else { p.Kill(); p.Dispose(); } // penceresi yok (tepsi uygulaması)
                }
                catch { p.Dispose(); }
            }
        }
        if (waiting.Count == 0) return;
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline && waiting.Any(p => !p.HasExited)) Thread.Sleep(300);
        foreach (var p in waiting)
        {
            try { if (!p.HasExited) p.Kill(); } catch { }
            p.Dispose();
        }
    }

    /// <summary>Pilde kapatılan uygulamaları yeniden açar.</summary>
    public static void Restore()
    {
        foreach (var (name, path) in S.Killed.ToList())
        {
            if (Process.GetProcessesByName(name).Length == 0 && File.Exists(path))
            {
                try
                {
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path)! });
                }
                catch { }
            }
        }
        S.Killed.Clear();
        S.Save();
    }
}
