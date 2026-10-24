"""PilKontrol tepsi uygulaması: arka planda çalışır, tepsi ikonu + gösterge paneli.

    pythonw pilkontrol_app.py          # pencere + tepsi
    pythonw pilkontrol_app.py --tray   # sadece tepsi (başlangıçta bu kullanılır)
"""
import ctypes
import sys
import threading
import time
import tkinter as tk
import winreg
from collections import deque
from pathlib import Path

import pystray
from PIL import Image, ImageDraw, ImageFont

import pilkontrol as pk

AUTO, ALWAYS, OFF = "auto", "always", "off"
MODE_LABELS = {AUTO: "Otomatik", ALWAYS: "Her zaman tasarruf", OFF: "Kapalı"}
SAMPLE_EVERY = 30          # sn, watt ölçümü
POLICY_EVERY = 10          # sn, prize takıldı/çıkarıldı kontrolü
RUN_KEY = r"Software\Microsoft\Windows\CurrentVersion\Run"

BG, CARD, FG, MUTED = "#14161b", "#1e2128", "#e8eaed", "#8b93a1"
GREEN, ORANGE, BLUE, RED = "#3ddc84", "#ffb020", "#4da3ff", "#ff5c5c"


class Engine:
    """Arka plan iş parçacığı: planı yönetir ve watt örnekleri toplar."""

    def __init__(self):
        st = pk.load_state()
        self.mode = st.get("mode", AUTO)
        self.stats = st.get("stats", {})   # plan adı -> [toplam_watt, adet]
        self.kill = list(st.get("kill", []))  # pilde kapatılacak uygulamalar
        self.procs = []                    # [(ad, cpu%)] Uygulamalar sekmesi için
        self.want_procs = False
        self.samples = deque(maxlen=120)   # (zaman, watt)
        self.watts = None
        self.remaining_wh = self.full_wh = None
        self.lock = threading.Lock()
        self.stop = threading.Event()

    def save(self):
        st = pk.load_state()
        st.update(mode=self.mode, stats=self.stats, kill=self.kill)
        pk.save_state(st)

    def saving(self):
        return self.active_name() in (pk.PLAN_NAME, pk.PLAN_ULTRA)

    def toggle_kill(self, name, on):
        if on and name not in self.kill:
            self.kill.append(name)
        elif not on and name in self.kill:
            self.kill.remove(name)
        self.save()
        if on and self.saving():  # tasarruf zaten aktifse hemen kapat
            threading.Thread(target=pk.kill_apps, args=([name],), daemon=True).start()

    def set_mode(self, mode):
        self.mode = mode
        self.save()
        self.apply()

    def apply(self):
        if self.mode == OFF:
            if pk.find_plan(pk.PLAN_NAME) and self.active_name() in (pk.PLAN_NAME, pk.PLAN_ULTRA):
                pk.disable()
        elif self.mode == ALWAYS:
            if self.active_name() != pk.PLAN_NAME:
                pk.enable()
        else:
            pk.apply_policy()

    @staticmethod
    def active_name():
        return next((n for _, n, a in pk.list_plans() if a), "")

    def sample(self):
        watts, rem, full = pk.battery_wmi()
        with self.lock:
            self.watts, self.remaining_wh, self.full_wh = watts, rem, full
            if watts:
                self.samples.append((time.time(), watts))
                plan = "saver" if self.active_name() in (pk.PLAN_NAME, pk.PLAN_ULTRA) else "normal"
                s = self.stats.setdefault(plan, [0.0, 0])
                s[0] += watts
                s[1] += 1
        if watts:
            self.save()

    def run(self):
        last_sample = last_eco = last_procs = 0
        while not self.stop.is_set():
            try:
                self.apply()
                if time.time() - last_eco >= 60:
                    if self.saving():
                        pk.eco_apps(True)  # sonradan açılan uygulamaları da yakala
                        pk.kill_apps(self.kill)
                    last_eco = time.time()
                if self.want_procs and time.time() - last_procs >= 6:
                    self.procs = pk.running_apps()
                    last_procs = time.time()
                if time.time() - last_sample >= SAMPLE_EVERY:
                    self.sample()
                    last_sample = time.time()
            except Exception:
                pass
            self.stop.wait(POLICY_EVERY)

    def avg(self, plan):
        s = self.stats.get(plan)
        return s[0] / s[1] if s and s[1] >= 3 else None


def autostart_enabled():
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, RUN_KEY) as k:
            winreg.QueryValueEx(k, "PilKontrol")
            return True
    except OSError:
        return False


def set_autostart(on):
    exe = Path(sys.executable).with_name("pythonw.exe")
    cmd = f'"{exe}" "{Path(__file__).resolve()}" --tray'
    with winreg.OpenKey(winreg.HKEY_CURRENT_USER, RUN_KEY, 0, winreg.KEY_SET_VALUE) as k:
        if on:
            winreg.SetValueEx(k, "PilKontrol", 0, winreg.REG_SZ, cmd)
        else:
            try:
                winreg.DeleteValue(k, "PilKontrol")
            except OSError:
                pass


def make_icon(pct, color):
    img = Image.new("RGBA", (64, 64), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.rounded_rectangle((2, 12, 62, 56), 10, fill=color)
    text = "?" if pct is None else str(pct)
    try:
        font = ImageFont.truetype("arialbd.ttf", 34 if len(text) < 3 else 26)
    except OSError:
        font = ImageFont.load_default()
    d.text((32, 35), text, fill="#101114", font=font, anchor="mm")
    return img


class Dashboard:
    def __init__(self, engine, request_quit):
        self.e = engine
        self.request_quit = request_quit
        self.root = tk.Tk()
        r = self.root
        r.title("PilKontrol")
        r.geometry("460x600+60+20")
        r.configure(bg=BG)
        r.resizable(False, False)
        r.protocol("WM_DELETE_WINDOW", self.hide)
        self.mode_var = tk.StringVar(value=engine.mode)
        self.auto_var = tk.BooleanVar(value=autostart_enabled())
        self._build()
        self.refresh()

    def card(self, parent, title):
        f = tk.Frame(parent, bg=CARD)
        f.pack(fill="x", padx=16, pady=4)
        tk.Label(f, text=title, bg=CARD, fg=MUTED, font=("Segoe UI", 9)).pack(anchor="w", padx=14, pady=(10, 0))
        v = tk.Label(f, text="-", bg=CARD, fg=FG, font=("Segoe UI Semibold", 20))
        v.pack(anchor="w", padx=14, pady=(0, 2))
        sub = tk.Label(f, text="", bg=CARD, fg=MUTED, font=("Segoe UI", 9), justify="left")
        sub.pack(anchor="w", padx=14, pady=(0, 10))
        return v, sub

    def _build(self):
        r = self.root
        tk.Label(r, text="PilKontrol", bg=BG, fg=FG, font=("Segoe UI Semibold", 16)).pack(anchor="w", padx=18, pady=(14, 0))
        self.badge = tk.Label(r, text="", bg=BG, fg=GREEN, font=("Segoe UI", 10))
        self.badge.pack(anchor="w", padx=18)

        tabs = tk.Frame(r, bg=BG)
        tabs.pack(fill="x", padx=16, pady=(6, 0))
        self.tab_btns = {}
        for key, label in (("main", "Genel"), ("apps", "Pilde kapatılacak uygulamalar")):
            b = tk.Button(tabs, text=label, relief="flat", bd=0, padx=12, pady=5,
                          font=("Segoe UI", 10), command=lambda k=key: self.show_tab(k))
            b.pack(side="left", padx=(0, 6))
            self.tab_btns[key] = b

        main = self.pages = {}
        main["main"] = tk.Frame(r, bg=BG)
        main["apps"] = tk.Frame(r, bg=BG)
        pg = main["main"]

        self.pct_v, self.pct_s = self.card(pg, "PİL")
        self.watt_v, self.watt_s = self.card(pg, "ANLIK GÜÇ TÜKETİMİ")
        self.save_v, self.save_s = self.card(pg, "TASARRUF ETKİSİ")

        self.chart = tk.Canvas(pg, width=428, height=100, bg=CARD, highlightthickness=0)
        self.chart.pack(padx=16, pady=4)

        box = tk.Frame(pg, bg=BG)
        box.pack(fill="x", padx=18, pady=(6, 0))
        for key, label in MODE_LABELS.items():
            tk.Radiobutton(box, text=label, value=key, variable=self.mode_var,
                           command=lambda: self.e.set_mode(self.mode_var.get()),
                           bg=BG, fg=FG, selectcolor=CARD, activebackground=BG,
                           activeforeground=FG, font=("Segoe UI", 10)).pack(side="left", padx=(0, 10))
        tk.Checkbutton(pg, text="Windows açılışında otomatik başlat", variable=self.auto_var,
                       command=lambda: set_autostart(self.auto_var.get()),
                       bg=BG, fg=FG, selectcolor=CARD, activebackground=BG,
                       activeforeground=FG, font=("Segoe UI", 10)).pack(anchor="w", padx=18, pady=6)

        ap = main["apps"]
        tk.Label(ap, text="İşaretlediğin uygulamalar pile geçince kapatılır,\nprize takınca otomatik yeniden açılır.\n"
                          "Önce nazikçe kapatılır, 8 sn sonra hâlâ açıksa zorla kapatılır.",
                 bg=BG, fg=MUTED, font=("Segoe UI", 9), justify="left").pack(anchor="w", padx=18, pady=(10, 4))
        self.app_list = tk.Frame(ap, bg=BG)
        self.app_list.pack(fill="x", padx=18)
        self.app_rows = {}      # ad -> (BooleanVar, cpu_label)
        self.app_names = None
        self.tab = None
        self.show_tab("main")

    def show_tab(self, key):
        self.tab = key
        for k, p in self.pages.items():
            p.pack_forget()
            self.tab_btns[k].config(bg=CARD if k == key else BG, fg=FG if k == key else MUTED)
        self.pages[key].pack(fill="both", expand=True)
        self.e.want_procs = key == "apps"

    def refresh_apps(self):
        cpu = dict(self.e.procs)
        names = sorted(set(cpu) | set(self.e.kill), key=str.lower)
        if names != self.app_names:  # liste değiştiyse yeniden çiz
            for w in self.app_list.winfo_children():
                w.destroy()
            self.app_rows = {}
            for n in names:
                row = tk.Frame(self.app_list, bg=CARD)
                row.pack(fill="x", pady=2)
                var = tk.BooleanVar(value=n in self.e.kill)
                tk.Checkbutton(row, text=n, variable=var, anchor="w", bg=CARD, fg=FG,
                               selectcolor=BG, activebackground=CARD, activeforeground=FG,
                               font=("Segoe UI", 10),
                               command=lambda n=n, v=var: self.e.toggle_kill(n, v.get())
                               ).pack(side="left", padx=8, pady=4)
                lbl = tk.Label(row, bg=CARD, fg=MUTED, font=("Segoe UI", 9))
                lbl.pack(side="right", padx=10)
                self.app_rows[n] = lbl
            if not names:
                tk.Label(self.app_list, text="Uygulamalar taranıyor...", bg=BG, fg=MUTED).pack()
            self.app_names = names
        for n, lbl in self.app_rows.items():
            lbl.config(text=f"CPU %{cpu[n]:.1f}" if n in cpu else "çalışmıyor")

    def refresh(self):
        e = self.e
        pct, ac, left = pk.battery()
        plan = e.active_name()
        saving = plan in (pk.PLAN_NAME, pk.PLAN_ULTRA)
        self.badge.config(
            text=("● Ultra tasarruf" if plan == pk.PLAN_ULTRA else "● Tasarruf aktif") if saving
            else ("● Prizde, normal çalışıyor" if ac else "● Normal mod"),
            fg=(ORANGE if plan == pk.PLAN_ULTRA else GREEN) if saving else MUTED)

        self.pct_v.config(text=f"%{pct}" if pct is not None else "-")
        with e.lock:
            watts, rem, full = e.watts, e.remaining_wh, e.full_wh
            samples = list(e.samples)
        self.pct_s.config(text=("Şarj oluyor / prizde" if ac else "Pille çalışıyor")
                          + (f" · {rem:.0f}/{full:.0f} Wh" if rem else ""))

        if watts and not ac:
            self.watt_v.config(text=f"{watts:.1f} W")
            hours = rem / watts if rem else None
            self.watt_s.config(text=f"Bu hızla yaklaşık {int(hours)} sa {int(hours % 1 * 60)} dk dayanır" if hours else "")
        else:
            self.watt_v.config(text="-")
            self.watt_s.config(text="Prizden çekince ölçüm başlar")

        n, s = e.avg("normal"), e.avg("saver")
        if n and s:
            diff = (n - s) / n * 100
            gain = (rem / s - rem / n) * 60 if rem else None
            self.save_v.config(text=f"%{diff:.0f} daha az güç" if diff > 0 else "Fark yok", fg=GREEN if diff > 0 else FG)
            self.save_s.config(text=f"Ortalama: normal {n:.1f} W → tasarruf {s:.1f} W"
                               + (f"\nTam şarjda yaklaşık {gain:.0f} dk fazla ömür" if gain and gain > 0 else ""))
        else:
            self.save_v.config(text="Veri toplanıyor", fg=FG)
            self.save_s.config(text="Hem normal hem tasarruf modunda pille biraz\nçalıştıkça karşılaştırma burada görünür")

        self.draw_chart(samples)
        if self.tab == "apps":
            self.refresh_apps()
        self.root.after(2000, self.refresh)

    def draw_chart(self, samples):
        c = self.chart
        c.delete("all")
        c.create_text(10, 8, text="Güç tüketimi (W)", anchor="nw", fill=MUTED, font=("Segoe UI", 9))
        if len(samples) < 2:
            c.create_text(214, 62, text="Henüz veri yok", fill=MUTED, font=("Segoe UI", 10))
            return
        vals = [w for _, w in samples]
        hi, lo = max(vals) * 1.1, 0
        pts = []
        for i, w in enumerate(vals):
            x = 12 + i * (404 / (len(vals) - 1))
            y = 100 - (w - lo) / (hi - lo) * 70
            pts += [x, y]
        c.create_line(*pts, fill=BLUE, width=2, smooth=True)
        c.create_text(418, 8, text=f"maks {max(vals):.0f} W", anchor="ne", fill=MUTED, font=("Segoe UI", 9))

    def show(self):
        self.root.deiconify()
        self.root.lift()
        self.root.focus_force()

    def hide(self):
        self.root.withdraw()


def single_instance():
    h = ctypes.windll.kernel32.CreateMutexW(None, False, "PilKontrolSingleInstance")
    return ctypes.windll.kernel32.GetLastError() != 183, h  # 183 = zaten var


def main():
    ok, _mutex = single_instance()
    if not ok:
        return
    tray_only = "--tray" in sys.argv
    engine = Engine()
    threading.Thread(target=engine.run, daemon=True).start()
    ui = Dashboard(engine, None)
    if tray_only:
        ui.hide()

    def color():
        _, ac, _ = pk.battery()
        plan = engine.active_name()
        if plan == pk.PLAN_ULTRA:
            return ORANGE
        return GREEN if plan == pk.PLAN_NAME else ("#c9ced6" if ac else BLUE)

    def set_mode(m):
        return lambda icon, item: (engine.set_mode(m), ui.root.after(0, lambda: ui.mode_var.set(m)))

    def quit_app(icon, item):
        engine.stop.set()
        try:
            pk.disable()
        except Exception:
            pass
        icon.stop()
        ui.root.after(0, ui.root.destroy)

    menu = pystray.Menu(
        pystray.MenuItem("Paneli aç", lambda i, it: ui.root.after(0, ui.show), default=True),
        pystray.MenuItem("Mod", pystray.Menu(*[
            pystray.MenuItem(label, set_mode(m), checked=lambda it, m=m: engine.mode == m, radio=True)
            for m, label in MODE_LABELS.items()])),
        pystray.MenuItem("Çıkış (normal plana dön)", quit_app))
    icon = pystray.Icon("PilKontrol", make_icon(pk.battery()[0], color()), "PilKontrol", menu)

    def update_icon():
        while not engine.stop.is_set():
            pct, _, _ = pk.battery()
            icon.icon = make_icon(pct, color())
            icon.title = f"PilKontrol · %{pct} · {engine.active_name()}"
            engine.stop.wait(30)

    icon.run_detached()
    threading.Thread(target=update_icon, daemon=True).start()
    ui.root.mainloop()


if __name__ == "__main__":
    main()
