import os
import struct
import math
import copy
import tkinter as tk
from tkinter import filedialog, messagebox, simpledialog
from tkinter import ttk
from PIL import Image, ImageTk, ImageDraw

# ================= Data Structures =================
class ImageDef:
    def __init__(self, raw_name, width, height, pivot_x, pivot_y):
        self.raw_name = raw_name
        # Convert .png to .agi.png
        if raw_name.lower().endswith('.png'):
            self.actual_name = raw_name[:-4] + '.agi.png'
        else:
            self.actual_name = raw_name + '.agi.png'
            
        self.width = width
        self.height = height
        self.pivot_x = pivot_x
        self.pivot_y = pivot_y
        self.pil_img = None

class Keyframe:
    def __init__(self, time, data_bytes):
        self.time = time
        # 40-byte key payload
        unpacked = struct.unpack('<I i i i i i I i i i', data_bytes)
        self.color1 = unpacked[0]
        self.rot_x = unpacked[1]
        self.rot_y = unpacked[2]
        self.rot_z = unpacked[3]
        self.scale_x = unpacked[4]
        self.scale_y = unpacked[5]
        self.color2 = unpacked[6]
        self.pos_x = unpacked[7]
        self.pos_y = unpacked[8]
        self.pos_z = unpacked[9]

    def interpolate(self, other, progress):
        """Linear interpolation between two keyframes."""
        res = Keyframe(0, b'\x00'*40)
        res.color1 = lerp_argb(self.color1, other.color1, progress)
        res.color2 = lerp_argb(self.color2, other.color2, progress)
        res.rot_z = self.rot_z + (other.rot_z - self.rot_z) * progress
        res.scale_x = self.scale_x + (other.scale_x - self.scale_x) * progress
        res.scale_y = self.scale_y + (other.scale_y - self.scale_y) * progress
        res.pos_x = self.pos_x + (other.pos_x - self.pos_x) * progress
        res.pos_y = self.pos_y + (other.pos_y - self.pos_y) * progress
        return res

def lerp_argb(ca, cb, p):
    ca = int(ca) & 0xFFFFFFFF
    cb = int(cb) & 0xFFFFFFFF
    aa, ra, ga, ba = (ca >> 24) & 0xFF, (ca >> 16) & 0xFF, (ca >> 8) & 0xFF, ca & 0xFF
    ab, rb, gb, bb = (cb >> 24) & 0xFF, (cb >> 16) & 0xFF, (cb >> 8) & 0xFF, cb & 0xFF
    a = int(round(aa + (ab - aa) * p)) & 0xFF
    r = int(round(ra + (rb - ra) * p)) & 0xFF
    g = int(round(ga + (gb - ga) * p)) & 0xFF
    b = int(round(ba + (bb - ba) * p)) & 0xFF
    return (a << 24) | (r << 16) | (g << 8) | b

def key_visible_from_color1(color1):
    # ARGB: high byte is alpha. A==0 => hidden.
    a = (int(color1) >> 24) & 0xFF
    return a != 0

class Layer:
    def __init__(self, layer_id, img_index, name=""):
        self.layer_id = layer_id
        self.img_index = img_index
        self.name = name
        self.parent_index = -1
        self.unk36 = 0
        self.unk40 = 0
        self.unk44 = 0
        self.unk56 = 0
        self.flags = 0
        self.keyframes = []

    def get_state(self, current_time):
        if not self.keyframes: 
            return None
        if current_time <= self.keyframes[0].time: 
            return self.keyframes[0]
        if current_time >= self.keyframes[-1].time: 
            return self.keyframes[-1]
        
        for i in range(len(self.keyframes) - 1):
            k1 = self.keyframes[i]
            k2 = self.keyframes[i+1]
            if k1.time <= current_time < k2.time:
                progress = (current_time - k1.time) / (k2.time - k1.time)
                return k1.interpolate(k2, progress)
        return self.keyframes[-1]

class AnimationData:
    def __init__(self):
        self.total_layers = 0
        self.total_frames = 0
        self.tick_rate = 0
        self.fps = 0
        self.marker_a = 0
        self.marker_b = 0
        self.header_flags = 0
        self.images = []
        self.layers = []
        self.actions = []

def pack_keyframe_payload(kf):
    return struct.pack(
        '<I i i i i i I i i i',
        int(kf.color1),
        int(kf.rot_x),
        int(kf.rot_y),
        int(kf.rot_z),
        int(kf.scale_x),
        int(kf.scale_y),
        int(kf.color2),
        int(kf.pos_x),
        int(kf.pos_y),
        int(kf.pos_z),
    )

def save_a2a_file(anim, filepath):
    layer_count = len(anim.layers)
    header = struct.pack(
        '<4sIIIII',
        b'A2A ',
        int(anim.tick_rate if anim.tick_rate > 0 else 60),
        int(anim.total_frames),
        (int(anim.marker_b) << 16) | (int(anim.marker_a) & 0xFFFF),
        int(anim.header_flags),
        int(layer_count),
    )

    img_meta = {img.raw_name: img for img in anim.images}
    entry_bytes = bytearray()
    key_bytes = bytearray()
    for layer in anim.layers:
        name_b = (layer.name or "").encode('utf-8', 'ignore')[:31]
        name_b += b'\0' * (32 - len(name_b))
        meta = img_meta.get(layer.name)
        w = int(meta.width) if meta else 0
        h = int(meta.height) if meta else 0
        entry_bytes += struct.pack(
            '<32s i I I I i i I I',
            name_b,
            int(layer.parent_index),
            int(layer.unk36),
            int(layer.unk40),
            int(layer.unk44),
            int(w),
            int(h),
            int(layer.unk56),
            int(layer.flags),
        )
        keyframes = sorted(layer.keyframes, key=lambda x: x.time)
        key_bytes += struct.pack('<II', int(layer.layer_id), int(len(keyframes)))
        for kf in keyframes:
            key_bytes += struct.pack('<I', int(kf.time))
            key_bytes += pack_keyframe_payload(kf)

    with open(filepath, 'wb') as f:
        f.write(header)
        f.write(entry_bytes)
        f.write(key_bytes)

# ================= File Parsing =================
def parse_a2a_file(filepath):
    anim = AnimationData()
    with open(filepath, 'rb') as f:
        magic = f.read(4)
        if magic != b'A2A ':
            raise ValueError('Invalid A2A magic')
        # Header dwords: fps, frame_count, marker_pair, flags, layer_count.
        h0, h1, h2, h3, layer_count = struct.unpack('<I I I I I', f.read(20))
        anim.tick_rate = h0
        anim.fps = h0
        anim.total_frames = h1
        anim.marker_a = h2 & 0xFFFF
        anim.marker_b = (h2 >> 16) & 0xFFFF
        anim.header_flags = h3
        # Fixed layer table: layer_count * 0x40 bytes.
        layer_entries = []
        for _ in range(layer_count):
            entry = f.read(64)
            if len(entry) < 64:
                break
            layer_entries.append(entry)
        # Variable key block: [u32 link_or_target, u32 key_count, key_count * 44]
        image_map = {}
        for entry in layer_entries:
            raw_name = entry[0:32].split(b'\0', 1)[0].decode('utf-8', 'ignore')
            # Entry offsets: +48/+52 likely size fields.
            width, height = struct.unpack('<I I', entry[48:56])
            if raw_name not in image_map:
                image_map[raw_name] = len(anim.images)
                anim.images.append(ImageDef(raw_name, width, height, 0, 0))
            img_index = image_map[raw_name]
            block_head = f.read(8)
            if len(block_head) < 8:
                break
            layer_id, num_keys = struct.unpack('<I I', block_head)
            layer = Layer(layer_id, img_index, raw_name)
            layer.parent_index = struct.unpack('<i', entry[32:36])[0]
            layer.unk36 = struct.unpack('<I', entry[36:40])[0]
            layer.unk40 = struct.unpack('<I', entry[40:44])[0]
            layer.unk44 = struct.unpack('<I', entry[44:48])[0]
            layer.unk56 = struct.unpack('<I', entry[56:60])[0]
            layer.flags = struct.unpack('<I', entry[60:64])[0]
            for _k in range(num_keys):
                key_raw = f.read(44)
                if len(key_raw) < 44:
                    break
                time = struct.unpack('<I', key_raw[0:4])[0]
                layer.keyframes.append(Keyframe(time, key_raw[4:44]))
            anim.layers.append(layer)
        anim.total_layers = len(anim.layers)
        anim.total_images = len(anim.images)
        anim.actions = infer_actions(anim)
    return anim

def infer_actions(anim):
    """Infer action segments from header markers and keyframe times."""
    points = {0, max(0, int(anim.total_frames))}
    # marker_a/marker_b come from header dword2 low/high 16-bit values.
    for marker in (anim.marker_a, anim.marker_b):
        if 0 <= marker <= anim.total_frames:
            points.add(int(marker))
    sorted_points = sorted(points)
    actions = []
    idx = 1
    for i in range(len(sorted_points) - 1):
        start = sorted_points[i]
        end = sorted_points[i + 1]
        if end >= start:
            actions.append((f"动作{idx}", start, end))
            idx += 1
    if not actions:
        actions.append(("动作1", 0, max(0, int(anim.total_frames))))
    return actions

# ================= GUI =================
class AnimationViewerApp:
    def __init__(self, root):
        self.root = root
        self.root.title("A2A 动画查看器")
        self.root.geometry("1400x900")
        self.root.minsize(1280, 760)
        
        self.anim = None
        self.base_dir = ""
        self.current_frame = 0.0
        self.playing = False
        self.photo_cache = []
        self.updating_listbox = False
        self.updating_file_listbox = False
        self.current_action_range = (0, 0)
        self.visible_frames = []
        self.current_file_path = ""
        self.playback_speed = 1.0
        self.display_fps = 30.0
        self.coord_space = "绝对坐标"
        self.rotate_mode = "新方向(-rot)"
        self.use_scale = True
        self.scale_center_comp = True
        self.selected_layer_index = None
        self.layer_adjust = {}
        self.layer_thumb_refs = []
        self.updating_layer_xy = False
        self.updating_layer_alpha = False
        self.layer_screen_xy = {}
        self.preview_hidden_layers = set()
        self.last_view_scale = 1.0
        self.last_view_off_x = 0
        self.last_view_off_y = 0
        self.undo_stack = []
        self.redo_stack = []
        
        self.virtual_w = 640
        self.virtual_h = 480

        # Top menu
        self.create_menu()

        style = ttk.Style()
        style.theme_use('clam')

        # Main layout: file panel (left) + canvas (center) + panel (right)
        self.main_frame = tk.Frame(root)
        self.main_frame.pack(fill=tk.BOTH, expand=True)

        self.file_panel = tk.Frame(self.main_frame, width=260, bg="#f3f6fb")
        self.file_panel.pack(side=tk.LEFT, fill=tk.Y)
        self.file_panel.pack_propagate(False)
        tk.Label(
            self.file_panel,
            text="同目录 A2A 文件",
            bg="#f3f6fb",
            fg="#2b2f36",
            anchor=tk.W,
            font=("Microsoft YaHei UI", 10, "bold"),
        ).pack(fill=tk.X, padx=10, pady=(10, 6))
        self.file_list_container = tk.Frame(self.file_panel, bg="#f3f6fb")
        self.file_list_container.pack(fill=tk.BOTH, expand=True, padx=10, pady=(0, 10))
        self.file_listbox = tk.Listbox(
            self.file_list_container,
            exportselection=False,
            bg="#ffffff",
            selectbackground="#2f6fed",
            selectforeground="#ffffff",
        )
        self.file_scrollbar = tk.Scrollbar(self.file_list_container, orient=tk.VERTICAL, command=self.file_listbox.yview)
        self.file_listbox.config(yscrollcommand=self.file_scrollbar.set)
        self.file_listbox.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        self.file_scrollbar.pack(side=tk.RIGHT, fill=tk.Y)
        self.file_listbox.bind("<<ListboxSelect>>", self.on_file_select)

        self.canvas = tk.Canvas(self.main_frame, bg='#282828', highlightthickness=0)
        self.canvas.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)

        self.side_panel = tk.Frame(self.main_frame, width=320, bg="#f5f5f5")
        self.side_panel.pack(side=tk.RIGHT, fill=tk.Y)
        self.side_panel.pack_propagate(False)

        self.play_btn = ttk.Button(self.side_panel, text="播放", command=self.toggle_play_button)
        self.play_btn.pack(side=tk.BOTTOM, fill=tk.X, padx=10, pady=(0, 10))

        self.layer_frame = ttk.LabelFrame(self.side_panel, text="图层图片表")
        self.layer_frame.pack(fill=tk.BOTH, padx=10, pady=6)
        self.layer_tree = ttk.Treeview(self.layer_frame, columns=("vis",), show="tree headings", height=8)
        self.layer_tree.heading("#0", text="图层")
        self.layer_tree.heading("vis", text="显示")
        self.layer_tree.column("#0", width=240, anchor=tk.W)
        self.layer_tree.column("vis", width=52, anchor=tk.CENTER, stretch=False)
        self.layer_tree.pack(fill=tk.BOTH, expand=True, padx=8, pady=(8, 8))
        self.layer_tree.bind("<<TreeviewSelect>>", self.on_layer_select)
        self.layer_tree.bind("<Button-1>", self.on_layer_tree_left_click)
        self.layer_tree.bind("<Button-3>", self.on_layer_tree_right_click)
        self.layer_ctx_menu = tk.Menu(self.root, tearoff=0)
        self.layer_ctx_menu.add_command(label="重命名图片引用", command=self.rename_selected_layer_image)
        layer_btn_row = tk.Frame(self.layer_frame)
        layer_btn_row.pack(fill=tk.X, padx=8, pady=(0, 8))
        ttk.Button(layer_btn_row, text="新增静态层", command=self.add_static_layer).pack(side=tk.LEFT, fill=tk.X, expand=True, padx=(0, 4))
        ttk.Button(layer_btn_row, text="删除选中层", command=self.delete_selected_layer).pack(side=tk.LEFT, fill=tk.X, expand=True, padx=(4, 0))
        move_btn_row = tk.Frame(self.layer_frame)
        move_btn_row.pack(fill=tk.X, padx=8, pady=(0, 8))
        ttk.Button(move_btn_row, text="批量改帧范围坐标", command=self.batch_move_frames).pack(side=tk.LEFT, fill=tk.X, expand=True, padx=(0, 4))
        ttk.Button(move_btn_row, text="跟随图层坐标", command=self.follow_layer_pos).pack(side=tk.LEFT, fill=tk.X, expand=True, padx=(4, 0))
        alpha_btn_row = tk.Frame(self.layer_frame)
        alpha_btn_row.pack(fill=tk.X, padx=8, pady=(0, 8))
        ttk.Button(alpha_btn_row, text="批量改帧范围透明度", command=self.batch_alpha_frames).pack(side=tk.LEFT, fill=tk.X, expand=True, padx=(0, 4))
        ttk.Button(alpha_btn_row, text="跟随图层透明度", command=self.follow_layer_alpha).pack(side=tk.LEFT, fill=tk.X, expand=True, padx=(4, 0))
        self.layer_tune = ttk.LabelFrame(self.layer_frame, text="选中层微调")
        self.layer_tune.pack(fill=tk.X, padx=8, pady=(0, 8))
        r1 = tk.Frame(self.layer_tune)
        r1.pack(fill=tk.X, padx=6, pady=(6, 2))
        tk.Label(r1, text="当前X").pack(side=tk.LEFT)
        ttk.Button(r1, text="-10", width=4, command=lambda: self.nudge_current_xy(-10, 0)).pack(side=tk.LEFT, padx=(6, 4))
        self.layer_x_var = tk.StringVar(value="0")
        tk.Entry(r1, width=10, textvariable=self.layer_x_var).pack(side=tk.LEFT, padx=(0, 4))
        ttk.Button(r1, text="+10", width=4, command=lambda: self.nudge_current_xy(10, 0)).pack(side=tk.LEFT)

        r1b = tk.Frame(self.layer_tune)
        r1b.pack(fill=tk.X, padx=6, pady=(0, 4))
        tk.Label(r1b, text="当前Y").pack(side=tk.LEFT)
        ttk.Button(r1b, text="-10", width=4, command=lambda: self.nudge_current_xy(0, -10)).pack(side=tk.LEFT, padx=(6, 4))
        self.layer_y_var = tk.StringVar(value="0")
        tk.Entry(r1b, width=10, textvariable=self.layer_y_var).pack(side=tk.LEFT, padx=(0, 4))
        ttk.Button(r1b, text="+10", width=4, command=lambda: self.nudge_current_xy(0, 10)).pack(side=tk.LEFT)
        r2 = tk.Frame(self.layer_tune)
        r2.pack(fill=tk.X, padx=6, pady=(0, 6))
        tk.Label(r2, text="补偿").pack(side=tk.LEFT)
        self.layer_comp_combo = ttk.Combobox(r2, state="readonly", values=["默认", "关", "中心", "仅X", "仅Y"], width=8)
        self.layer_comp_combo.pack(side=tk.LEFT, padx=(6, 0))
        self.layer_comp_combo.current(0)
        self.layer_comp_combo.bind("<<ComboboxSelected>>", self.on_layer_tune_changed)
        ttk.Button(r2, text="继承上一帧", command=self.inherit_prev_frame).pack(side=tk.RIGHT)
        vis_row = tk.Frame(self.layer_tune)
        vis_row.pack(fill=tk.X, padx=6, pady=(0, 6))
        tk.Label(vis_row, text="当前A").pack(side=tk.LEFT)
        ttk.Button(vis_row, text="-10", width=4, command=lambda: self.nudge_current_alpha(-10)).pack(side=tk.LEFT, padx=(6, 4))
        self.layer_alpha_var = tk.StringVar(value="255")
        self.layer_alpha_entry = tk.Entry(vis_row, width=10, textvariable=self.layer_alpha_var)
        self.layer_alpha_entry.pack(side=tk.LEFT, padx=(0, 4))
        self.layer_alpha_entry.bind("<Return>", self.on_layer_alpha_changed)
        self.layer_alpha_entry.bind("<FocusOut>", self.on_layer_alpha_changed)
        ttk.Button(vis_row, text="+10", width=4, command=lambda: self.nudge_current_alpha(10)).pack(side=tk.LEFT)
        self.layer_final_var = tk.StringVar(value="当前XY: - , -")
        tk.Label(self.layer_tune, textvariable=self.layer_final_var, anchor=tk.W).pack(fill=tk.X, padx=6, pady=(0, 6))

        self.action_frame = ttk.LabelFrame(self.side_panel, text="动作段")
        self.action_frame.pack(fill=tk.X, padx=10, pady=6)
        self.action_combo = ttk.Combobox(self.action_frame, state="readonly")
        self.action_combo.pack(fill=tk.X, padx=8, pady=8)
        self.action_combo.bind("<<ComboboxSelected>>", self.on_action_selected)

        self.frame_section = ttk.LabelFrame(self.side_panel, text="帧列表")
        self.frame_section.pack(fill=tk.BOTH, expand=True, padx=10, pady=(6, 10))
        self.frame_list_container = tk.Frame(self.frame_section)
        self.frame_list_container.pack(fill=tk.BOTH, expand=True, padx=8, pady=(0, 8))
        self.frame_listbox = tk.Listbox(
            self.frame_list_container,
            exportselection=False,
            bg="#ffffff",
            selectbackground="#2f6fed",
            selectforeground="#ffffff",
        )
        self.frame_scrollbar = tk.Scrollbar(self.frame_list_container, orient=tk.VERTICAL, command=self.frame_listbox.yview)
        self.frame_listbox.config(yscrollcommand=self.frame_scrollbar.set)
        self.frame_listbox.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        self.frame_scrollbar.pack(side=tk.RIGHT, fill=tk.Y)
        self.frame_listbox.bind("<<ListboxSelect>>", self.on_frame_select)

        # Bind controls
        self.root.bind('<space>', self.toggle_play)
        self.root.bind('<Control-z>', self.on_undo_shortcut)
        self.root.bind('<Control-y>', self.on_redo_shortcut)
        
        # Start render loop
        self.animate()

    def create_menu(self):
        """Create top menu."""
        menubar = tk.Menu(self.root)
        menubar.add_command(label="打开", command=self.open_file)
        menubar.add_command(label="保存", command=self.save_file)
        menubar.add_command(label="另存为", command=self.save_file_as)
        menubar.add_command(label="撤销", command=self.undo)
        menubar.add_command(label="重做", command=self.redo)
        self.root.config(menu=menubar)

    def snapshot_state(self):
        if not self.anim:
            return None
        return copy.deepcopy((self.anim, self.layer_adjust, self.current_frame, self.current_action_range))

    def restore_state(self, snap):
        if snap is None:
            return
        self.anim, self.layer_adjust, self.current_frame, self.current_action_range = copy.deepcopy(snap)
        self.layer_screen_xy = {}
        self.load_images()
        self.populate_layer_table()
        self.populate_action_list()
        self.render_frame()

    def push_undo(self):
        snap = self.snapshot_state()
        if snap is None:
            return
        self.undo_stack.append(snap)
        if len(self.undo_stack) > 50:
            self.undo_stack.pop(0)
        self.redo_stack.clear()

    def undo(self):
        if not self.undo_stack:
            return
        cur = self.snapshot_state()
        snap = self.undo_stack.pop()
        if cur is not None:
            self.redo_stack.append(cur)
        self.restore_state(snap)

    def redo(self):
        if not self.redo_stack:
            return
        cur = self.snapshot_state()
        snap = self.redo_stack.pop()
        if cur is not None:
            self.undo_stack.append(cur)
        self.restore_state(snap)

    def on_undo_shortcut(self, _event=None):
        self.undo()

    def on_redo_shortcut(self, _event=None):
        self.redo()

    def open_file(self):
        """Open and parse A2A file."""
        file_path = filedialog.askopenfilename(
            title="选择动画文件",
            filetypes=[("A2A Animation", "*.a2a")]
        )
        if not file_path:
            return

        self.populate_file_list(os.path.dirname(file_path), file_path)
        self.load_a2a_file(file_path)

    def save_file(self):
        if not self.anim or not self.current_file_path:
            return
        try:
            save_a2a_file(self.anim, self.current_file_path)
            messagebox.showinfo("保存", f"已保存：{os.path.basename(self.current_file_path)}")
        except Exception as e:
            messagebox.showerror("错误", f"保存失败: {e}")

    def save_file_as(self):
        if not self.anim:
            return
        path = filedialog.asksaveasfilename(
            title="另存为 A2A",
            defaultextension=".a2a",
            filetypes=[("A2A Animation", "*.a2a"), ("All Files", "*.*")],
            initialdir=self.base_dir or None,
            initialfile=os.path.basename(self.current_file_path) if self.current_file_path else "new.a2a",
        )
        if not path:
            return
        try:
            save_a2a_file(self.anim, path)
            self.current_file_path = path
            self.base_dir = os.path.dirname(path)
            self.populate_file_list(self.base_dir, path)
            self.root.title(f"A2A 动画查看器 - {os.path.basename(path)}")
            messagebox.showinfo("保存", f"已保存：{os.path.basename(path)}")
        except Exception as e:
            messagebox.showerror("错误", f"保存失败: {e}")

    def populate_file_list(self, directory, selected_path=""):
        self.file_listbox.delete(0, tk.END)
        if not directory or not os.path.isdir(directory):
            return
        files = []
        for name in sorted(os.listdir(directory), key=lambda s: s.lower()):
            if name.lower().endswith(".a2a"):
                files.append(os.path.join(directory, name))
                self.file_listbox.insert(tk.END, name)
        if not files:
            return
        if not selected_path:
            selected_path = files[0]
        selected_norm = os.path.normcase(os.path.normpath(selected_path))
        selected_index = 0
        for idx, full in enumerate(files):
            if os.path.normcase(os.path.normpath(full)) == selected_norm:
                selected_index = idx
                break
        self.updating_file_listbox = True
        self.file_listbox.selection_clear(0, tk.END)
        self.file_listbox.selection_set(selected_index)
        self.file_listbox.see(selected_index)
        self.updating_file_listbox = False

    def on_file_select(self, _event=None):
        if self.updating_file_listbox:
            return
        sel = self.file_listbox.curselection()
        if not sel or not self.base_dir:
            return
        filename = self.file_listbox.get(sel[0])
        file_path = os.path.join(self.base_dir, filename)
        if os.path.normcase(os.path.normpath(file_path)) == os.path.normcase(os.path.normpath(self.current_file_path)):
            return
        self.load_a2a_file(file_path)

    def load_a2a_file(self, file_path):
        try:
            self.anim = parse_a2a_file(file_path)
            self.base_dir = os.path.dirname(file_path)
            self.current_file_path = file_path
            self.current_frame = 0.0
            self.playing = False
            self.layer_adjust = {}
            self.preview_hidden_layers = set()
            self.selected_layer_index = None
            self.layer_screen_xy = {}
            self.load_images()
            self.populate_layer_table()
            self.populate_action_list()
            self.root.title(f"A2A 动画查看器 - {os.path.basename(file_path)}")
            self.render_frame()
        except Exception as e:
            messagebox.showerror("错误", f"解析文件失败: {e}")

    def populate_action_list(self):
        self.action_combo["values"] = []
        if not self.anim:
            return
        values = [f"{name} [{start}-{end}]" for name, start, end in self.anim.actions]
        self.action_combo["values"] = values
        if values:
            self.action_combo.current(0)
            self.current_action_range = (self.anim.actions[0][1], self.anim.actions[0][2])
        self.populate_frame_list()

    def on_action_selected(self, _event=None):
        if not self.anim:
            return
        idx = self.action_combo.current()
        if idx < 0 or idx >= len(self.anim.actions):
            return
        _, start, end = self.anim.actions[idx]
        self.current_action_range = (start, end)
        self.populate_frame_list()
        self.current_frame = float(start)
        self.select_frame_in_list(start)
        self.render_frame()

    def populate_frame_list(self):
        self.frame_listbox.delete(0, tk.END)
        if not self.anim:
            return
        start, end = self.current_action_range
        end = min(end, int(self.anim.total_frames))
        start = max(0, min(start, end))
        # Inclusive range: action [start, end]
        self.visible_frames = list(range(start, end + 1))
        for i in self.visible_frames:
            self.frame_listbox.insert(tk.END, f"第 {i:04d} 帧")
        self.select_frame_in_list(int(self.current_frame))

    def select_frame_in_list(self, index):
        if self.frame_listbox.size() == 0 or not self.visible_frames:
            return
        if index < self.visible_frames[0]:
            index = self.visible_frames[0]
        if index > self.visible_frames[-1]:
            index = self.visible_frames[-1]
        list_index = self.visible_frames.index(index)
        self.updating_listbox = True
        self.frame_listbox.selection_clear(0, tk.END)
        self.frame_listbox.selection_set(list_index)
        self.frame_listbox.see(list_index)
        self.updating_listbox = False

    def on_frame_select(self, _event=None):
        if self.updating_listbox or not self.anim:
            return
        sel = self.frame_listbox.curselection()
        if not sel:
            return
        list_index = sel[0]
        if 0 <= list_index < len(self.visible_frames):
            self.current_frame = float(self.visible_frames[list_index])
        self.render_frame()

    def play(self):
        if self.anim:
            start, end = self.current_action_range
            if self.current_frame >= end:
                self.current_frame = float(start)
                self.select_frame_in_list(int(self.current_frame))
            self.playing = True
            self.play_btn.config(text="暂停")

    def toggle_play_button(self):
        if not self.anim:
            return
        if self.playing:
            self.pause()
        else:
            self.play()

    def populate_layer_table(self):
        self.layer_tree.delete(*self.layer_tree.get_children())
        self.layer_thumb_refs.clear()
        if not self.anim:
            return
        for idx, layer in enumerate(self.anim.layers):
            if layer.img_index < 0 or layer.img_index >= len(self.anim.images):
                continue
            img_def = self.anim.images[layer.img_index]
            thumb = img_def.pil_img.copy()
            thumb.thumbnail((26, 26), Image.Resampling.BILINEAR)
            tk_thumb = ImageTk.PhotoImage(thumb)
            self.layer_thumb_refs.append(tk_thumb)
            vis = "☐" if idx in self.preview_hidden_layers else "☑"
            label = f"{idx:02d}  {layer.name}"
            self.layer_tree.insert("", "end", iid=str(idx), text=label, image=tk_thumb, values=(vis,))

    def on_layer_tree_left_click(self, event):
        if not self.anim:
            return
        iid = self.layer_tree.identify_row(event.y)
        if not iid:
            return
        col = self.layer_tree.identify_column(event.x)
        # Click on right "显示" column toggles preview visibility.
        if col == "#1":
            idx = int(iid)
            if idx in self.preview_hidden_layers:
                self.preview_hidden_layers.remove(idx)
            else:
                self.preview_hidden_layers.add(idx)
            self.populate_layer_table()
            self.layer_tree.selection_set(iid)
            self.layer_tree.see(iid)
            self.selected_layer_index = idx
            self.render_frame()
            return "break"

    def on_layer_select(self, _event=None):
        sel = self.layer_tree.selection()
        if not sel:
            self.selected_layer_index = None
            self.layer_final_var.set("当前XY: - , -")
            self.updating_layer_alpha = True
            self.layer_alpha_var.set("-")
            self.updating_layer_alpha = False
            return
        self.selected_layer_index = int(sel[0])
        cfg = self.layer_adjust.get(self.selected_layer_index, {"comp": "默认"})
        self.updating_layer_xy = True
        if self.selected_layer_index in self.layer_screen_xy:
            fx, fy = self.layer_screen_xy[self.selected_layer_index]
            self.layer_x_var.set(str(int(fx)))
            self.layer_y_var.set(str(int(fy)))
        self.updating_layer_xy = False
        comp = cfg.get("comp", "默认")
        if comp not in list(self.layer_comp_combo["values"]):
            comp = "默认"
        self.layer_comp_combo.set(comp)
        layer = self.anim.layers[self.selected_layer_index]
        state = layer.get_state(self.current_frame)
        if state:
            alpha = (int(state.color1) >> 24) & 0xFF
            self.updating_layer_alpha = True
            self.layer_alpha_var.set(str(alpha))
            self.updating_layer_alpha = False
        else:
            self.updating_layer_alpha = True
            self.layer_alpha_var.set("-")
            self.updating_layer_alpha = False
        self.render_frame()

    def on_layer_tree_right_click(self, event):
        if not self.anim:
            return
        iid = self.layer_tree.identify_row(event.y)
        if not iid:
            return
        self.layer_tree.selection_set(iid)
        self.selected_layer_index = int(iid)
        self.layer_ctx_menu.tk_popup(event.x_root, event.y_root)
        self.layer_ctx_menu.grab_release()

    def rename_selected_layer_image(self):
        if self.selected_layer_index is None or not self.anim:
            return
        idx = self.selected_layer_index
        if not (0 <= idx < len(self.anim.layers)):
            return
        layer = self.anim.layers[idx]
        new_name = simpledialog.askstring(
            "重命名图片引用",
            "输入新的图片名（例如 if_xxx_001.png）:",
            initialvalue=layer.name or "",
            parent=self.root,
        )
        if not new_name:
            return
        new_name = new_name.strip()
        if not new_name:
            return
        self.push_undo()

        img_index = None
        for i, img in enumerate(self.anim.images):
            if img.raw_name == new_name:
                img_index = i
                break
        if img_index is None:
            # Create image def for new reference and probe size if file exists.
            w, h = 64, 64
            p = os.path.join(self.base_dir, new_name[:-4] + ".agi.png" if new_name.lower().endswith(".png") else new_name + ".agi.png")
            if os.path.exists(p):
                try:
                    with Image.open(p) as im:
                        w, h = im.size
                except Exception:
                    pass
            self.anim.images.append(ImageDef(new_name, w, h, 0, 0))
            img_index = len(self.anim.images) - 1

        layer.name = new_name
        layer.img_index = img_index
        self.anim.total_images = len(self.anim.images)
        self.load_images()
        self.populate_layer_table()
        self.layer_tree.selection_set(str(idx))
        self.layer_tree.see(str(idx))
        self.render_frame()

    def on_layer_alpha_changed(self, _event=None):
        if self.selected_layer_index is None or not self.anim:
            return
        if self.updating_layer_alpha:
            return
        s = (self.layer_alpha_var.get() or "").strip()
        if s == "" or s == "-":
            return
        try:
            a = max(0, min(255, int(s)))
        except Exception:
            return
        layer = self.anim.layers[self.selected_layer_index]
        self.push_undo()
        kf = self.get_or_create_keyframe_at_current(layer)
        rgb = int(kf.color1) & 0x00FFFFFF
        kf.color1 = ((a & 0xFF) << 24) | rgb
        self.updating_layer_alpha = True
        self.layer_alpha_var.set(str(a))
        self.updating_layer_alpha = False
        self.render_frame()

    def nudge_current_alpha(self, da):
        if self.selected_layer_index is None:
            return
        try:
            cur = int((self.layer_alpha_var.get() or "").strip())
        except Exception:
            cur = 255
        nxt = max(0, min(255, cur + int(da)))
        self.updating_layer_alpha = True
        self.layer_alpha_var.set(str(nxt))
        self.updating_layer_alpha = False
        self.on_layer_alpha_changed()

    def on_layer_tune_changed(self, _event=None):
        if self.selected_layer_index is None:
            return
        if self.updating_layer_xy:
            return
        old_xy = self.layer_screen_xy.get(self.selected_layer_index)
        if old_xy and self.anim and self.last_view_scale > 0:
            try:
                old_x, old_y = old_xy
                new_x = int(self.layer_x_var.get().strip())
                new_y = int(self.layer_y_var.get().strip())
                dx_world = int(round((new_x - old_x) / self.last_view_scale))
                dy_world = int(round((new_y - old_y) / self.last_view_scale))
                if dx_world != 0 or dy_world != 0:
                    self.push_undo()
                    layer = self.anim.layers[self.selected_layer_index]
                    kf = self.get_or_create_keyframe_at_current(layer)
                    kf.pos_x += dx_world
                    kf.pos_y += dy_world
            except Exception:
                pass
        self.layer_adjust[self.selected_layer_index] = {
            "comp": self.layer_comp_combo.get() or "默认",
        }
        self.render_frame()

    def nudge_current_xy(self, dx, dy):
        if self.selected_layer_index is None:
            return
        try:
            x = int(self.layer_x_var.get().strip())
            y = int(self.layer_y_var.get().strip())
        except Exception:
            x, y = self.layer_screen_xy.get(self.selected_layer_index, (0, 0))
        self.layer_x_var.set(str(int(x) + int(dx)))
        self.layer_y_var.set(str(int(y) + int(dy)))
        self.on_layer_tune_changed()

    def get_or_create_keyframe_at_current(self, layer):
        t = int(self.current_frame)
        for kf in layer.keyframes:
            if int(kf.time) == t:
                return kf
        src = None
        for kf in sorted(layer.keyframes, key=lambda x: x.time):
            if int(kf.time) <= t:
                src = kf
            else:
                break
        if src is None and layer.keyframes:
            src = sorted(layer.keyframes, key=lambda x: x.time)[0]
        if src is None:
            payload = struct.pack('<I i i i i i I i i i', 0xFFFFFFFF, 0, 0, 0, 100, 100, 0xFFFFFFFF, 320, 240, 0)
            new_kf = Keyframe(t, payload)
        else:
            new_kf = Keyframe(t, pack_keyframe_payload(src))
        new_kf.time = t
        layer.keyframes.append(new_kf)
        layer.keyframes.sort(key=lambda x: x.time)
        return new_kf

    def inherit_prev_frame(self):
        if self.selected_layer_index is None or not self.anim:
            return
        layer = self.anim.layers[self.selected_layer_index]
        t = int(self.current_frame)
        prev = None
        for kf in sorted(layer.keyframes, key=lambda x: x.time):
            if int(kf.time) < t:
                prev = kf
            else:
                break
        if prev is None:
            return
        self.push_undo()
        cur = self.get_or_create_keyframe_at_current(layer)
        copied = Keyframe(t, pack_keyframe_payload(prev))
        copied.time = t
        cur.color1 = copied.color1
        cur.rot_x = copied.rot_x
        cur.rot_y = copied.rot_y
        cur.rot_z = copied.rot_z
        cur.scale_x = copied.scale_x
        cur.scale_y = copied.scale_y
        cur.color2 = copied.color2
        cur.pos_x = copied.pos_x
        cur.pos_y = copied.pos_y
        cur.pos_z = copied.pos_z
        self.render_frame()

    def delete_selected_layer(self):
        if not self.anim:
            return
        sel = self.layer_tree.selection()
        if not sel:
            return
        idx = int(sel[0])
        if not (0 <= idx < len(self.anim.layers)):
            return
        self.push_undo()
        del self.anim.layers[idx]
        # Re-link parent indices after deletion.
        for layer in self.anim.layers:
            if layer.parent_index == idx:
                layer.parent_index = -1
            elif layer.parent_index > idx:
                layer.parent_index -= 1
        # Shift per-layer adjustments after deletion.
        new_adjust = {}
        for k, v in self.layer_adjust.items():
            if k == idx:
                continue
            nk = k - 1 if k > idx else k
            new_adjust[nk] = v
        self.layer_adjust = new_adjust
        new_hidden = set()
        for k in self.preview_hidden_layers:
            if k == idx:
                continue
            nk = k - 1 if k > idx else k
            new_hidden.add(nk)
        self.preview_hidden_layers = new_hidden
        self.selected_layer_index = None
        self.anim.total_layers = len(self.anim.layers)
        self.anim.actions = infer_actions(self.anim)
        start, _ = self.current_action_range
        self.current_action_range = (start, min(self.anim.total_frames, self.current_action_range[1]))
        self.populate_layer_table()
        self.render_frame()

    def add_static_layer(self):
        if not self.anim:
            return
        res = self.prompt_new_layer()
        if not res:
            return
        name, init_x, init_y = res
        # Input X/Y are rendered coords; convert to raw world coords.
        if self.last_view_scale > 0:
            init_x = int(round((int(init_x) - self.last_view_off_x) / self.last_view_scale))
            init_y = int(round((int(init_y) - self.last_view_off_y) / self.last_view_scale))
        self.push_undo()
        img_index = None
        for i, img in enumerate(self.anim.images):
            if img.raw_name == name:
                img_index = i
                break
        if img_index is None:
            # Try to probe image size from base_dir
            w, h = 64, 64
            p = os.path.join(self.base_dir, name[:-4] + ".agi.png" if name.lower().endswith(".png") else name + ".agi.png")
            if os.path.exists(p):
                try:
                    with Image.open(p) as im:
                        w, h = im.size
                except Exception:
                    pass
            self.anim.images.append(ImageDef(name, w, h, 0, 0))
            img_index = len(self.anim.images) - 1
        layer_id = max([l.layer_id for l in self.anim.layers], default=-1) + 1
        layer = Layer(layer_id, img_index, name)
        layer.parent_index = -1
        layer.unk36 = 0xFFFFFFFF
        layer.unk40 = 0x3000
        layer.unk44 = 0xFFFFFFFF
        layer.unk56 = 0
        layer.flags = 0
        kf = Keyframe(0, b"\x00" * 40)
        kf.color1 = 0xFFFFFFFF
        kf.color2 = 0xFFFFFFFF
        kf.scale_x = 100
        kf.scale_y = 100
        kf.rot_x = kf.rot_y = kf.rot_z = 0
        kf.pos_x = int(init_x)
        kf.pos_y = int(init_y)
        kf.pos_z = 0
        layer.keyframes.append(kf)
        self.anim.layers.append(layer)
        self.anim.total_layers = len(self.anim.layers)
        self.anim.total_images = len(self.anim.images)
        self.load_images()
        self.populate_layer_table()
        self.render_frame()

    def prompt_new_layer(self):
        dlg = tk.Toplevel(self.root)
        dlg.title("新增静态层")
        dlg.transient(self.root)
        dlg.grab_set()
        dlg.update_idletasks()
        rw = self.root.winfo_width()
        rh = self.root.winfo_height()
        rx = self.root.winfo_rootx()
        ry = self.root.winfo_rooty()
        ww, wh = 340, 170
        px = rx + max(0, (rw - ww) // 2)
        py = ry + max(0, (rh - wh) // 2)
        dlg.geometry(f"{ww}x{wh}+{px}+{py}")
        tk.Label(dlg, text="图片名:").grid(row=0, column=0, sticky="w", padx=8, pady=(8, 4))
        name_var = tk.StringVar(value="")
        x_var = tk.IntVar(value=320)
        y_var = tk.IntVar(value=240)
        tk.Entry(dlg, textvariable=name_var, width=28).grid(row=0, column=1, padx=8, pady=(8, 4))
        tk.Label(dlg, text="初始X:").grid(row=1, column=0, sticky="w", padx=8, pady=4)
        tk.Spinbox(dlg, from_=-4096, to=4096, textvariable=x_var, width=10).grid(row=1, column=1, sticky="w", padx=8, pady=4)
        tk.Label(dlg, text="初始Y:").grid(row=2, column=0, sticky="w", padx=8, pady=4)
        tk.Spinbox(dlg, from_=-4096, to=4096, textvariable=y_var, width=10).grid(row=2, column=1, sticky="w", padx=8, pady=4)
        out = {"ok": False}
        def on_ok():
            nm = name_var.get().strip()
            if not nm:
                messagebox.showwarning("提示", "图片名不能为空", parent=dlg)
                return
            out["ok"] = True
            out["name"] = nm
            out["x"] = int(x_var.get())
            out["y"] = int(y_var.get())
            dlg.destroy()
        def on_cancel():
            dlg.destroy()
        btn_row = tk.Frame(dlg)
        btn_row.grid(row=3, column=0, columnspan=2, pady=(8, 10))
        ttk.Button(btn_row, text="确定", command=on_ok).pack(side=tk.LEFT, padx=(0, 6))
        ttk.Button(btn_row, text="取消", command=on_cancel).pack(side=tk.LEFT)
        dlg.wait_window()
        if not out.get("ok"):
            return None
        return out["name"], out["x"], out["y"]

    def batch_move_frames(self):
        if self.selected_layer_index is None or not self.anim:
            return
        dlg = tk.Toplevel(self.root)
        dlg.title("批量改帧范围坐标")
        dlg.transient(self.root)
        dlg.grab_set()
        dlg.update_idletasks()
        rw, rh = self.root.winfo_width(), self.root.winfo_height()
        rx, ry = self.root.winfo_rootx(), self.root.winfo_rooty()
        ww, wh = 420, 210
        dlg.geometry(f"{ww}x{wh}+{rx + max(0, (rw-ww)//2)}+{ry + max(0, (rh-wh)//2)}")

        start_var = tk.StringVar(value="0")
        end_var = tk.StringVar(value=str(int(self.current_frame)))
        expr_var = tk.StringVar(value="")
        x_var = tk.StringVar(value="")
        y_var = tk.StringVar(value="")
        tk.Label(dlg, text="起始帧:").grid(row=0, column=0, sticky="w", padx=8, pady=(8, 4))
        tk.Entry(dlg, textvariable=start_var, width=10).grid(row=0, column=1, sticky="w", padx=8, pady=(8, 4))
        tk.Label(dlg, text="结束帧:").grid(row=0, column=2, sticky="w", padx=8, pady=(8, 4))
        tk.Entry(dlg, textvariable=end_var, width=10).grid(row=0, column=3, sticky="w", padx=8, pady=(8, 4))
        tk.Label(dlg, text="坐标表达式(可选):").grid(row=1, column=0, sticky="w", padx=8, pady=4)
        tk.Entry(dlg, textvariable=expr_var, width=30).grid(row=1, column=1, columnspan=3, sticky="we", padx=8, pady=4)
        tk.Label(dlg, text="X表达式:").grid(row=2, column=0, sticky="w", padx=8, pady=4)
        tk.Entry(dlg, textvariable=x_var, width=14).grid(row=2, column=1, sticky="w", padx=8, pady=4)
        tk.Label(dlg, text="Y表达式:").grid(row=2, column=2, sticky="w", padx=8, pady=4)
        tk.Entry(dlg, textvariable=y_var, width=14).grid(row=2, column=3, sticky="w", padx=8, pady=4)
        tk.Label(dlg, text="坐标表达式优先于X/Y；示例: 120 / #+10 / #-5 / 100+200j").grid(row=3, column=0, columnspan=4, sticky="w", padx=8, pady=4)

        def parse_axis_expr(v):
            s = (v or "").strip().lower().replace("i", "j")
            if not s:
                return ("keep", 0)
            if s.startswith("#"):
                d = int(s[1:])
                return ("ramp", d)
            return ("abs", int(s))

        def apply_batch():
            try:
                st = int(start_var.get().strip())
                ed = int(end_var.get().strip())
                if ed < st:
                    st, ed = ed, st
                comp = (expr_var.get() or "").strip().lower().replace("i", "j")
                ex = (x_var.get() or "").strip()
                ey = (y_var.get() or "").strip()
                if comp:
                    if "j" in comp:
                        c = complex(comp)
                        ex = str(int(c.real))
                        ey = str(int(c.imag))
                    elif "," in comp:
                        a, b = comp.split(",", 1)
                        ex, ey = a.strip(), b.strip()
                    else:
                        ex = comp
                        ey = comp
                layer_idx = self.selected_layer_index
                layer = self.anim.layers[layer_idx]
                cur_xy = self.layer_screen_xy.get(layer_idx, (0, 0))
                mode_x, val_x = parse_axis_expr(ex)
                mode_y, val_y = parse_axis_expr(ey)
                old_t = self.current_frame
                # Baseline at start frame for ramp expressions.
                self.current_frame = float(st)
                self.render_frame()
                base_ox, base_oy = self.layer_screen_xy.get(layer_idx, cur_xy)
                self.push_undo()

                # Seal interpolation boundaries by forcing anchor keys.
                if st > 0:
                    self.current_frame = float(st - 1)
                    self.render_frame()
                    self.get_or_create_keyframe_at_current(layer)
                self.current_frame = float(st)
                self.render_frame()
                self.get_or_create_keyframe_at_current(layer)
                if (ed + 1) <= int(self.anim.total_frames):
                    self.current_frame = float(ed + 1)
                    self.render_frame()
                    self.get_or_create_keyframe_at_current(layer)

                for t in range(st, ed + 1):
                    self.current_frame = float(t)
                    # Get render-space base for this frame.
                    self.render_frame()
                    ox, oy = self.layer_screen_xy.get(layer_idx, cur_xy)
                    nx, ny = int(ox), int(oy)
                    if mode_x == "abs":
                        nx = val_x
                    elif mode_x == "ramp":
                        nx = int(base_ox) + (t - st) * val_x
                    if mode_y == "abs":
                        ny = val_y
                    elif mode_y == "ramp":
                        ny = int(base_oy) + (t - st) * val_y
                    # Convert screen delta back to raw keyframe delta at this frame.
                    if self.last_view_scale > 0:
                        dx_world = int(round((int(nx) - int(ox)) / self.last_view_scale))
                        dy_world = int(round((int(ny) - int(oy)) / self.last_view_scale))
                        kf = self.get_or_create_keyframe_at_current(layer)
                        if dx_world or dy_world:
                            kf.pos_x += dx_world
                            kf.pos_y += dy_world
                self.current_frame = old_t
                dlg.destroy()
                self.render_frame()
            except Exception as e:
                messagebox.showwarning("提示", f"输入格式错误: {e}", parent=dlg)

        btn = tk.Frame(dlg)
        btn.grid(row=4, column=0, columnspan=4, pady=(8, 10))
        ttk.Button(btn, text="应用", command=apply_batch).pack(side=tk.LEFT, padx=(0, 6))
        ttk.Button(btn, text="取消", command=dlg.destroy).pack(side=tk.LEFT)
        dlg.wait_window()

    def batch_alpha_frames(self):
        if self.selected_layer_index is None or not self.anim:
            return
        dlg = tk.Toplevel(self.root)
        dlg.title("批量改帧范围透明度")
        dlg.transient(self.root)
        dlg.grab_set()
        dlg.update_idletasks()
        rw, rh = self.root.winfo_width(), self.root.winfo_height()
        rx, ry = self.root.winfo_rootx(), self.root.winfo_rooty()
        ww, wh = 360, 180
        dlg.geometry(f"{ww}x{wh}+{rx + max(0, (rw-ww)//2)}+{ry + max(0, (rh-wh)//2)}")

        start_var = tk.StringVar(value="0")
        end_var = tk.StringVar(value=str(int(self.current_frame)))
        alpha_var = tk.StringVar(value="255")

        tk.Label(dlg, text="起始帧:").grid(row=0, column=0, sticky="w", padx=8, pady=(10, 4))
        tk.Entry(dlg, textvariable=start_var, width=10).grid(row=0, column=1, sticky="w", padx=8, pady=(10, 4))
        tk.Label(dlg, text="结束帧:").grid(row=0, column=2, sticky="w", padx=8, pady=(10, 4))
        tk.Entry(dlg, textvariable=end_var, width=10).grid(row=0, column=3, sticky="w", padx=8, pady=(10, 4))
        tk.Label(dlg, text="透明度(0-255):").grid(row=1, column=0, sticky="w", padx=8, pady=4)
        tk.Entry(dlg, textvariable=alpha_var, width=10).grid(row=1, column=1, sticky="w", padx=8, pady=4)
        tk.Label(dlg, text="仅修改选中层；范围外帧保持不变。").grid(row=2, column=0, columnspan=4, sticky="w", padx=8, pady=4)

        def apply_alpha():
            try:
                st = int(start_var.get().strip())
                ed = int(end_var.get().strip())
                if ed < st:
                    st, ed = ed, st
                st = max(0, st)
                ed = min(int(self.anim.total_frames), ed)
                a = int(alpha_var.get().strip())
                a = max(0, min(255, a))
                layer = self.anim.layers[self.selected_layer_index]
                old_t = self.current_frame
                self.push_undo()

                # Seal interpolation boundaries.
                if st > 0:
                    self.current_frame = float(st - 1)
                    self.get_or_create_keyframe_at_current(layer)
                self.current_frame = float(st)
                self.get_or_create_keyframe_at_current(layer)
                if (ed + 1) <= int(self.anim.total_frames):
                    self.current_frame = float(ed + 1)
                    self.get_or_create_keyframe_at_current(layer)

                for t in range(st, ed + 1):
                    self.current_frame = float(t)
                    kf = self.get_or_create_keyframe_at_current(layer)
                    rgb = int(kf.color1) & 0x00FFFFFF
                    kf.color1 = ((a & 0xFF) << 24) | rgb

                self.current_frame = old_t
                dlg.destroy()
                self.render_frame()
            except Exception as e:
                messagebox.showwarning("提示", f"输入格式错误: {e}", parent=dlg)

        btn = tk.Frame(dlg)
        btn.grid(row=3, column=0, columnspan=4, pady=(8, 10))
        ttk.Button(btn, text="应用", command=apply_alpha).pack(side=tk.LEFT, padx=(0, 6))
        ttk.Button(btn, text="取消", command=dlg.destroy).pack(side=tk.LEFT)
        dlg.wait_window()

    def follow_layer_alpha(self):
        if not self.anim or len(self.anim.layers) < 2:
            return
        dlg = tk.Toplevel(self.root)
        dlg.title("跟随图层透明度")
        dlg.transient(self.root)
        dlg.grab_set()
        dlg.update_idletasks()
        rw, rh = self.root.winfo_width(), self.root.winfo_height()
        rx, ry = self.root.winfo_rootx(), self.root.winfo_rooty()
        ww, wh = 700, 420
        dlg.geometry(f"{ww}x{wh}+{rx + max(0, (rw-ww)//2)}+{ry + max(0, (rh-wh)//2)}")

        row = tk.Frame(dlg)
        row.pack(fill=tk.BOTH, expand=True, padx=10, pady=(10, 6))
        left = ttk.LabelFrame(row, text="要修改的图层")
        right = ttk.LabelFrame(row, text="跟随来源图层")
        left.pack(side=tk.LEFT, fill=tk.BOTH, expand=True, padx=(0, 6))
        right.pack(side=tk.LEFT, fill=tk.BOTH, expand=True, padx=(6, 0))

        t1 = ttk.Treeview(left, show="tree")
        t2 = ttk.Treeview(right, show="tree")
        t1.pack(fill=tk.BOTH, expand=True, padx=6, pady=6)
        t2.pack(fill=tk.BOTH, expand=True, padx=6, pady=6)

        thumb_refs = []
        for idx, layer in enumerate(self.anim.layers):
            if layer.img_index < 0 or layer.img_index >= len(self.anim.images):
                continue
            img_def = self.anim.images[layer.img_index]
            thumb = img_def.pil_img.copy()
            thumb.thumbnail((26, 26), Image.Resampling.BILINEAR)
            tk_thumb = ImageTk.PhotoImage(thumb)
            thumb_refs.append(tk_thumb)
            label = f"{idx:02d}  {layer.name}"
            t1.insert("", "end", iid=str(idx), text=label, image=tk_thumb)
            t2.insert("", "end", iid=str(idx), text=label, image=tk_thumb)

        if self.selected_layer_index is not None and str(self.selected_layer_index) in t1.get_children():
            t1.selection_set(str(self.selected_layer_index))
            t1.see(str(self.selected_layer_index))

        info = tk.Label(dlg, text="把右侧图层的每帧透明度复制到左侧图层。", anchor=tk.W)
        info.pack(fill=tk.X, padx=10, pady=(0, 6))

        def apply_follow():
            s1 = t1.selection()
            s2 = t2.selection()
            if not s1 or not s2:
                messagebox.showwarning("提示", "请先在左右各选一个图层。", parent=dlg)
                return
            dst = int(s1[0])
            src = int(s2[0])
            if dst == src:
                messagebox.showwarning("提示", "目标图层和来源图层不能相同。", parent=dlg)
                return
            dst_layer = self.anim.layers[dst]
            src_layer = self.anim.layers[src]
            old_t = self.current_frame
            self.push_undo()
            for t in range(0, int(self.anim.total_frames) + 1):
                src_state = src_layer.get_state(float(t))
                if not src_state:
                    continue
                src_a = (int(src_state.color1) >> 24) & 0xFF
                self.current_frame = float(t)
                dst_kf = self.get_or_create_keyframe_at_current(dst_layer)
                rgb = int(dst_kf.color1) & 0x00FFFFFF
                dst_kf.color1 = ((src_a & 0xFF) << 24) | rgb
            self.current_frame = old_t
            dlg.destroy()
            self.render_frame()

        btn = tk.Frame(dlg)
        btn.pack(fill=tk.X, padx=10, pady=(0, 10))
        ttk.Button(btn, text="应用", command=apply_follow).pack(side=tk.LEFT, padx=(0, 6))
        ttk.Button(btn, text="取消", command=dlg.destroy).pack(side=tk.LEFT)
        dlg.wait_window()

    def follow_layer_pos(self):
        if not self.anim or len(self.anim.layers) < 2:
            return
        dlg = tk.Toplevel(self.root)
        dlg.title("跟随图层坐标")
        dlg.transient(self.root)
        dlg.grab_set()
        dlg.update_idletasks()
        rw, rh = self.root.winfo_width(), self.root.winfo_height()
        rx, ry = self.root.winfo_rootx(), self.root.winfo_rooty()
        ww, wh = 700, 420
        dlg.geometry(f"{ww}x{wh}+{rx + max(0, (rw-ww)//2)}+{ry + max(0, (rh-wh)//2)}")

        row = tk.Frame(dlg)
        row.pack(fill=tk.BOTH, expand=True, padx=10, pady=(10, 6))
        left = ttk.LabelFrame(row, text="要修改的图层")
        right = ttk.LabelFrame(row, text="跟随来源图层")
        left.pack(side=tk.LEFT, fill=tk.BOTH, expand=True, padx=(0, 6))
        right.pack(side=tk.LEFT, fill=tk.BOTH, expand=True, padx=(6, 0))

        t1 = ttk.Treeview(left, show="tree")
        t2 = ttk.Treeview(right, show="tree")
        t1.pack(fill=tk.BOTH, expand=True, padx=6, pady=6)
        t2.pack(fill=tk.BOTH, expand=True, padx=6, pady=6)

        thumb_refs = []
        for idx, layer in enumerate(self.anim.layers):
            if layer.img_index < 0 or layer.img_index >= len(self.anim.images):
                continue
            img_def = self.anim.images[layer.img_index]
            thumb = img_def.pil_img.copy()
            thumb.thumbnail((26, 26), Image.Resampling.BILINEAR)
            tk_thumb = ImageTk.PhotoImage(thumb)
            thumb_refs.append(tk_thumb)
            label = f"{idx:02d}  {layer.name}"
            t1.insert("", "end", iid=str(idx), text=label, image=tk_thumb)
            t2.insert("", "end", iid=str(idx), text=label, image=tk_thumb)

        if self.selected_layer_index is not None and str(self.selected_layer_index) in t1.get_children():
            t1.selection_set(str(self.selected_layer_index))
            t1.see(str(self.selected_layer_index))

        info = tk.Label(dlg, text="把右侧图层的每帧坐标(X/Y)复制到左侧图层。", anchor=tk.W)
        info.pack(fill=tk.X, padx=10, pady=(0, 6))

        def apply_follow():
            s1 = t1.selection()
            s2 = t2.selection()
            if not s1 or not s2:
                messagebox.showwarning("提示", "请先在左右各选一个图层。", parent=dlg)
                return
            dst = int(s1[0])
            src = int(s2[0])
            if dst == src:
                messagebox.showwarning("提示", "目标图层和来源图层不能相同。", parent=dlg)
                return
            dst_layer = self.anim.layers[dst]
            src_layer = self.anim.layers[src]
            old_t = self.current_frame
            self.push_undo()
            for t in range(0, int(self.anim.total_frames) + 1):
                src_state = src_layer.get_state(float(t))
                if not src_state:
                    continue
                self.current_frame = float(t)
                dst_kf = self.get_or_create_keyframe_at_current(dst_layer)
                dst_kf.pos_x = int(src_state.pos_x)
                dst_kf.pos_y = int(src_state.pos_y)
            self.current_frame = old_t
            dlg.destroy()
            self.render_frame()

        btn = tk.Frame(dlg)
        btn.pack(fill=tk.X, padx=10, pady=(0, 10))
        ttk.Button(btn, text="应用", command=apply_follow).pack(side=tk.LEFT, padx=(0, 6))
        ttk.Button(btn, text="取消", command=dlg.destroy).pack(side=tk.LEFT)
        dlg.wait_window()

    def pause(self):
        self.playing = False
        self.play_btn.config(text="播放")

    def stop(self):
        self.playing = False
        self.play_btn.config(text="播放")
        if self.anim:
            start, _end = self.current_action_range
            self.current_frame = float(start)
            self.select_frame_in_list(int(self.current_frame))
            self.render_frame()

    def load_images(self):
        """Load image assets, or create placeholders."""
        if not self.anim: return
        
        for img_def in self.anim.images:
            full_path = os.path.join(self.base_dir, img_def.actual_name)
            if os.path.exists(full_path):
                img_def.pil_img = Image.open(full_path).convert("RGBA")
            else:
                w, h = max(10, img_def.width), max(10, img_def.height)
                placeholder = Image.new("RGBA", (w, h), (255, 0, 255, 128))
                draw = ImageDraw.Draw(placeholder)
                draw.rectangle([0, 0, w-1, h-1], outline=(255, 255, 255, 255), width=2)
                img_def.pil_img = placeholder

    def toggle_play(self, event=None):
        if self.anim:
            if self.playing:
                self.pause()
            else:
                self.play()

    def step_forward(self, event=None):
        if self.anim:
            _start, end = self.current_action_range
            max_frame = max(0, min(self.anim.total_frames, end))
            self.current_frame = min(self.current_frame + 1, max_frame)
            self.select_frame_in_list(int(self.current_frame))
            if not self.playing: self.render_frame()

    def step_backward(self, event=None):
        if self.anim:
            start, _end = self.current_action_range
            self.current_frame = max(self.current_frame - 1, start)
            self.select_frame_in_list(int(self.current_frame))
            if not self.playing: self.render_frame()

    def render_frame(self):
        self.canvas.delete("all")
        self.photo_cache.clear()
        
        center_x, center_y = self.canvas.winfo_width() // 2, self.canvas.winfo_height() // 2
        
        # Hint text before loading
        if not self.anim:
            self.layer_final_var.set("当前XY: - , -")
            self.canvas.create_text(
                center_x,
                center_y,
                text="点击顶部 [打开] 选择 A2A 动画文件",
                fill="gray",
                font=("Microsoft YaHei UI", 16),
            )
            return
        
        # Virtual-resolution viewport (A2A coords are close to 640x480 absolute coords).
        cw, ch = self.canvas.winfo_width(), self.canvas.winfo_height()
        # Keep original spacing close to game by avoiding upscale.
        view_scale = min(cw / self.virtual_w, ch / self.virtual_h, 1.0)
        self.last_view_scale = view_scale
        view_off_x = int((cw - self.virtual_w * view_scale) * 0.5)
        view_off_y = int((ch - self.virtual_h * view_scale) * 0.5)
        self.last_view_off_x = view_off_x
        self.last_view_off_y = view_off_y
        self.canvas.create_rectangle(
            view_off_x,
            view_off_y,
            view_off_x + int(self.virtual_w * view_scale),
            view_off_y + int(self.virtual_h * view_scale),
            outline="#555555",
        )
        
        # Render layers
        world_tf_cache = {}
        selected_layer_updated = False
        for layer_index, layer in enumerate(self.anim.layers):
            if layer_index in self.preview_hidden_layers:
                continue
            if layer.img_index < 0 or layer.img_index >= len(self.anim.images):
                continue
                
            state = layer.get_state(self.current_frame)
            if not state: continue
                
            img_def = self.anim.images[layer.img_index]
            pil_img = img_def.pil_img
            
            world_x, world_y = float(state.pos_x), float(state.pos_y)
            world_rot = float(state.rot_z)
            world_scx = float(state.scale_x) / 100.0
            world_scy = float(state.scale_y) / 100.0
            if not key_visible_from_color1(state.color1):
                continue
            sc_x = world_scx
            sc_y = world_scy
            # Confirmed rule: entry +0x24 == 5 => mirrored X.
            if layer.unk36 == 5:
                sc_x = -sc_x
            if sc_x == 0 or sc_y == 0: continue
            
            # Always render from a copy to avoid mutating source image alpha.
            tmp_img = pil_img.copy()
            # Mirror
            if sc_x < 0:
                tmp_img = tmp_img.transpose(Image.FLIP_LEFT_RIGHT)
                sc_x = abs(sc_x)
            if sc_y < 0:
                tmp_img = tmp_img.transpose(Image.FLIP_TOP_BOTTOM)
                sc_y = abs(sc_y)
                
            # Scale
            new_w = int(tmp_img.width * sc_x)
            new_h = int(tmp_img.height * sc_y)
            if new_w <= 0 or new_h <= 0: continue
            
            if new_w != tmp_img.width or new_h != tmp_img.height:
                tmp_img = tmp_img.resize((new_w, new_h), Image.Resampling.BILINEAR)
                
            # Rotate
            rot_draw = -world_rot if self.rotate_mode == "新方向(-rot)" else world_rot
            if rot_draw != 0:
                tmp_img = tmp_img.rotate(rot_draw, expand=True)

            # Apply keyframe alpha from color1 (ARGB high byte).
            alpha = (int(state.color1) >> 24) & 0xFF
            if alpha <= 0:
                continue
            if alpha < 255:
                a = tmp_img.getchannel("A")
                # Multiply source alpha by key alpha.
                a = a.point(lambda v: (v * alpha) // 255)
                tmp_img.putalpha(a)

            # Position
            final_x = int(view_off_x + world_x * view_scale)
            final_y = int(view_off_y + world_y * view_scale)
            layer_cfg = self.layer_adjust.get(layer_index, None)
            comp_mode = layer_cfg.get("comp", "默认") if layer_cfg else "默认"
            do_x = False
            do_y = False
            if comp_mode == "默认":
                if self.scale_center_comp:
                    do_x = abs(sc_x - 1.0) > 1e-6
                    do_y = abs(sc_y - 1.0) > 1e-6
                    # For strong one-axis stretch (e.g. 1000/100), center compensation
                    # on dominant axis usually causes obvious drift.
                    if abs(sc_x - sc_y) > 0.5:
                        if abs(sc_x - 1.0) > abs(sc_y - 1.0) * 2.0:
                            do_x = False
                        if abs(sc_y - 1.0) > abs(sc_x - 1.0) * 2.0:
                            do_y = False
            elif comp_mode == "中心":
                do_x = True
                do_y = True
            elif comp_mode == "仅X":
                do_x = True
            elif comp_mode == "仅Y":
                do_y = True

            if do_x or do_y:
                dx = int(((new_w - pil_img.width) * view_scale) * 0.5)
                dy = int(((new_h - pil_img.height) * view_scale) * 0.5)
                if do_x:
                    final_x -= dx
                if do_y:
                    final_y -= dy
            self.layer_screen_xy[layer_index] = (int(final_x), int(final_y))

            if self.selected_layer_index == layer_index:
                self.layer_final_var.set(f"当前XY: {int(final_x)} , {int(final_y)}")
                self.updating_layer_xy = True
                self.layer_x_var.set(str(int(final_x)))
                self.layer_y_var.set(str(int(final_y)))
                self.updating_layer_xy = False
                self.updating_layer_alpha = True
                self.layer_alpha_var.set(str((int(state.color1) >> 24) & 0xFF))
                self.updating_layer_alpha = False
                selected_layer_updated = True

            # Draw
            tk_img = ImageTk.PhotoImage(tmp_img)
            self.photo_cache.append(tk_img)  # keep reference
            # Keep top-left anchor for all layers.
            self.canvas.create_image(final_x, final_y, image=tk_img, anchor=tk.NW)

        if self.selected_layer_index is not None and not selected_layer_updated:
            self.layer_final_var.set("当前XY: - , -")
            self.updating_layer_alpha = True
            self.layer_alpha_var.set("-")
            self.updating_layer_alpha = False
            
        # HUD
        info_text = (
            f"帧: {int(self.current_frame)} / {self.anim.total_frames}   "
            f"Rate: {self.anim.tick_rate}  播放: {int(self.display_fps)}fps   标记A/B: {self.anim.marker_a}/{self.anim.marker_b}"
        )
        self.canvas.create_text(
            10, 10, text=info_text, fill="white", anchor=tk.NW, font=("Microsoft YaHei UI", 12)
        )

    def get_world_transform(self, layer_index, current_time, cache, visiting):
        if layer_index in cache:
            return cache[layer_index]
        if layer_index in visiting:
            return (0.0, 0.0, 0.0, 1.0, 1.0)
        visiting.add(layer_index)
        layer = self.anim.layers[layer_index]
        state = layer.get_state(current_time)
        x = float(state.pos_x) if state else 0.0
        y = float(state.pos_y) if state else 0.0
        rot = float(state.rot_z) if state else 0.0
        scx = (float(state.scale_x) / 100.0) if state else 1.0
        scy = (float(state.scale_y) / 100.0) if state else 1.0
        p = layer.parent_index
        if 0 <= p < len(self.anim.layers):
            px, py, prot, pscx, pscy = self.get_world_transform(p, current_time, cache, visiting)
            lx = x * pscx
            ly = y * pscy
            rad = math.radians(prot)
            rx = lx * math.cos(rad) - ly * math.sin(rad)
            ry = lx * math.sin(rad) + ly * math.cos(rad)
            x = px + rx
            y = py + ry
            rot = prot + rot
            scx = pscx * scx
            scy = pscy * scy
        visiting.remove(layer_index)
        cache[layer_index] = (x, y, rot, scx, scy)
        return cache[layer_index]

    def animate(self):
        if self.playing and self.anim:
            start, end = self.current_action_range
            if end <= start:
                start, end = 0, self.anim.total_frames
            tick_rate = self.anim.tick_rate if self.anim.tick_rate > 0 else 30.0
            frame_step = (tick_rate / self.display_fps) * self.playback_speed
            if frame_step <= 0:
                frame_step = 1.0
            max_frame = max(start, end)
            if self.current_frame < max_frame:
                self.current_frame += frame_step
                if self.current_frame > max_frame:
                    self.current_frame = float(max_frame)
            else:
                self.current_frame = float(max_frame)
                self.playing = False
                self.play_btn.config(text="播放")
            self.select_frame_in_list(int(self.current_frame))
                
        self.render_frame()
        # Drive UI at display fps (game-like), timeline advances by tick_rate/display_fps.
        interval = int(max(1, 1000 / self.display_fps))
        self.root.after(interval, self.animate)

if __name__ == "__main__":
    root = tk.Tk()
    app = AnimationViewerApp(root)
    root.mainloop()

