using RBLAOI.ViewModels;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace RBLAOI.Views
{
    public partial class HistoryResultWindow : Window
    {
        private readonly MainViewModel _viewModel;
        private List<ResultItem> _items;

        public class ResultItem
        {
            public string Name { get; set; }
            public string Time { get; set; }
            public string Occupied { get; set; }
            public string FullPath { get; set; }
        }

        public HistoryResultWindow(MainViewModel vm, List<string> resultDirs, string currentFolder)
        {
            InitializeComponent();
            _viewModel = vm;

            _items = resultDirs.Select(dir =>
            {
                string folderName = Path.GetFileName(dir);
                string timeStr = "";
                // 2026-08-27：目录名支持 Result_{yyyyMMdd_HHmmss} 及带后缀（如 _Run1，精度检测每轮历史）——
                // 用正则提取时间戳，不再依赖固定 Substring(7)
                var m = System.Text.RegularExpressions.Regex.Match(folderName, @"(\d{8})_(\d{6})");
                if (m.Success && DateTime.TryParseExact($"{m.Groups[1].Value}_{m.Groups[2].Value}",
                    "yyyyMMdd_HHmmss", null, System.Globalization.DateTimeStyles.None, out DateTime dt))
                    timeStr = dt.ToString("yyyy-MM-dd HH:mm:ss");
                return new ResultItem
                {
                    Name = folderName,
                    Time = timeStr,
                    Occupied = (folderName == currentFolder) ? "✓" : "",
                    FullPath = dir
                };
            }).ToList();

            lvHistory.ItemsSource = _items;
        }

        /// <summary>导出单个历史结果为 CSV 文件（另存为）</summary>
        private void ExportOne(string resultDir)
        {
            string folderName = Path.GetFileName(resultDir);
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "导出结果",
                Filter = "CSV 文件|*.csv",
                FileName = $"{folderName}.csv",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
            };
            if (dlg.ShowDialog() != true) return;
            ExportToCsv(resultDir, dlg.FileName);
        }

        /// <summary>导出全部历史结果到方案目录 Export/ 文件夹</summary>
        private void ExportAll()
        {
            string projectPath = _viewModel.GetProjectPath();
            if (string.IsNullOrEmpty(projectPath)) return;

            string exportDir = Path.Combine(projectPath, "Export");
            Directory.CreateDirectory(exportDir);

            int count = 0;
            foreach (var item in _items)
            {
                string csvPath = Path.Combine(exportDir, $"{item.Name}.csv");
                if (ExportToCsv(item.FullPath, csvPath))
                    count++;
            }

            MessageBox.Show($"导出完成，共 {count} 个文件\n保存位置: {exportDir}", "导出成功",
                MessageBoxButton.OK, MessageBoxImage.Information);

            // 打开导出目录
            try { Process.Start(exportDir); } catch { }
        }

        /// <summary>将指定目录的 DetectResults.json 导出为 CSV</summary>
        private bool ExportToCsv(string resultDir, string csvPath)
        {
            string resultFile = Path.Combine(resultDir, "DetectResults.json");
            if (!File.Exists(resultFile)) return false;

            string json = File.ReadAllText(resultFile);
            var results = Newtonsoft.Json.JsonConvert.DeserializeObject<List<Models.PinResult>>(json);
            if (results == null || results.Count == 0) return false;

            using (var sw = new StreamWriter(csvPath, false, Encoding.UTF8))
            {
                sw.Write('﻿'); // BOM
                sw.WriteLine("PINID,检测位,位号,DX,DY,DH,结果,X,Y");
                foreach (var pin in results)
                {
                    sw.WriteLine($"{pin.PINID},{pin.DetectIndex},{pin.PinIndex},{pin.DiffX},{pin.DiffY},{pin.DiffH},{pin.Result},{pin.X:F3},{pin.Y:F3}");
                }
            }
            return true;
        }

        private void LvHistory_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (lvHistory.SelectedItem is ResultItem item)
            {
                if (_viewModel.LoadResultFromDir(item.FullPath))
                {
                    Close();
                }
            }
        }

        private void BtnExportOne_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.CommandParameter is string fullPath)
                ExportOne(fullPath);
        }

        private void BtnOpenFile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.CommandParameter is string fullPath)
            {
                string csvPath = Path.Combine(fullPath, $"{Path.GetFileName(fullPath)}.csv");
                // 先导出到临时位置
                if (ExportToCsv(fullPath, csvPath))
                {
                    try { Process.Start(csvPath); } catch { }
                }
            }
        }

        private void BtnExportAll_Click(object sender, RoutedEventArgs e)
        {
            ExportAll();
        }

        private void BtnDelete_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.CommandParameter is string fullPath)
            {
                if (MessageBox.Show($"确定删除历史结果「{Path.GetFileName(fullPath)}」？\n该操作不可恢复。",
                    "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                {
                    try
                    {
                        Directory.Delete(fullPath, recursive: true);
                        _items.RemoveAll(i => i.FullPath == fullPath);
                        lvHistory.ItemsSource = null;
                        lvHistory.ItemsSource = _items;
                        string folderName = Path.GetFileName(fullPath);
                        if (_viewModel.GetCurrentResultFolder() == folderName)
                        {
                            var latest = _items.OrderByDescending(i => i.Name).FirstOrDefault();
                            if (latest != null)
                                _viewModel.LoadResultFromDir(latest.FullPath);
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"删除失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
