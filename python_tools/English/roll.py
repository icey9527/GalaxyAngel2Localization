import sys, json, re
from pathlib import Path
from char import make_translation_converter

FW = "\t"

RE_TAIL_Y  = re.compile(r'^(?P<text>.*?)(?P<context>\s*(?P<size>\d+)\s+(?P<align>[LRC])\s+(?P<x>\d+)\s+(?P<y>[+-]?\d+)\s*)$')
RE_TAIL_NO = re.compile(r'^(?P<text>.*?)(?P<context>\s*(?P<size>\d+)\s+(?P<align>[LRC])\s+(?P<x>\d+)\s*)$')

def strip_outer_quotes(s: str) -> str:
    s = s.strip()
    if len(s) >= 2 and s[0] == '"' and s[-1] == '"':
        return s[1:-1]
    return s

def parse_line(line: str):
    m = RE_TAIL_Y.match(line)
    if not m:
        m = RE_TAIL_NO.match(line)
    if not m:
        return None
    text = strip_outer_quotes(m.group("text"))
    if not text:
        return None
    ctx = m.group("context").strip()
    return text, ctx

def dump_dir(inp: Path, outp: Path):
    outp.mkdir(parents=True, exist_ok=True)
    for src in inp.glob("*.txt"):
        out = []
        with src.open("r", encoding="cp932", errors="ignore") as f:
            for lineno, line in enumerate(f, 1):
                s = line.strip()
                if not s or s.startswith("//"):
                    continue
                r = parse_line(line.rstrip("\r\n"))
                if not r:
                    continue
                text, ctx = r
                out.append({
                    "key": str(lineno),
                    "original": text,
                    "translation": "",
                    "context": ctx,
                    "stage": 0
                })
        (outp / (src.stem + ".json")).write_text(
            json.dumps(out, ensure_ascii=False, indent=2),
            encoding="utf-8"
        )

def emit_dir(inp: Path, outp: Path| None) -> None:
    conv = make_translation_converter()
    outp.mkdir(parents=True, exist_ok=True)
    for src in inp.rglob("*.json"):
        data = json.loads(src.read_text(encoding="utf-8"))
        data.sort(key=lambda o: int(o.get("key", "0")))

        dst = outp / (src.stem + ".txt")
        with dst.open("w", encoding="cp932", errors="ignore", newline="\n") as w:
            w.write("[000]\n")
            for o in data:
                text = (o.get("translation") or o.get("original") or "").replace("\r", "").replace("\n", "")
                if " " in text:
                    text = f"\"{text}\""
                ctx = (o.get("context") or "").strip()
                w.write(f"{conv(text)}{FW}{ctx}\n")

def main(argv: list[str]) -> None:
    if len(argv) != 4:
        raise SystemExit("用法: python roll_batch3.py d|e 输入文件夹 输出文件夹")
    mode, inp, outp = argv[1], Path(argv[2]), Path(argv[3])
    if mode == "d":
        dump_dir(inp, outp)
    elif mode == "e":
        emit_dir(inp, outp)
    else:
        raise SystemExit("模式只能是 d 或 e")

if __name__ == "__main__":
    main(sys.argv)