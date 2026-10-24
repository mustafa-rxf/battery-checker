using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using PilKontrol.Core;

namespace PilKontrol;

public class AppRow : INotifyPropertyChanged
{
    bool kill, throttle;
    string cpuText = "";
    public string Name { get; init; } = "";
    public bool Kill
    {
        get => kill;
        set
        {
            if (kill == value) return;
            kill = value;
            Changed(nameof(Kill));
            KillChanged?.Invoke(this);
        }
    }
    public bool Throttle
    {
        get => throttle;
        set
        {
            if (throttle == value) return;
            throttle = value;
            Changed(nameof(Throttle));
            ThrottleChanged?.Invoke(this);
        }
    }
    public string CpuText
    {
        get => cpuText;
        set { cpuText = value; Changed(nameof(CpuText)); }
    }
    public Action<AppRow>? KillChanged, ThrottleChanged;
    public event PropertyChangedEventHandler? PropertyChanged;
    void Changed(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public partial class MainWindow : Window
{
    static readonly Color Green = Color.FromRgb(61, 220, 132), Orange = Color.FromRgb(255, 176, 32),
        Blue = Color.FromRgb(77, 163, 255), Gray = Color.FromRgb(160, 168, 181);

    readonly Engine engine = App.Engine;
    readonly List<AppRow> rows = new();
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(2) };
    bool loading = true;

    public MainWindow()
    {
        InitializeComponent();
        foreach (var r in new[] { ModeAuto, ModeAlways, ModeOff }) r.IsChecked = (string)r.Tag == engine.Mode;
        AutostartBox.IsChecked = App.AutostartEnabled;
        loading = false;

        timer.Tick += (_, _) => Refresh();
        timer.Start();
        Refresh();
    }

    // Kapatınca tepsiye iner, program çalışmaya devam eder.
    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }

    void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (PageMain == null) return; // InitializeComponent sırasında
        var apps = TabApps.IsChecked == true;
        PageMain.Visibility = apps ? Visibility.Collapsed : Visibility.Visible;
        PageApps.Visibility = apps ? Visibility.Visible : Visibility.Collapsed;
        engine.WantProcs = apps;
        Refresh();
    }

    void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (!loading) engine.SetMode((string)((RadioButton)sender).Tag);
    }

    void Autostart_Changed(object sender, RoutedEventArgs e)
    {
        if (!loading) App.AutostartEnabled = AutostartBox.IsChecked == true;
    }

    void Chart_SizeChanged(object sender, SizeChangedEventArgs e) => DrawChart();

    void Refresh()
    {
        if (!IsVisible) return;
        var b = Telemetry.GetBattery();

        // Rozet
        var (text, color) = engine.ActiveName == PowerPlans.Ultra ? ("Ultra tasarruf aktif", Orange)
            : engine.Saving ? ("Tasarruf aktif", Green)
            : b.OnAc ? ("Prizde, normal çalışıyor", Gray) : ("Pilde, normal mod", Blue);
        BadgeText.Text = text;
        BadgeDot.Fill = new SolidColorBrush(color);

        // Pil
        var rem = engine.RemainingWh; var full = engine.FullWh;
        PctText.Text = b.Percent is { } p ? $"%{p}" : "-";
        PctSub.Text = b.OnAc ? "prizde" : "pilde";
        DrawGauge(b.Percent ?? 0, b.Percent <= 20 && !b.OnAc ? Orange : Green);

        // Anlık güç ve kalan süre
        if (engine.Watts is { } w && !b.OnAc)
        {
            WattText.Text = $"{w:0.0} W";
            WattSub.Text = rem is { } r && full is { } f ? $"{r:0} / {f:0} Wh kaldı" : "";
            if (rem is { } re)
            {
                var h = re / w;
                TimeText.Text = $"{(int)h} sa {(int)(h % 1 * 60)} dk";
            }
        }
        else
        {
            WattText.Text = "-";
            WattSub.Text = b.OnAc ? "Prizden çekince ölçüm başlar" : "Ölçülüyor...";
            TimeText.Text = "-";
        }

        // Tasarruf etkisi
        var n = engine.Stat("normal")?.Average; var s = engine.Stat("saver")?.Average;
        if (n is { } nv && s is { } sv)
        {
            var diff = (nv - sv) / nv * 100;
            SaveText.Text = diff > 0 ? $"%{diff:0} daha az güç" : "Fark yok";
            SaveText.Foreground = diff > 0 ? new SolidColorBrush(Green) : Foreground;
            var gain = rem is { } rr ? (rr / sv - rr / nv) * 60 : 0;
            SaveSub.Text = $"Ortalama: normal {nv:0.0} W → tasarruf {sv:0.0} W"
                + (gain > 0 ? $"\nTam şarjda yaklaşık {gain:0} dk fazla ömür" : "");
        }
        else
        {
            SaveText.Text = "Veri toplanıyor";
            SaveText.Foreground = Foreground;
            SaveSub.Text = "Hem normal hem tasarruf modunda pille biraz çalıştıkça karşılaştırma burada görünür";
        }

        DrawChart();
        if (TabApps.IsChecked == true) RefreshApps();
    }

    /// <summary>Üstten başlayıp saat yönünde dolan halka.</summary>
    void DrawGauge(double percent, Color color)
    {
        const double cx = 65, cy = 65, r = 56;
        var sweep = Math.Clamp(percent, 0, 99.99) / 100 * 360;
        var rad = (sweep - 90) * Math.PI / 180;
        var end = new Point(cx + r * Math.Cos(rad), cy + r * Math.Sin(rad));
        var fig = new PathFigure { StartPoint = new Point(cx, cy - r), IsClosed = false };
        if (sweep > 0.5)
            fig.Segments.Add(new ArcSegment(end, new Size(r, r), 0, sweep > 180, SweepDirection.Clockwise, true));
        GaugeArc.Data = new PathGeometry(new[] { fig });
        GaugeArc.Stroke = new SolidColorBrush(color);
    }

    void DrawChart()
    {
        Chart.Children.Clear();
        double[] vals;
        lock (engine.Samples) vals = engine.Samples.Select(x => x.Watts).ToArray();
        ChartEmpty.Visibility = vals.Length < 2 ? Visibility.Visible : Visibility.Collapsed;
        ChartMax.Text = vals.Length < 2 ? "" : $"maks {vals.Max():0} W";
        if (vals.Length < 2 || Chart.ActualWidth < 10) return;

        double wd = Chart.ActualWidth, ht = Chart.ActualHeight, hi = vals.Max() * 1.15;
        var pts = vals.Select((v, i) => new Point(i * wd / (vals.Length - 1), ht - v / hi * ht)).ToList();

        var fill = new Polygon
        {
            Points = new PointCollection(pts.Prepend(new Point(0, ht)).Append(new Point(wd, ht))),
            Fill = new LinearGradientBrush(Color.FromArgb(90, 77, 163, 255), Color.FromArgb(0, 77, 163, 255), 90),
        };
        var line = new Polyline
        {
            Points = new PointCollection(pts),
            Stroke = new SolidColorBrush(Blue),
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round,
        };
        Chart.Children.Add(fill);
        Chart.Children.Add(line);
    }

    void RefreshApps()
    {
        var cpu = engine.Procs.ToDictionary(x => x.Name, x => x.Cpu, StringComparer.OrdinalIgnoreCase);
        var names = cpu.Keys.Concat(engine.KillList).Concat(engine.ThrottleList).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

        // Liste değiştiyse yeniden kur, değilse sadece CPU yazılarını güncelle (titreme olmasın).
        if (!names.SequenceEqual(rows.Select(r => r.Name), StringComparer.OrdinalIgnoreCase))
        {
            rows.Clear();
            foreach (var n in names)
                rows.Add(new AppRow
                {
                    Name = n,
                    Kill = engine.KillList.Contains(n),
                    Throttle = engine.ThrottleList.Contains(n),
                    KillChanged = r => engine.ToggleKill(r.Name, r.Kill),
                    ThrottleChanged = r => engine.ToggleThrottle(r.Name, r.Throttle),
                });
            AppList.ItemsSource = null;
            AppList.ItemsSource = rows;
        }
        foreach (var r in rows)
            r.CpuText = cpu.TryGetValue(r.Name, out var c) ? $"CPU %{c:0.0}" : "çalışmıyor";
        AppsEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
