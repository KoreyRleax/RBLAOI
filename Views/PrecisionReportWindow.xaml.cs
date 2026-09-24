using RBLAOI.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RBLAOI.Views
{
    public partial class PrecisionReportWindow : Window
    {
        private readonly PrecisionReportSummary _summary;
        private readonly List<PrecisionPinStat> _stats;

        /// <summary>每轮明细显示项（左侧信息单元格 + 右侧每轮 dx dy h 行）</summary>
        public class DetailBlock
        {
            public string Header { get; set; }          // "id{Seq}  kind  Pos  X  Y"（独占单元格）
            public List<DetailRow> Rows { get; set; }   // 每轮一行
        }

        public class DetailRow
        {
            public int Run { get; set; }
            public string Dx { get; set; }
            public string Dy { get; set; }
            public string H { get; set; }
            public bool Matched { get; set; }
        }

        public PrecisionReportWindow(PrecisionReportSummary summary, List<PrecisionPinStat> stats, string reportName = "")
        {
            InitializeComponent();
            _summary = summary;
            _stats = stats ?? new List<PrecisionPinStat>();

            // 标题显示报告名称
            if (!string.IsNullOrEmpty(reportName))
                titleBar.Title = $"精度检测报告 - {reportName}";

            // 填写汇总区
            txtTotalRuns.Text = summary.TotalRuns.ToString();
            txtTotalPins.Text = summary.TotalPins.ToString();
            txtDuration.Text = $"{summary.Duration.TotalMinutes:F1}分";
            txtAvgDxStd.Text = summary.AvgDxStdDev.ToString("F4");
            txtAvgDyStd.Text = summary.AvgDyStdDev.ToString("F4");
            txtAvgHStd.Text = summary.AvgHStdDev.ToString("F4");

            // 绑定数据
            dgStats.ItemsSource = _stats;

            // 2026-08-27 (e)：汇总明细表（id kind Pos X Y σdx σdy σh ∂dx ∂dy ∂h）→ 数据源已有 PinType/Samples
            // 2026-08-27 (d)：每轮明细表
            BuildDetailBlocks();

            // 2026-08-27 (f)：按 kind 细分的极差分布表
            BuildKindRangeTable();
        }

        /// <summary>构建每轮明细块（id kind Pos X Y 独占单元格 + 对齐 N 行 dx dy h）</summary>
        private void BuildDetailBlocks()
        {
            int totalRuns = Math.Max(1, _summary.TotalRuns);
            var blocks = new List<DetailBlock>();

            foreach (var s in _stats)
            {
                // 左侧单元格：id{序号} kind Pos X Y（X/Y 以第一轮 XY 显示，即参考轮 X/Y）
                string header = $"id{s.Seq}  {s.PinType ?? "A"}  {s.DetectIndex}  {s.X:F3}  {s.Y:F3}";
                var rows = new List<DetailRow>();

                // 每轮一行 dx dy h（漏检/匹配失败轮 → 空值）；Samples 为 null（旧报告）时按 RunIndex 兜底
                for (int r = 1; r <= totalRuns; r++)
                {
                    var sample = s.Samples?.FirstOrDefault(x => x.RunIndex == r);
                    if (sample != null && sample.Matched)
                    {
                        rows.Add(new DetailRow
                        {
                            Run = r,
                            Dx = sample.Dx.ToString("F3"),
                            Dy = sample.Dy.ToString("F3"),
                            H = sample.H.ToString("F3"),
                            Matched = true,
                        });
                    }
                    else
                    {
                        rows.Add(new DetailRow { Run = r, Dx = "", Dy = "", H = "", Matched = false });
                    }
                }
                blocks.Add(new DetailBlock { Header = header, Rows = rows });
            }

            lvDetails.ItemsSource = blocks;
            txtDetailCount.Text = _stats.Count.ToString();
        }

        /// <summary>构建按针型细分的极差分布表（行=等级，列=全部+各 kind）</summary>
        private void BuildKindRangeTable()
        {
            // 数据行：等级标签 + 各列数量(占比)
            var kinds = _summary.KindRanges?.Select(k => k.Kind).ToList() ?? new List<string>();
            var dataRows = new List<string[]>();

            // 全部列（聚合）
            int totalAll = _summary.TotalPins;
            string Pct(int n) => totalAll > 0 ? $"{100.0 * n / totalAll:F0}%" : "-";
            dataRows.Add(new[] { "≤0.01mm（优秀）", $"{_summary.RangeExcellent} ({Pct(_summary.RangeExcellent)})" });
            dataRows.Add(new[] { "≤0.02mm（正常）", $"{_summary.RangeNormal} ({Pct(_summary.RangeNormal)})" });
            dataRows.Add(new[] { "0.02~0.03mm（偏大）", $"{_summary.RangeSlightlyLarge} ({Pct(_summary.RangeSlightlyLarge)})" });
            dataRows.Add(new[] { "0.03~0.04mm（较大）", $"{_summary.RangeLarge} ({Pct(_summary.RangeLarge)})" });
            dataRows.Add(new[] { ">0.04mm（严重）", $"{_summary.RangeSevere} ({Pct(_summary.RangeSevere)})" });

            // 每 kind 列
            var kindRows = _summary.KindRanges ?? new List<KindRangeStat>();
            foreach (var kr in kindRows)
            {
                int t = Math.Max(1, kr.Total);
                string P(int n) => $"{n} ({100.0 * n / t:F0}%)";
                dataRows[0] = dataRows[0].Concat(new[] { P(kr.Excellent) }).ToArray();
                dataRows[1] = dataRows[1].Concat(new[] { P(kr.Normal) }).ToArray();
                dataRows[2] = dataRows[2].Concat(new[] { P(kr.SlightlyLarge) }).ToArray();
                dataRows[3] = dataRows[3].Concat(new[] { P(kr.Large) }).ToArray();
                dataRows[4] = dataRows[4].Concat(new[] { P(kr.Severe) }).ToArray();
            }

            // 动态构建 Grid：列数 = 1(等级) + 1(全部) + kinds.Count
            kindRangeGrid.Children.Clear();
            int colCount = 2 + kinds.Count;
            for (int c = 0; c < colCount; c++)
                kindRangeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int r = 0; r <= dataRows.Count; r++)
                kindRangeGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(r == 0 ? 28 : 24) });

            // 表头
            AddCell("等级", 0, 0, true, TextAlignment.Left);
            AddCell("全部", 0, 1, true, TextAlignment.Center);
            for (int i = 0; i < kinds.Count; i++)
                AddCell(kinds[i], 0, 2 + i, true, TextAlignment.Center);

            // 数据行（颜色沿用原分布表语义）
            var rowColors = new[] { "#4FC3F7", "#81C784", "#FFD54F", "#FFA726", "#E57373" };
            for (int r = 0; r < dataRows.Count; r++)
            {
                for (int c = 0; c < colCount; c++)
                {
                    bool isLabel = c == 0;
                    AddCell(dataRows[r][c], r + 1, c, false,
                        isLabel ? TextAlignment.Left : TextAlignment.Center,
                        isLabel ? null : rowColors[r]);
                }
            }
        }

        private void AddCell(string text, int row, int col, bool isHeader, TextAlignment align, string colorHex = null)
        {
            // 主题资源跟随：表头=ThemeTextSecondary，标签列=ThemeText，数值列=等级色（深浅主题共用强调色）
            Brush fg;
            if (isHeader)
                fg = (Application.Current.TryFindResource("ThemeTextSecondary") as Brush) ?? Brushes.Gray;
            else if (colorHex != null)
                fg = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorHex));
            else
                fg = (Application.Current.TryFindResource("ThemeText") as Brush) ?? Brushes.LightGray;

            var tb = new TextBlock
            {
                Text = text,
                FontSize = 12,
                FontWeight = isHeader ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = fg,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = align == TextAlignment.Left ? HorizontalAlignment.Left : HorizontalAlignment.Center,
                Margin = new Thickness(isHeader && align == TextAlignment.Left ? 12 : 4, 0, 4, 0),
            };
            Grid.SetRow(tb, row);
            Grid.SetColumn(tb, col);
            kindRangeGrid.Children.Add(tb);
        }

        private void BtnExport_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "导出精度检测报告",
                Filter = "CSV 文件|*.csv",
                FileName = $"精度报告_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
            };

            if (dlg.ShowDialog() != true) return;

            try
            {
                using (var sw = new StreamWriter(dlg.FileName, false, Encoding.UTF8))
                {
                    sw.Write('﻿'); // BOM
                    sw.WriteLine("精度检测报告");
                    sw.WriteLine($"检测次数,{_summary.TotalRuns}");
                    sw.WriteLine($"匹配针数,{_summary.TotalPins}");
                    sw.WriteLine($"耗时,{_summary.Duration.TotalMinutes:F1}分");
                    sw.WriteLine($"σdx均值,{_summary.AvgDxStdDev:F4}");
                    sw.WriteLine($"σdy均值,{_summary.AvgDyStdDev:F4}");
                    sw.WriteLine($"σh均值,{_summary.AvgHStdDev:F4}");
                    sw.WriteLine($"综合重复性,{_summary.CompositeRepeatability:F4}");
                    sw.WriteLine();
                    sw.WriteLine("极差分布统计");
                    sw.WriteLine($"≤0.01mm（优秀）,{_summary.RangeExcellent},{(_summary.TotalPins > 0 ? $"{100.0 * _summary.RangeExcellent / _summary.TotalPins:F0}%" : "-")}");
                    sw.WriteLine($"≤0.02mm（正常）,{_summary.RangeNormal},{(_summary.TotalPins > 0 ? $"{100.0 * _summary.RangeNormal / _summary.TotalPins:F0}%" : "-")}");
                    sw.WriteLine($"0.02~0.03mm（偏大）,{_summary.RangeSlightlyLarge},{(_summary.TotalPins > 0 ? $"{100.0 * _summary.RangeSlightlyLarge / _summary.TotalPins:F0}%" : "-")}");
                    sw.WriteLine($"0.03~0.04mm（较大）,{_summary.RangeLarge},{(_summary.TotalPins > 0 ? $"{100.0 * _summary.RangeLarge / _summary.TotalPins:F0}%" : "-")}");
                    sw.WriteLine($">0.04mm（严重）,{_summary.RangeSevere},{(_summary.TotalPins > 0 ? $"{100.0 * _summary.RangeSevere / _summary.TotalPins:F0}%" : "-")}");
                    sw.WriteLine();

                    // 2026-08-27 (f)：按 kind 细分分布
                    if (_summary.KindRanges != null && _summary.KindRanges.Count > 0)
                    {
                        sw.WriteLine("极差分布统计（按针型）");
                        sw.WriteLine("针型,针数,≤0.01mm（优秀）,≤0.02mm（正常）,0.02~0.03mm（偏大）,0.03~0.04mm（较大）,>0.04mm（严重）");
                        foreach (var kr in _summary.KindRanges)
                        {
                            sw.WriteLine($"{kr.Kind},{kr.Total},{kr.Excellent},{kr.Normal},{kr.SlightlyLarge},{kr.Large},{kr.Severe}");
                        }
                        sw.WriteLine();
                    }

                    // 2026-08-27 (e)：汇总明细（id kind Pos X Y σdx σdy σh ∂dx ∂dy ∂h，无 PINID）
                    sw.WriteLine("汇总明细（id kind Pos X Y σdx σdy σh ∂dx ∂dy ∂h）");
                    sw.WriteLine("id,kind,Pos,X,Y,σdx,σdy,σh,∂dx,∂dy,∂h");
                    foreach (var s in _stats)
                    {
                        sw.WriteLine($"{s.Seq},{s.PinType ?? "A"},{s.DetectIndex},{s.X:F3},{s.Y:F3}," +
                            $"{s.DxStdDev:F4},{s.DyStdDev:F4},{s.HStdDev:F4}," +
                            $"{s.DxRange:F4},{s.DyRange:F4},{s.HRange:F4}");
                    }
                    sw.WriteLine();

                    // 2026-08-27 (d)：每轮明细（每针一块：id kind Pos X Y + 各轮 dx dy h）
                    sw.WriteLine("每轮明细（id kind Pos X Y | 每轮 dx,dy,h；H=0 轮次保留原始值，统计已排除）");
                    sw.WriteLine("id,kind,Pos,X,Y,轮次,dx,dy,h");
                    foreach (var s in _stats)
                    {
                        for (int r = 1; r <= _summary.TotalRuns; r++)
                        {
                            var sample = s.Samples?.FirstOrDefault(x => x.RunIndex == r);
                            if (sample != null && sample.Matched)
                                sw.WriteLine($"{s.Seq},{s.PinType ?? "A"},{s.DetectIndex},{s.X:F3},{s.Y:F3},{r},{sample.Dx:F3},{sample.Dy:F3},{sample.H:F3}");
                            else
                                sw.WriteLine($"{s.Seq},{s.PinType ?? "A"},{s.DetectIndex},{s.X:F3},{s.Y:F3},{r},,,");
                        }
                    }
                }

                MessageBox.Show($"导出成功\n{dlg.FileName}", "导出完成", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"导出失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
