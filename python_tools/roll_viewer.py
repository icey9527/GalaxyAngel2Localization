import sys, re
from PySide6.QtCore import Qt, QTimer
from PySide6.QtGui import QFont
from PySide6.QtWidgets import QApplication, QWidget, QFileDialog, QVBoxLayout, QHBoxLayout, QPushButton, QLabel, QSlider, QFrame

RE_WITH_Y = re.compile(r'^\s*(?P<text>.*?)\s*(?P<size>\d+)\s+(?P<align>[LRC])\s+(?P<x>\d+)\s+(?P<y>[+-]?\d+)\s*$')
RE_NO_Y   = re.compile(r'^\s*(?P<text>.*?)\s*(?P<size>\d+)\s+(?P<align>[LRC])\s+(?P<x>\d+)\s*$')
RE_SEC    = re.compile(r'^\s*\[(?P<n>\d+)\]\s*$')

VW, VH = 640, 480

def unq(s):
    s = s.strip()
    return s[1:-1] if len(s) >= 2 and s[0] == '"' and s[-1] == '"' else s

def parse_from_000(path):
    items, in_000, last_y = [], False, 0
    with open(path, "r", encoding="cp932", errors="replace") as f:
        for line in f:
            line = line.rstrip("\r\n")

            msec = RE_SEC.match(line)
            if msec:
                n = msec.group("n")
                if n == "000":
                    in_000 = True
                    last_y = 0
                    continue
                if in_000:
                    break
                continue

            if not in_000:
                continue

            s = line.strip()
            if not s or s.startswith("//"):
                continue

            m = RE_WITH_Y.match(line)
            ytok = None
            if m:
                ytok = m.group("y")
            else:
                m = RE_NO_Y.match(line)
            if not m:
                continue

            text = unq(m.group("text"))
            if not text:
                continue

            size = int(m.group("size"))
            align = m.group("align")
            x = int(m.group("x"))

            if ytok is None:
                y_abs = last_y
            elif ytok[0] in "+-":
                y_abs = last_y + int(ytok)
            else:
                y_abs = int(ytok)

            last_y = y_abs
            items.append((y_abs, text, size, align, x))

    items.sort(key=lambda t: t[0])
    return items

class Roll(QWidget):
    def __init__(self):
        super().__init__()
        self.setWindowTitle("Staff Roll Player (640x480)")
        self.setMinimumSize(1020, 720)

        self.stage = QFrame()
        self.stage.setObjectName("stage")
        self.stage.setFixedSize(VW, VH)
        self.stage.setStyleSheet("""
        QFrame#stage {
            background: qlineargradient(x1:0,y1:0,x2:0,y2:1, stop:0 #0b1020, stop:1 #05060c);
            border-radius: 14px;
            border: 1px solid rgba(255,255,255,0.08);
        }
        """)

        top = QHBoxLayout()
        self.btn_open = QPushButton("选择TXT")
        self.btn_open.clicked.connect(self.open_file)

        self.btn_play = QPushButton("暂停")
        self.btn_play.clicked.connect(self.toggle)
        self.btn_play.setEnabled(False)

        self.speed = QSlider(Qt.Horizontal)
        self.speed.setRange(10, 240)
        self.speed.setValue(60)
        self.speed.setFixedWidth(260)

        self.info = QLabel("未加载")
        self.info.setStyleSheet("color: rgba(255,255,255,0.72);")

        top.addWidget(self.btn_open)
        top.addWidget(self.btn_play)
        top.addSpacing(10)
        top.addWidget(QLabel("速度"))
        top.addWidget(self.speed)
        top.addStretch(1)
        top.addWidget(self.info)

        root = QVBoxLayout(self)
        root.addLayout(top)
        root.addWidget(self.stage, 0, Qt.AlignHCenter)

        self.setStyleSheet("""
        QWidget { background: #070812; }
        QPushButton {
            color: rgba(255,255,255,0.88);
            background: rgba(255,255,255,0.08);
            border: 1px solid rgba(255,255,255,0.12);
            padding: 8px 14px;
            border-radius: 10px;
        }
        QPushButton:hover { background: rgba(255,255,255,0.12); }
        QPushButton:disabled { color: rgba(255,255,255,0.35); border-color: rgba(255,255,255,0.06); }
        QLabel { color: rgba(255,255,255,0.9); font-size: 13px; }
        QSlider::groove:horizontal { height: 6px; background: rgba(255,255,255,0.10); border-radius: 3px; }
        QSlider::handle:horizontal { width: 16px; margin: -6px 0; border-radius: 8px; background: rgba(255,255,255,0.85); }
        """)

        self.timer = QTimer(self)
        self.timer.timeout.connect(self.tick)
        self.timer.start(16)

        self.items = []
        self.labels = []
        self.meta = []
        self.ymin = 0
        self.ymax = 0
        self.t = 0.0
        self.running = False
        self.loaded_name = "未加载"

        self.base0 = 0
        self.margin = 80

    def open_file(self):
        path, _ = QFileDialog.getOpenFileName(self, "选择 staffroll txt", "", "Text Files (*.txt);;All Files (*)")
        if path:
            self.load(path)

    def clear_stage(self):
        for lb in self.labels:
            lb.deleteLater()
        self.labels.clear()
        self.meta.clear()

    def load(self, path):
        self.clear_stage()
        self.items = parse_from_000(path)
        self.loaded_name = path.split("/")[-1].split("\\")[-1]

        if not self.items:
            self.running = False
            self.btn_play.setEnabled(False)
            self.info.setText(f"{self.loaded_name} | 解析到 0 行（确认是否有 [000]）")
            return

        ys = [y for y, *_ in self.items]
        self.ymin, self.ymax = min(ys), max(ys)

        for y, text, size, align, x in self.items:
            lb = QLabel(text, self.stage)
            f = QFont("Noto Sans CJK JP")
            f.setPointSize(max(9, int(size * 0.95)))
            lb.setFont(f)
            lb.setStyleSheet("background: transparent; color: rgba(255,255,255,0.92);")
            lb.adjustSize()
            lb.show()
            self.labels.append(lb)
            self.meta.append((y, align, x))

        self.btn_play.setEnabled(True)
        self.running = True
        self.btn_play.setText("暂停")
        self.t = 0.0

        self.reflow_x()

        def init_pos():
            first_y = self.items[0][0]
            self.base0 = first_y - VH - self.margin
            self.place_once()
            self.info.setText(f"{self.loaded_name} | 行数 {len(self.items)} | Running")

        QTimer.singleShot(0, init_pos)

    def toggle(self):
        if not self.items:
            return
        self.running = not self.running
        self.btn_play.setText("暂停" if self.running else "播放")
        st = "Running" if self.running else "Paused"
        self.info.setText(f"{self.loaded_name} | 行数 {len(self.items)} | {st}")

    def reflow_x(self):
        for lb, (y, align, x) in zip(self.labels, self.meta):
            tw = lb.width()
            if align == "L":
                xx = x
            elif align == "C":
                xx = x - tw // 2
            else:
                xx = x - tw
            lb.move(max(0, min(VW - tw, xx)), lb.y())

    def place_once(self):
        if not self.items:
            return
        base = self.base0 + self.t * self.speed.value()
        for lb, (y, _, _) in zip(self.labels, self.meta):
            yy = int(y - base)
            lb.move(lb.x(), yy)

    def tick(self):
        if not self.items:
            return

        if self.running:
            self.t += 0.016

        speed = self.speed.value()
        base = self.base0 + self.t * speed

        total = (self.ymax - self.ymin) + VH + self.margin * 2
        if base > self.base0 + total:
            self.t = 0.0
            base = self.base0

        for lb, (y, _, _) in zip(self.labels, self.meta):
            yy = int(y - base)
            lb.move(lb.x(), yy)

app = QApplication(sys.argv)
win = Roll()
win.show()
sys.exit(app.exec())