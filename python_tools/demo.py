import os, sys, json, struct, re
from pathlib import Path
from collections import deque
import textJson
from char import encode_cp932_or_die, make_translation_converter

def u32(b, o): return struct.unpack_from("<I", b, o)[0]
def u16(b, o): return struct.unpack_from("<H", b, o)[0]
def w32(buf, o, v): struct.pack_into("<I", buf, o, v & 0xFFFFFFFF)

SEC_RE = re.compile(r"^\s*\[([^\]]+)\]\s*(?://.*)?$")
ROW_RE = re.compile(r"^\s*#(\d+)\s*=\s*(.+?)\s*$")

def readz_cp932(data: bytes, off: int) -> str:
    if off < 0 or off >= len(data): return ""
    end = data.find(b"\x00", off)
    if end < 0: end = len(data)
    return data[off:end].decode("cp932").replace("\n", "\\n")

def writez_cp932(s: str) -> bytes:
    return s.replace("\\n", "\n").encode("cp932") + b"\x00"

def parse_key(key: str):
    a, b = key.split("_", 1)
    return int(a, 16), int(b, 16)

def load_tbl_char3(tbl_path: Path) -> dict:
    if not tbl_path.exists(): return {}
    out, sec, mp = {}, None, {}
    for raw in tbl_path.read_text(encoding="utf-8", errors="ignore").splitlines():
        s = raw.strip()
        if not s or s.startswith(";"): continue
        m = SEC_RE.match(s)
        if m:
            if sec is not None: out[sec] = mp
            sec, mp = m.group(1).strip(), {}
            continue
        m = ROW_RE.match(raw)
        if not m or sec is None: continue
        idx = int(m.group(1))
        parts = [p.strip() for p in m.group(2).split(",")]
        if len(parts) >= 3:
            try: mp[idx] = int(parts[2], 10)
            except ValueError: pass
    if sec is not None: out[sec] = mp
    return out

def split_personality_char(x: int):
    if x == -1: return None, None
    if 100 <= x <= 999: return x // 100, x % 100
    return 0, x

def pick_name(names: dict, packed: int) -> str:
    p, cid = split_personality_char(packed)
    if p is None or cid is None: return ""
    t = names.get(cid)
    if not t: return str(packed)
    if p in t and t[p]: return t[p]
    if 0 in t and t[0]: return t[0]
    return str(packed)

def extract_one(dat_path: Path, tbl_sections: dict, names: dict):
    data = dat_path.read_bytes()
    n, pc, end0 = len(data), 0, 0

    cur_tag = None
    cur_row = None

    out = []

    while pc + 4 <= n:
        op, ln, tag = data[pc], data[pc + 1], u16(data, pc + 2)
        if ln < 4 or (ln % 4) or pc + ln > n:
            break
        p0 = u32(data, pc + 4) if ln >= 8 else 0

        if op == 0x10:
            # 10 改为“设置当前说话人上下文”，不入队不出队
            cur_tag = int(tag)
            cur_row = int(p0)

        elif op == 0x5D:
            off = p0

            # 这几种不提取，也不改变当前说话人（不会“消耗10”）
            if off == 0 or off >= n or data[off] == 0:
                pc += ln
                continue

            s = readz_cp932(data, off)
            if s == "■":
                pc += ln
                continue

            name = ""
            if cur_tag is not None and cur_row is not None:
                row_map = tbl_sections.get(f"TBL00_{cur_tag:02d}")
                packed = row_map.get(cur_row) if row_map else None
                if packed is not None:
                    name = pick_name(names, packed)

            out.append({
                "key": f"0x{pc+4:X}_0x{off:X}",
                "original": s,
                "translation": "",
                "stage": 0,
                "context": name
            })

        pc += ln
        if op == 0x00:
            end0 += 1
            if end0 >= 2:
                break

    return out

def inject_one(dat_path: Path, json_path: Path, out_path: Path):
    src = bytearray(dat_path.read_bytes())
    items = json.loads(json_path.read_text(encoding="utf-8", errors="ignore"))
    if not isinstance(items, list) or not items:
        out_path.write_bytes(src); return

    pairs, offs = [], []
    for it in items:
        if not isinstance(it, dict): continue
        k = it.get("key", "")
        if not isinstance(k, str) or "_" not in k: continue
        ptr, off = parse_key(k)
        pairs.append((ptr, it))
        offs.append(off)

    if not offs:
        out_path.write_bytes(src); return

    text_start = min(offs)
    new = bytearray(src[:text_start])

    for ptr, it in pairs:
        stage = int(it.get("stage", 0))
        s = conv(it.get("translation", "") if stage == 1 else it.get("original", ""))
        pos = len(new)
        new.extend(writez_cp932(s))
        w32(new, ptr, pos)

    out_path.parent.mkdir(parents=True, exist_ok=True)
    out_path.write_bytes(new)

def iter_opdemo(extract_dir: Path):
    for dirpath, _, files in os.walk(extract_dir):
        for fn in files:
            if fn.lower() == "slg_opdemo.dat":
                p = Path(dirpath) / fn
                yield p, Path(dirpath).name



if __name__ == "__main__":
    if len(sys.argv) < 4:
        print("Usage:\n  demo.py d <extract_dir> <out_json_dir>\n  demo.py e <extract_dir> <in_json_dir> <out_dat_dir>")
        raise SystemExit(2)

    mode = sys.argv[1].lower()
    extract_dir = Path(sys.argv[2]).resolve()

    if mode == "d":
        out_dir = Path(sys.argv[3]).resolve()
        out_dir.mkdir(parents=True, exist_ok=True)

        adv_dir = extract_dir / "adv"
        voicetbl_dir = extract_dir / "slg" / "voicetbl"
        if not adv_dir.exists(): raise SystemExit(f"[ERR] adv not found: {adv_dir}")
        if not voicetbl_dir.exists(): raise SystemExit(f"[ERR] voicetbl not found: {voicetbl_dir}")

        names, _ = textJson.load_char_names(adv_dir)

        cnt = 0
        for dat_path, stage_id in iter_opdemo(extract_dir):
            cnt += 1
            tbl_path = voicetbl_dir / f"slgV{stage_id}.tbl"
            tbl_sections = load_tbl_char3(tbl_path)
            items = extract_one(dat_path, tbl_sections, names)
            (out_dir / f"{stage_id}.json").write_text(json.dumps(items, ensure_ascii=False, indent=2), encoding="utf-8")

        sys.stderr.write(f"[OK] extracted {cnt} files\n")
        sys.exit(0)
    

    if mode == "e":
        if len(sys.argv) != 5:
            print("Usage: demo.py e <extract_dir> <in_json_dir> <out_dat_dir>")
            raise SystemExit(2)
        
        conv = make_translation_converter()

        json_dir = Path(sys.argv[3]).resolve()
        out_dat_dir = Path(sys.argv[4]).resolve()
        out_dat_dir.mkdir(parents=True, exist_ok=True)

        cnt = 0
        for dat_path, stage_id in iter_opdemo(extract_dir):
            jp = json_dir / f"{stage_id}.json"
            if not jp.exists(): 
                continue
            cnt += 1
            rel = dat_path.relative_to(extract_dir)
            out_path = out_dat_dir / rel
            inject_one(dat_path, jp, out_path)

        sys.stderr.write(f"[OK] injected {cnt} files\n")
        sys.exit(0)

    raise SystemExit("mode must be d or e")