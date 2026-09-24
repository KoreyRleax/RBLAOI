using RBLAOI.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;

namespace RBLAOI.Views
{
    public partial class PrecisionReportListWindow : Window
    {
        private readonly string _projectPath;

        public class ReportItem
        {
            public string Name { get; set; }
            public string Time { get; set; }
            public string FullPath { get; set; }
        }

        public PrecisionReportListWindow(string projectPath)
        {
            InitializeComponent();
            _projectPath = projectPath;

            string reportDir = Path.Combine(projectPath, "Report");
            if (!Directory.Exists(reportDir))
            {
                MessageBox.Show("没有精度检测报告", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                Close();
                return;
            }

            var dirs = Directory.GetDirectories(reportDir, "Report_*")
                .OrderByDescending(d => d).ToList();

            var items = new List<ReportItem>();
            foreach (var dir in dirs)
            {
                string folderName = Path.GetFileName(dir);
                string timeStr = "";
                if (folderName.StartsWith("Report_") && folderName.Length >= 20)
                {
                    string ts = folderName.Substring(7); // 20260623_143000
                    if (DateTime.TryParseExact(ts, "yyyyMMdd_HHmmss", null,
                        System.Globalization.DateTimeStyles.None, out var dt))
                        timeStr = dt.ToString("yyyy-MM-dd HH:mm:ss");
                }
                items.Add(new ReportItem
                {
                    Name = folderName,
                    Time = timeStr,
                    FullPath = dir
                });
            }

            if (items.Count == 0)
            {
                MessageBox.Show("没有精度检测报告", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                Close();
                return;
            }

            lvReports.ItemsSource = items;
        }

        private void OpenReport(string reportDir)
        {
            try
            {
                string jsonPath = Path.Combine(reportDir, "report.json");
                if (!File.Exists(jsonPath))
                {
                    MessageBox.Show("报告文件不存在", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                string json = File.ReadAllText(jsonPath);
                var data = Newtonsoft.Json.JsonConvert.DeserializeAnonymousType(json,
                    new { summary = new PrecisionReportSummary(), stats = new List<PrecisionPinStat>() });

                if (data?.summary == null || data?.stats == null)
                {
                    MessageBox.Show("报告数据解析失败", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                string reportName = Path.GetFileName(reportDir);
                var win = new PrecisionReportWindow(data.summary, data.stats, reportName);
                win.Owner = this;
                win.ShowDialog();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"打开报告失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void LvReports_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (lvReports.SelectedItem is ReportItem item)
                OpenReport(item.FullPath);
        }

        private void BtnOpenReport_Click(object sender, RoutedEventArgs e)
        {
            if (((FrameworkElement)sender).DataContext is ReportItem item)
                OpenReport(item.FullPath);
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
