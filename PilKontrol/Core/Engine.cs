using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PilKontrol.Core;

/// <summary>Arka plan döngüsü: planı yönetir, watt örnekleri toplar.</summary>
public sealed class Engine
{
    public const string Auto = "auto", Always = "always", Off = "off";

    readonly Settings s = Settings.Current;
    readonly object gate = new();
    readonly CancellationTokenSource cts = new();
    Thread? thread;

    public string ActiveName { get; private set; } = "";
    public bool Saving => PowerPlans.IsOurs(ActiveName);
    public double? Watts { get; private set; }
    public double? RemainingWh { get; private set; }
    public double? FullWh { get; private set; }
    public volatile bool WantProcs;
    public IReadOnlyList<(string Name, double Cpu)> Procs { get; private set; } = new List<(string, double)>();
    public List<(DateTime Time, double Watts)> Samples { get; } = new();

    public string Mode => s.Mode;
    public IReadOnlyList<string> KillList => s.Kill;

    public void Start()
    {
        thread = new Thread(Loop) { IsBackground = true, Name = "PilKontrol" };
        thread.Start();
    }

    /// <summary>Durdurur ve eski güç planı, ekran hızı ve uygulamaları geri getirir.</summary>
    public void Shutdown()
    {
        cts.Cancel();
        thread?.Join(3000);
        lock (gate)
        {
            try { if (Saving || PowerPlans.IsOurs(PowerPlans.Active().Name)) Optimizer.Disable(); } catch { }
        }
    }

    public void SetMode(string mode)
    {
        s.Mode = mode;
        s.Save();
        Task.Run(Apply);
    }

    public void ToggleKill(string name, bool on)
    {
        lock (gate)
        {
            if (on && !s.Kill.Contains(name)) s.Kill.Add(name);
            else if (!on) s.Kill.Remove(name);
            s.Save();
        }
        if (on && Saving) Task.Run(() => { lock (gate) Optimizer.Kill(new[] { name }); });
    }

    public IReadOnlyList<string> ThrottleList => s.Throttle;

    public void ToggleThrottle(string name, bool on)
    {
        lock (gate)
        {
            if (on && !s.Throttle.Contains(name)) s.Throttle.Add(name);
            else if (!on) s.Throttle.Remove(name);
            s.Save();
        }
        if (Saving) Task.Run(() => { lock (gate) Optimizer.ThrottleOne(name, on); });
    }

    public PlanStat? Stat(string key) => s.Stats.GetValueOrDefault(key);

    void Loop()
    {
        var sw = Stopwatch.StartNew();
        double lastSample = -999, lastEco = 0, lastProcs = -999;
        while (!cts.IsCancellationRequested)
        {
            try
            {
                Apply();
                var now = sw.Elapsed.TotalSeconds;
                if (now - lastEco >= 60)
                {
                    lastEco = now;
                    if (Saving)
                    {
                        Optimizer.Throttle(true);   // sonradan açılanları da yakala
                        lock (gate) Optimizer.Kill(s.Kill);
                    }
                }
                if (now - lastSample >= 30) { lastSample = now; Sample(); }
                if (WantProcs && now - lastProcs >= 6) { lastProcs = now; Procs = Telemetry.RunningApps(); }
            }
            catch { /* bir tur başarısız olursa sonrakinde tekrar dene */ }
            cts.Token.WaitHandle.WaitOne(10_000);
        }
    }

    void Apply()
    {
        lock (gate)
        {
            ActiveName = PowerPlans.Active().Name;
            var b = Telemetry.GetBattery();
            switch (s.Mode)
            {
                case Off:
                    if (Saving) Optimizer.Disable();
                    break;
                case Always:
                    if (ActiveName != PowerPlans.Saver) Optimizer.Enable(false);
                    break;
                default:
                    if (b.OnAc)
                    {
                        if (Saving) Optimizer.Disable();
                    }
                    else
                    {
                        var want = b.Percent <= Optimizer.UltraBelowPercent ? PowerPlans.Ultra : PowerPlans.Saver;
                        if (ActiveName != want) Optimizer.Enable(want == PowerPlans.Ultra);
                    }
                    break;
            }
            ActiveName = PowerPlans.Active().Name;
        }
    }

    void Sample()
    {
        var (w, rem, full) = Telemetry.ReadWmi();
        Watts = w; RemainingWh = rem; FullWh = full;
        if (w is not { } watts) return;
        lock (Samples)
        {
            Samples.Add((DateTime.Now, watts));
            if (Samples.Count > 120) Samples.RemoveAt(0);
        }
        var key = Saving ? "saver" : "normal";
        if (!s.Stats.TryGetValue(key, out var st)) s.Stats[key] = st = new PlanStat();
        st.Sum += watts; st.Count++;
        s.Save();
    }
}
