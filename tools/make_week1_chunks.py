#!/usr/bin/env python3
"""
从 pdf_to_md.py 的输出里挑出 Week1 的十几个块，生成 data/chunks.jsonl。

这是 Week1 的脚手架：块由人挑（下方 PICKS 列表），但页码、印刷页码、章节路径、
字符偏移、id 全部由脚本从 Markdown 算出来，不手填 —— 手填的偏移是假的，
而 Week3 的评测集判定命中要靠它。

Week2 会用 C# 写自动切分器产出同一 schema，届时本脚本退役。

用法：
    python tools/make_week1_chunks.py data/sv660n-manual.md data/chunks.jsonl
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

DOC_ID = "sv660n"
SOURCE_FILE = "sv660n-manual.pdf"

ANCHOR_PAT = re.compile(r"<!-- page:(\d+) label:(\d*) -->")
HEADING_PAT = re.compile(r"^(#{2,3})\s+(.+)$", re.M)


# --------------------------------------------------------------- 选块清单
#
# TableRow : 用 row 定位表格里的一行（子串匹配），整行会被上下文化
# Text     : 用 heading 定位一个小节，取其正文
#
PICKS: list[dict] = [
    # ---- 故障码表：本项目最理想的语料，全是精确术语 ----
    {"kind": "TableRow", "row": "| E201.2 |", "title": "故障代码一览表"},
    {"kind": "TableRow", "row": "| E208.2 |", "title": "故障代码一览表"},
    {"kind": "TableRow", "row": "| E210.0 |", "title": "故障代码一览表"},
    {"kind": "TableRow", "row": "| E420.0 |", "title": "故障代码一览表"},
    # ---- 对象字典：CiA402 标准对象 ----
    {"kind": "TableRow", "row": "| 6041 | 00 | 状态字", "title": "对象组6000h 分配一览"},
    {"kind": "TableRow", "row": "| 6040 | 00 | 控制字", "title": "对象组6000h 分配一览"},
    {"kind": "TableRow", "row": "| 6060 | 00 |", "title": "对象组6000h 分配一览"},
    # ---- 厂商私有参数：通用模型不可能知道，检索失效就必然答错 ----
    {"kind": "TableRow", "row": "| H06-08 | 速度正向限制", "title": "参数一览表"},
    {"kind": "TableRow", "row": "| H06-11 | 转矩前馈控制选择", "title": "参数一览表"},
    {"kind": "TableRow", "row": "| H0B-00 | 速度反馈", "title": "参数一览表"},
    # ---- 散文段落 ----
    {"kind": "Text", "heading": "5.1 故障和警告分类"},
    {"kind": "Text", "heading": "3.2.2 制动电阻接线举例"},
    {"kind": "Text", "heading": "3.2.5 主电路配线注意事项"},
    {"kind": "Text", "heading": "4.4.1 旋转方向选择"},
    {"kind": "Text", "heading": "5.5 通信故障的处理方法"},
]


# --------------------------------------------------------------- 定位工具


class Locator:
    """按字符偏移反查页码 / 印刷页码 / 章节路径。"""

    def __init__(self, md: str):
        self.md = md
        self.anchors = [(m.start(), int(m.group(1)), m.group(2))
                        for m in ANCHOR_PAT.finditer(md)]
        self.headings = [(m.start(), len(m.group(1)), m.group(2).strip())
                         for m in HEADING_PAT.finditer(md)]

    def page(self, pos: int) -> tuple[int, str]:
        best = (0, 0, "")
        for a in self.anchors:
            if a[0] <= pos:
                best = a
            else:
                break
        return best[1], best[2]

    def section_path(self, pos: int) -> str:
        chapter = section = ""
        for start, depth, text in self.headings:
            if start > pos:
                break
            if depth == 2:
                chapter, section = text, ""
            else:
                section = text
        return f"{chapter} > {section}" if section else chapter


def section_slug(section_path: str) -> str:
    """与 C# 侧 ChunkIdFactory.SectionSlug 保持一致：取最后一级的编号部分。"""
    if not section_path.strip():
        return "sec"
    last = [s.strip() for s in section_path.split(">") if s.strip()][-1]
    out = []
    for ch in last:
        if ch.isascii() and ch.isdigit():
            out.append(ch)
        elif ch in ".-" and out:
            out.append("-")
        elif out:
            break
    slug = "".join(out).strip("-")
    return slug or "sec"


# --------------------------------------------------------------- 取块


def split_row(line: str) -> list[str]:
    return [c.strip() for c in line.strip().strip("|").split("|")]


def find_table_bounds(md: str, row_pos: int) -> tuple[int, int]:
    """从表格内任一位置出发，向上向下扩到整张表。"""
    start = row_pos
    while start > 0:
        prev = md.rfind("\n", 0, start - 1) + 1
        if not md[prev:start].lstrip().startswith("|"):
            break
        start = prev
    end = md.find("\n", row_pos)
    while end != -1:
        nxt = md.find("\n", end + 1)
        line = md[end + 1: nxt if nxt != -1 else len(md)]
        if not line.lstrip().startswith("|"):
            break
        end = nxt
    return start, end if end != -1 else len(md)


def contextualize_row(md: str, row_pos: int, title: str, section_path: str) -> str:
    """
    把表格的一行拼成脱离原表也能独立理解的短文本。

    关键处理：合并单元格造成的续行空值要从上一行前向填充。
    故障码表里 E201.1 / E201.2 这些行的「故障」列是空的（与 E201 合并），
    不填充的话这一行离开表就丢了 E201 这个最关键的检索词。
    """
    tstart, tend = find_table_bounds(md, row_pos)
    lines = [l for l in md[tstart:tend].split("\n") if l.strip().startswith("|")]

    header = split_row(lines[0])
    body = [split_row(l) for l in lines[1:] if not set(l.strip()) <= set("|- ")]

    # 复合表头：参数一览表的表头横跨三行 ——
    #   | 参数组 | 列2   | 列3   | 名称 | …
    #   | 十六进制|      | 十进制 |      | …
    #   | 组别   | 索引码 | 参数   |      | …
    # 第一行里空缺的列被 pdf_to_md 填成了「列N」占位符，直接用会输出
    # 「列2: 09h」这种没有信息量的字段名。从紧随其后的两行里取真实字段名。
    for col, name in enumerate(header):
        if not re.fullmatch(r"列\d+", name):
            continue
        for sub in body[:2]:
            if col < len(sub) and sub[col]:
                header[col] = sub[col]

    target_line = md[md.rfind("\n", 0, row_pos) + 1: md.find("\n", row_pos)].strip()
    target = split_row(target_line)

    # 前向填充：对目标行的空单元格，回溯到上方最近一个非空值
    idx = next((i for i, r in enumerate(body) if r == target), None)
    if idx is not None:
        filled = list(target)
        for c in range(len(filled)):
            if filled[c]:
                continue
            for back in range(idx - 1, -1, -1):
                if c < len(body[back]) and body[back][c]:
                    filled[c] = body[back][c]
                    break
        target = filled

    pairs = [f"{header[i] if i < len(header) else f'字段{i + 1}'}: {v}"
             for i, v in enumerate(target) if v]

    return f"{title}（{section_path}）| " + " | ".join(pairs)


def take_section_text(md: str, heading: str, max_chars: int = 700) -> tuple[int, str]:
    m = re.search(rf"^#{{2,3}}\s+{re.escape(heading)}\s*$", md, re.M)
    if not m:
        raise SystemExit(f"找不到小节标题：{heading}")

    def is_han(ch: str) -> bool:
        return "一" <= ch <= "鿿"

    pos = m.end() + 1
    out: list[str] = []
    for raw in md[pos:].split("\n"):
        line = raw.strip()
        if line.startswith("#") or line.startswith("|"):
            break
        if line.startswith("<!--") or not line:
            continue

        # 接线图里的引脚标签会变成一行一个（L1 / PE / U / V / W），过滤掉。
        # 只筛纯 ASCII 的短行 —— 中文的短行往往是被换行拆开的句子尾巴
        # （"…故障清除方" / "法。"），一并丢掉会造成句子残缺。
        if len(line) < 4 and line.isascii():
            continue

        # 上一行以汉字结尾、本行以汉字开头，说明是同一句被排版换行拆开的，接回去。
        # 以标点结尾的不接，避免把并列的条目粘成一句。
        if out and is_han(out[-1][-1]) and is_han(line[0]):
            out[-1] += line
        else:
            out.append(line)

        if sum(len(x) for x in out) >= max_chars:
            break

    text = "\n".join(out)
    # 有些小节的正文整个是一张表（如 2.1.2 环境条件），散文侧只会取到一个表标题。
    # 这种块没有检索价值，直接报错而不是悄悄产出一个 13 字的垃圾块。
    if len(text) < 60:
        raise SystemExit(
            f"小节「{heading}」只取到 {len(text)} 字：{text!r}\n"
            "该小节正文可能整个是表格，请改用 TableRow 取，或换一个小节。")
    return pos, text


# --------------------------------------------------------------- 入口


def build(md_path: Path, out_path: Path) -> list[dict]:
    md = md_path.read_text(encoding="utf-8")
    loc = Locator(md)
    ordinals: dict[str, int] = {}
    chunks: list[dict] = []

    for pick in PICKS:
        if pick["kind"] == "TableRow":
            pos = md.find(pick["row"])
            if pos < 0:
                raise SystemExit(f"找不到表格行：{pick['row']}")
            page, label = loc.page(pos)
            section = loc.section_path(pos)
            text = contextualize_row(md, pos, pick["title"], section)
        else:
            pos, text = take_section_text(md, pick["heading"])
            page, label = loc.page(pos)
            section = loc.section_path(pos)

        slug = section_slug(section)
        key = f"{page}#{slug}"
        ordinals[key] = ordinals.get(key, -1) + 1

        chunk = {
            "id": f"{DOC_ID}#p{page}#{slug}#{ordinals[key]:03d}",
            "sourceFile": SOURCE_FILE,
            "pageNumber": page,
            "sectionPath": section,
            "charOffset": pos,
            "kind": pick["kind"],
            "text": text,
            "metadata": {"pageLabel": label} if label else {},
        }
        chunks.append(chunk)

    header = [
        "// 由 tools/make_week1_chunks.py 从 data/sv660n-manual.md 生成，请勿手工编辑。",
        "// 页码 / 印刷页码 / 章节路径 / 字符偏移 全部取自 Markdown 实际位置，不是手填的。",
        f"// 语料：《SV660N 系列伺服用户手册-简易版》（汇川），共 {len(chunks)} 块。",
        "// 重新生成：tools/.venv/Scripts/python.exe tools/make_week1_chunks.py data/sv660n-manual.md data/chunks.jsonl",
    ]
    body = [json.dumps(c, ensure_ascii=False) for c in chunks]
    out_path.write_text("\n".join(header + body) + "\n", encoding="utf-8")
    return chunks


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("md", type=Path)
    ap.add_argument("out", type=Path)
    args = ap.parse_args()

    chunks = build(args.md, args.out)

    kinds: dict[str, int] = {}
    for c in chunks:
        kinds[c["kind"]] = kinds.get(c["kind"], 0) + 1

    print(f"输出   : {args.out}")
    print(f"块数   : {len(chunks)}  {kinds}")
    print(f"页码范围: p.{min(c['pageNumber'] for c in chunks)} ~ p.{max(c['pageNumber'] for c in chunks)}")
    print(f"文本长度: 最短 {min(len(c['text']) for c in chunks)} / 最长 {max(len(c['text']) for c in chunks)}")
    print()
    for c in chunks:
        print(f"  {c['id']:<28} p.{c['pageNumber']:<4} {c['kind']:<9} {len(c['text']):>4}字  {c['text'][:46]}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
