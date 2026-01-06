#!/usr/bin/env python3
# -*- coding: utf-8 -*-

import json
import re
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple

IMG_BASE = "https://ga3.wbnb.top/face"
VOICE_BASE = "https://ga3.wbnb.top/advvoice"

PUSHI_RE = re.compile(r"^\s*PUSHI\s+([0-9A-Fa-f]{8})\s*$")
PUSHL_RE = re.compile(r"^\s*PUSHL\s+([0-9A-Fa-f]{2})\s*$")
PUSHG_RE = re.compile(r"^\s*PUSHG\s+([0-9A-Fa-f]{8})\s*$")
PUSHF_RE = re.compile(r"^\s*PUSHF\s+([0-9A-Fa-f]{8})\s*$")

CALLB_RE = re.compile(r"^\s*CALLB\s+([0-9A-Fa-f]{2}),\s*([0-9A-Fa-f]{8})\s*$")
CALLI_RE = re.compile(r"^\s*CALLI\s+([0-9A-Fa-f]{2}),\s*([0-9A-Fa-f]{8})\s*$")
CALLL_RE = re.compile(r"^\s*CALLL\s+([0-9A-Fa-f]{2}),\s*([0-9A-Fa-f]{8})\s*$")
CALLG_RE = re.compile(r"^\s*CALLG\s+([0-9A-Fa-f]{2}),\s*([0-9A-Fa-f]{8})\s*$")

OTHER_OP_RE = re.compile(
    r"^\s*(?:"
    r"NOP|PUSHR|POP|POPL|POPG|POPF|STIL|STIG|STIF|INCL|DECL|INCG|DECG|EXCH|NEG|"
    r"ADD|SUB|MUL|DIV|MOD|AND|OR|NOT|EQ|NEQ|LT|GT|LEQ|GEQ|"
    r"B|BZ|BNZ|BTBL|JMPL|JMPG|CALLL|CALLG|RET|RETN|YIELD"
    r")\b"
)

TBL_FILE_RE = re.compile(r"^char(\d{3})\.tbl$", re.IGNORECASE)
ID_RE = re.compile(r"^\s*ID\s*=\s*(\d+)\s*$", re.IGNORECASE)
NAME_RE = re.compile(r"^\s*NAME(\d{2})\s*=\s*(.*?)\s*$", re.IGNORECASE)

SECTION_RE = re.compile(r"^\s*\[([A-Za-z0-9_]+)\]\s*$")
FACE_MAP_RE = re.compile(r"^\s*#\s*([0-9A-Fa-f]+)\s*=\s*([^\s;\/]+)")

HEX_KEY_RE = re.compile(r"^\s*([0-9A-Fa-f]+)")          # key 前导 hex
PAREN_COMMENT_RE = re.compile(r"\([^)]*\)")
MAP_LINE_RE = re.compile(r"^\s*([0-9A-Fa-f]{2,4})\s*=\s*(.*?)\s*$")


def u32(hex8: str) -> int:
    return int(hex8, 16) & 0xFFFFFFFF

def s32(x: int) -> int:
    return x - 0x100000000 if x & 0x80000000 else x

def is_minus1_u32(x: int) -> bool:
    return s32(x) == -1

def split_packed_id(raw_u32: Optional[int]) -> Tuple[Optional[int], Optional[int]]:
    if raw_u32 is None or is_minus1_u32(raw_u32):
        return None, None
    return raw_u32 & 0xFFFF, (raw_u32 >> 16) & 0xFFFF

def id16_or_none(raw_u32: Optional[int]) -> Optional[int]:
    if raw_u32 is None or is_minus1_u32(raw_u32):
        return None
    return raw_u32 & 0xFFFF

def voice_url(voice_id_16: int) -> str:
    return f"{VOICE_BASE}/{voice_id_16}.wav"

def is_section_header(line: str) -> bool:
    t = line.strip()
    return t.startswith("[") and t.endswith("]")

def is_text_param_line(line: str) -> bool:
    t = line.strip()
    if not t:
        return False
    if t.startswith("[") or t.startswith("@") or t.startswith("__"):
        return False
    if PUSHI_RE.match(t) or PUSHL_RE.match(t) or PUSHG_RE.match(t) or PUSHF_RE.match(t):
        return False
    if CALLB_RE.match(t) or CALLI_RE.match(t) or CALLL_RE.match(t) or CALLG_RE.match(t):
        return False
    if OTHER_OP_RE.match(t):
        return False
    return True

def key_from_codeline(code_line: int) -> str:
    return format(max(code_line, 0), "X").upper()

def parse_codeline_from_key(key: str) -> Optional[int]:
    m = HEX_KEY_RE.match(key or "")
    if not m:
        return None
    try:
        return int(m.group(1), 16)
    except ValueError:
        return None

def strip_paren_comments(s: str) -> str:
    out = PAREN_COMMENT_RE.sub("", s)
    out = re.sub(r"\s+", " ", out).strip()
    return out

def read_text_guess(p: Path) -> str:
    data = p.read_bytes()
    try:
        return data.decode("utf-8")
    except UnicodeDecodeError:
        return data.decode("cp932", errors="replace")

def iter_txt_files(root: Path) -> List[Path]:
    return sorted([p for p in root.rglob("*.txt") if p.is_file()])

def iter_json_files(root: Path) -> List[Path]:
    return sorted([p for p in root.rglob("*.json") if p.is_file()])


@dataclass
class StackVal:
    kind: str  # "int" / "sym" / "text"
    value: Any
    code_line: Optional[int] = None  # text 用

@dataclass
class UiState:
    char_id: Optional[int] = None
    name_idx: Optional[int] = None
    face_id: Optional[int] = None
    voice_id: Optional[int] = None
    thought: bool = False
    narration: bool = False


# -------- tbl：名字 + face --------

def load_char_names(tbl_dir: Path) -> Tuple[Dict[int, Dict[int, str]], Dict[int, Dict[int, str]]]:
    names: Dict[int, Dict[int, str]] = {}
    face_l: Dict[int, Dict[int, str]] = {}

    def flush_one(cid: Optional[int], cn: Dict[int, str], fl: Dict[int, str]) -> None:
        if cid is None:
            return
        if cn:
            names[cid] = dict(cn)
        if fl:
            face_l[cid] = dict(fl)

    for p in sorted(tbl_dir.glob("char*.tbl")):
        if not TBL_FILE_RE.match(p.name):
            continue

        lines = p.read_text(encoding="cp932", errors="replace").splitlines()

        cur_id: Optional[int] = None
        cur_names: Dict[int, str] = {}
        cur_face_l: Dict[int, str] = {}
        cur_section = ""

        for line in lines:
            s = line.strip()
            if not s or s.startswith(";") or s.startswith("//"):
                continue

            msec = SECTION_RE.match(s)
            if msec:
                cur_section = msec.group(1).upper()
                continue

            mid = ID_RE.match(line)
            if mid:
                new_id = int(mid.group(1), 10)
                if cur_id is not None and new_id != cur_id:
                    flush_one(cur_id, cur_names, cur_face_l)
                    cur_names.clear()
                    cur_face_l.clear()
                cur_id = new_id
                continue

            if cur_section == "BASIC":
                mn = NAME_RE.match(line)
                if mn and cur_id is not None:
                    idx = int(mn.group(1), 10)
                    nm = mn.group(2).strip()
                    if nm:
                        cur_names[idx] = nm
                continue

            # 彻底忽略 FACE_S，只解析 FACE_L
            if cur_section == "FACE_L":
                mm = FACE_MAP_RE.match(line)
                if mm and cur_id is not None:
                    fid = int(mm.group(1))
                    fn = mm.group(2).strip()
                    if fn:
                        cur_face_l[fid] = fn
                continue

        flush_one(cur_id, cur_names, cur_face_l)

    return names, face_l

def pick_name(char_names: Dict[int, Dict[int, str]], char_id: Optional[int], name_idx: Optional[int]) -> str:
    if char_id is None:
        return ""
    table = char_names.get(char_id)
    if not table:
        return f"ID{char_id:04d}"
    idx = 0 if name_idx is None else name_idx
    if idx in table:
        return table[idx]
    if 0 in table:
        return table[0]
    return table[sorted(table.keys())[0]]

def pick_face_filename(
    face_l: Dict[int, Dict[int, str]],
    char_id: Optional[int],
    face_id: Optional[int],
) -> str:
    if char_id is None:
        return ""

    table = face_l.get(char_id, {})
    if not table:
        return ""

    # 先按 face_id 查（face_id 可能是 None）
    if face_id is not None:
        fn = table.get(face_id, "")
        if fn:
            return fn

    # 匹配不到 -> 用默认（最小 key）
    default_key = min(table.keys())
    return table.get(default_key, "")

def build_context(
    st: UiState,
    char_names: Dict[int, Dict[int, str]],
    face_l: Dict[int, Dict[int, str]],
) -> str:
    parts: List[str] = []
    if st.narration:
        parts.append("旁白")

    fn = pick_face_filename(face_l, st.char_id, st.face_id)
    if fn:
        parts.append(f"<img src=\"{IMG_BASE}/{fn}.png\">")
    #elif st.char_id is not None and st.face_id is not None:parts.append(f"【FACE_L缺失】char={st.char_id:03d} face=0x{st.face_id:04X}")        

    name = pick_name(char_names, st.char_id, st.name_idx)
    if name:
        if st.thought:
            name = f"{name}【思考】"
        parts.append(name)

    if st.voice_id is not None:
        parts.append(f"<audio controls src=\"{voice_url(st.voice_id)}\"></audio>")

    return "\n".join(parts).strip()


# -------- 提取 --------

def extract_from_text(
    script_text: str,
    char_names: Dict[int, Dict[int, str]],
    face_l: Dict[int, Dict[int, str]],
) -> List[dict]:
    in_code = False
    stack: List[StackVal] = []
    st = UiState()

    out: List[dict] = []
    used_keys: set[str] = set()
    code_line = 0  # [CODE] 内行号（从 [CODE] 后第一行算 1）

    def make_key(cl: int) -> str:
        base = key_from_codeline(cl)
        key = base
        n = 2
        while key in used_keys:
            key = f"{base}_{n}"
            n += 1
        used_keys.add(key)
        return key

    def emit_text(text_line: str, cl: int, context: str) -> None:
        out.append({
            "key": make_key(cl),
            "original": text_line,
            "translation": "",
            "stage": 0,
            "context": context,
        })

    def handle_call(argc: int, func: int) -> None:
        nonlocal stack, st

        take = min(argc, len(stack))
        args = stack[-take:]
        stack = stack[:-take]

        if func in (0x13, 0x14):
            st.narration = False
            st.thought = (func == 0x14)

            p1 = args[0].value if len(args) >= 1 and args[0].kind == "int" else None
            p2 = args[1].value if len(args) >= 2 and args[1].kind == "int" else None
            p3 = args[2].value if len(args) >= 3 and args[2].kind == "int" else None

            st.char_id, st.name_idx = split_packed_id(p1 if isinstance(p1, int) else None)
            st.face_id = id16_or_none(p2 if isinstance(p2, int) else None)
            st.voice_id = id16_or_none(p3 if isinstance(p3, int) else None)

        elif func == 0x4B:
            st.narration = True
            st.thought = False
            st.char_id = None
            st.name_idx = None
            st.face_id = None
            p6 = args[5].value if len(args) >= 6 and args[5].kind == "int" else None
            st.voice_id = id16_or_none(p6 if isinstance(p6, int) else None)

        if func in (0x00, 0x01, 0x02):
            texts: List[StackVal] = [a for a in args if a.kind == "text" and str(a.value).strip()]
            if not texts:
                return

            if func == 0x00:
                for a in texts:
                    emit_text(str(a.value), a.code_line or 0, build_context(st, char_names, face_l))
            elif func == 0x01:
                for k, a in enumerate(texts, start=1):
                    emit_text(str(a.value), a.code_line or 0, f"选项{k}")
            else:
                for k, a in enumerate(texts, start=1):
                    emit_text(str(a.value), a.code_line or 0, f"选项{k}")

    def apply_stack_effect(op: str) -> None:
        nonlocal stack

        if op in ("ADD", "SUB", "MUL", "DIV", "MOD", "AND", "OR", "EQ", "NEQ", "LT", "GT", "LEQ", "GEQ"):
            if len(stack) >= 2:
                stack.pop()
                stack.pop()
            stack.append(StackVal("sym", op))
            return

        if op in ("NEG", "NOT"):
            if stack:
                stack.pop()
            stack.append(StackVal("sym", op))
            return

        if op == "EXCH":
            if len(stack) >= 2:
                stack[-1], stack[-2] = stack[-2], stack[-1]
            return

        if op in ("POP", "POPL", "POPG", "POPF", "STIL", "STIG", "STIF"):
            if stack:
                stack.pop()
            return

        if op in ("BZ", "BNZ", "BTBL"):
            if stack:
                stack.pop()
            return

        if op == "PUSHR":
            stack.append(StackVal("sym", "R"))
            return

    for raw in script_text.splitlines():
        line = raw.rstrip("\r\n")

        if line.strip() == "[CODE]":
            in_code = True
            code_line = 0
            continue

        if in_code and is_section_header(line) and line.strip() != "[CODE]":
            in_code = False

        if not in_code:
            continue

        code_line += 1

        m = PUSHI_RE.match(line)
        if m:
            stack.append(StackVal("int", u32(m.group(1))))
            continue
        m = PUSHL_RE.match(line)
        if m:
            stack.append(StackVal("sym", f"L:{m.group(1).upper()}"))
            continue
        m = PUSHG_RE.match(line)
        if m:
            stack.append(StackVal("sym", f"G:{m.group(1).upper()}"))
            continue
        m = PUSHF_RE.match(line)
        if m:
            stack.append(StackVal("sym", f"F:{m.group(1).upper()}"))
            continue

        m = CALLB_RE.match(line) or CALLI_RE.match(line)
        if m:
            argc = int(m.group(1), 16)
            func = u32(m.group(2))
            handle_call(argc, func)
            continue

        m = CALLL_RE.match(line) or CALLG_RE.match(line)
        if m:
            argc = int(m.group(1), 16)
            take = min(argc, len(stack))
            stack = stack[:-take]
            continue

        op = line.strip().split()[0] if line.strip() else ""
        if op and OTHER_OP_RE.match(op):
            apply_stack_effect(op)
            continue

        if is_text_param_line(line):
            stack.append(StackVal("text", line, code_line))
            continue

    return out


# -------- list.txt（可选） --------

def parse_list_txt(list_path: Path) -> Dict[str, str]:
    m: Dict[str, str] = {}
    if not list_path.exists():
        return m

    for raw in list_path.read_text(encoding="cp932", errors="replace").splitlines():
        line = raw.strip()
        if not line:
            continue
        parts = re.split(r"\s+", line, maxsplit=1)
        fname = parts[0].strip()
        note = parts[1].strip() if len(parts) > 1 else ""

        if "：" in note:
            title = note.split("：", 1)[1].strip()
        elif ":" in note:
            title = note.split(":", 1)[1].strip()
        else:
            title = note.strip()

        m[fname] = strip_paren_comments(title)

    return m

def output_json_path(out_root: Path, rel_txt: Path, title: str) -> Path:
    stem = rel_txt.stem
    if title and Path(title).stem.strip() != Path(stem).stem.strip():
        name = f"{stem}({title}).json"
    else:
        name = f"{stem}.json"
    return out_root / rel_txt.parent / name


# -------- 回写映射表：UTF-16LE --------

def load_writeback_map(map_path: Path) -> Dict[str, int]:
    """
    UTF-16LE(BOM/LE 都可)：
      889F=亚
    重复定义：保留第一个，后续忽略。
    """
    txt = map_path.read_text(encoding="utf-16", errors="replace")
    out: Dict[str, int] = {}

    for raw in txt.splitlines():
        line = raw.strip()
        if not line or line.startswith(";") or line.startswith("//"):
            continue
        m = MAP_LINE_RE.match(line)
        if not m:
            continue

        hexcode = m.group(1).strip()
        rhs = m.group(2).strip()
        rhs = rhs.split(";", 1)[0].split("//", 1)[0].strip()
        if not rhs:
            continue
        if len(rhs) != 1:
            raise SystemExit(f"码表右侧不是单字符：{line}")

        if rhs in out:
            continue  # 重复：用第一个

        out[rhs] = int(hexcode, 16)

    return out

def encode_cp932_with_map(s: str, cmap: Optional[Dict[str, int]], strict_map: bool) -> bytes:
    """
    性能优化：
    - 没有 cmap：直接 cp932 strict
    - 有 cmap：分段编码，只有遇到映射字符才插入指定字节
    """
    if cmap is None:
        return s.encode("cp932")  # strict

    # 快速路径：strict_map=False 且完全不含映射字符 -> 直接 cp932
    if not strict_map:
        for ch in s:
            if ch in cmap:
                break
        else:
            return s.encode("cp932")

    buf = bytearray()
    bad: Dict[str, int] = {}
    normal_chunk: List[str] = []

    def flush_normal() -> None:
        nonlocal normal_chunk
        if not normal_chunk:
            return
        chunk = "".join(normal_chunk)
        normal_chunk = []
        try:
            buf.extend(chunk.encode("cp932"))
        except UnicodeEncodeError:
            # 定位坏字符
            for c in chunk:
                try:
                    c.encode("cp932")
                except UnicodeEncodeError:
                    bad[c] = ord(c)

    for ch in s:
        if ch in cmap:
            flush_normal()
            code = cmap[ch]
            if code <= 0xFF:
                buf.append(code & 0xFF)
            else:
                buf.append((code >> 8) & 0xFF)
                buf.append(code & 0xFF)
            continue

        if strict_map:
            bad[ch] = ord(ch)
            continue

        normal_chunk.append(ch)

    flush_normal()

    if bad:
        items = ", ".join([f"{c}(U+{u:04X})" for c, u in sorted(bad.items(), key=lambda x: x[1])])
        raise SystemExit(f"发现无法编码的字符（不在码表且 cp932 不支持，或 strict-map 开启）：{items}")

    return bytes(buf)


# -------- d / e 命令 --------

def cmd_decode_extract(inp: Path, out: Path, tbl_dir: Path, list_txt: Optional[Path]) -> None:
    char_names, face_l = load_char_names(tbl_dir)
    mapping = parse_list_txt(list_txt) if (list_txt is not None and list_txt.exists()) else {}

    for txt in iter_txt_files(inp):
        script = read_text_guess(txt)
        items = extract_from_text(script, char_names, face_l)

        rel = txt.relative_to(inp)
        title = mapping.get(f"{txt.stem}.asb", "")
        out_json = output_json_path(out, rel, title)
        out_json.parent.mkdir(parents=True, exist_ok=True)
        out_json.write_text(json.dumps(items, ensure_ascii=False, indent=2), encoding="utf-8")

def build_json_to_txt_map(json_dir: Path) -> Dict[str, Path]:
    """
    json -> txt 的映射：
      rel_dir / "abc(标题).json"  -> rel_dir / "abc.txt"
    返回：txt_rel_posix -> json_path
    """
    m: Dict[str, Path] = {}
    for jp in iter_json_files(json_dir):
        rel = jp.relative_to(json_dir)
        base = strip_paren_comments(jp.stem)  # 去掉括号
        txt_rel = f"{base}.txt"

        if txt_rel in m and m[txt_rel] != jp:
            raise SystemExit(f"多个 JSON 指向同一个脚本：{txt_rel}\n  1) {m[txt_rel]}\n  2) {jp}")
        m[txt_rel] = jp
    return m

def cmd_encode_writeback(
    scripts_in: Path,
    scripts_out: Path,
    json_dir: Path,
    map_path: Optional[Path],
    strict_map: bool,
) -> None:
    cmap = load_writeback_map(map_path) if (map_path is not None and str(map_path).strip()) else None
    mapping = build_json_to_txt_map(json_dir)

    missing: List[str] = []

    for txt_rel_posix, jp in mapping.items():
        txt_rel = Path(txt_rel_posix)
        in_txt = scripts_in / txt_rel
        if not in_txt.exists():
            missing.append(txt_rel_posix)
            continue

        raw_bytes = in_txt.read_bytes()
        script = read_text_guess(in_txt)
        newline = "\r\n" if b"\r\n" in raw_bytes else "\n"

        items = json.loads(jp.read_text(encoding="utf-8", errors="replace"))

        trans_map: Dict[int, str] = {}
        if isinstance(items, list):
            for it in items:
                if not isinstance(it, dict):
                    continue
                tr = it.get("translation")
                if not isinstance(tr, str) or not tr.strip():
                    continue
                key = it.get("key", "")
                if not isinstance(key, str) or not key.strip():
                    continue

                cl = parse_codeline_from_key(key)
                if not isinstance(cl, int) or cl <= 0:
                    continue

                tr_norm = tr.replace("\r", "").replace("\n", "\\n")

                if cl in trans_map and trans_map[cl] != tr_norm:
                    raise SystemExit(f"同一行号出现多个不同翻译：script={txt_rel_posix} line={cl} key={key}")
                trans_map[cl] = tr_norm

        in_code = False
        code_line = 0
        out_lines: List[str] = []

        for raw in script.splitlines():
            line = raw.rstrip("\r\n")

            if line.strip() == "[CODE]":
                in_code = True
                code_line = 0
                out_lines.append(line)
                continue

            if in_code and is_section_header(line) and line.strip() != "[CODE]":
                in_code = False
                out_lines.append(line)
                continue

            if not in_code:
                out_lines.append(line)
                continue

            code_line += 1

            if is_text_param_line(line) and code_line in trans_map:
                leading_ws = re.match(r"^(\s*)", raw).group(1) if raw else ""
                out_lines.append(f"{leading_ws}{trans_map[code_line]}")
            else:
                out_lines.append(line)

        out_text = newline.join(out_lines) + newline
        out_bytes = encode_cp932_with_map(out_text, cmap, strict_map=strict_map)

        out_path = scripts_out / txt_rel
        out_path.parent.mkdir(parents=True, exist_ok=True)
        out_path.write_bytes(out_bytes)

    if missing:
        # 直接报错更安全：避免你以为都写回了
        show = "\n".join(missing[:50])
        more = "" if len(missing) <= 50 else f"\n... 还有 {len(missing)-50} 个"
        raise SystemExit(f"找不到对应的脚本 txt（按 json 名字去括号推导）：\n{show}{more}")


# -------- sys.argv CLI --------

def print_help_and_exit(code: int = 0) -> None:
    msg = (
        "用法：\n"
        "  解码/提取到 JSON：\n"
        "    python textJson.py d <脚本txt目录> <json输出目录> <tbl目录> [list.txt]\n"
        "\n"
        "  编码/写回脚本（按 json 推导 txt：去掉括号，后缀改 txt）：\n"
        "    python textJson.py e <脚本txt目录> <写回输出目录> <json目录> [映射码表]\n"
        "\n"
        "可选参数：\n"
        "  映射码表：UTF-16LE，格式：889F=亚\n"
        "  --strict-map：开启后（且提供码表时）字符不在码表里就报错\n"
    )
    raise SystemExit(msg if code == 0 else msg)

def main() -> None:
    argv = sys.argv[1:]
    strict_map = False

    rest: List[str] = []
    for a in argv:
        if a in ("-h", "--help", "/?"):
            print_help_and_exit(0)
        elif a == "--strict-map":
            strict_map = True
        else:
            rest.append(a)

    if len(rest) < 4:
        raise SystemExit("参数不完整。\n" + (
            "用法：python textJson.py d <脚本txt目录> <json输出目录> <tbl目录> [list.txt]\n"
            "   或：python textJson.py e <脚本txt目录> <写回输出目录> <json目录> [映射码表]\n"
        ))

    mode = rest[0].lower()
    inp = Path(rest[1])
    out = Path(rest[2])
    third = Path(rest[3])
    fourth = Path(rest[4]) if len(rest) >= 5 and rest[4].strip() else None

    out.mkdir(parents=True, exist_ok=True)

    if mode == "d":
        tbl_dir = third
        list_txt = fourth
        cmd_decode_extract(inp, out, tbl_dir, list_txt)
    elif mode == "e":
        json_dir = third
        map_path = fourth
        cmd_encode_writeback(inp, out, json_dir, map_path, strict_map=strict_map)
    else:
        raise SystemExit("mode 必须是 d 或 e。\n用法：python textJson.py d ... 或 python textJson.py e ...\n")

if __name__ == "__main__":
    main()