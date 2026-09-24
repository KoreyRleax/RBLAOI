using System;
using System.Collections.Generic;

namespace RBLAOI.Models
{
    /// <summary>单轮（某次检测）中某针的原始偏差/高度样本（精度报告每轮明细用）</summary>
    public class PrecisionSample
    {
        /// <summary>轮次序号（1-based，第几次检测）</summary>
        public int RunIndex { get; set; }

        public double Dx { get; set; }
        public double Dy { get; set; }
        public double H { get; set; }

        /// <summary>该轮是否匹配到该针（漏检/超阈值失败时为 false，明细表显示空）</summary>
        public bool Matched { get; set; }
    }

    /// <summary>单个针脚的精度统计</summary>
    public class PrecisionPinStat
    {
        public int Seq { get; set; }
        public int PINID { get; set; }
        public int DetectIndex { get; set; }
        public int GridIndex { get; set; }
        public int PinIndex { get; set; }

        /// <summary>针型标记（A/B/C...，2026-08-27 新增，汇总/明细按 kind 显示与细分）</summary>
        public string PinType { get; set; } = "A";

        public double X { get; set; }
        public double Y { get; set; }
        public int SampleCount { get; set; }

        public double DxMean { get; set; }
        public double DxStdDev { get; set; }
        public double DxRange { get; set; }
        public double DxMin { get; set; }
        public double DxMax { get; set; }

        public double DyMean { get; set; }
        public double DyStdDev { get; set; }
        public double DyRange { get; set; }
        public double DyMin { get; set; }
        public double DyMax { get; set; }

        public double HMean { get; set; }
        public double HStdDev { get; set; }
        public double HRange { get; set; }
        public double HMin { get; set; }
        public double HMax { get; set; }

        /// <summary>每轮明细样本（2026-08-27 新增：报告每轮明细表用；H=0 的轮次仍记录原始值，统计时另行排除）</summary>
        public List<PrecisionSample> Samples { get; set; } = new List<PrecisionSample>();
    }

    /// <summary>某一针型的极差分布统计（2026-08-27 新增：汇总良好→严重按 kind 细分）</summary>
    public class KindRangeStat
    {
        /// <summary>针型标记（A/B/C...）</summary>
        public string Kind { get; set; }

        /// <summary>该针型的针数</summary>
        public int Total { get; set; }

        /// <summary>极差≤0.01mm</summary>
        public int Excellent { get; set; }
        /// <summary>极差≤0.02mm</summary>
        public int Normal { get; set; }
        /// <summary>极差0.02~0.03mm</summary>
        public int SlightlyLarge { get; set; }
        /// <summary>极差0.03~0.04mm</summary>
        public int Large { get; set; }
        /// <summary>极差>0.04mm</summary>
        public int Severe { get; set; }
    }

    /// <summary>精度检测报告摘要</summary>
    public class PrecisionReportSummary
    {
        public int TotalRuns { get; set; }
        public int TotalPins { get; set; }
        public DateTime StartTime { get; set; }
        public TimeSpan Duration { get; set; }

        /// <summary>X方向平均重复性（所有针σdx的均值）</summary>
        public double AvgDxStdDev { get; set; }
        /// <summary>Y方向平均重复性（所有针σdy的均值）</summary>
        public double AvgDyStdDev { get; set; }
        /// <summary>高度平均重复性（所有针σdh的均值）</summary>
        public double AvgHStdDev { get; set; }
        /// <summary>综合位置重复性 = sqrt(σdx² + σdy²) 取均值</summary>
        public double CompositeRepeatability { get; set; }

        // ========== 极差分布统计（全体） ==========
        /// <summary>极差≤0.01mm</summary>
        public int RangeExcellent { get; set; }
        /// <summary>极差≤0.02mm</summary>
        public int RangeNormal { get; set; }
        /// <summary>极差0.02~0.03mm</summary>
        public int RangeSlightlyLarge { get; set; }
        /// <summary>极差0.03~0.04mm</summary>
        public int RangeLarge { get; set; }
        /// <summary>极差>0.04mm</summary>
        public int RangeSevere { get; set; }

        /// <summary>按针型细分的极差分布（2026-08-27 新增；旧报告反序列化为 null，窗口需容错）</summary>
        public List<KindRangeStat> KindRanges { get; set; }
    }
}
