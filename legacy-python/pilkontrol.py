"""PilKontrol - Windows laptoplar için pil ömrünü uzatan otomatik güç yöneticisi.

Kullanım:
    python pilkontrol.py status     # pil ve aktif plan bilgisi
    python pilkontrol.py enable     # tasarruf planını elle aç
    python pilkontrol.py disable    # önceki plana dön
    python pilkontrol.py watch      # arka planda izle: pilde tasarruf, prizde normal
    python pilkontrol.py gui        # basit pencere
"""
import ctypes
import json
import re
import subprocess
import sys
import time
from pathlib import Path

PLAN_NAME = "PilKontrol Tasarruf"
PLAN_ULTRA = "PilKontrol Ultra"
STATE_FILE = Path.home() / ".pilkontrol.json"
CREATE_NO_WINDOW = 0x08000000

# (alt grup, ayar, pildeki değer). Alias'lar powercfg'nin kendi isimleridir.
SAVER = [
    ("SUB_PROCESSOR", "PROCTHROTTLEMAX", 99),   # %100 altı = Intel turbo kapanır
    ("SUB_PROCESSOR", "PERFBOOSTMODE", 0),      # turbo boost kapalı
    ("SUB_PROCESSOR", "PERFEPP", 80),           # verimlilik eğilimli
    ("SUB_PCIEXPRESS", "ASPM", 2),              # PCIe maksimum tasarruf
    ("SUB_VIDEO", "VIDEOIDLE", 90),             # 1,5 dk boşta ekranı kapat
    ("SUB_SLEEP", "STANDBYIDLE", 600),          # 10 dk boşta uyku
]
# Pilde verimlilik moduna (EcoQoS) alınacak arka plan uygulamaları.
ECO_APPS = ["MedalEncoder.exe", "Medal.exe", "BackgroundDownload.exe",
            "urban-vpn-app.exe", "DynamicWin.exe"]
LOW_REFRESH = 60  # Hz, pilde ekran yenileme hızı
ULTRA = [
    ("SUB_PROCESSOR", "PROCTHROTTLEMAX", 60),
    ("SUB_PROCESSOR", "PERFBOOSTMODE", 0),
    ("SUB_PROCESSOR", "PERFEPP", 100),
    ("SUB_VIDEO", "VIDEOIDLE", 30),
    ("SUB_PCIEXPRESS", "ASPM", 2),
    ("SUB_SLEEP", "STANDBYIDLE", 180),
]
ULTRA_BELOW = 20  # % - pil bunun altına inince Ultra plan


class SystemPowerStatus(ctypes.Structure):
    _fields_ = [
        ("ACLineStatus", ctypes.c_ubyte),
        ("BatteryFlag", ctypes.c_ubyte),
        ("BatteryLifePercent", ctypes.c_ubyte),
        ("SystemStatusFlag", ctypes.c_ubyte),
        ("BatteryLifeTime", ctypes.c_ulong),
        ("BatteryFullLifeTime", ctypes.c_ulong),
    ]


def battery():
    """(yüzde, prizde_mi, kalan_saniye|None) döner."""
    s = SystemPowerStatus()
    ctypes.windll.kernel32.GetSystemPowerStatus(ctypes.byref(s))
    left = None if s.BatteryLifeTime == 0xFFFFFFFF else s.BatteryLifeTime
    pct = None if s.BatteryLifePercent == 255 else s.BatteryLifePercent
    return pct, s.ACLineStatus == 1, left


def battery_wmi():
    """(çekilen_watt|None, kalan_Wh|None, tam_dolu_Wh|None). Okunamazsa hepsi None."""
    cmd = ("$s=Get-CimInstance -Namespace root\\wmi -ClassName BatteryStatus;"
           "$f=Get-CimInstance -Namespace root\\wmi -ClassName BatteryFullChargedCapacity;"
           "\"$($s.DischargeRate) $($s.RemainingCapacity) $($f.FullChargedCapacity)\"")
    try:
        r = subprocess.run(["powershell", "-NoProfile", "-Command", cmd],
                           capture_output=True, text=True, timeout=15,
                           creationflags=CREATE_NO_WINDOW)
        rate, rem, full = (int(x) for x in r.stdout.split()[:3])
    except Exception:
        return None, None, None
    return (rate / 1000 if rate > 0 else None), rem / 1000, full / 1000


class DEVMODEW(ctypes.Structure):
    _fields_ = [
        ("dmDeviceName", ctypes.c_wchar * 32), ("dmSpecVersion", ctypes.c_ushort),
        ("dmDriverVersion", ctypes.c_ushort), ("dmSize", ctypes.c_ushort),
        ("dmDriverExtra", ctypes.c_ushort), ("dmFields", ctypes.c_ulong),
        ("dmPositionX", ctypes.c_long), ("dmPositionY", ctypes.c_long),
        ("dmDisplayOrientation", ctypes.c_ulong), ("dmDisplayFixedOutput", ctypes.c_ulong),
        ("dmColor", ctypes.c_short), ("dmDuplex", ctypes.c_short),
        ("dmYResolution", ctypes.c_short), ("dmTTOption", ctypes.c_short),
        ("dmCollate", ctypes.c_short), ("dmFormName", ctypes.c_wchar * 32),
        ("dmLogPixels", ctypes.c_ushort), ("dmBitsPerPel", ctypes.c_ulong),
        ("dmPelsWidth", ctypes.c_ulong), ("dmPelsHeight", ctypes.c_ulong),
        ("dmDisplayFlags", ctypes.c_ulong), ("dmDisplayFrequency", ctypes.c_ulong),
        ("dmICMMethod", ctypes.c_ulong), ("dmICMIntent", ctypes.c_ulong),
        ("dmMediaType", ctypes.c_ulong), ("dmDitherType", ctypes.c_ulong),
        ("dmReserved1", ctypes.c_ulong), ("dmReserved2", ctypes.c_ulong),
        ("dmPanningWidth", ctypes.c_ulong), ("dmPanningHeight", ctypes.c_ulong),
    ]


def _set_refresh(hz):
    """Ana ekranın yenileme hızını geçici olarak (kayıt defterine yazmadan) değiştirir."""
    u = ctypes.windll.user32
    dm = DEVMODEW()
    dm.dmSize = ctypes.sizeof(DEVMODEW)
    if not u.EnumDisplaySettingsW(None, -1, ctypes.byref(dm)):
        return False
    if dm.dmDisplayFrequency == hz:
        return True
    dm.dmDisplayFrequency = hz
    dm.dmFields = 0x400000  # DM_DISPLAYFREQUENCY
    if u.ChangeDisplaySettingsExW(None, ctypes.byref(dm), None, 2, None) != 0:  # CDS_TEST
        return False
    return u.ChangeDisplaySettingsExW(None, ctypes.byref(dm), None, 0, None) == 0


def current_refresh():
    dm = DEVMODEW()
    dm.dmSize = ctypes.sizeof(DEVMODEW)
    ctypes.windll.user32.EnumDisplaySettingsW(None, -1, ctypes.byref(dm))
    return dm.dmDisplayFrequency


def refresh_low(on):
    """Pilde LOW_REFRESH'e düşür, değilse eski değere dön."""
    state = load_state()
    now = current_refresh()
    if on:
        if now > LOW_REFRESH:
            state["refresh_orig"] = now
            save_state(state)
            _set_refresh(LOW_REFRESH)
    else:
        orig = state.pop("refresh_orig", None)
        if orig:
            _set_refresh(orig)
            save_state(state)


def eco_apps(on, names=None):
    """Listedeki çalışan uygulamaları EcoQoS (verimlilik modu) yapar / geri alır."""
    names = {n.lower() for n in (names or ECO_APPS)}
    r = subprocess.run(["tasklist", "/fo", "csv", "/nh"], capture_output=True,
                       text=True, errors="replace", creationflags=CREATE_NO_WINDOW)
    k = ctypes.windll.kernel32
    state = (ctypes.c_ulong * 3)(1, 1, 1 if on else 0)  # Version, ControlMask, StateMask
    for line in r.stdout.splitlines():
        parts = [p.strip('"') for p in line.split('","')]
        if len(parts) > 1 and parts[0].lower() in names and parts[1].isdigit():
            h = k.OpenProcess(0x200, False, int(parts[1]))  # PROCESS_SET_INFORMATION
            if h:
                k.SetProcessInformation(h, 4, ctypes.byref(state), ctypes.sizeof(state))
                k.CloseHandle(h)


IGNORE = {  # asla listelenmez / kapatılmaz
    "idle", "_total", "system", "registry", "svchost", "dwm", "csrss", "wininit",
    "winlogon", "services", "lsass", "explorer", "python", "pythonw", "wmiprvse",
    "msmpeng", "searchhost", "sihost", "fontdrvhost", "ctfmon", "taskhostw",
    "runtimebroker", "audiodg", "memory compression", "smss", "conhost",
    "startmenuexperiencehost", "shellexperiencehost", "textinputhost",
    "securityhealthsystray", "nvdisplay.container", "claude", "code",
    "srtasks", "wmiapsrv", "sqlservr", "aggregatorhost", "appactions",
    "system interrupts", "secure system", "msedgewebview2",
}
IGNORE_PREFIX = ("codesetup", "crossdevice", "microsoft.", "nvidia", "intel", "realtek")


def running_apps(top=8):
    """[(ad, toplam_cpu_yüzdesi)] CPU'ya göre azalan; kullanıcı uygulamaları."""
    cmd = ("Get-CimInstance Win32_PerfFormattedData_PerfProc_Process|"
           "ForEach-Object{\"$($_.Name)|$($_.PercentProcessorTime)\"};"
           "(Get-CimInstance Win32_ComputerSystem).NumberOfLogicalProcessors")
    try:
        r = subprocess.run(["powershell", "-NoProfile", "-Command", cmd],
                           capture_output=True, text=True, timeout=20,
                           creationflags=CREATE_NO_WINDOW)
    except Exception:
        return []
    lines = r.stdout.split()
    cores = int(lines[-1]) if lines and lines[-1].isdigit() else 1
    total = {}
    for line in lines[:-1]:
        name, _, cpu = line.rpartition("|")
        name = re.sub(r"#\d+$", "", name)
        low = name.lower()
        if low in IGNORE or low.startswith(IGNORE_PREFIX) or not cpu.isdigit():
            continue
        total[name] = total.get(name, 0) + int(cpu) / cores
    busy = [(n, c) for n, c in total.items() if c >= 0.1]  # boşta olanları gösterme
    return sorted(busy, key=lambda x: -x[1])[:top]


def _pids(name):
    r = subprocess.run(["tasklist", "/fo", "csv", "/nh", "/fi", f"imagename eq {name}"],
                       capture_output=True, text=True, errors="replace",
                       creationflags=CREATE_NO_WINDOW)
    out = []
    for line in r.stdout.splitlines():
        p = [x.strip('"') for x in line.split('","')]
        if len(p) > 1 and p[0].lower() == name.lower() and p[1].isdigit():
            out.append(int(p[1]))
    return out


def _exe_path(pid):
    k = ctypes.windll.kernel32
    h = k.OpenProcess(0x1000, False, pid)  # PROCESS_QUERY_LIMITED_INFORMATION
    if not h:
        return None
    buf = ctypes.create_unicode_buffer(1024)
    size = ctypes.c_ulong(1024)
    ok = k.QueryFullProcessImageNameW(h, 0, buf, ctypes.byref(size))
    k.CloseHandle(h)
    return buf.value if ok else None


def kill_apps(names):
    """Önce nazikçe kapatır, 8 sn sonra kalanı zorla. Yolları geri açmak için saklar."""
    state = load_state()
    killed = state.get("killed", {})
    alive = []
    for n in names:
        exe = n if n.lower().endswith(".exe") else n + ".exe"
        pids = _pids(exe)
        if not pids:
            continue
        path = next(filter(None, map(_exe_path, pids)), None)
        if path and "windowsapps" not in path.lower():
            killed[exe] = path
        subprocess.run(["taskkill", "/im", exe], capture_output=True,
                       creationflags=CREATE_NO_WINDOW)
        alive.append(exe)
    if alive:
        time.sleep(8)
        for exe in alive:
            if _pids(exe):
                subprocess.run(["taskkill", "/f", "/im", exe], capture_output=True,
                               creationflags=CREATE_NO_WINDOW)
        state["killed"] = killed
        save_state(state)


def restore_apps():
    state = load_state()
    for exe, path in state.pop("killed", {}).items():
        if not _pids(exe) and Path(path).exists():
            try:
                subprocess.Popen([path], cwd=str(Path(path).parent),
                                 creationflags=0x00000008 | 0x00000200)  # DETACHED | NEW_GROUP
            except OSError:
                pass
    save_state(state)


def powercfg(*args):
    r = subprocess.run(["powercfg", *map(str, args)], capture_output=True,
                       text=True, errors="replace", creationflags=CREATE_NO_WINDOW)
    return r.returncode, r.stdout


GUID_RE = re.compile(r"([0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12})")


def list_plans():
    """[(guid, isim, aktif_mi)]"""
    _, out = powercfg("/list")
    plans = []
    for line in out.splitlines():
        m = GUID_RE.search(line)
        if m:
            name = re.search(r"\((.*)\)", line)
            plans.append((m.group(1), name.group(1) if name else "", "*" in line))
    return plans


def active_guid():
    for guid, _, active in list_plans():
        if active:
            return guid


def find_plan(name):
    for guid, n, _ in list_plans():
        if n == name:
            return guid


def ensure_plan(name, settings):
    """Planı yoksa Dengeli'den kopyalayıp oluşturur, ayarları uygular."""
    guid = find_plan(name)
    if not guid:
        _, out = powercfg("/duplicatescheme", "SCHEME_BALANCED")
        m = GUID_RE.search(out)
        if not m:
            raise RuntimeError("Güç planı oluşturulamadı: " + out)
        guid = m.group(1)
        powercfg("/changename", guid, name)
    for sub, setting, value in settings:
        # Sadece pildeki (DC) değeri değiştir; prizdeki performans bozulmaz.
        powercfg("/setdcvalueindex", guid, sub, setting, value)
    return guid


def load_state():
    try:
        return json.loads(STATE_FILE.read_text())
    except Exception:
        return {}


def save_state(state):
    STATE_FILE.write_text(json.dumps(state))


def enable(ultra=False):
    name, settings = (PLAN_ULTRA, ULTRA) if ultra else (PLAN_NAME, SAVER)
    own = {find_plan(PLAN_NAME), find_plan(PLAN_ULTRA)}
    current = active_guid()
    state = load_state()
    if current not in own:
        state["previous"] = current
        save_state(state)
    guid = ensure_plan(name, settings)
    powercfg("/setactive", guid)  # aktifse bile yeniden uygula (yeni ayarlar için)
    refresh_low(True)
    eco_apps(True)
    kill_apps(load_state().get("kill", []))
    return name


def disable():
    previous = load_state().get("previous")
    valid = {g for g, _, _ in list_plans()}
    if previous not in valid:
        previous = next((g for g, n, _ in list_plans()
                         if n not in (PLAN_NAME, PLAN_ULTRA)), None)
    if previous and active_guid() != previous:
        powercfg("/setactive", previous)
    refresh_low(False)
    eco_apps(False)
    restore_apps()
    return previous


def status_text():
    pct, ac, left = battery()
    active = next((n for g, n, a in list_plans() if a), "?")
    kalan = f"{left // 3600}s {left % 3600 // 60}dk" if left else "-"
    return (f"Pil: %{pct if pct is not None else '?'} | "
            f"{'Prizde' if ac else 'Pilde'} | Kalan: {kalan} | Plan: {active}")


def apply_policy():
    """Duruma göre doğru planı seç. Yapılan işi metin olarak döner."""
    pct, ac, _ = battery()
    active = next((n for g, n, a in list_plans() if a), "")
    if ac:
        if active in (PLAN_NAME, PLAN_ULTRA):
            disable()
            return "Prize takıldı, normal plana dönüldü"
        return None
    want = PLAN_ULTRA if pct is not None and pct <= ULTRA_BELOW else PLAN_NAME
    if active != want:
        enable(ultra=want == PLAN_ULTRA)
        return f"Pilde: {want} aktif"
    return None


def watch(interval=10):
    print("PilKontrol izleme modunda. Çıkmak için Ctrl+C.")
    try:
        while True:
            msg = apply_policy()
            if msg:
                print(time.strftime("%H:%M:%S"), msg)
            time.sleep(interval)
    except KeyboardInterrupt:
        print("Durduruldu.")


def gui():
    import tkinter as tk

    root = tk.Tk()
    root.title("PilKontrol")
    root.geometry("420x190")
    label = tk.Label(root, text="", font=("Segoe UI", 11), justify="left")
    label.pack(pady=12)
    auto = tk.BooleanVar(value=True)
    tk.Checkbutton(root, text="Otomatik (pilde tasarruf, prizde normal)",
                   variable=auto).pack()
    row = tk.Frame(root)
    row.pack(pady=10)
    tk.Button(row, text="Tasarrufu Aç", command=enable).pack(side="left", padx=6)
    tk.Button(row, text="Ultra", command=lambda: enable(ultra=True)).pack(side="left", padx=6)
    tk.Button(row, text="Kapat", command=disable).pack(side="left", padx=6)

    def tick():
        if auto.get():
            apply_policy()
        label.config(text=status_text())
        root.after(5000, tick)

    tick()
    root.mainloop()


def main():
    if sys.platform != "win32":
        sys.exit("PilKontrol sadece Windows için.")
    cmd = sys.argv[1] if len(sys.argv) > 1 else "gui"
    if cmd == "status":
        print(status_text())
    elif cmd == "enable":
        print("Aktif:", enable())
    elif cmd == "ultra":
        print("Aktif:", enable(ultra=True))
    elif cmd == "disable":
        disable()
        print("Önceki plana dönüldü.")
    elif cmd == "watch":
        watch()
    elif cmd == "gui":
        gui()
    else:
        print(__doc__)


if __name__ == "__main__":
    main()
