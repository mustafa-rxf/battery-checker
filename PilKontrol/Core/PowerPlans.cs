using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace PilKontrol.Core;

/// <summary>powercfg ile Windows güç planlarını yönetir.</summary>
public static class PowerPlans
{
    public const string Saver = "PilKontrol Tasarruf";
    public const string Ultra = "PilKontrol Ultra";

    static readonly Regex GuidRx = new(@"[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}");
    static readonly Regex NameRx = new(@"\((.*)\)");

    public static string Run(params string[] args)
    {
        var psi = new ProcessStartInfo("powercfg")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return output;
    }

    public static List<(string Guid, string Name, bool Active)> List()
    {
        var list = new List<(string, string, bool)>();
        foreach (var line in Run("/list").Split('\n'))
        {
            var g = GuidRx.Match(line);
            if (!g.Success) continue;
            var n = NameRx.Match(line);
            list.Add((g.Value, n.Success ? n.Groups[1].Value : "", line.Contains('*')));
        }
        return list;
    }

    /// <summary>Aktif planın (guid, ad) bilgisi.</summary>
    public static (string Guid, string Name) Active()
    {
        var o = Run("/getactivescheme");
        var g = GuidRx.Match(o);
        var n = NameRx.Match(o);
        return (g.Success ? g.Value : "", n.Success ? n.Groups[1].Value : "");
    }

    public static string? Find(string name)
    {
        foreach (var p in List()) if (p.Name == name) return p.Guid;
        return null;
    }

    public static bool IsOurs(string name) => name == Saver || name == Ultra;

    /// <summary>Plan yoksa Dengeli'den kopyalar, sadece pil (DC) değerlerini yazar.</summary>
    public static string Ensure(string name, (string Sub, string Setting, int Value)[] settings)
    {
        var guid = Find(name);
        if (guid == null)
        {
            var m = GuidRx.Match(Run("/duplicatescheme", "SCHEME_BALANCED"));
            if (!m.Success) throw new InvalidOperationException("Güç planı oluşturulamadı");
            guid = m.Value;
            Run("/changename", guid, name);
        }
        foreach (var (sub, setting, value) in settings)
            Run("/setdcvalueindex", guid, sub, setting, value.ToString());
        return guid;
    }

    public static void SetActive(string guid) => Run("/setactive", guid);
}
