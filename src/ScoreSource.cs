// 官方曲库的数据获取与 BPM 推导(与界面分开,方便单独测试)
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text;

namespace WdsChartForge
{
    static class ScoreSource
    {
        public static string Download(int song, int diff)
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; }
            catch { }
            string url = string.Format(CultureInfo.InvariantCulture, Catalog.ScoreBase, song, diff);
            using (WebClient wc = new WebClient())
            {
                wc.Headers.Add("User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                    "(KHTML, like Gecko) Chrome/122.0 Safari/537.36");
                byte[] data = wc.DownloadData(url);
                return new UTF8Encoding(false).GetString(data);
            }
        }

        public static List<double> CollectTimes(string text)
        {
            List<double> ts = new List<double>();
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                string[] f = line.Split(',');
                if (f.Length < 3) continue;
                double v;
                if (double.TryParse(f[0], NumberStyles.Float, CultureInfo.InvariantCulture, out v)
                    && v >= 0)
                {
                    ts.Add(v);
                }
            }
            ts.Sort();
            return ts;
        }

        /// <summary>
        /// 推导 BPM:时间点乘 (bpm*8) 后应当都是整数,按平均偏差打分取最优。
        /// 得到的只是"基准值",真实 BPM 可能是它的整数倍,需要人工用 ×2/÷2 确认。
        /// </summary>
        public static double InferBpm(List<double> ts, out double bestScore)
        {
            return InferBpm(ts, 60.0, 300.0, out bestScore);
        }

        /// <summary>
        /// 在 [lo, hi] 区间里搜索。音游 BPM 大多落在 100~220,
        /// 而数据本身分不清"基准值"和它的整数倍,所以默认就在这个区间里找。
        /// </summary>
        public static double InferBpm(List<double> ts, double lo, double hi, out double bestScore)
        {
            bestScore = double.MaxValue;
            double best = 120.0;
            if (ts == null || ts.Count == 0) return best;
            int n = Math.Min(ts.Count, 400);
            for (double bpm = lo; bpm <= hi + 0.0001; bpm += 0.005)
            {
                double s = 0;
                for (int i = 0; i < n; i++)
                {
                    double x = ts[i] * bpm * 8.0;
                    s += Math.Abs(x - Math.Round(x));
                }
                s /= n;
                if (s < bestScore)
                {
                    bestScore = s;
                    best = bpm;
                }
            }
            return Math.Round(best * 1000.0) / 1000.0;
        }
    }
}
