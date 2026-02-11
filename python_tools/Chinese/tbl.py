import json
import re
import sys
from pathlib import Path
from char import encode_cp932_or_die, make_translation_converter

JP_RE = re.compile(r"[\u4e00-\u9fff\u3040-\u30ff\u31f0-\u31ff]")






def strip_inline_comment(s: str) -> str:
    if "//" in s:
        s = s.split("//", 1)[0]
    if ";" in s:
        s = s.split(";", 1)[0]
    return s.strip()


def split_comment(raw: str) -> tuple[str, str]:
    p1, p2 = raw.find("//"), raw.find(";")
    ps = [p for p in (p1, p2) if p != -1]
    if not ps:
        return raw, ""
    p = min(ps)
    return raw[:p], raw[p:]


def parse_section_name(raw: str) -> str | None:
    body, _ = split_comment(raw)
    s = body.strip()
    if not (s.startswith("[") and "]" in s):
        return None
    i = s.find("[")
    j = s.find("]", i + 1)
    if j == -1:
        return None
    return s[i + 1 : j].strip()


def contains_jp(s: str) -> bool:
    return bool(JP_RE.search(s))


def read_tbl(path: Path) -> dict[str, dict[str, str]]:
    out: dict[str, dict[str, str]] = {}
    sec: str | None = None
    for raw in path.read_text(encoding="cp932", errors="ignore").splitlines():
        line = strip_inline_comment(raw.strip())
        if not line:
            continue
        if line.startswith("[") and "]" in line:
            name = parse_section_name(raw)
            if name is not None:
                sec = name
                out.setdefault(sec, {})
            continue
        if sec and "=" in line:
            k, v = line.split("=", 1)
            if len(v) >= 2 and v[0] == '"' and v[-1] == '"':
                v = v[1:-1]
            out[sec][k.strip()] = v
    return out


def dump_json(input_dir: Path, out_json: Path) -> None:
    items: list[dict] = []
    for tbl in sorted(input_dir.rglob("*.tbl")):
        rel = tbl.relative_to(input_dir).as_posix()
        for sec, kv in read_tbl(tbl).items():
            for k, v in kv.items():
                if contains_jp(v):
                    items.append({"key": f"{rel}[{sec}]{k}", "original": v, "translation": "", "stage": 0})
    out_json.parent.mkdir(parents=True, exist_ok=True)
    out_json.write_text(json.dumps(items, ensure_ascii=False, indent=4), encoding="utf-8")


def parse_key(full: str) -> tuple[str, str, str]:
    i, j = full.rfind("["), full.rfind("]")
    if i < 0 or j < 0 or j < i:
        raise SystemExit(full)
    return full[:i], full[i + 1 : j], full[j + 1 :]


def update_tbl_text(text: str, updates: dict[str, dict[str, str]]) -> str:
    sec = None
    out = []

    for raw in text.splitlines(keepends=True):
        name = parse_section_name(raw)
        if name is not None:
            sec = name
            out.append(raw)
            continue

        if not sec or sec not in updates:
            out.append(raw)
            continue

        newline = "\r\n" if raw.endswith("\r\n") else ("\n" if raw.endswith("\n") else "")
        line = raw[:-len(newline)] if newline else raw

        head, sep, tail = line.partition("\t")

        target = head if sep else line
        if "=" not in target:
            out.append(raw)
            continue

        left, _ = target.split("=", 1)
        key = left.strip()
        if key not in updates[sec]:
            out.append(raw)
            continue

        new_left = left
        new_val = updates[sec][key]
        if " " in new_val:
            new_val = '"' + new_val + '"'

        if sep:
            out.append(new_left + "=" + new_val + "\t" + tail + newline)
        else:
            out.append(new_left + "=" + new_val + newline)

    return "".join(out)


def writeback(src_dir: Path, out_dir: Path, json_path: Path, map_path: Path | None) -> None:
    data = json.loads(json_path.read_text(encoding="utf-8"))
    conv = make_translation_converter(map_path)

    upd: dict[str, dict[str, dict[str, str]]] = {}
    for o in data:
        t = (o.get("translation") or "")
        if not t:
            continue
        t = conv(t)
        rel, sec, k = parse_key(o["key"])
        upd.setdefault(rel, {}).setdefault(sec, {})[k] = t

    for rel, secmap in upd.items():
        src = src_dir / rel
        dst = out_dir / rel
        dst.parent.mkdir(parents=True, exist_ok=True)
        original = src.read_text(encoding="cp932")
        new_text = update_tbl_text(original, secmap)
        dst.write_bytes(encode_cp932_or_die(new_text))


def main(argv: list[str]) -> None:
    if len(argv) < 2:
        raise SystemExit(
            "python d <输入文件夹> <输出json>\n"
            "python e <脚本txt目录> <写回输出目录> <json目录> [映射码表]"
        )

    cmd = argv[1]
    if cmd == "d":
        if len(argv) != 4:
            raise SystemExit("python d <输入文件夹> <输出json>")
        dump_json(Path(argv[2]), Path(argv[3]))
        return

    if cmd == "e":
        if len(argv) not in (5, 6):
            raise SystemExit("python e <脚本txt目录> <写回输出目录> <json目录> [映射码表]")
        src_dir = Path(argv[2])
        out_dir = Path(argv[3])
        json_path = Path(argv[4])
        map_path = Path(argv[5]) if len(argv) == 6 else None
        writeback(src_dir, out_dir, json_path, map_path)
        return

    raise SystemExit(cmd)


if __name__ == "__main__":
    main(sys.argv)
