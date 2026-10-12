# csv2wds

把 WDS 编辑器的**官方谱面 CSV** 转换成 `.wdschart` 的小工具。

CSV 里只有小数秒、没有 BPM,直接导入会整首错位。这个工具按你给的 BPM 把
时间换算回 tick,并按原始音符网格对齐,同时补上 CSV 里没有的那部分数据
(长条星星的归属、并发线)。

## 特性

- 单文件绿色 exe(`WDS转谱器.exe`),双击即用,只依赖 Windows 自带的 .NET Framework,不用装 Python
- 也可以把 CSV 直接拖到 exe 或窗口上
- 支持整个文件夹批量转换
- **官方曲库模式**:按曲名 / 编号搜索 + 选难度,自动下载谱面并推导 BPM
- 附带同功能的 Python 命令行版,多一个 `--verify` 可以对拍参考谱面
- 另一个命令行版本 `osu2wds` 的产物、以及编辑器保存出来的谱面都能互相读

## 两条转谱路径

### 一、官方曲库(自动推导 BPM)

点主界面的「官方曲库转谱...」打开搜索窗口:

1. 输入曲名或曲目编号(留空 = 列出全部 241 首)
2. 选中曲子 → 选难度(Normal / Hard / Extra / Stella / Olivier)
3. 点「下载并转谱」→ 自动下载谱面、推导 BPM、转成 `.wdschart`

谱面来自同人资源站 `redive.estertion.win` 公开的 WDS 谱面文本,
地址形如 `pub-…r2.dev/music/<曲目编号>/<难度>.txt`(难度 = 1~5)。

**关于推导出来的 BPM**:原理是"时间点 × BPM × 8 应当都是整数",按此搜索最优解。
但**数据本身分不清一个 BPM 和它的整数倍**(例如 66.375 / 132.75 / 265.5 都能让
tick 落在整数上),所以推导给出的只是**基准值**,面板上配了 `÷2` 和 `×2`
两个按钮一键校正,也可以直接手改。好消息是:**倍数取错不影响音符位置**,
只影响 tick 数值和编辑器里的小节线。

### 二、自定义 CSV(手动填 BPM)

自己做的 CSV 时间精度不够,推不出 BPM,**必须手动填**。填好 BPM 再点
「开始转谱」;留空会直接拒绝转换。

## 图形界面用法

1. 双击 `WDS转谱器.exe`
2. 把 `.csv` 拖进窗口,或点「浏览...」
3. **手动填写 BPM**
4. 点「开始转谱」→ 同目录生成同名 `.wdschart`

「整个文件夹批量转...」会对文件夹里所有 `.csv` 用**同一个** BPM 转换。

## 命令行用法

```bash
# 图形界面的 exe 也能当命令行用
WDS转谱器.exe "chart.csv" --bpm 194
WDS转谱器.exe "chart.csv" --bpm 194 --out "out.wdschart"
WDS转谱器.exe "chart.csv" --bpm 194 --keep-900
WDS转谱器.exe "chart.csv" --bpm 194 --quiet     # 不开窗口,静默转换

# 直接从官方曲库拉谱面(--fetch 曲目编号 难度),BPM 自动推导
WDS转谱器.exe --fetch 7 3 --out "7-3.wdschart"
WDS转谱器.exe --fetch 7 3 --bpm 202.3 --out "7-3.wdschart"   # 手动指定 BPM

# 只打开曲库窗口
WDS转谱器.exe --catalog

# Python 版
python csv2wdschart.py chart.csv --bpm 194
python csv2wdschart.py chart.csv --bpm 194 --verify reference.wdschart
```

`--bpm` 是必填项,不填直接报错退出。

## 官方 CSV 的格式

每行 7 列:

```
起始秒 , 结束秒(-1 表示没有尾巴) , noteType , lane(从 1 开始) , 宽度 , gimmick , 参数
```

## 转换规则

1. **时间 → tick**:`tick = round(秒 × BPM × TPQ / 60)`,再吸附到 **1/24 拍**
   的网格上(TPQ 480 时 = 20 tick 一格)。1/24 拍正好能整除 16 分、24 分、32 分
   时值。落在网格之外的行(例如 noteType=0 这类特殊数据)只做四舍五入,不吸附。
2. **字段**:`noteType` 原样;`lane = CSV 的 lane − 1`;`宽度` 原样;
   NOTES 按起始 tick 升序,同一 tick 内保持 CSV 原有顺序。
3. **gimmick 两列 → A / B**

| CSV gimmick | noteType | A | B |
| --- | --- | --- | --- |
| `0` | 任意 | 0 | 参数 |
| `JumpScratch` | 110 / 111(划键链) | 0 | 参数 |
| `JumpScratch` | 100(长条主体) | 1 | 参数 |
| `OneDirection` | 除 50 外 | 2 | 参数 |
| `OneDirection` | 50 | 0 | `参数=0 ? −宽度 : +宽度` |
| 数字 | 任意 | 该数字 | 参数 |

4. **noteType = 900 的行**不是音符(编辑器还没实现的分裂线数据),导入时会被
   丢掉;想保留用 `--keep-900`。
5. **分裂线**在 CSV 里的特征很明确:`noteType = 0`,第 6 列(gimmick)是
   `11 / 12 / 13 / 14 / 15 / 16 / 32 / 33` 之一,第 7 列是一个 4~6 位的编号
   (例如 10120、11660、11540)。在 `.wdschart` 里写成:

   ```
   N <id> <tick> <tick2> 0 <lane> <宽度> <gimmick> <编号> -1
   ```

   分裂线必须**横跨整个场地**,也就是 `lane = 0`、`宽度 = 场地宽度`(WDS 的
   场地固定是 12 个单位,4 键和 12 键都一样)。

   有些 CSV 会把这两格留空(写成 `0` 或 `-1`),那样生成的谱面编辑器认得,
   但**导出 CSV 时分裂线会写不出来**。所以本工具遇到分裂线时会强制
   `lane = 0`,宽度没填(≤0)就补成 12,并在日志里说明补了几条。
6. **长条里的星星**(编辑器 UI 里同宽滑键上的节点)在 `.wdschart` 里写成:

   ```
   N <id> <tick> <tick> <30|31> <lane> <宽度> 0 0 <所属长条的 id>
   ```

   普通长条(100 / 101)里的星星是 `30`,滑键长条(110 / 111)里的星星是 `31`,
   最后一格写它所在那条长条主体的 note id。CSV 里星星可以写 `30` / `31`,也可以
   写 `40`;写 `40` 时工具会按它落在哪条长条里自动换成 30 或 31,并把最后一格补上。
7. **CONCURRENT 段**是编辑器保存时算出来的派生数据,CSV 里没有,工具会照着
   同样的规则重建:取每个时刻"起点参与的音符 ∪ 终点参与的音符",参与数 ≥ 2 时
   输出 `C <毫秒> <最小 lane> <max(lane+宽度) − 最小 lane>`。

   起点参与的类型:`10 20 50 80 81 82 83`
   终点参与的类型:`10 20 80 82 83 100 101 110 111`

## 构建

exe 用 Windows 自带的 C# 编译器即可生成,不需要 Visual Studio:

```bat
build.cmd
```

或手动:

```bat
csc /target:winexe /codepage:65001 /out:WDS转谱器.exe ^
    /r:System.Windows.Forms.dll /r:System.Drawing.dll src\ChartForge.cs
```

## 已知限制

- `TIMING` 段按单 BPM 输出 `T 0 <bpm> 4 4 3`。CSV 里没有拍号信息,
  需要的话在编辑器里补。
- 输出的是 WDSCHART 5。旧版(WDSCHART 4)谱面的 A/B 两列语义不同。
- `noteType` 0 / 30 / 31 / 40 等特殊行按原样搬运,能否识别取决于编辑器版本。
