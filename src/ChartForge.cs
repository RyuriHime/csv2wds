// WDS 转谱器 —— 官方谱面 CSV -> .wdschart
// 单文件 WinForms 程序,用 .NET Framework 自带的 csc.exe 编译,零外部依赖。
// 转换规则见同目录 README.md。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace WdsChartForge
{
    // ------------------------------------------------------------ 数据与规则

    class Note
    {
        public int Seq;     // CSV 原始顺序,用来做稳定排序
        public int NoteId = -1;
        public int OwnerId = -1;    // 星星专用:所属长条主体的 note id
        public int Tick;
        public int TickEnd;
        public int NoteType;
        public int Lane;
        public int Width;
        public int Gim;
        public int Param;
    }

    class ConvertResult
    {
        public string ChartPath;
        public int CsvRows;
        public int Dropped900;
        public int ClampedLane;
        public int NoteCount;
        public int HoldCount;
        public int StarCount;
        public int StarAlias;
        public int StarOrphan;
        public int ConcCount;
        public double FirstSecond;
        public double LastSecond;
        public string Text;
    }

    static class Converter
    {
        const int ChartVersion = 5;

        static readonly HashSet<int> DropTypes = new HashSet<int> { 900 };
        static readonly HashSet<int> StartContrib = new HashSet<int> { 10, 20, 50, 80, 81, 82, 83 };
        static readonly HashSet<int> EndContrib = new HashSet<int> { 10, 20, 80, 82, 83, 100, 101, 110, 111 };
        // 长条里的"星星":chart 里写成 30/31,末字段填所属长条主体的 id。
        // 官方 CSV 里可以写 30/31,也可以写 40(要按所属长条推断成 30 还是 31)。
        static readonly HashSet<int> StarTypes = new HashSet<int> { 30, 31 };
        static readonly HashSet<int> StarAliasTypes = new HashSet<int> { 40 };
        static readonly HashSet<int> HoldBodyTypes = new HashSet<int> { 100, 101, 110, 111 };
        static readonly HashSet<int> StarWideOwnerTypes = new HashSet<int> { 110, 111 };
        // 分裂线:noteType=0 + 下面这些 gimmick 之一 + 第 7 列是 4~6 位编号。
        // chart 里写成  N <id> <tick> <tick2> 0 0 <场地宽度> <gimmick> <编号> -1
        // 有些 CSV 没写 lane/宽度(0 或 -1),要在这里补上,否则编辑器
        // 会把这条分裂线当成无效数据,导出时写不出来。
        static readonly HashSet<int> SplitGimmicks =
            new HashSet<int> { 11, 12, 13, 14, 15, 16, 32, 33 };
        const int FieldWidth = 12;      // WDS 场地宽度(4K / 12K 都是 12 个单位)

        public static string Num(double v)
        {
            if (v == Math.Floor(v) && Math.Abs(v) < 1e15)
                return ((long)v).ToString(CultureInfo.InvariantCulture);
            string s = v.ToString("0.######", CultureInfo.InvariantCulture);
            return s;
        }

        static int Snap(double sec, double bpm, int tpq, int grid)
        {
            double raw = sec * bpm * tpq / 60.0;
            if (grid <= 1) return (int)Math.Floor(raw + 0.5);
            double near = Math.Floor(raw / grid + 0.5) * grid;
            if (Math.Abs(raw - near) <= 1.0) return (int)near;
            return (int)Math.Floor(raw + 0.5);
        }

        static void MapGimmick(int noteType, string text, int param, int width, out int gim, out int par)
        {
            text = (text ?? "").Trim();
            if (text == "JumpScratch")
            {
                gim = (noteType == 100) ? 1 : 0;
                par = param;
                return;
            }
            if (text == "OneDirection")
            {
                if (noteType == 50)
                {
                    gim = 0;
                    par = (param == 0) ? -width : width;
                    return;
                }
                gim = 2;
                par = param;
                return;
            }
            int v;
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
            {
                gim = v;
                par = param;
                return;
            }
            throw new Exception("无法识别的 gimmick 值: " + text);
        }

        public static ConvertResult Convert(string csvPath, double bpm, int tpq, int grid,
                                            bool keep900, string outPath, out string log)
        {
            StringBuilder sb = new StringBuilder();
            string[] lines = File.ReadAllLines(csvPath, Encoding.UTF8);

            List<Note> notes = new List<Note>();
            int rows = 0, dropped = 0, clamped = 0;
            int splitFixed = 0;
            double first = double.MaxValue, last = double.MinValue;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0) continue;
                string[] f = line.Split(',');
                if (f.Length != 7)
                    throw new Exception(string.Format("第 {0} 行列数不是 7(读到 {1} 列)", i + 1, f.Length));
                for (int k = 0; k < 7; k++) f[k] = f[k].Trim();

                double t0 = double.Parse(f[0], CultureInfo.InvariantCulture);
                double t1 = double.Parse(f[1], CultureInfo.InvariantCulture);
                int noteType = (int)Math.Floor(double.Parse(f[2], CultureInfo.InvariantCulture) + 0.5);
                int laneIn = (int)Math.Floor(double.Parse(f[3], CultureInfo.InvariantCulture) + 0.5);
                int width = (int)Math.Floor(double.Parse(f[4], CultureInfo.InvariantCulture) + 0.5);
                int param = (int)Math.Floor(double.Parse(f[6], CultureInfo.InvariantCulture) + 0.5);
                rows++;

                if (DropTypes.Contains(noteType) && !keep900) { dropped++; continue; }

                int gim, par;
                MapGimmick(noteType, f[5], param, width, out gim, out par);

                Note n = new Note();
                n.Tick = Snap(t0, bpm, tpq, grid);
                n.TickEnd = (t1 >= 0) ? Snap(t1, bpm, tpq, grid) : n.Tick;
                n.NoteType = noteType;
                n.Lane = laneIn - 1;
                if (n.Lane < 0) { n.Lane = 0; clamped++; }
                n.Width = width;
                if (noteType == 0 && SplitGimmicks.Contains(gim))
                {
                    // 分裂线要横跨整个场地:lane 归 0,宽度没填就补满
                    n.Lane = 0;
                    if (n.Width <= 0) { n.Width = FieldWidth; splitFixed++; }
                }
                n.Gim = gim;
                n.Param = par;
                n.Seq = notes.Count;
                notes.Add(n);

                if (t0 < first) first = t0;
                if (t0 > last) last = t0;
            }

            // 按起始 tick 升序;同一 tick 内保持 CSV 原有顺序(稳定排序)
            notes.Sort(delegate (Note a, Note b)
            {
                int c = a.Tick.CompareTo(b.Tick);
                return (c != 0) ? c : a.Seq.CompareTo(b.Seq);
            });

            // 分配 id,并解析"星星"的归属(末字段要填它所在那条长条的 id)
            for (int i = 0; i < notes.Count; i++) notes[i].NoteId = i;
            int stars = 0, starAlias = 0, starOrphan = 0;
            foreach (Note n in notes)
            {
                if (!StarTypes.Contains(n.NoteType) && !StarAliasTypes.Contains(n.NoteType)) continue;
                Note owner = FindStarOwner(notes, n);
                if (owner == null)
                {
                    // 找不到所属长条:降级成普通键,保证整份谱面还能被编辑器读入
                    starOrphan++;
                    n.NoteType = 10;
                    n.OwnerId = -1;
                    continue;
                }
                if (StarAliasTypes.Contains(n.NoteType))
                {
                    n.NoteType = StarWideOwnerTypes.Contains(owner.NoteType) ? 31 : 30;
                    starAlias++;
                }
                n.OwnerId = owner.NoteId;
                stars++;
            }

            List<string> conc = BuildConcurrent(notes, bpm, tpq);

            StringBuilder text = new StringBuilder();
            text.Append("WDSCHART ").Append(ChartVersion).Append('\n');
            text.Append("BPM ").Append(Num(bpm)).Append('\n');
            text.Append("TPQ ").Append(tpq).Append('\n');
            text.Append("TIMING 1\n");
            text.Append("T 0 ").Append(Num(bpm)).Append(" 4 4 3\n");
            text.Append("NOTES ").Append(notes.Count).Append('\n');
            for (int i = 0; i < notes.Count; i++)
            {
                Note n = notes[i];
                int tail = (n.NoteType == 30 || n.NoteType == 31) ? n.OwnerId : -1;
                text.Append("N ").Append(n.NoteId).Append(' ')
                    .Append(n.Tick).Append(' ').Append(n.TickEnd).Append(' ')
                    .Append(n.NoteType).Append(' ').Append(n.Lane).Append(' ')
                    .Append(n.Width).Append(' ').Append(n.Gim).Append(' ')
                    .Append(n.Param).Append(' ').Append(tail).Append('\n');
            }
            text.Append("CONCURRENT ").Append(conc.Count).Append('\n');
            for (int i = 0; i < conc.Count; i++) text.Append(conc[i]).Append('\n');
            text.Append("END\n");

            if (outPath == null) outPath = Path.ChangeExtension(csvPath, ".wdschart");
            File.WriteAllText(outPath, text.ToString(), new UTF8Encoding(false));

            ConvertResult r = new ConvertResult();
            r.ChartPath = outPath;
            r.CsvRows = rows;
            r.Dropped900 = dropped;
            r.ClampedLane = clamped;
            r.NoteCount = notes.Count;
            r.StarCount = stars;
            r.StarAlias = starAlias;
            r.StarOrphan = starOrphan;
            r.ConcCount = conc.Count;
            r.FirstSecond = (first == double.MaxValue) ? 0 : first;
            r.LastSecond = (last == double.MinValue) ? 0 : last;
            int holds = 0;
            foreach (Note n in notes) if (n.TickEnd != n.Tick) holds++;
            r.HoldCount = holds;
            r.Text = text.ToString();

            sb.AppendFormat("CSV 行数      : {0}\n", rows);
            sb.AppendFormat("BPM / TPQ     : {0} / {1}   吸附网格 {2} tick (1/{3} 拍)\n",
                            Num(bpm), tpq, grid, Math.Max(1, tpq / grid));
            sb.AppendFormat("音符          : {0} 条(其中长条 {1} 条)\n", notes.Count, holds);
            if (dropped > 0)
                sb.AppendFormat("丢弃          : noteType=900 共 {0} 行(编辑器导入时同样会丢弃)\n", dropped);
            if (clamped > 0)
                sb.AppendFormat("提示          : lane 列写 0 的 {0} 行已按 lane=0 处理\n", clamped);
            if (stars > 0)
                sb.AppendFormat("长条星星      : {0} 条{1}\n", stars,
                    starAlias > 0 ? string.Format("(其中 {0} 条是 CSV 里写 40 的,已按所属长条换成 30/31)", starAlias) : "");
            if (starOrphan > 0)
                sb.AppendFormat("注意          : {0} 条星星找不到所属长条,已降级为普通键\n", starOrphan);
            sb.AppendFormat("并发线        : {0} 条\n", conc.Count);
            sb.AppendFormat("时间轴        : {0:0.###} ~ {1:0.###} 秒\n", r.FirstSecond, r.LastSecond);
            sb.AppendFormat("输出          : {0}\n", outPath);
            log = sb.ToString();
            return r;
        }

        static Note FindStarOwner(List<Note> notes, Note star)
        {
            Note best = null;
            foreach (Note n in notes)
            {
                if (n.TickEnd <= n.Tick) continue;
                if (n.Lane != star.Lane) continue;
                if (!(n.Tick <= star.Tick && star.Tick <= n.TickEnd)) continue;
                if (!HoldBodyTypes.Contains(n.NoteType)) continue;
                if (best == null || n.TickEnd < best.TickEnd) best = n;
            }
            return best;
        }

        static List<string> BuildConcurrent(List<Note> notes, double bpm, int tpq)
        {
            double msPerTick = 60000.0 / (bpm * tpq);
            SortedDictionary<int, List<int[]>> starts = new SortedDictionary<int, List<int[]>>();
            SortedDictionary<int, List<int[]>> ends = new SortedDictionary<int, List<int[]>>();

            foreach (Note n in notes)
            {
                if (StartContrib.Contains(n.NoteType))
                {
                    int ms = (int)Math.Floor(n.Tick * msPerTick + 0.5);
                    if (!starts.ContainsKey(ms)) starts[ms] = new List<int[]>();
                    starts[ms].Add(new int[] { n.Lane, n.Width });
                }
                if (n.TickEnd != n.Tick && EndContrib.Contains(n.NoteType))
                {
                    int ms = (int)Math.Floor(n.TickEnd * msPerTick + 0.5);
                    if (!ends.ContainsKey(ms)) ends[ms] = new List<int[]>();
                    ends[ms].Add(new int[] { n.Lane, n.Width });
                }
            }

            SortedSet<int> times = new SortedSet<int>();
            foreach (int k in starts.Keys) times.Add(k);
            foreach (int k in ends.Keys) times.Add(k);

            List<string> outp = new List<string>();
            foreach (int ms in times)
            {
                List<int[]> pts = new List<int[]>();
                List<int[]> tmp;
                if (starts.TryGetValue(ms, out tmp)) pts.AddRange(tmp);
                if (ends.TryGetValue(ms, out tmp)) pts.AddRange(tmp);
                if (pts.Count < 2) continue;
                int left = int.MaxValue, right = int.MinValue;
                foreach (int[] p in pts)
                {
                    if (p[0] < left) left = p[0];
                    if (p[0] + p[1] > right) right = p[0] + p[1];
                }
                outp.Add(string.Format(CultureInfo.InvariantCulture, "C {0} {1} {2}", ms, left, right - left));
            }
            return outp;
        }

    }

    // ------------------------------------------------------------ 图形界面

    class MainForm : Form
    {
        TextBox txtCsv = new TextBox();
        TextBox txtBpm = new TextBox();
        Label lblBpmHint = new Label();
        CheckBox chk900 = new CheckBox();
        Button btnGo = new Button();
        Button btnBatch = new Button();
        Button btnBrowse = new Button();
        Button btnClear = new Button();
        TextBox txtLog = new TextBox();
        string pendingFile = null;

        public MainForm(string initialFile)
        {
            Text = "WDS 转谱器 —— 官方谱面 CSV → .wdschart";
            ClientSize = new Size(760, 560);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9f);
            AllowDrop = true;
            DragEnter += OnDragEnter;
            DragDrop += OnDragDrop;
            pendingFile = initialFile;

            Label l1 = new Label();
            l1.Text = "谱面 CSV";
            l1.Location = new Point(16, 16);
            l1.Size = new Size(70, 22);
            Controls.Add(l1);

            txtCsv.Location = new Point(92, 14);
            txtCsv.Size = new Size(540, 24);
            txtCsv.TextChanged += delegate { OnCsvChanged(); };
            Controls.Add(txtCsv);

            btnBrowse.Text = "浏览...";
            btnBrowse.Location = new Point(640, 13);
            btnBrowse.Size = new Size(104, 26);
            btnBrowse.Click += OnBrowse;
            Controls.Add(btnBrowse);

            Label hint0 = new Label();
            hint0.Text = "也可以直接把 CSV 文件拖进这个窗口";
            hint0.ForeColor = Color.Gray;
            hint0.Location = new Point(92, 40);
            hint0.Size = new Size(400, 18);
            Controls.Add(hint0);

            Label l2 = new Label();
            l2.Text = "BPM";
            l2.Location = new Point(16, 74);
            l2.Size = new Size(70, 22);
            Controls.Add(l2);

            txtBpm.Location = new Point(92, 72);
            txtBpm.Size = new Size(120, 24);
            Controls.Add(txtBpm);

            lblBpmHint.Text = "CSV 里没有 BPM,需要在这里填";
            lblBpmHint.ForeColor = Color.Gray;
            lblBpmHint.Location = new Point(224, 76);
            lblBpmHint.Size = new Size(520, 18);
            Controls.Add(lblBpmHint);

            chk900.Text = "保留 noteType=900 的行(分裂线数据;默认丢弃,与编辑器导入行为一致)";
            chk900.Location = new Point(92, 104);
            chk900.Size = new Size(600, 24);
            Controls.Add(chk900);

            btnGo.Text = "开始转谱";
            btnGo.Location = new Point(92, 136);
            btnGo.Size = new Size(140, 32);
            btnGo.Click += OnGo;
            Controls.Add(btnGo);

            btnBatch.Text = "整个文件夹批量转...";
            btnBatch.Location = new Point(240, 136);
            btnBatch.Size = new Size(180, 32);
            btnBatch.Click += OnBatch;
            Controls.Add(btnBatch);

            btnClear.Text = "清空日志";
            btnClear.Location = new Point(428, 136);
            btnClear.Size = new Size(100, 32);
            btnClear.Click += delegate { txtLog.Clear(); };
            Controls.Add(btnClear);

            txtLog.Multiline = true;
            txtLog.ReadOnly = true;
            txtLog.ScrollBars = ScrollBars.Vertical;
            txtLog.Location = new Point(16, 180);
            txtLog.Size = new Size(728, 362);
            txtLog.BackColor = Color.White;
            txtLog.Font = new Font("Consolas", 9.5f);
            Controls.Add(txtLog);

            Shown += delegate
            {
                if (pendingFile != null)
                {
                    txtCsv.Text = pendingFile;
                    pendingFile = null;
                    // 没填 BPM 时不自动开始,只把文件放好等使用者自己填
                    double b;
                    if (double.TryParse(txtBpm.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out b) && b > 0)
                        OnGo(null, null);
                }
            };
        }

        public void SetBpm(double bpm)
        {
            txtBpm.Text = Converter.Num(bpm);
        }

        void OnDragEnter(object s, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
        }

        void OnDragDrop(object s, DragEventArgs e)
        {
            string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files == null || files.Length == 0) return;
            string csv = null;
            foreach (string f in files)
            {
                if (f.ToLowerInvariant().EndsWith(".csv")) { csv = f; break; }
            }
            if (csv == null) csv = files[0];
            txtCsv.Text = csv;
            if (files.Length > 1) Log("(一次只处理一个 CSV,已取第一个)");
        }

        void OnBrowse(object s, EventArgs e)
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Filter = "官方谱面 CSV (*.csv)|*.csv|所有文件 (*.*)|*.*";
                d.Title = "选择要转换的谱面 CSV";
                if (d.ShowDialog(this) == DialogResult.OK) txtCsv.Text = d.FileName;
            }
        }

        void OnCsvChanged()
        {
            string f = txtCsv.Text.Trim().Trim('"');
            if (!File.Exists(f)) return;
            // CSV 里没有 BPM,也不做任何猜测,一律由使用者自己填
            lblBpmHint.Text = "必须手动填写 BPM(CSV 里没有这个信息,填错会导致整首谱面错位)";
            lblBpmHint.ForeColor = Color.Firebrick;
        }

        void Log(string s)
        {
            txtLog.AppendText(s + "\r\n");
        }

        void OnGo(object s, EventArgs e)
        {
            string csv = txtCsv.Text.Trim().Trim('"');
            if (!File.Exists(csv)) { MessageBox.Show(this, "请先选择要转换的 CSV 文件。", "WDS 转谱器"); return; }
            double manual;
            bool hasManual = double.TryParse(txtBpm.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out manual) && manual > 0;
            if (!hasManual)
            {
                MessageBox.Show(this, "请填写正确的 BPM(例如 194)。", "WDS 转谱器");
                return;
            }
            DoOne(csv, manual);
        }

        void OnBatch(object s, EventArgs e)
        {
            using (FolderBrowserDialog d = new FolderBrowserDialog())
            {
                d.Description = "选择放着 CSV 的文件夹(里面所有 .csv 都会转成同名 .wdschart)";
                if (File.Exists(txtCsv.Text.Trim().Trim('"')))
                    d.SelectedPath = Path.GetDirectoryName(Path.GetFullPath(txtCsv.Text.Trim().Trim('"')));
                if (d.ShowDialog(this) != DialogResult.OK) return;

                string[] files = Directory.GetFiles(d.SelectedPath, "*.csv");
                if (files.Length == 0) { Log("该文件夹里没有 .csv 文件。"); return; }

                double manual;
                bool hasManual = double.TryParse(txtBpm.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out manual) && manual > 0;
                if (!hasManual)
                {
                    MessageBox.Show(this, "批量转谱也需要先填写 BPM(所有 CSV 会用同一个 BPM)。", "WDS 转谱器");
                    return;
                }

                Log("=================================================");
                Log("批量转谱:" + d.SelectedPath + "  共 " + files.Length + " 个 CSV");
                Log("统一使用 BPM " + Converter.Num(manual));
                int ok = 0, fail = 0;
                foreach (string f in files)
                {
                    if (DoOne(f, manual)) ok++; else fail++;
                }
                Log("-------------------------------------------------");
                Log(string.Format("批量完成:成功 {0} 个,失败/跳过 {1} 个", ok, fail));
            }
        }

        bool DoOne(string csv, double bpm)
        {
            try
            {
                string log;
                Converter.Convert(csv, bpm, 480, 20, chk900.Checked, null, out log);
                Log("=================================================");
                Log("[完成] " + Path.GetFileName(csv) + "   BPM " + Converter.Num(bpm));
                foreach (string ln in log.Split('\n')) if (ln.Trim().Length > 0) Log("   " + ln);
                return true;
            }
            catch (Exception ex)
            {
                Log("[出错] " + Path.GetFileName(csv) + " —— " + ex.Message);
                return false;
            }
        }
    }

    // ------------------------------------------------------------ 入口

    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            string csv = null;
            double bpm = 0;
            bool keep900 = false, quiet = false;
            string outPath = null;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a == "--bpm" && i + 1 < args.Length) { double.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out bpm); }
                else if (a == "--out" && i + 1 < args.Length) { outPath = args[++i]; }
                else if (a == "--keep-900") keep900 = true;
                else if (a == "--quiet") quiet = true;
                else if (!a.StartsWith("-")) csv = a;
            }

            if (csv != null && quiet)
            {
                try
                {
                    if (bpm <= 0) throw new Exception("必须用 --bpm 指定 BPM(CSV 里没有这个信息,不做任何猜测)");
                    string log;
                    Converter.Convert(csv, bpm, 480, 20, keep900, outPath, out log);
                    Environment.Exit(0);
                }
                catch (Exception ex)
                {
                    try { Console.Error.WriteLine(ex.Message); } catch { }
                    Environment.Exit(1);
                }
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            MainForm f = new MainForm(csv);
            if (bpm > 0) f.SetBpm(bpm);
            Application.Run(f);
        }
    }
}
