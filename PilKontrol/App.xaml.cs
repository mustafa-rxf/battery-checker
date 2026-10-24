using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using PilKontrol.Core;

namespace PilKontrol;

public partial class App : Application
{
    public static Engine Engine { get; } = new();
    static Mutex? mutex;
    EventWaitHandle? showSignal;
    Tray? tray;
    MainWindow? window;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Kurulum/kaldırma sihirbazı tarafından kullanılır.
        if (e.Args.Contains("--quit")) { SignalRunningInstance("PilKontrolQuit", waitForExit: true); Shutdown(); return; }
        if (e.Args.Contains("--cleanup")) { Cleanup(); Shutdown(); return; }

        mutex = new Mutex(true, "PilKontrolSingleInstance", out var first);
        showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "PilKontrolShow");
        if (!first)
        {
            showSignal.Set(); // çalışan kopyanın penceresini öne getir
            Shutdown();
            return;
        }

        Engine.Start();
        window = new MainWindow();
        tray = new Tray(ShowWindow, Quit);

        var quitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "PilKontrolQuit");
        Task.Run(() =>
        {
            while (showSignal.WaitOne()) Dispatcher.Invoke(ShowWindow);
        });
        Task.Run(() =>
        {
            quitSignal.WaitOne();
            Dispatcher.Invoke(Quit);
        });

        if (!e.Args.Contains("--tray")) ShowWindow();
    }

    void ShowWindow()
    {
        window!.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    void Quit()
    {
        tray?.Dispose();
        Engine.Shutdown(); // eski güç planına, ekran hızına ve uygulamalara dön
        Shutdown();
    }

    /// <summary>Çalışan kopyaya sinyal gönderir; istenirse kapanmasını bekler (en çok 15 sn).</summary>
    static void SignalRunningInstance(string eventName, bool waitForExit)
    {
        if (!EventWaitHandle.TryOpenExisting(eventName, out var ev)) return;
        ev.Set();
        if (!waitForExit) return;
        for (var i = 0; i < 50; i++)
        {
            if (Mutex.TryOpenExisting("PilKontrolSingleInstance", out var m)) { m.Dispose(); Thread.Sleep(300); }
            else return;
        }
    }

    /// <summary>Kaldırırken: eski güç planına dön, PilKontrol planlarını sil, otomatik başlatmayı kaldır.</summary>
    static void Cleanup()
    {
        try { Optimizer.Disable(); } catch { }
        try
        {
            foreach (var p in PowerPlans.List().Where(p => PowerPlans.IsOurs(p.Name) && !p.Active))
                PowerPlans.Run("/delete", p.Guid);
        }
        catch { }
        try { AutostartEnabled = false; } catch { }
    }

    // ---- Windows ile başlat ----

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool AutostartEnabled
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue("PilKontrol") != null;
        }
        set
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, true)!;
            if (value) k.SetValue("PilKontrol", $"\"{Environment.ProcessPath}\" --tray");
            else k.DeleteValue("PilKontrol", false);
        }
    }
}
