#!/usr/bin/env python3
"""
PDF -> Markdown，为 industrial-docs-rag 的切分环节做预处理。

输出契约（下游 C# 只认这一条）：
    每页开头一行   <!-- page:{物理页码} label:{印刷页码} -->

物理页码给 Week4 的 PDF 阅读器跳转用（阅读器只认物理页），
印刷页码给人看（手册前面有封面和目录，两者会错位）。

这是一次性脚本，参数是照着汇川 SV660N 简易版手册的版面调的，不追求通用。
换一本手册时预期要重调 HEADER_HINTS / FOOTER_PAT。

用法：
    python tools/pdf_to_md.py data/sv660n-manual.pdf data/sv660n-manual.md
"""

from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

import pymupdf

# ---------------------------------------------------------------- 版面特征

# 页脚的印刷页码，形如 "- 165 -"。
# 必须带 re.M：它既要在整页文本里 search（页脚在末尾几行），也要对单行 match。
FOOTER_PAT = re.compile(r"^\s*[-‑–]\s*(\d{1,4})\s*[-‑–]\s*$", re.M)

# 页眉里必然出现的字样。命中即整行丢弃 —— 它每页重复，进了切块只会污染检索。
HEADER_HINTS = ("系列伺服用户手册", "伺服用户手册-简易版", "伺服用户手册简易版")

# 章标题，如 "第5章 故障处理"
CHAPTER_PAT = re.compile(r"第\s*(\d+)\s*章\s*(.{2,30}?)\s*$")

# 节标题，如 "2.2.3 安装注意事项"。要求整行且不太长，避免把表格里的数字行误判成标题。
SECTION_PAT = re.compile(r"^(\d+(?:\.\d+){1,3})\s+(\S.{0,38})$")

# 表标题，如 "表2-1 安装注意事项"
TABLE_TITLE_PAT = re.compile(r"^表\s*\d+\s*[-‑–]\s*\d+\s*(.*)$")

# 两个中日韩字符之间的空白：PyMuPDF 换行造成的，不是真空格。
# 只在 CJK 之间处理，ASCII（0x2312、UINT16、0~0xFFFF）之间的空格必须留着。
CJK_GAP_PAT = re.compile(r"(?<=[㐀-鿿])[ \t]+(?=[㐀-鿿])")

# 手册里成对出现的装饰符号，如 "◆\n◆安装过程禁止…"
BULLET_PAT = re.compile(r"^[◆■●■●]{1,2}\s*")


# ---------------------------------------------------------------- 文本清洗


def normalize_cell(text: str | None) -> str:
    """
    表格单元格：合并换行造成的断字（"可访\n问性" -> "可访问性"）。

    另外去掉单元格开头的孤立标点：原文行尾的逗号有时会被 find_tables 划到
    右边一列去，形成 "，重新焊接、插紧…" 这种开头。语义不受影响，但看着像脏数据。
    """
    if not text:
        return ""
    s = text.replace("\n", " ").replace("|", "／")
    s = CJK_GAP_PAT.sub("", s)
    s = re.sub(r"\s{2,}", " ", s).strip()
    return s.lstrip("，。、；：,;: ")


def clean_line(line: str) -> str:
    s = BULLET_PAT.sub("", line.strip())
    s = CJK_GAP_PAT.sub("", s)
    return re.sub(r"\s{2,}", " ", s).strip()


def is_header(line: str) -> bool:
    return any(h in line for h in HEADER_HINTS)


# ---------------------------------------------------------------- 页面处理


def is_real_table(rows: list[list[str | None]]) -> bool:
    """
    判断 find_tables 检出的区域是不是真表格。

    手册里大量带边框的「警告」提示框和接线图框会被 find_tables 误判成表格，
    而误判的后果很严重：提取时文字会被按格子打散重排，三条独立的安全警告能被
    拼成一句语义完全错误的话。这种 chunk 一旦被引用，答案里就会出现凭空编造的
    警告内容 —— 工业手册场景下这是不能接受的。

    真表格的可靠特征：表头有多个非空单元格，且大多数数据行也填了多列。
    提示框恰好相反：只有一列有内容，其余全空。
    """
    cleaned = [[normalize_cell(c) for c in row] for row in rows]
    cleaned = [r for r in cleaned if any(r)]

    if len(cleaned) < 3:            # 至少要有表头 + 两行数据
        return False

    if max(len(r) for r in cleaned) < 2:
        return False

    if sum(1 for c in cleaned[0] if c) < 2:   # 表头只有一格有字 -> 提示框
        return False

    body = cleaned[1:]
    multi_column_rows = sum(1 for r in body if sum(1 for c in r if c) >= 2)
    return multi_column_rows >= 0.6 * len(body)


def table_to_markdown(rows: list[list[str | None]]) -> list[str]:
    """转成 GFM 表格。空表头或全空行直接丢弃。"""
    cleaned = [[normalize_cell(c) for c in row] for row in rows]
    cleaned = [r for r in cleaned if any(c for c in r)]
    if len(cleaned) < 2:
        return []

    width = max(len(r) for r in cleaned)
    cleaned = [r + [""] * (width - len(r)) for r in cleaned]

    header, *body = cleaned
    # 表头单元格为空时补个占位，否则 Markdown 表格结构不合法
    header = [h or f"列{i + 1}" for i, h in enumerate(header)]

    out = ["| " + " | ".join(header) + " |",
           "|" + "|".join([" --- "] * width) + "|"]
    out.extend("| " + " | ".join(r) + " |" for r in body)
    return out


def blocks_outside(page: pymupdf.Page, table_rects: list[pymupdf.Rect]) -> list[tuple[float, float, str]]:
    """取不落在表格区域内的文本块，按阅读顺序（先上后下、先左后右）返回。"""
    result = []
    for x0, y0, x1, y1, text, _no, btype in page.get_text("blocks"):
        if btype != 0 or not text.strip():
            continue
        rect = pymupdf.Rect(x0, y0, x1, y1)
        area = rect.get_area()
        # 与任一表格重叠超过一半就认为属于该表，交给 find_tables 处理
        if area > 0 and any((rect & t).get_area() / area > 0.5 for t in table_rects):
            continue
        result.append((y0, x0, text))
    return sorted(result, key=lambda b: (round(b[0], 1), b[1]))


def render_page(page: pymupdf.Page, physical: int, state: dict) -> tuple[list[str], int, int]:
    raw = page.get_text()

    label_match = FOOTER_PAT.search(raw)
    label = label_match.group(1) if label_match else ""

    lines: list[str] = [f"<!-- page:{physical} label:{label} -->", ""]

    detected = page.find_tables().tables
    real, rejected = [], 0
    for t in detected:
        if is_real_table(t.extract()):
            real.append(t)
        else:
            rejected += 1

    # 只排除真表格所占的区域。被判为误检的区域交回给线性文本处理，
    # 否则那里的正确文字会被整段丢掉。
    table_rects = [pymupdf.Rect(t.bbox) for t in real]

    body: list[str] = []
    for _y, _x, block in blocks_outside(page, table_rects):
        for raw_line in block.splitlines():
            line = clean_line(raw_line)
            if not line or is_header(line) or FOOTER_PAT.match(line):
                continue

            chapter = CHAPTER_PAT.match(line)
            if chapter:
                title = f"第{chapter.group(1)}章 {chapter.group(2)}"
                # 章标题就是页眉，每页都会出现；只在换章时输出一次
                if title != state.get("chapter"):
                    state["chapter"] = title
                    body.append(f"## {title}")
                continue

            section = SECTION_PAT.match(line)
            if section:
                body.append(f"### {section.group(1)} {section.group(2)}")
                continue

            table_title = TABLE_TITLE_PAT.match(line)
            if table_title:
                body.append(f"**{line}**")
                continue

            body.append(line)

    lines.extend(body)

    for table in real:
        md = table_to_markdown(table.extract())
        if md:
            lines.append("")
            lines.extend(md)

    lines.append("")
    return lines, len(real), rejected


# ---------------------------------------------------------------- 入口


def convert(pdf_path: Path, md_path: Path) -> dict:
    doc = pymupdf.open(pdf_path)
    state: dict = {}
    out: list[str] = [
        f"<!-- source: {pdf_path.name} -->",
        f"<!-- pages: {doc.page_count} -->",
        "",
    ]

    tables_total = 0
    rejected_total = 0
    labelled = 0

    for i in range(doc.page_count):
        before = len(out)
        page_lines, kept, rejected = render_page(doc[i], i + 1, state)
        out.extend(page_lines)
        tables_total += kept
        rejected_total += rejected
        if re.search(r"label:\d+", out[before]):
            labelled += 1

    md_path.parent.mkdir(parents=True, exist_ok=True)
    md_path.write_text("\n".join(out), encoding="utf-8")

    return {
        "pages": doc.page_count,
        "tables": tables_total,
        "rejected": rejected_total,
        "pages_with_label": labelled,
        "chars": sum(len(line) for line in out),
    }


def main() -> int:
    parser = argparse.ArgumentParser(description="PDF -> Markdown（带页码锚点）")
    parser.add_argument("pdf", type=Path)
    parser.add_argument("md", type=Path)
    args = parser.parse_args()

    if not args.pdf.exists():
        print(f"找不到 PDF：{args.pdf}", file=sys.stderr)
        return 1

    stats = convert(args.pdf, args.md)

    print(f"输出          : {args.md}")
    print(f"页数          : {stats['pages']}")
    print(f"含印刷页码    : {stats['pages_with_label']} 页")
    print(f"采纳表格      : {stats['tables']} 张")
    print(f"判为误检丢弃  : {stats['rejected']} 张（提示框/图框，其文字按普通段落处理）")
    print(f"字符数        : {stats['chars']}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
