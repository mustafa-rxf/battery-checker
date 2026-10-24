using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace PilKontrol.Core;

public class PlanStat
{
    public double Sum { get; set; }
    public int Count { get; set; }
    public double? Average => Count >= 3 ? Sum / Count : null;
}

/// <summary>%APPDATA%\PilKontrol\settings.json içinde saklanan kalıcı durum.</summary>
public class Settings
{
    public string Mode { get; set; } = "auto";                       // auto | always | off
    public List<string> Kill { get; set; } = new();                  // pilde kapatılacak uygulamalar
    public List<string> Throttle { get; set; } =                     // pilde kısılacak (kapatılmayan) uygulamalar
        new() { "DynamicWin", "BackgroundDownload", "urban-vpn-app", "Medal", "MedalEncoder" };
    public Dictionary<string, string> Killed { get; set; } = new();  // kapatılanlar -> exe yolu
    public string? PreviousPlan { get; set; }
    public int RefreshOriginal { get; set; }
    public Dictionary<string, PlanStat> Stats { get; set; } = new();

    // PILKONTROL_DATA ile ayar klasörü değiştirilebilir (test / taşınabilir kullanım).
    static readonly string FilePath = Path.Combine(
        Environment.GetEnvironmentVariable("PILKONTROL_DATA")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PilKontrol"),
        "settings.json");
    static readonly object Gate = new();

    public static Settings Current { get; } = Load();

    static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { return new(); }
    }

    public void Save()
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { /* ayar yazılamazsa sessizce devam et */ }
        }
    }
}
