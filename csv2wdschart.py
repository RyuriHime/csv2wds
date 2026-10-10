#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
csv2wdschart.py —— WDS「官方谱面 CSV」→ WDS 编辑器谱面 (.wdschart) 转谱器

背景
----
WDS 编辑器 (wds_editor.exe) 支持导入「官方谱面」CSV。该 CSV 每行 7 列:

    起始秒 , 结束秒(-1 表示无尾) , noteType , lane(1 起) , 宽度 , gimmick , 参数

CSV **不包含 BPM**,时间只有小数秒(通常 3~4 位),所以要还原成 .wdschart 的
tick 坐标必须由外部给定 BPM,并把时间吸附回原始的音符网格。实测原始谱面的
tick 都落在 1/24 拍(TPQ 480 时 = 20 tick)上,而 1/24 正好能整除 16 分、24 分、
32 分时值,所以默认网格取 TPQ/24。

用法
----
    python csv2wdschart.py <谱面.csv> --bpm 194 [-o 输出.wdschart]

常用选项
----
    --bpm BPM        曲子的 BPM(必填)
    --tpq N          tick 精度,默认 480
    --grid N         时间吸附网格(单位 tick),默认 TPQ/24;--grid 1 表示只做四舍五入
    -o, --out FILE   输出文件,默认与输入同名 .wdschart
    --keep-900       保留 noteType=900 的行(默认丢弃,与编辑器导入行为一致)
    --no-concurrent  不生成 CONCURRENT 段(输出 CONCURRENT 0)
    --verify FILE    与参考 .wdschart 比对 NOTE/CONCURRENT,打印差异
    --title / --artist / --designer / --difficulty  仅用于打印信息,不写入谱面
    --quiet          安静模式

"""

import argparse
import collections
import math
import os
import sys

# ---------------------------------------------------------------- 规则常量

# 官方 CSV 里 noteType=900 的行不是音符(分裂线等编辑器尚未支持的数据),
# 编辑器导入时会把它们丢掉。见 README/报告说明。
DROP_NOTE_TYPES = {900}

# 长条里的"星星"(编辑器 UI 里叫同宽滑键的节点):
#   chart 里写成  N <id> <tick> <tick> <30|31> <lane> <width> 0 0 <所属长条id>
#   末字段 = 它所在那条长条主体(100/101/110/111)的 note id
#   普通长条(100/101)里的星星是 30,滑键长条(110/111)里的星星是 31
# 官方 CSV 里星星有两种写法:直接写 30/31,或者写 40(需要按所在长条推断 30/31)
STAR_TYPES = {30, 31}
STAR_ALIAS_TYPES = {40}
HOLD_BODY_TYPES = {100, 101, 110, 111}
STAR_OWNER_TYPES_WIDE = {110, 111}      # 落在这些长条里的星星是 31

# 分裂线:noteType=0 + gimmick 是下面这些值之一 + 第 7 列是 4~6 位的编号
#   (例如 noteType=0, gimmick=14, param=11660)
#   chart 里写成  N <id> <tick> <tick2> 0 <lane> <width> <gimmick> <编号> -1
#   它必须横跨整个场地,所以 lane=0、width=场地宽度(见 FIELD_WIDTH)。
#   有些 CSV 里这两格是空的(0 / -1),需要在这里补上,否则编辑器
#   会把这条分裂线当成无效数据,导出时写不出来。
SPLIT_GIMMICKS = {11, 12, 13, 14, 15, 16, 32, 33}
FIELD_WIDTH = 12                        # WDS 场地宽度(4K / 12K 都是 12 个单位)

# CONCURRENT(C 行)的生成规则 —— 从 3 个真实谱面(共 2054 条 C 行)反推,
# 100% 复现。C 行格式: C <毫秒> <左端 lane> <线宽>
#   左端 = 该时刻参与音符里最小的 lane
#   线宽 = max(lane + 宽度) - 左端
# 参与与否取决于音符在"该时刻是起点还是终点":
CONCURRENT_START_CONTRIB = {10, 20, 50, 80, 81, 82, 83}
CONCURRENT_END_CONTRIB = {10, 20, 80, 82, 83, 100, 101, 110, 111}
CONCURRENT_MIN_NOTES = 2

# 官方 CSV 的 gimmick 列可以写名字,也可以写数字(chart 里的枚举值)。
# 实测(逐行比对真实谱面与其 CSV 导出):
#   gimmick='JumpScratch' : noteType=110/111(划键链) -> A=0
#                           noteType=100(长条主体)   -> A=1
#   gimmick='OneDirection': -> A=2
#   gimmick=数字          : -> A=该数字
# B 一般等于参数列;只有 noteType=50 的 OneDirection 行是 B=±宽度。
GIMMICK_NAMES = {"JumpScratch", "OneDirection"}

CHART_VERSION = 5


# ---------------------------------------------------------------- 工具函数

def fmt_num(x):
    """把数字格式化成 WDS 风格:整数不带小数点,小数去掉多余的 0。"""
    if isinstance(x, float) and x.is_integer():
        return str(int(x))
    s = ("%.6f" % float(x)).rstrip("0").rstrip(".")
    return s if s else "0"


class Note(object):
    __slots__ = ("tick", "tick_end", "note_type", "lane", "width", "gimmick", "param",
                 "src_line", "note_id", "owner_id", "was_alias")

    def __init__(self, tick, tick_end, note_type, lane, width, gimmick, param, src_line):
        self.tick = tick
        self.tick_end = tick_end
        self.note_type = note_type
        self.lane = lane
        self.width = width
        self.gimmick = gimmick
        self.param = param
        self.src_line = src_line
        self.note_id = -1
        self.owner_id = -1          # 星星专用:所属长条主体的 note id
        self.was_alias = False      # 是否由 CSV 的 40 换写而来

    @property
    def is_hold(self):
        return self.tick_end != self.tick

    @property
    def is_star(self):
        return self.note_type in STAR_TYPES

    def line(self):
        tail = self.owner_id if self.is_star else -1
        return "N %d %d %d %d %d %d %d %d %d" % (
            self.note_id, self.tick, self.tick_end, self.note_type,
            self.lane, self.width, self.gimmick, self.param, tail)


def parse_gimmick(note_type, text, param, width):
    """官方 CSV 的 gimmick/参数两列 -> chart 的 (A, B) 两个字段。

    规则由真实谱面与其 CSV 导出逐行比对反推得到。
    """
    text = text.strip()
    if text == "JumpScratch":
        # 划键(scratch):长条主体上会带 1 个"跳跃箭头",划键链段本身不带
        return (1 if note_type == 100 else 0), param
    if text == "OneDirection":
        if note_type == 50:
            # 单向甩键:参数列在这里是方向标志(0=左,1=右),B 记的是 ±宽度
            return 0, (-width if param == 0 else width)
        return 2, param
    try:
        return int(text), param
    except ValueError:
        raise ValueError("无法识别的 gimmick 值: %r" % text)


def read_csv_rows(path):
    """读官方 CSV,返回 (行号, 7 个字段字符串) 列表。"""
    rows = []
    with open(path, "r", encoding="utf-8-sig", errors="replace") as f:
        for lineno, raw in enumerate(f, 1):
            line = raw.strip()
            if not line:
                continue
            parts = [p.strip() for p in line.split(",")]
            if len(parts) != 7:
                raise ValueError("%s:%d 列数不是 7(读到 %d 列): %s" % (path, lineno, len(parts), line))
            rows.append((lineno, parts))
    return rows


def make_snapper(bpm, tpq, grid):
    """返回 秒 -> tick 的转换函数。

    原始谱面的 tick 落在 1/24 拍网格上,但 CSV 里时间只有 3~4 位小数,
    换算成 tick 会有不到 ±1 tick 的误差,所以:
      * 若换算值离最近的网格点足够近(<=1 tick),说明它就是普通音符,
        吸附到网格;
      * 否则(例如 noteType=0 的特殊行,时间本来就落在网格之外)只做四舍五入。
    """
    ticks_per_second = bpm * tpq / 60.0

    def snap(seconds):
        raw = seconds * ticks_per_second
        if grid <= 1:
            return int(math.floor(raw + 0.5))
        near = math.floor(raw / grid + 0.5) * grid
        if abs(raw - near) <= 1.0:
            return int(near)
        return int(math.floor(raw + 0.5))

    return snap


def find_star_owner(notes, star):
    """找覆盖这颗星星的长条主体(同 lane、区间包含、类型是 100/101/110/111)。"""
    best = None
    for n in notes:
        if n.tick_end <= n.tick:
            continue
        if n.lane != star.lane:
            continue
        if not (n.tick <= star.tick <= n.tick_end):
            continue
        if n.note_type not in HOLD_BODY_TYPES:
            continue
        if best is None or n.tick_end < best.tick_end:
            best = n
    return best


def build_notes(rows, snap, keep_900=False, warn=None):
    notes = []
    dropped = collections.Counter()
    clamped = 0
    stars = 0
    split_fixed = 0
    star_alias = 0
    star_orphan = 0
    for lineno, parts in rows:
        time_s = float(parts[0])
        end_s = float(parts[1])
        note_type = int(float(parts[2]))
        lane_in = int(float(parts[3]))
        width = int(float(parts[4]))
        param = int(float(parts[6]))
        gimmick, param = parse_gimmick(note_type, parts[5], param, width)

        if note_type in DROP_NOTE_TYPES and not keep_900:
            dropped[note_type] += 1
            continue

        tick = snap(time_s)
        tick_end = snap(end_s) if end_s >= 0 else tick
        lane = lane_in - 1
        if lane < 0:
            # CSV 里 lane=0 的行(chart 里 lane 从 0 开始,但官方 CSV 用 1 起),
            # 只出现在 noteType=0 / 分裂线这类特殊数据里,按 0 处理。
            lane = 0
            clamped += 1
        if note_type == 0 and gimmick in SPLIT_GIMMICKS:
            # 分裂线要横跨整个场地:lane 归 0,宽度没填就补满
            lane = 0
            if width <= 0:
                width = FIELD_WIDTH
                split_fixed += 1
        notes.append(Note(tick, tick_end, note_type, lane, width, gimmick, param, lineno))
    if warn is not None and clamped:
        warn(clamped)
    # 参照真实谱面:NOTES 按起始 tick 升序(同一 tick 内保持 CSV 原有顺序)
    notes.sort(key=lambda n: n.tick)

    # 分配 id,并解析"星星"的归属(末字段要填它所在那条长条的 id)
    for i, n in enumerate(notes):
        n.note_id = i
    for n in notes:
        if n.note_type not in STAR_TYPES and n.note_type not in STAR_ALIAS_TYPES:
            continue
        owner = find_star_owner(notes, n)
        if owner is None:
            # 找不到所属长条:降级成普通键,保证整份谱面还能被编辑器读入
            star_orphan += 1
            n.note_type = 10
            n.owner_id = -1
            continue
        if n.note_type in STAR_ALIAS_TYPES:
            n.note_type = 31 if owner.note_type in STAR_OWNER_TYPES_WIDE else 30
            n.was_alias = True
            star_alias += 1
        n.owner_id = owner.note_id
        stars += 1
    stats = {"stars": stars, "star_alias": star_alias, "star_orphan": star_orphan,
             "clamped": clamped, "split_fixed": split_fixed}
    return notes, dropped, stats


def build_concurrent(notes, bpm, tpq):
    """按逆推出的规则生成 CONCURRENT 段。"""
    ms_per_tick = 60000.0 / (bpm * tpq)

    def to_ms(tick):
        return int(math.floor(tick * ms_per_tick + 0.5))

    starts = collections.defaultdict(list)
    ends = collections.defaultdict(list)
    for n in notes:
        if n.note_type in CONCURRENT_START_CONTRIB:
            starts[to_ms(n.tick)].append((n.lane, n.width))
        if n.is_hold and n.note_type in CONCURRENT_END_CONTRIB:
            ends[to_ms(n.tick_end)].append((n.lane, n.width))

    out = []
    for ms in sorted(set(starts) | set(ends)):
        pts = starts.get(ms, []) + ends.get(ms, [])
        if len(pts) < CONCURRENT_MIN_NOTES:
            continue
        left = min(p[0] for p in pts)
        right = max(p[0] + p[1] for p in pts)
        out.append("C %d %d %d" % (ms, left, right - left))
    return out


def render_chart(notes, conc_lines, bpm, tpq, timing=None):
    if timing is None:
        timing = [(0, bpm, 4, 4, 3)]
    out = []
    out.append("WDSCHART %d" % CHART_VERSION)
    out.append("BPM %s" % fmt_num(bpm))
    out.append("TPQ %d" % tpq)
    out.append("TIMING %d" % len(timing))
    for t in timing:
        out.append("T %d %s %d %d %d" % (t[0], fmt_num(t[1]), t[2], t[3], t[4]))
    out.append("NOTES %d" % len(notes))
    for n in notes:
        out.append(n.line())
    out.append("CONCURRENT %d" % len(conc_lines))
    out.extend(conc_lines)
    out.append("END")
    return "\n".join(out) + "\n"


# ---------------------------------------------------------------- 校验

def load_chart(path):
    head, notes, conc = {}, [], []
    for raw in open(path, "r", encoding="utf-8-sig", errors="replace"):
        line = raw.rstrip("\r\n")
        if not line.strip():
            continue
        if line.startswith("N "):
            notes.append(line.split())
        elif line.startswith("C "):
            conc.append(line.split())
        elif line.startswith("T "):
            pass
        else:
            k, _, v = line.partition(" ")
            head[k] = v
    return head, notes, conc


def verify(text, ref_path, bpm, tpq, quiet=False):
    """把生成的谱面与参考谱面比对(忽略 NOTE 顺序,比较内容多重集)。"""
    import tempfile
    fd, tmp = tempfile.mkstemp(suffix=".wdschart")
    os.close(fd)
    with open(tmp, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)
    try:
        _, got_n, got_c = load_chart(tmp)
        _, ref_n, ref_c = load_chart(ref_path)
    finally:
        os.remove(tmp)

    def keyn(n):
        return (int(n[2]), int(n[3]), int(n[4]), int(n[5]), int(n[6]), int(n[7]), int(n[8]))

    gm = collections.Counter(keyn(n) for n in got_n)
    rm = collections.Counter(keyn(n) for n in ref_n)
    note_diff = (gm - rm) + (rm - gm)
    gc = collections.Counter((int(c[1]), int(c[2]), int(c[3])) for c in got_c)
    rc = collections.Counter((int(c[1]), int(c[2]), int(c[3])) for c in ref_c)
    conc_diff = (gc - rc) + (rc - gc)

    ok = not note_diff and not conc_diff
    if not quiet:
        print("  参考谱面: %s" % ref_path)
        print("  音符   : 生成 %d 条 / 参考 %d 条 / 差异 %d" % (len(got_n), len(ref_n), sum(note_diff.values())))
        if note_diff:
            print("     仅生成有:")
            for k, v in list((gm - rm).items())[:8]:
                print("        %s x%d" % (k, v))
            print("     仅参考有:")
            for k, v in list((rm - gm).items())[:8]:
                print("        %s x%d" % (k, v))
        print("  并发线 : 生成 %d 条 / 参考 %d 条 / 差异 %d" % (len(got_c), len(ref_c), sum(conc_diff.values())))
        if conc_diff:
            print("     仅生成有:")
            for k, v in list((gc - rc).items())[:8]:
                print("        %s x%d" % (k, v))
            print("     仅参考有:")
            for k, v in list((rc - gc).items())[:8]:
                print("        %s x%d" % (k, v))
        print("  结论   : %s" % ("完全一致 ✔" if ok else "存在差异 ✘"))
    return ok


# ---------------------------------------------------------------- main

def main(argv=None):
    ap = argparse.ArgumentParser(add_help=True, description="WDS 官方 CSV → .wdschart 转谱器")
    ap.add_argument("csv")
    ap.add_argument("--bpm", type=float, required=True, help="曲子 BPM(必填)")
    ap.add_argument("--tpq", type=int, default=480, help="tick 精度,默认 480")
    ap.add_argument("--grid", type=int, default=0, help="吸附网格(默认 TPQ/24)")
    ap.add_argument("-o", "--out")
    ap.add_argument("--keep-900", action="store_true", help="保留 noteType=900 的行")
    ap.add_argument("--no-concurrent", action="store_true", help="不生成 CONCURRENT 段")
    ap.add_argument("--verify")
    ap.add_argument("--quiet", action="store_true")
    ap.add_argument("--title")
    ap.add_argument("--artist")
    ap.add_argument("--designer")
    ap.add_argument("--difficulty")
    args = ap.parse_args(argv)

    grid = args.grid if args.grid > 0 else max(1, args.tpq // 24)
    rows = read_csv_rows(args.csv)
    snap = make_snapper(args.bpm, args.tpq, grid)
    warn_msgs = []
    notes, dropped, stats = build_notes(
        rows, snap, args.keep_900,
        warn=lambda n: warn_msgs.append("lane 列写 0 的行 %d 条已按 lane=0 处理" % n))
    conc = [] if args.no_concurrent else build_concurrent(notes, args.bpm, args.tpq)
    text = render_chart(notes, conc, args.bpm, args.tpq)

    out_path = args.out or (os.path.splitext(args.csv)[0] + ".wdschart")
    with open(out_path, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)

    if not args.quiet:
        name = args.title or os.path.splitext(os.path.basename(args.csv))[0]
        print("[转谱] %s" % name)
        if args.artist:
            print("       曲名   : %s" % args.artist)
        if args.difficulty:
            print("       难度   : %s" % args.difficulty)
        print("       CSV    : %d 行" % len(rows))
        print("       BPM    : %s   TPQ %d   吸附网格 %d tick (1/%d 拍)"
              % (fmt_num(args.bpm), args.tpq, grid, max(1, args.tpq // grid)))
        print("       音符   : %d 条" % len(notes))
        if dropped:
            print("       丢弃   : %s(注:编辑器导入时也会丢弃)" %
                  ", ".join("noteType=%d x%d" % (k, v) for k, v in sorted(dropped.items())))
        if stats["stars"]:
            extra = ""
            if stats["star_alias"]:
                extra = "(其中 %d 条是 CSV 里写 40 的,已按所属长条换成 30/31)" % stats["star_alias"]
            print("       长条星星: %d 条%s" % (stats["stars"], extra))
        if stats["star_orphan"]:
            print("       注意   : %d 条星星找不到所属长条,已降级为普通键" % stats["star_orphan"])
        if stats.get("split_fixed"):
            print("       分裂线 : %d 条 CSV 里没写宽度,已按整场宽 %d 补上"
                  % (stats["split_fixed"], FIELD_WIDTH))
        for m in warn_msgs:
            print("       提示   : %s" % m)
        print("       并发线 : %d 条" % len(conc))
        print("       输出   : %s" % out_path)

    if args.verify:
        if not args.quiet:
            print()
        verify(text, args.verify, args.bpm, args.tpq, args.quiet)
    return 0


if __name__ == "__main__":
    sys.exit(main())
