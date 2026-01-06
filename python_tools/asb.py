import re
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Dict, Iterable, List, Optional, Sequence, Set, Tuple, Union

_EMBEDDED_STR_JSON = {
    "CALLB": {
        "0x00000000": [1],
        "0x00000001": [-1],
        "0x00000002": [-1],
    },
    "CALLL": {},
}

_HEX_TOKEN_RE = re.compile(r"^(?:0[xX])?[0-9a-fA-F]+$")


@dataclass(frozen=True)
class OpcodeDef:
    name: str
    total_size: int  # 包含 opcode 自身的总字节数；-1 表示可变长度


_EMBEDDED_OPCODES: Dict[int, OpcodeDef] = {
    0x00: OpcodeDef("NOP", 1),
    0x01: OpcodeDef("PUSHI", 5),
    0x02: OpcodeDef("PUSHL", 2),
    0x03: OpcodeDef("PUSHG", 5),
    0x04: OpcodeDef("PUSHF", 5),
    0x05: OpcodeDef("PUSHR", 1),
    0x06: OpcodeDef("POP", 1),
    0x07: OpcodeDef("POPL", 2),
    0x08: OpcodeDef("POPG", 5),
    0x09: OpcodeDef("POPF", 5),
    0x0A: OpcodeDef("STIL", 6),
    0x0B: OpcodeDef("STIG", 9),
    0x0C: OpcodeDef("STIF", 9),
    0x0D: OpcodeDef("INCL", 2),
    0x0E: OpcodeDef("DECL", 2),
    0x0F: OpcodeDef("INCG", 5),
    0x10: OpcodeDef("DECG", 5),
    0x11: OpcodeDef("EXCH", 1),
    0x12: OpcodeDef("NEG", 1),
    0x13: OpcodeDef("ADD", 1),
    0x14: OpcodeDef("SUB", 1),
    0x15: OpcodeDef("MUL", 1),
    0x16: OpcodeDef("DIV", 1),
    0x17: OpcodeDef("MOD", 1),
    0x18: OpcodeDef("AND", 1),
    0x19: OpcodeDef("OR", 1),
    0x1A: OpcodeDef("NOT", 1),
    0x1B: OpcodeDef("EQ", 1),
    0x1C: OpcodeDef("NEQ", 1),
    0x1D: OpcodeDef("LT", 1),
    0x1E: OpcodeDef("GT", 1),
    0x1F: OpcodeDef("LEQ", 1),
    0x20: OpcodeDef("GEQ", 1),
    0x21: OpcodeDef("B", 5),
    0x22: OpcodeDef("BZ", 5),
    0x23: OpcodeDef("BNZ", 5),
    0x24: OpcodeDef("BTBL", -1),
    0x25: OpcodeDef("JMPL", 5),
    0x26: OpcodeDef("JMPG", 9),
    0x27: OpcodeDef("CALLL", 6),
    0x28: OpcodeDef("CALLG", 10),
    0x29: OpcodeDef("CALLB", 6),
    0x2A: OpcodeDef("RET", 1),
    0x2B: OpcodeDef("RETN", 1),
    0x2C: OpcodeDef("YIELD", 1),
}


_OPCODE_TABLE: List[OpcodeDef] = []
if _EMBEDDED_OPCODES:
    _max_opcode = max(_EMBEDDED_OPCODES.keys())
    _OPCODE_TABLE = [OpcodeDef("NOP", 1)] * (_max_opcode + 1)
    for _i in range(_max_opcode + 1):
        _OPCODE_TABLE[_i] = _EMBEDDED_OPCODES.get(_i, OpcodeDef("NOP", 1))


def _u32_le(b: bytes) -> int:
    return int.from_bytes(b, "little", signed=False)


StrTable = Dict[int, Tuple[int, ...]]
StrConfig = Dict[str, StrTable]


def _parse_positions(v: Any, *, ctx: str) -> Tuple[int, ...]:
    if isinstance(v, int):
        return (v,)
    if isinstance(v, (list, tuple)) and all(isinstance(x, int) for x in v):
        return tuple(v)
    raise ValueError(f"{ctx}: 期望 int 或 int 列表（支持 -1）: {v!r}")


def _parse_func_id(k: Any, *, ctx: str) -> Optional[int]:
    if isinstance(k, int):
        return k
    if isinstance(k, str):
        return int(k, 0)
    raise ValueError(f"{ctx}: func_id key 必须是 int 或可解析的字符串: {k!r}")


def load_str_config_from_embedded_json() -> StrConfig:
    data = _EMBEDDED_STR_JSON
    if not isinstance(data, dict):
        raise ValueError("embedded str.json 必须是 JSON 对象（字典）")

    upper_keys = {str(k).upper() for k in data.keys()}
    if not any(k in {"CALLB", "CALLL"} for k in upper_keys):
        raise ValueError('embedded str.json 仅支持分表格式：{"CALLB": {...}, "CALLL": {...}}')

    out: StrConfig = {"CALLB": {}, "CALLL": {}}
    for call_name in ("CALLB", "CALLL"):
        table_any = None
        for k, v in data.items():
            if str(k).upper() == call_name:
                table_any = v
                break
        if table_any is None:
            continue
        if not isinstance(table_any, dict):
            raise ValueError(f"embedded str.json:{call_name} 必须是字典")
        tbl: StrTable = {}
        for fk, fv in table_any.items():
            func_id = _parse_func_id(fk, ctx=f"embedded str.json:{call_name}")
            tbl[func_id] = _parse_positions(fv, ctx=f"embedded str.json:{call_name}:{fk}")
        out[call_name] = tbl
    return out


def escape_text_for_line(s: str) -> str:
    return s.replace("\n", "\\n")


def unescape_text_from_line(s: str) -> str:
    return s.replace("\\n", "\n")


def decode_text(raw: bytes) -> str:
    return raw.decode("cp932", errors="ignore")


def encode_text(s: str) -> bytes:
    return s.encode("cp932", errors="ignore")


def fmt_u32(n: int) -> str:
    return f"{n & 0xFFFFFFFF:08X}"


def fmt_u8(n: int) -> str:
    return f"{n & 0xFF:02X}"


def parse_hex_int(token: str) -> int:
    t = token.strip()
    if not t:
        raise ValueError("empty token")
    if not _HEX_TOKEN_RE.match(t):
        raise ValueError(f"not a hex token: {token!r}")
    return int(t, 0) if t.lower().startswith("0x") else int(t, 16)


def _read_u32_at(buf: bytes, off: int) -> int:
    return _u32_le(buf[off : off + 4])


@dataclass(frozen=True)
class AsbHeader:
    filename: str
    entry_table_off: int
    entry_count: int
    code_off: int
    code_size: int
    str1_off: int
    str1_size: int
    str2_off: int
    str2_size: int


@dataclass(frozen=True)
class AsbEntry:
    index: int
    name_off: int
    unk04: int
    locals_count: int
    param_count: int
    flag0c: int
    unk10: int
    name: str


def parse_asb_header(file_bytes: bytes) -> AsbHeader:
    if len(file_bytes) < 0x44:
        raise ValueError("文件太小，无法解析 ASB 头")

    raw_name = file_bytes[0x04:0x14]
    filename = raw_name.split(b"\x00", 1)[0].decode("ascii", errors="ignore")

    entry_table_off = _read_u32_at(file_bytes, 0x24)
    entry_count = _read_u32_at(file_bytes, 0x28)
    code_off = _read_u32_at(file_bytes, 0x2C)
    code_size = _read_u32_at(file_bytes, 0x30)
    str1_off = _read_u32_at(file_bytes, 0x34)
    str1_size = _read_u32_at(file_bytes, 0x38)
    str2_off = _read_u32_at(file_bytes, 0x3C)
    str2_size = _read_u32_at(file_bytes, 0x40)

    return AsbHeader(
        filename=filename,
        entry_table_off=entry_table_off,
        entry_count=entry_count,
        code_off=code_off,
        code_size=code_size,
        str1_off=str1_off,
        str1_size=str1_size,
        str2_off=str2_off,
        str2_size=str2_size,
    )


def _slice_region(file_bytes: bytes, start: int, size: int) -> bytes:
    if size <= 0:
        return b""
    if start < 0 or start >= len(file_bytes):
        return b""
    end = start + size
    if end > len(file_bytes):
        end = len(file_bytes)
    if end <= start:
        return b""
    return file_bytes[start:end]


def _read_cstring(region: bytes, off: int) -> bytes:
    if off < 0 or off >= len(region):
        return b""
    end = region.find(b"\x00", off)
    if end == -1:
        end = len(region)
    return region[off:end]


def parse_entry_table(header: AsbHeader, file_bytes: bytes) -> List[AsbEntry]:
    str1 = _slice_region(file_bytes, header.str1_off, header.str1_size)
    table = _slice_region(file_bytes, header.entry_table_off, header.entry_count * 20)

    entries: List[AsbEntry] = []
    for i in range(header.entry_count):
        chunk = table[i * 20 : (i + 1) * 20]
        if len(chunk) < 20:
            break

        name_off = _u32_le(chunk[0x00:0x04])
        unk04 = _u32_le(chunk[0x04:0x08])
        split = _u32_le(chunk[0x08:0x0C])
        locals_count = split & 0xFFFF
        param_count = (split >> 16) & 0xFFFF
        flag0c = _u32_le(chunk[0x0C:0x10])
        unk10 = _u32_le(chunk[0x10:0x14])
        name = decode_text(_read_cstring(str1, name_off))

        entries.append(
            AsbEntry(
                index=i,
                name_off=name_off,
                unk04=unk04,
                locals_count=locals_count,
                param_count=param_count,
                flag0c=flag0c,
                unk10=unk10,
                name=name,
            )
        )

    return entries


@dataclass
class VmInsn:
    name: str
    operand_bytes: bytes
    operands: List[str]
    text_only: bool = False  # 用于把 PUSHI 字符串参数直接输出为一行纯文本


@dataclass(frozen=True)
class CStringInfo:
    start: int
    end: int
    text: str


class CStringIndex:
    def __init__(self, region: bytes):
        self._region = region
        self._items: List[CStringInfo] = []
        i = 0
        while i < len(region):
            if region[i] == 0:
                i += 1
                continue
            start = i
            end = region.find(b"\x00", start)
            if end == -1:
                end = len(region)
            raw = region[start:end]
            self._items.append(CStringInfo(start=start, end=end, text=decode_text(raw)))
            i = end + 1

    @property
    def items(self) -> Sequence[CStringInfo]:
        return self._items

    def resolve(self, off: int) -> Optional[CStringInfo]:
        if off < 0 or off >= len(self._region):
            return None
        for it in self._items:
            if it.start <= off <= it.end:
                return it
        return None


def _format_operand_list(raw: bytes) -> List[str]:
    if not raw:
        return []
    if len(raw) == 1:
        return [fmt_u8(raw[0])]
    if len(raw) == 4:
        return [fmt_u32(_u32_le(raw))]
    if len(raw) == 5:
        # 输出顺序：argcount(byte), function_id(imm32)
        return [fmt_u8(raw[4]), fmt_u32(_u32_le(raw[:4]))]
    if len(raw) == 8:
        return [fmt_u32(_u32_le(raw[:4])), fmt_u32(_u32_le(raw[4:]))]
    if len(raw) == 9:
        return [fmt_u32(_u32_le(raw[:4])), fmt_u32(_u32_le(raw[4:8])), fmt_u8(raw[8])]
    return [fmt_u8(x) for x in raw]


def parse_code(code: bytes, opcode_table: List[OpcodeDef]) -> List[VmInsn]:
    pc = 0
    out: List[VmInsn] = []

    while pc < len(code):
        opcode = code[pc]
        pc += 1

        if opcode >= len(opcode_table):
            continue

        op_def = opcode_table[opcode]
        op_name = op_def.name

        if op_def.total_size == -1:
            if pc >= len(code):
                break
            count = code[pc]
            pc += 1
            need = count * 4
            if pc + need > len(code):
                break
            raw = bytes([count]) + code[pc : pc + need]
            pc += need
            operands = [fmt_u8(count)] + [fmt_u32(_u32_le(raw[1 + i * 4 : 1 + i * 4 + 4])) for i in range(count)]
        else:
            operand_len = max(op_def.total_size - 1, 0)
            if pc + operand_len > len(code):
                break
            raw = code[pc : pc + operand_len]
            pc += operand_len
            operands = _format_operand_list(raw)

        out.append(VmInsn(name=op_name, operand_bytes=raw, operands=operands))

    return out


def _call_argcount(insn: VmInsn) -> Optional[int]:
    b = insn.operand_bytes
    if insn.name in {"CALLB", "CALLL"} and len(b) == 5:
        return b[4]
    if insn.operands:
        try:
            return parse_hex_int(insn.operands[0])
        except Exception:
            return None
    return None


def apply_string_mappings(
    insns: List[VmInsn],
    str1_index: CStringIndex,
    str2_index: CStringIndex,
    entry_name_offs: Iterable[int],
    str_cfg: StrConfig,
) -> Tuple[Set[int], Set[int]]:
    used_str1: Set[int] = set()
    used_str2: Set[int] = set()

    for off in entry_name_offs:
        it = str1_index.resolve(off)
        if it:
            used_str1.add(it.start)

    for i, insn in enumerate(insns):
        if insn.name == "JMPG" and len(insn.operand_bytes) == 8:
            off2 = _u32_le(insn.operand_bytes[:4])
            off1 = _u32_le(insn.operand_bytes[4:])
            s2 = str2_index.resolve(off2)
            s1 = str1_index.resolve(off1)
            if s2:
                used_str2.add(s2.start)
            if s1:
                used_str1.add(s1.start)
            a = escape_text_for_line(s2.text) if s2 else fmt_u32(off2)
            b = escape_text_for_line(s1.text) if s1 else fmt_u32(off1)
            insn.operands = [a, b]
            continue

        if insn.name == "CALLG" and len(insn.operand_bytes) == 9:
            off2 = _u32_le(insn.operand_bytes[:4])
            off1 = _u32_le(insn.operand_bytes[4:8])
            argc = insn.operand_bytes[8]
            s2 = str2_index.resolve(off2)
            s1 = str1_index.resolve(off1)
            if s2:
                used_str2.add(s2.start)
            else:
                print(f"[WARN] CALLG 脚本名指针 {fmt_u32(off2)} 不在 STRINGS_2 内", file=sys.stderr)
            if s1:
                used_str1.add(s1.start)
            else:
                print(f"[WARN] CALLG 变量名指针 {fmt_u32(off1)} 不在 STRINGS_1 内", file=sys.stderr)
            a = escape_text_for_line(s2.text) if s2 else fmt_u32(off2)
            b = escape_text_for_line(s1.text) if s1 else fmt_u32(off1)
            insn.operands = [a, b, fmt_u8(argc)]
            continue

        if insn.name in {"CALLB", "CALLL"}:
            func_id = _u32_le(insn.operand_bytes[:4]) if len(insn.operand_bytes) >= 4 else None
            if func_id is None:
                continue

            table = str_cfg.get(insn.name, {})
            if func_id not in table:
                continue
            argcount = _call_argcount(insn)
            positions = table[func_id]

            arg_idxs_rev: List[int] = []
            for j in range(i - 1, -1, -1):
                nm = insns[j].name
                if nm in {"PUSHI", "PUSHL", "PUSHG", "PUSHF", "PUSHR"}:
                    arg_idxs_rev.append(j)
                    continue
                break
            if not arg_idxs_rev:
                continue

            arg_idxs = list(reversed(arg_idxs_rev))

            if len(positions) == 1 and positions[0] == -1:
                target_positions = list(range(1, len(arg_idxs) + 1))
            else:
                target_positions = list(positions)

            for pos in target_positions:
                if pos <= 0 or pos > len(arg_idxs):
                    continue
                target_j = arg_idxs[pos - 1]
                pin = insns[target_j]
                if pin.name != "PUSHI" or len(pin.operand_bytes) != 4:
                    continue
                off = _u32_le(pin.operand_bytes)
                it = str1_index.resolve(off)
                if not it:
                    print(
                        f"[WARN] 字符串指针 {fmt_u32(off)} 不在 STRINGS_1 内：CALL={insn.name} {', '.join(insn.operands)} func_id={fmt_u32(func_id)} argcount={argcount} arg#{pos}",
                        file=sys.stderr,
                    )
                    continue
                used_str1.add(it.start)
                pin.name = ""
                pin.operands = [escape_text_for_line(it.text)]
                pin.text_only = True

    return used_str1, used_str2


def decode_asb_to_txt(input_file: Union[str, Path], output_file: Union[str, Path]) -> None:
    opcode_table = _OPCODE_TABLE
    str_cfg = load_str_config_from_embedded_json()

    file_bytes = Path(input_file).read_bytes()
    header = parse_asb_header(file_bytes)

    entries = parse_entry_table(header, file_bytes)
    code = _slice_region(file_bytes, header.code_off, header.code_size)
    str1 = _slice_region(file_bytes, header.str1_off, header.str1_size)
    str2 = _slice_region(file_bytes, header.str2_off, header.str2_size)

    str1_index = CStringIndex(str1)
    str2_index = CStringIndex(str2)

    output_lines: List[str] = []

    output_lines.append("[VARIABLE]")
    for e in entries:
        safe_name = escape_text_for_line(e.name)
        output_lines.append(
            f"{safe_name},{fmt_u32(e.unk04)},{e.locals_count},{e.param_count},{fmt_u32(e.flag0c)},{fmt_u32(e.unk10)}"
        )

    output_lines.append("")
    output_lines.append("[CODE]")
    insns = parse_code(code, opcode_table)
    used_str1, used_str2 = apply_string_mappings(
        insns,
        str1_index=str1_index,
        str2_index=str2_index,
        entry_name_offs=[e.name_off for e in entries],
        str_cfg=str_cfg,
    )
    for insn in insns:
        if insn.text_only:
            output_lines.append(insn.operands[0] if insn.operands else "")
            continue
        if not insn.name:
            continue
        if insn.operands:
            output_lines.append(f"{insn.name} {', '.join(insn.operands)}")
        else:
            output_lines.append(insn.name)

    remaining_1 = [it for it in str1_index.items if it.start not in used_str1]
    if remaining_1:
        output_lines.append("")
        output_lines.append("[STRINGS_1]")
        for it in remaining_1:
            safe = escape_text_for_line(it.text)
            output_lines.append(f"{fmt_u32(it.start)}: {safe}")

    remaining_2 = [it for it in str2_index.items if it.start not in used_str2]
    if remaining_2:
        output_lines.append("")
        output_lines.append("[STRINGS_2]")
        for it in remaining_2:
            safe = escape_text_for_line(it.text)
            output_lines.append(f"{fmt_u32(it.start)}: {safe}")

    Path(output_file).write_text("\n".join(output_lines), encoding="utf-8", errors="ignore")


@dataclass(frozen=True)
class VariableRow:
    name: str
    unk04: int
    locals_count: int
    param_count: int
    flag0c: int
    unk10: int


@dataclass(frozen=True)
class CodeLineInsn:
    name: str
    operands: List[str]
    raw_line: str


@dataclass(frozen=True)
class CodeLineString:
    text: str


CodeLine = Union[CodeLineInsn, CodeLineString]

def _pack_u32(n: int) -> bytes:
    return int(n & 0xFFFFFFFF).to_bytes(4, "little", signed=False)


def _pack_u8(n: int) -> bytes:
    return int(n & 0xFF).to_bytes(1, "little", signed=False)


def _looks_like_hex_token(s: str) -> bool:
    return bool(_HEX_TOKEN_RE.match(s.strip()))


def _split_operands(s: str) -> List[str]:
    if not s.strip():
        return []
    return [p.strip() for p in s.split(",") if p.strip()]


def _parse_txt_sections(text: str) -> Dict[str, List[str]]:
    sec: Optional[str] = None
    out: Dict[str, List[str]] = {}
    for raw_line in text.splitlines():
        line = raw_line.rstrip("\r\n")
        if line.startswith("\ufeff"):
            line = line.lstrip("\ufeff")
        if line.startswith("[") and line.endswith("]"):
            sec = line.strip()[1:-1].strip().upper()
            out.setdefault(sec, [])
            continue
        if sec is None:
            continue
        out[sec].append(line)
    return out


def _parse_variable_rows(lines: Sequence[str]) -> List[VariableRow]:
    rows: List[VariableRow] = []
    for raw in lines:
        line = raw.strip()
        if not line:
            continue
        parts = [p.strip() for p in line.split(",", 5)]
        if len(parts) < 6:
            continue
        name = unescape_text_from_line(parts[0])
        unk04 = parse_hex_int(parts[1])
        locals_count = int(parts[2], 10)
        param_count = int(parts[3], 10)
        flag0c = parse_hex_int(parts[4])
        unk10 = parse_hex_int(parts[5])
        rows.append(
            VariableRow(
                name=name,
                unk04=unk04,
                locals_count=locals_count,
                param_count=param_count,
                flag0c=flag0c,
                unk10=unk10,
            )
        )
    return rows


def _unescape_operand_token(token: str) -> str:
    return unescape_text_from_line(token)


def _parse_code_lines(lines: Sequence[str], opcode_table: Sequence[OpcodeDef], opcode_index_by_name: Dict[str, int]) -> List[CodeLine]:
    out: List[CodeLine] = []
    opcode_names = set(opcode_index_by_name.keys())
    for raw in lines:
        line = raw.rstrip()
        if not line:
            continue
        name = line.split(None, 1)[0].strip()
        if name in opcode_names:
            rest = line[len(name) :].strip()
            operands = [_unescape_operand_token(x) for x in _split_operands(rest)]
            op_def = opcode_table[opcode_index_by_name[name]]

            if op_def.total_size == -1:
                if not operands or not _looks_like_hex_token(operands[0]) or any(not _looks_like_hex_token(x) for x in operands[1:]):
                    out.append(CodeLineString(text=unescape_text_from_line(line)))
                    continue
                out.append(CodeLineInsn(name=name, operands=operands, raw_line=line))
                continue

            operand_len = max(op_def.total_size - 1, 0)
            ok = True
            if operand_len == 0:
                ok = len(operands) == 0
            elif operand_len == 1:
                ok = len(operands) >= 1 and _looks_like_hex_token(operands[0])
            elif operand_len == 4:
                ok = len(operands) >= 1 and _looks_like_hex_token(operands[0])
            elif operand_len == 5:
                ok = len(operands) >= 2 and _looks_like_hex_token(operands[0]) and _looks_like_hex_token(operands[1])
            elif operand_len == 8:
                if name == "JMPG":
                    ok = len(operands) >= 2 and (_looks_like_hex_token(operands[0]) or operands[0]) and (
                        _looks_like_hex_token(operands[1]) or operands[1]
                    )
                else:
                    ok = len(operands) >= 2 and _looks_like_hex_token(operands[0]) and _looks_like_hex_token(operands[1])
            elif operand_len == 9:
                if name == "CALLG":
                    ok = len(operands) >= 3 and (_looks_like_hex_token(operands[0]) or operands[0]) and (
                        _looks_like_hex_token(operands[1]) or operands[1]
                    ) and _looks_like_hex_token(operands[2])
                else:
                    ok = (
                        len(operands) >= 3
                        and _looks_like_hex_token(operands[0])
                        and _looks_like_hex_token(operands[1])
                        and _looks_like_hex_token(operands[2])
                    )
            else:
                ok = False

            if not ok:
                out.append(CodeLineString(text=unescape_text_from_line(line)))
                continue

            out.append(CodeLineInsn(name=name, operands=operands, raw_line=line))
        else:
            out.append(CodeLineString(text=unescape_text_from_line(line)))
    return out


def _build_str2_pool(code_lines: Sequence[CodeLine]) -> Dict[str, int]:
    pool: Dict[str, int] = {}
    off = 0
    for it in code_lines:
        if not isinstance(it, CodeLineInsn):
            continue
        if it.name in {"JMPG", "CALLG"} and it.operands:
            s2 = it.operands[0]
            if _looks_like_hex_token(s2):
                continue
            if s2 in pool:
                continue
            pool[s2] = off
            off += len(encode_text(s2)) + 1
    return pool


def _build_str1_region(variable_rows: Sequence[VariableRow], code_lines: Sequence[CodeLine]) -> Tuple[bytes, List[int], Dict[str, int]]:
    buf = bytearray()
    entry_name_offs: List[int] = [0] * len(variable_rows)

    # 变量名字符串区倒序放置：最后面的 __main 放到第一个
    text_to_off: Dict[str, int] = {}
    last = len(variable_rows) - 1
    for idx in [last] + list(range(0, last)):
        entry_name_offs[idx] = len(buf)
        name = variable_rows[idx].name
        text_to_off.setdefault(name, entry_name_offs[idx])
        buf.extend(encode_text(name))
        buf.append(0)

    def ensure_extra(s: str) -> int:
        # 代码区域引用的 STRINGS_1：如果文本已存在则复用 offset，避免重复占用空间
        if s in text_to_off:
            return text_to_off[s]
        off = len(buf)
        buf.extend(encode_text(s))
        buf.append(0)
        text_to_off[s] = off
        return off

    for it in code_lines:
        if isinstance(it, CodeLineString):
            ensure_extra(it.text)
            continue
        if it.name in {"JMPG", "CALLG"} and len(it.operands) >= 2:
            s1 = it.operands[1]
            if not _looks_like_hex_token(s1):
                ensure_extra(s1)

    return bytes(buf), entry_name_offs, text_to_off


def _build_str2_region(str2_pool: Dict[str, int]) -> bytes:
    if not str2_pool:
        return b""
    items = sorted(str2_pool.items(), key=lambda kv: kv[1])
    buf = bytearray()
    for s, _ in items:
        buf.extend(encode_text(s))
        buf.append(0)
    return bytes(buf)


def _encode_operand_bytes(
    insn: CodeLineInsn,
    opcode: OpcodeDef,
    *,
    str1_extra_pool: Dict[str, int],
    str2_pool: Dict[str, int],
) -> bytes:
    operand_len = max(opcode.total_size - 1, 0)
    if opcode.total_size == -1:
        if not insn.operands:
            raise ValueError("BTBL missing count")
        count = parse_hex_int(insn.operands[0]) & 0xFF
        entries = [parse_hex_int(x) & 0xFFFFFFFF for x in insn.operands[1:]]
        if count != len(entries):
            count = len(entries)
        return _pack_u8(count) + b"".join(_pack_u32(x) for x in entries)

    if operand_len == 0:
        return b""
    if operand_len == 1:
        if len(insn.operands) < 1:
            raise ValueError("missing u8 operand")
        return _pack_u8(parse_hex_int(insn.operands[0]))
    if operand_len == 4:
        if len(insn.operands) < 1:
            raise ValueError("missing u32 operand")
        return _pack_u32(parse_hex_int(insn.operands[0]))
    if operand_len == 5:
        if len(insn.operands) < 2:
            raise ValueError("missing operands for 5-byte payload")
        argc = parse_hex_int(insn.operands[0])
        imm = parse_hex_int(insn.operands[1])
        return _pack_u32(imm) + _pack_u8(argc)
    if operand_len == 8:
        if len(insn.operands) < 2:
            raise ValueError("missing operands for 8-byte payload")
        a = insn.operands[0]
        b = insn.operands[1]
        if insn.name == "JMPG":
            off2 = parse_hex_int(a) if _looks_like_hex_token(a) else str2_pool[a]
            off1 = parse_hex_int(b) if _looks_like_hex_token(b) else str1_extra_pool[b]
            return _pack_u32(off2) + _pack_u32(off1)
        return _pack_u32(parse_hex_int(a)) + _pack_u32(parse_hex_int(b))
    if operand_len == 9:
        if len(insn.operands) < 3:
            raise ValueError("missing operands for 9-byte payload")
        a = insn.operands[0]
        b = insn.operands[1]
        c = insn.operands[2]
        if insn.name == "CALLG":
            off2 = parse_hex_int(a) if _looks_like_hex_token(a) else str2_pool[a]
            off1 = parse_hex_int(b) if _looks_like_hex_token(b) else str1_extra_pool[b]
            argc = parse_hex_int(c)
            return _pack_u32(off2) + _pack_u32(off1) + _pack_u8(argc)
        return _pack_u32(parse_hex_int(a)) + _pack_u32(parse_hex_int(b)) + _pack_u8(parse_hex_int(c))

    raise ValueError(f"unsupported operand_len={operand_len} for {opcode.name}")


def encode_txt_to_asb(input_file: Union[str, Path], output_file: Union[str, Path]) -> None:
    opcode_table = _OPCODE_TABLE
    opcode_index_by_name = {op.name: i for i, op in enumerate(opcode_table)}

    txt = Path(input_file).read_text(encoding="utf-8", errors="ignore")
    sections = _parse_txt_sections(txt)

    variable_rows = _parse_variable_rows(sections.get("VARIABLE", []))
    code_lines = _parse_code_lines(sections.get("CODE", []), opcode_table, opcode_index_by_name)

    str2_pool = _build_str2_pool(code_lines)
    str1_region, entry_name_offs, str1_text_to_off = _build_str1_region(variable_rows, code_lines)
    str2_region = _build_str2_region(str2_pool)

    entry_table = bytearray()
    for i, row in enumerate(variable_rows):
        name_off = entry_name_offs[i]
        split = (row.locals_count & 0xFFFF) | ((row.param_count & 0xFFFF) << 16)
        entry_table.extend(_pack_u32(name_off))
        entry_table.extend(_pack_u32(row.unk04))
        entry_table.extend(_pack_u32(split))
        entry_table.extend(_pack_u32(row.flag0c))
        entry_table.extend(_pack_u32(row.unk10))

    code_bytes = bytearray()
    for it in code_lines:
        if isinstance(it, CodeLineString):
            # 无法解析成命令 -> 当成字符串，编码成 PUSHI <STRINGS_1 offset>
            off = str1_text_to_off[it.text]
            code_bytes.append(opcode_index_by_name["PUSHI"])
            code_bytes.extend(_pack_u32(off))
            continue

        opcode_idx = opcode_index_by_name[it.name]
        op_def = opcode_table[opcode_idx]
        operand_bytes = _encode_operand_bytes(
            it,
            op_def,
            str1_extra_pool=str1_text_to_off,
            str2_pool=str2_pool,
        )

        code_bytes.append(opcode_idx)
        code_bytes.extend(operand_bytes)

    filename = Path(output_file).name
    out_bytes = build_asb_bytes(
        filename=filename,
        entry_table_bytes=bytes(entry_table),
        entry_count=len(variable_rows),
        code_bytes=bytes(code_bytes),
        str1_bytes=str1_region,
        str2_bytes=str2_region,
    )
    Path(output_file).write_bytes(out_bytes)


def build_asb_bytes(
    *,
    filename: str,
    entry_table_bytes: bytes,
    entry_count: int,
    code_bytes: bytes,
    str1_bytes: bytes,
    str2_bytes: bytes,
) -> bytes:
    header_size = 0x44
    entry_table_off = header_size
    code_off = entry_table_off + len(entry_table_bytes)
    str1_off = (header_size + len(entry_table_bytes) + len(code_bytes) + 3) & ~3
    str2_off = str1_off + len(str1_bytes)

    header = bytearray(b"\x00" * header_size)
    # 生成的 ASB：前 4 字节为 0，后 16 字节为文件名（含 .asb）
    name_bytes = filename.encode("ascii", errors="ignore")[:0x10]
    header[0x04:0x14] = name_bytes.ljust(0x10, b"\x00")

    header[0x24:0x28] = _pack_u32(entry_table_off)
    header[0x28:0x2C] = _pack_u32(entry_count)
    header[0x2C:0x30] = _pack_u32(code_off)
    header[0x30:0x34] = _pack_u32(len(code_bytes))
    header[0x34:0x38] = _pack_u32(str1_off)
    header[0x38:0x3C] = _pack_u32(len(str1_bytes))
    header[0x3C:0x40] = _pack_u32(str2_off)
    header[0x40:0x44] = _pack_u32(len(str2_bytes))

    out = bytearray()
    out.extend(header)
    out.extend(entry_table_bytes)
    out.extend(code_bytes)
    out.extend(b"\x00" * (str1_off - len(out)))
    out.extend(str1_bytes)
    out.extend(str2_bytes)
    return bytes(out)


def _decode_dir(input_dir: Path, output_dir: Path) -> None:
    for p in input_dir.rglob("*.asb"):
        rel = p.relative_to(input_dir)
        out_path = output_dir / rel.with_suffix(".txt")
        out_path.parent.mkdir(parents=True, exist_ok=True)
        decode_asb_to_txt(p, out_path)


def _encode_dir(input_dir: Path, output_dir: Path) -> None:
    for p in input_dir.rglob("*.txt"):
        rel = p.relative_to(input_dir)
        out_path = output_dir / rel.with_suffix(".asb")
        out_path.parent.mkdir(parents=True, exist_ok=True)
        encode_txt_to_asb(p, out_path)


def main(argv: Sequence[str]) -> int:
    if len(argv) != 4:
        print("用法: python asb.py d/e <input> <output>", file=sys.stderr)
        return 2

    mode = argv[1].strip().lower()
    input_path = Path(argv[2])
    output_path = Path(argv[3])

    if mode not in {"d", "e"}:
        print("模式必须是 d 或 e", file=sys.stderr)
        return 2

    if mode == "d":
        if input_path.is_dir():
            output_path.mkdir(parents=True, exist_ok=True)
            _decode_dir(input_path, output_path)
        else:
            decode_asb_to_txt(input_path, output_path)
        return 0

    if input_path.is_dir():
        output_path.mkdir(parents=True, exist_ok=True)
        _encode_dir(input_path, output_path)
    else:
        encode_txt_to_asb(input_path, output_path)
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
