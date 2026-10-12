// 官方曲库转谱窗口:搜曲名/编号 + 选难度 -> 下载 -> 自动推 BPM -> 转 .wdschart
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Windows.Forms;

namespace WdsChartForge
{
    class CatalogForm : Form
    {
        class Song
        {
            public int Id;
            public string Name;
            public int[] Diffs;
            public override string ToString()
            {
                return string.Format("{0,4}  {1}", Id, Name);
            }
        }

        TextBox txtSearch = new TextBox();
        ListBox lstSongs = new ListBox();
        ComboBox cboDiff = new ComboBox();
        TextBox txtBpm = new TextBox();
        Button btnHalf = new Button();
        Button btnDouble = new Button();
        Button btnFetch = new Button();
        Label lblStatus = new Label();
        TextBox txtLog = new TextBox();
        List<Song> all = new List<Song>();
        List<double> lastTimes = null;
        double lastRawBpm = 0;

        public CatalogForm()
        {
            Text = "官方曲库转谱";
            ClientSize = new Size(760, 600);
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Microsoft YaHei UI", 9f);
            MinimumSize = new Size(640, 480);

            Label l1 = new Label();
            l1.Text = "搜索曲名或编号";
            l1.Location = new Point(14, 14);
            l1.Size = new Size(110, 22);
            Controls.Add(l1);

            txtSearch.Location = new Point(130, 12);
            txtSearch.Size = new Size(400, 24);
            txtSearch.TextChanged += delegate { RefreshList(); };
            Controls.Add(txtSearch);

            Label l2 = new Label();
            l2.Text = "(清空 = 列出全部)";
            l2.ForeColor = Color.Gray;
            l2.Location = new Point(540, 16);
            l2.Size = new Size(200, 18);
            Controls.Add(l2);

            lstSongs.Location = new Point(14, 44);
            lstSongs.Size = new Size(516, 300);
            lstSongs.IntegralHeight = false;
            lstSongs.Font = new Font("Microsoft YaHei UI", 9.5f);
            lstSongs.SelectedIndexChanged += delegate { OnSongChanged(); };
            Controls.Add(lstSongs);

            Label l3 = new Label();
            l3.Text = "难度";
            l3.Location = new Point(546, 44);
            l3.Size = new Size(60, 22);
            Controls.Add(l3);

            cboDiff.Location = new Point(546, 68);
            cboDiff.Size = new Size(198, 24);
            cboDiff.DropDownStyle = ComboBoxStyle.DropDownList;
            cboDiff.Items.AddRange(new object[] {
                "1 - Normal", "2 - Hard", "3 - Extra", "4 - Stella", "5 - Olivier" });
            Controls.Add(cboDiff);

            Label l4 = new Label();
            l4.Text = "BPM(自动推导,可改)";
            l4.Location = new Point(546, 106);
            l4.Size = new Size(200, 22);
            Controls.Add(l4);

            txtBpm.Location = new Point(546, 130);
            txtBpm.Size = new Size(120, 24);
            Controls.Add(txtBpm);

            btnHalf.Text = "÷2";
            btnHalf.Location = new Point(672, 129);
            btnHalf.Size = new Size(34, 26);
            btnHalf.Click += delegate { ScaleBpm(0.5); };
            Controls.Add(btnHalf);

            btnDouble.Text = "×2";
            btnDouble.Location = new Point(710, 129);
            btnDouble.Size = new Size(34, 26);
            btnDouble.Click += delegate { ScaleBpm(2.0); };
            Controls.Add(btnDouble);

            lblStatus.Text = "在左边选一首曲子。";
            lblStatus.ForeColor = Color.Gray;
            lblStatus.Location = new Point(546, 166);
            lblStatus.Size = new Size(200, 120);
            Controls.Add(lblStatus);

            btnFetch.Text = "下载并转谱";
            btnFetch.Location = new Point(14, 354);
            btnFetch.Size = new Size(160, 34);
            btnFetch.Click += OnFetch;
            Controls.Add(btnFetch);

            Button btnClose = new Button();
            btnClose.Text = "关闭";
            btnClose.Location = new Point(184, 354);
            btnClose.Size = new Size(90, 34);
            btnClose.Click += delegate { Close(); };
            Controls.Add(btnClose);

            txtLog.Multiline = true;
            txtLog.ReadOnly = true;
            txtLog.ScrollBars = ScrollBars.Vertical;
            txtLog.Location = new Point(14, 398);
            txtLog.Size = new Size(732, 188);
            txtLog.BackColor = Color.White;
            txtLog.Font = new Font("Consolas", 9f);
            txtLog.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            Controls.Add(txtLog);

            LoadCatalog();
        }

        void LoadCatalog()
        {
            foreach (KeyValuePair<int, string> kv in Catalog.Names)
            {
                int[] diffs;
                if (!Catalog.Diffs.TryGetValue(kv.Key, out diffs)) continue;
                Song s = new Song();
                s.Id = kv.Key;
                s.Name = kv.Value;
                s.Diffs = diffs;
                all.Add(s);
            }
            all.Sort(delegate (Song a, Song b) { return a.Id.CompareTo(b.Id); });
            RefreshList();
            Log("曲库载入完成:共 " + all.Count + " 首有谱面的曲子。");
        }

        void Log(string s)
        {
            txtLog.AppendText(s + "\r\n");
        }

        void RefreshList()
        {
            string q = txtSearch.Text.Trim();
            lstSongs.BeginUpdate();
            lstSongs.Items.Clear();
            foreach (Song s in all)
            {
                if (q.Length == 0
                    || s.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                    || s.Id.ToString().StartsWith(q))
                {
                    lstSongs.Items.Add(s);
                }
            }
            lstSongs.EndUpdate();
        }

        void OnSongChanged()
        {
            Song s = lstSongs.SelectedItem as Song;
            if (s == null) return;
            cboDiff.Items.Clear();
            string[] labels = { "", "1 - Normal", "2 - Hard", "3 - Extra", "4 - Stella", "5 - Olivier" };
            foreach (int d in s.Diffs)
            {
                if (d >= 1 && d <= 5) cboDiff.Items.Add(labels[d]);
            }
            if (cboDiff.Items.Count > 0) cboDiff.SelectedIndex = 0;
            txtBpm.Text = "";
            lblStatus.Text = string.Format("已选 #{0} {1}\n点「下载并转谱」开始。", s.Id, s.Name);
            lblStatus.ForeColor = Color.Gray;
        }

        void ScaleBpm(double factor)
        {
            double v;
            if (!double.TryParse(txtBpm.Text.Trim(), NumberStyles.Float,
                                 CultureInfo.InvariantCulture, out v) || v <= 0) return;
            txtBpm.Text = Converter.Num(Math.Round(v * factor * 1000.0) / 1000.0);
        }

        Song Selected()
        {
            return lstSongs.SelectedItem as Song;
        }

        int SelectedDiff()
        {
            if (cboDiff.SelectedIndex < 0) return 0;
            string t = cboDiff.SelectedItem.ToString();
            int d;
            if (int.TryParse(t.Substring(0, 1), out d)) return d;
            return 0;
        }

        void OnFetch(object sender, EventArgs e)
        {
            Song s = Selected();
            if (s == null) { MessageBox.Show(this, "请先在列表里选一首曲子。", "官方曲库"); return; }
            int diff = SelectedDiff();
            if (diff == 0) { MessageBox.Show(this, "请选择难度。", "官方曲库"); return; }

            btnFetch.Enabled = false;
            lblStatus.ForeColor = Color.Gray;
            lblStatus.Text = "下载中...";
            Application.DoEvents();
            string text = null;
            try
            {
                text = Download(s.Id, diff);
            }
            catch (Exception ex)
            {
                Log("[下载失败] " + ex.Message);
                lblStatus.Text = "下载失败,详情见下方日志。";
                lblStatus.ForeColor = Color.Firebrick;
                btnFetch.Enabled = true;
                return;
            }
            Log(string.Format("下载完成: #{0} {1}  难度 {2}  ({3} 行)", s.Id, s.Name, diff, text.Split('\n').Length));

            if (txtBpm.Text.Trim().Length == 0)
            {
                List<double> ts = ScoreSource.CollectTimes(text);
                lastTimes = ts;
                double score;
                double bpm = ScoreSource.InferBpm(ts, out score);
                lastRawBpm = bpm;
                txtBpm.Text = Converter.Num(bpm);
                Log(string.Format("自动推导 BPM = {0}(平均 tick 偏差 {1:0.0000})",
                                  Converter.Num(bpm), score));
                Log("  注意:BPM 只能定到「基准值的整数倍」,不对时用 ÷2 / ×2 校正。");
                lblStatus.Text = string.Format("推导 BPM ≈ {0}\n不对就按 ÷2 / ×2。", Converter.Num(bpm));
            }

            double useBpm;
            if (!double.TryParse(txtBpm.Text.Trim(), NumberStyles.Float,
                                 CultureInfo.InvariantCulture, out useBpm) || useBpm <= 0)
            {
                lblStatus.Text = "BPM 无效。";
                lblStatus.ForeColor = Color.Firebrick;
                btnFetch.Enabled = true;
                return;
            }
            DoConvert(s, diff, text, useBpm);
            btnFetch.Enabled = true;
        }

        void DoConvert(Song s, int diff, string text, double bpm)
        {
            string tmp = Path.Combine(Path.GetTempPath(),
                                      string.Format("csv2wds_{0}_{1}.csv", s.Id, diff));
            try
            {
                File.WriteAllText(tmp, text, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Log("[写临时文件失败] " + ex.Message);
                return;
            }

            string safe = s.Name;
            foreach (char c in Path.GetInvalidFileNameChars()) safe = safe.Replace(c, '_');
            string defaultDir = Directory.Exists(@"D:\wds") ? @"D:\wds" : Environment.CurrentDirectory;
            string outPath;
            using (SaveFileDialog d = new SaveFileDialog())
            {
                d.Filter = "WDS 谱面 (*.wdschart)|*.wdschart";
                d.FileName = safe + ".wdschart";
                d.InitialDirectory = defaultDir;
                if (d.ShowDialog(this) != DialogResult.OK) { Log("已取消。"); return; }
                outPath = d.FileName;
            }

            try
            {
                string log;
                Converter.Convert(tmp, bpm, 480, 20, false, outPath, out log);
                Log("========== " + Path.GetFileName(outPath) + "  BPM " + Converter.Num(bpm));
                foreach (string ln in log.Split('\n'))
                    if (ln.Trim().Length > 0) Log("   " + ln);
                lblStatus.Text = "转换完成。";
                lblStatus.ForeColor = Color.SeaGreen;
            }
            catch (Exception ex)
            {
                Log("[转换失败] " + ex.Message);
                lblStatus.Text = "转换失败,详情见日志。";
                lblStatus.ForeColor = Color.Firebrick;
            }
            finally
            {
                try { File.Delete(tmp); } catch { }
            }
        }

        static string Download(int song, int diff)
        {
            return ScoreSource.Download(song, diff);
        }
    }
}
