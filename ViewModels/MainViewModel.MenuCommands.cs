using GlobalCameraModuleCs;
using HalconDotNet;
using Microsoft.Win32;
using Newtonsoft.Json;
using RBLAOI.Core;
using RBLAOI.Core.Controls;
using RBLAOI.Core.Device;
using RBLAOI.Core.LightSource;
using RBLAOI.Core.Managers;
using RBLAOI.Core.Motion;
using RBLAOI.Core.Utility;
using RBLAOI.Core.Vision;
using RBLAOI.Models;
using RBLAOI.Views;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using VM.Core;
using VM.PlatformSDKCS;

namespace RBLAOI.ViewModels
{
    public partial class MainViewModel : NotificationObject
    {
        #region ===================================主界面菜单===================================
        #region 文件
        public void ShowProgramHistory()
        {
            var dlg = new ProgramHistoryWindow();
            dlg.Owner = Application.Current.MainWindow;

            if (dlg.ShowDialog() == true && dlg.SelectedProjectPath != null)
            {
                string projectDir = dlg.SelectedProjectPath;
                string projectName = Path.GetFileName(projectDir);
                string parentDir = Path.GetDirectoryName(projectDir);

                ProjectManager.Instance.SetWorkingDirectory(parentDir);
                _lastOpenDirectory = parentDir;

                if (ProjectManager.Instance.LoadProject(projectName))
                {
                    var project = ProjectManager.Instance.CurrentProject;
                    project.ProjectPath = ProjectManager.Instance.CurrentProjectPath;

                    LoadProjectToUI(project);
                    ProjectManager.Instance.SaveCurrentProject();
                    _imageManager.Clear();
                    _vision.ClearHalconWindow();


                    GrabPositions = new ObservableCollection<GrabPosition>();
                    DetectPositions = new ObservableCollection<DetectPosition>();

                    var positions = LoadGrabPositions();
                    if (positions != null && positions.Count > 0)
                        UpdateGrabPositionLists(positions);

                    var savedInspPoints = LoadDetectPositions();
                    if (savedInspPoints != null && savedInspPoints.Count > 0
                        && GrabPositions != null
                        && savedInspPoints.All(ip => GrabPositions.Any(g => g.Index == ip.GrabIndex)))
                    {
                        foreach (var pos in GrabPositions)
                            pos.DetectPositionChanged -= OnGrabPositionDetectChanged;

                        foreach (var ip in savedInspPoints)
                        {
                            var gp = GrabPositions.FirstOrDefault(g => g.Index == ip.GrabIndex);
                            if (gp != null)
                            {
                                gp.IsDetectPosition = ip.IsDetectPosition;
                                gp.PinTypes = ip.PinTypes;
                            }
                        }

                        foreach (var pos in GrabPositions)
                            pos.DetectPositionChanged += OnGrabPositionDetectChanged;

                        DetectPositions = new ObservableCollection<DetectPosition>(savedInspPoints);
                    }

                    UpdateStatus($"当前方案: {projectName}");
                    _vmOverlays.Clear();
                    LoadPinResults();
                    SyncPinIdealHeight();
                    //LoadVmSolutionFromCurrentProject();
                    // 方案加载完成后检查轨宽
                    _ = CheckAndNotifyRailWidthAsync();
                    _ = LoadProjectImagesAndDisplayAsync();
                }
            }
        }

        public void CreateNewProject()
        {
            var dlg = new CreateProjectDialog();
            dlg.Owner = Application.Current.MainWindow;

            if (dlg.ShowDialog() == true)
            {
                string projectName = dlg.ProjectName;
                double boardWidth = dlg.BoardWidth;
                double boardHeight = dlg.BoardHeight;

                if (ProjectManager.Instance.CreateNewProject(projectName))
                {
                    var project = ProjectManager.Instance.CurrentProject;
                    project.BoardWidth = boardWidth;
                    project.BoardHeight = boardHeight;
                    project.ProjectPath = ProjectManager.Instance.CurrentProjectPath;
                    CurrentProjectPath = ProjectManager.Instance.CurrentProjectPath;

                    // 新方案：Fov 从相机分辨率×像素当量推算
                    SyncFovFromCameraParams();

                    // 新方案：重叠率从系统参数"最大重叠率"继承
                    project.Overlap = _sysParam.Data.MaxOverlap;

                    _imageManager.Clear();
                    _vision.ClearHalconWindow();
                    LoadProjectToUI(project);
                    ProjectManager.Instance.SaveCurrentProject();
                }
            }
        }

        public void OpenProject()
        {
            string initialDir = !string.IsNullOrEmpty(_lastOpenDirectory)
                ? _lastOpenDirectory
                : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Board");

            var dlg = new OpenFileDialog
            {
                Filter = "方案文件 (*.xml)|*.xml",
                Title = "打开方案",
                InitialDirectory = initialDir
            };

            if (dlg.ShowDialog() == true)
            {
                string configFile = dlg.FileName;
                string projectDir = Path.GetDirectoryName(configFile);
                string projectName = Path.GetFileName(projectDir);
                string parentDir = Path.GetDirectoryName(projectDir);

                ProjectManager.Instance.SetWorkingDirectory(parentDir);
                _lastOpenDirectory = parentDir;

                if (ProjectManager.Instance.LoadProject(projectName))
                {
                    var project = ProjectManager.Instance.CurrentProject;
                    project.ProjectPath = ProjectManager.Instance.CurrentProjectPath;

                    LoadProjectToUI(project);
                    ProjectManager.Instance.SaveCurrentProject();
                    _imageManager.Clear();
                    _vision.ClearHalconWindow();

                    GrabPositions = new ObservableCollection<GrabPosition>();
                    DetectPositions = new ObservableCollection<DetectPosition>();

                    var positions = LoadGrabPositions();
                    if (positions != null && positions.Count > 0)
                        UpdateGrabPositionLists(positions);

                    var savedInspPoints = LoadDetectPositions();
                    if (savedInspPoints != null && savedInspPoints.Count > 0
                        && GrabPositions != null
                        && savedInspPoints.All(ip => GrabPositions.Any(g => g.Index == ip.GrabIndex)))
                    {
                        foreach (var pos in GrabPositions)
                            pos.DetectPositionChanged -= OnGrabPositionDetectChanged;

                        foreach (var ip in savedInspPoints)
                        {
                            var gp = GrabPositions.FirstOrDefault(g => g.Index == ip.GrabIndex);
                            if (gp != null)
                            {
                                gp.IsDetectPosition = ip.IsDetectPosition;
                                gp.PinTypes = ip.PinTypes;
                            }
                        }

                        foreach (var pos in GrabPositions)
                            pos.DetectPositionChanged += OnGrabPositionDetectChanged;

                        DetectPositions = new ObservableCollection<DetectPosition>(savedInspPoints);
                    }

                    _vmOverlays.Clear();
                    LoadPinResults();
                    SyncPinIdealHeight();
                    // 方案加载完成后检查轨宽
                    _ = CheckAndNotifyRailWidthAsync();
                    _ = LoadProjectImagesAndDisplayAsync();
                    UpdateStatus($"当前方案: {projectName}");
                }
            }
        }

        public void SaveProject()
        {
            var project = ProjectManager.Instance.CurrentProject;
            if (project == null)
            {
                MessageBox.Show(Application.Current.MainWindow, "没有加载的方案", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 保存时自动退出选择模式
            ExitSelectInspectionMode();

            SaveUIToProject();
            ProjectManager.Instance.SaveCurrentProject();

            if (GrabPositions != null && GrabPositions.Count > 0)
                SaveGrabPositions(GrabPositions.ToList());

            SaveDetectPositions();

            UpdateStatus($"方案已保存: {project.ProjectName}");
        }

        public void SaveAsProject()
        {
            var project = ProjectManager.Instance.CurrentProject;
            if (project == null) return;

            var dlg = new SaveFileDialog
            {
                Filter = "方案文件 (*.xml)|*.xml",
                Title = "另存为",
                InitialDirectory = ProjectManager.Instance.WorkingDirectory,
                FileName = $"{project.ProjectName}_副本"
            };

            if (dlg.ShowDialog() == true)
            {
                string newFilePath = dlg.FileName;
                string newProjectDir = Path.GetDirectoryName(newFilePath);
                string newProjectName = Path.GetFileNameWithoutExtension(newFilePath);

                string oldProjectDir = ProjectManager.Instance.CurrentProjectPath;
                ProjectManager.Instance.SetWorkingDirectory(newProjectDir);

                SaveUIToProject();
                ProjectManager.Instance.SaveAsProject(newProjectName);

                if (!string.IsNullOrEmpty(oldProjectDir))
                {
                    void CopyDir(string src, string dst)
                    {
                        if (!Directory.Exists(src)) return;
                        Directory.CreateDirectory(dst);
                        foreach (var file in Directory.GetFiles(src))
                            File.Copy(file, Path.Combine(dst, Path.GetFileName(file)), overwrite: true);
                    }

                    newProjectDir = ProjectManager.Instance.CurrentProjectPath;
                    CopyDir(Path.Combine(oldProjectDir, "Images"),
                            Path.Combine(newProjectDir, "Images"));
                }

                var newProject = ProjectManager.Instance.CurrentProject;
                newProject.ProjectPath = ProjectManager.Instance.CurrentProjectPath;
                ProjectManager.Instance.SaveCurrentProject();

                CurrentProjectName = newProjectName;
                CurrentProjectPath = newProject.ProjectPath;
                _lastOpenDirectory = newProjectDir;

                if (GrabPositions != null && GrabPositions.Count > 0)
                    SaveGrabPositions(GrabPositions.ToList());

                SaveDetectPositions();

                UpdateStatus($"方案已另存为: {newProjectName}");
            }
        }

        public void ShowHistoryResult()
        {
            var project = ProjectManager.Instance.CurrentProject;
            if (project == null)
            {
                MessageBox.Show(Application.Current.MainWindow, "请先加载方案", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                var dirs = ListHistoryResultDirs();
                if (dirs.Count == 0)
                {
                    MessageBox.Show(Application.Current.MainWindow, "没有历史结果", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var win = new Views.HistoryResultWindow(this, dirs, _currentResultFolder);
                win.Owner = Application.Current.MainWindow;
                win.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                win.ShowDialog();
            }
            catch (Exception ex)
            {
                Log.Error($"打开历史结果窗口异常: {ex.Message}");
            }
        }

        public void ShowPrecisionReportList()
        {
            var project = ProjectManager.Instance.CurrentProject;
            if (project == null)
            {
                MessageBox.Show(Application.Current.MainWindow, "请先加载方案", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                var win = new Views.PrecisionReportListWindow(ProjectManager.Instance.CurrentProjectPath);
                win.Owner = Application.Current.MainWindow;
                win.ShowDialog();
            }
            catch (Exception ex)
            {
                Log.Error($"打开精度报告列表异常: {ex.Message}");
            }
        }

        public void ShowExportResult()
        {
            var project = ProjectManager.Instance.CurrentProject;
            if (project == null)
            {
                MessageBox.Show(Application.Current.MainWindow, "请先加载方案", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                var dirs = ListHistoryResultDirs();
                if (dirs.Count == 0)
                {
                    MessageBox.Show(Application.Current.MainWindow, "没有可导出的历史结果", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var dlg = new System.Windows.Forms.FolderBrowserDialog();
                dlg.Description = "选择导出目录";
                dlg.ShowNewFolderButton = true;
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.Cancel) return;

                string exportDir = dlg.SelectedPath;
                int count = 0;

                foreach (string resultDir in dirs)
                {
                    string resultFile = Path.Combine(resultDir, "DetectResults.json");
                    if (!File.Exists(resultFile)) continue;

                    string json = File.ReadAllText(resultFile);
                    var results = JsonConvert.DeserializeObject<List<PinResult>>(json);
                    if (results == null || results.Count == 0) continue;

                    string folderName = Path.GetFileName(resultDir);
                    string xlsPath = Path.Combine(exportDir, $"{folderName}.xls");

                    // CSV 格式：逗号分隔，字段加引号避免逗号/换行干扰
                    using (var sw = new StreamWriter(xlsPath, false, System.Text.Encoding.UTF8))
                    {
                        // 写入 UTF-8 BOM，确保 Excel 正确识别中文
                        sw.Write('﻿');
                        sw.WriteLine("PINID,检测位,位号,DX,DY,DH,结果,X,Y");

                        foreach (var pin in results)
                        {
                            sw.WriteLine($"{pin.PINID},{pin.DetectIndex},{pin.PinIndex},{pin.DiffX},{pin.DiffY},{pin.DiffH},{pin.Result},{pin.X:F3},{pin.Y:F3}");
                        }
                    }

                    count++;
                    Log.Info($"导出结果: {xlsPath}");
                }

                MessageBox.Show(Application.Current.MainWindow, $"导出完成，共 {count} 个文件\n保存位置: {exportDir}", "导出成功",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Log.Error($"导出结果异常: {ex.Message}");
                MessageBox.Show(Application.Current.MainWindow, $"导出失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public void CloseProject()
        {
            var project = ProjectManager.Instance.CurrentProject;
            if (project != null)
            {
                string name = project.ProjectName;
                ProjectManager.Instance.CloseProject();
                UpdateStatus($"方案已关闭: {name}");
            }
        }

        public void DeleteCurrentProject()
        {
            var project = ProjectManager.Instance.CurrentProject;
            if (project == null)
            {
                MessageBox.Show(Application.Current.MainWindow, "没有已加载的方案", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string name = project.ProjectName;
            var result = MessageBox.Show(Application.Current.MainWindow, $"确定要删除方案 \"{name}\" 吗？\n此操作不可恢复！", "确认删除",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;

            if (ProjectManager.Instance.DeleteProject(name))
            {
                UpdateStatus($"方案已删除: {name}");
                // 清空界面数据
                GrabPositions.Clear();
                DetectPositions.Clear();
                CurrentProjectName = "";
                CurrentProjectPath = "";
            }
            else
            {
                MessageBox.Show(Application.Current.MainWindow, "删除方案失败", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private RelayCommand _reloadProjectCommand;
        public RelayCommand ReloadProjectCommand =>
            _reloadProjectCommand ??= new RelayCommand(ReloadProject);

        private RelayCommand _saveProjectCommand;
        public RelayCommand SaveProjectCommand =>
            _saveProjectCommand ??= new RelayCommand(SaveProject);

        public void ReloadProject()
        {
            var project = ProjectManager.Instance.CurrentProject;
            if (project == null)
            {
                MessageBox.Show("没有加载的方案", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string projectName = project.ProjectName;
            string projectDir = ProjectManager.Instance.CurrentProjectPath;
            if (string.IsNullOrEmpty(projectDir)) return;

            string parentDir = Path.GetDirectoryName(projectDir);

            ProjectManager.Instance.SetWorkingDirectory(parentDir);

            if (ProjectManager.Instance.LoadProject(projectName))
            {
                project = ProjectManager.Instance.CurrentProject;
                project.ProjectPath = ProjectManager.Instance.CurrentProjectPath;

                _imageManager.Clear();
                _vision.ClearHalconWindow();
                LoadProjectToUI(project);
                ProjectManager.Instance.SaveCurrentProject();

                GrabPositions = new ObservableCollection<GrabPosition>();
                DetectPositions = new ObservableCollection<DetectPosition>();

                var positions = LoadGrabPositions();
                if (positions != null && positions.Count > 0)
                    UpdateGrabPositionLists(positions);

                var savedInspPoints = LoadDetectPositions();
                if (savedInspPoints != null && savedInspPoints.Count > 0
                    && GrabPositions != null
                    && savedInspPoints.All(ip => GrabPositions.Any(g => g.Index == ip.GrabIndex)))
                {
                    foreach (var pos in GrabPositions)
                        pos.DetectPositionChanged -= OnGrabPositionDetectChanged;

                    foreach (var ip in savedInspPoints)
                    {
                        var gp = GrabPositions.FirstOrDefault(g => g.Index == ip.GrabIndex);
                        if (gp != null)
                        {
                            gp.IsDetectPosition = ip.IsDetectPosition;
                            gp.PinTypes = ip.PinTypes;
                        }
                    }

                    foreach (var pos in GrabPositions)
                        pos.DetectPositionChanged += OnGrabPositionDetectChanged;

                    DetectPositions = new ObservableCollection<DetectPosition>(savedInspPoints);
                }

                _vmOverlays.Clear();
                LoadPinResults();
                SyncPinIdealHeight();
                //LoadVmSolutionFromCurrentProject();
                // 方案重新加载后检查轨宽
                _ = CheckAndNotifyRailWidthAsync();
                _ = LoadProjectImagesAndDisplayAsync();

                UpdateStatus($"方案已刷新: {projectName}");
            }
        }

        public void SendProgram() { }

        public async void ShowEditProjectDialog()
        {
            var project = ProjectManager.Instance.CurrentProject;
            if (project == null)
            {
                MessageBox.Show(Application.Current.MainWindow, "没有加载的方案", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            double originalHeight = BoardHeight;

            // 旧方案兼容：PluginFeatureCount可能为0，限制到1~10
            int pluginCount = project.PluginFeatureCount;
            if (pluginCount < 1 || pluginCount > 10)
                pluginCount = 1;

            // 确保 PinTypeParams 存在
            EnsurePinTypeParams(project);

            var dlg = new EditProjectWindow(
                BoardWidth, BoardHeight, FovWidth, FovHeight,
                Overlap, SubBoardCount,
                pluginCount, project.PinTypeParams,
                Remark,
                project.BoardFocus,
                project.CameraExposureTime,
                project.LightChannel1, project.LightChannel2,
                project.LightChannel3, project.LightChannel4,
                project.LightOn1, project.LightOn2, project.LightOn3, project.LightOn4);
            dlg.Owner = Application.Current.MainWindow;

            if (dlg.ShowDialog() == true)
            {
                BoardWidth = dlg.BoardWidth;
                BoardHeight = dlg.BoardHeight;
                FovWidth = dlg.FovWidth;
                FovHeight = dlg.FovHeight;
                Overlap = dlg.Overlap;
                SubBoardCount = dlg.SubBoardCount;
                Remark = dlg.Remark;

                // 保存检测参数、焦距参数、方案级曝光时间和光源参数
                project.PluginFeatureCount = dlg.PluginFeatureCount;
                project.PinTypeParams = dlg.PinTypeParams.ToArray();
                project.BoardFocus = dlg.BoardFocusValue;
                project.CameraExposureTime = dlg.CameraExposureValue;
                project.LightChannel1 = dlg.LightChannel1;
                project.LightChannel2 = dlg.LightChannel2;
                project.LightChannel3 = dlg.LightChannel3;
                project.LightChannel4 = dlg.LightChannel4;
                project.LightOn1 = dlg.LightOn1;
                project.LightOn2 = dlg.LightOn2;
                project.LightOn3 = dlg.LightOn3;
                project.LightOn4 = dlg.LightOn4;

                // 特征个数变化 -> 通知UI刷新标记工具栏
                UpdateMarkerToolbarState();

                bool heightChanged = Math.Abs(BoardHeight - originalHeight) > 0.01;

                if (heightChanged && _motionIO.IsConnected)
                {
                    var result = MessageBox.Show(Application.Current.MainWindow, "已经修改板高，是否需要自动调整轨道？",
                        "调整轨道", MessageBoxButton.YesNo, MessageBoxImage.Question);

                    if (result == MessageBoxResult.Yes)
                    {
                        double railHeight = HAxisMaxPosition - BoardHeight;
                        UpdateStatus($"正在调整轨宽: {railHeight}mm");
                        bool success = await _motionIO.MoveToAndWaitOK(h: railHeight, timeoutMs: (int)_sysParam.Data.AdjustRailWidthTimeout);

                        if (success)
                            UpdateStatus($"轨宽调整成功: {railHeight}mm");
                        else
                            UpdateStatus("轨宽调整超时");
                        Thread.Sleep(500);
                    }
                    else
                    {
                        BoardHeight = originalHeight;
                    }
                }

                UpdateDetectionParamModifiedFlags();

                // 检测参数是否变化，决定是否重算 dx/dy 和结果

                RecalculatePinResults();
                SyncVmOverlayResults();
                SaveVmOverlays();

                // 刷新 Halcon 窗口标注颜色（OK绿/NG红）
                var hw = _vision.HalconWindow;
                if (hw != null && _isOverlayVisible && (_vmOverlays.Count > 0))
                {
                    HOperatorSet.ClearWindow(hw);
                    var si = _imageManager.GetCurrentStitchedImage();
                    if (si != null && si.IsInitialized())
                    {
                        HOperatorSet.DispObj(si, hw);
                        si.Dispose();
                    }
                    if (_vmOverlays.Count > 0) { DrawVmRects(hw); DrawVmText(hw); }
                    _vision.RefreshDisplay();
                }

                SaveUIToProject();
                ProjectManager.Instance.SaveCurrentProject();
                UpdateStatus("方案已更新");
            }
        }
        #endregion
        #region 编辑
        public void ToggleEdit()
        {
            IsEditMode = !IsEditMode;
            EditModeChanged?.Invoke();
        }

        public void ShowCameraView() => MessageBox.Show("相机视图", "功能", MessageBoxButton.OK, MessageBoxImage.Information);
        public void LockCAD() => MessageBox.Show("锁定CAD", "功能", MessageBoxButton.OK, MessageBoxImage.Information);
        public void AddPin() => MessageBox.Show("增加PIN", "功能", MessageBoxButton.OK, MessageBoxImage.Information);
        public void DeletePin() => MessageBox.Show("删除PIN", "功能", MessageBoxButton.OK, MessageBoxImage.Information);
        public void MatchPin() => MessageBox.Show("匹配PIN", "功能", MessageBoxButton.OK, MessageBoxImage.Information);
        public void CalibrateMark() => MessageBox.Show("校正MARK", "功能", MessageBoxButton.OK, MessageBoxImage.Information);

        /// <summary>扫描此处：移动到检测位起点后执行SCAN3D扫描</summary>
        public async void StartScanAtPosition(int gridIndex)
        {
            if (!CheckMotionConnected()) return;
            if (isWorkFlowRun.Any())
            {
                MessageBox.Show(Application.Current.MainWindow, "当前有其他操作正在运行");
                return;
            }

            // 查找该Pin对应的检测位
            var detectPos = DetectPositions?.FirstOrDefault(p => p.GrabIndex == gridIndex);
            if (detectPos == null)
            {
                MessageBox.Show(Application.Current.MainWindow, "未找到该PIN对应的检测位", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            isWorkFlowRun[WorkFlowType.FovGrab] = true;
            try
            {
                double detectX = detectPos.X;
                double detectY = detectPos.Y;
                double offsetX = _sysParam.Data.Camera3DOffsetX;
                double offsetY = _sysParam.Data.Camera3DOffsetY;
                double scanHalfWidth = _sysParam.Data.Scan3DStroke / 2.0;
                double startX = detectX - scanHalfWidth - offsetX;
                double endX = detectX + scanHalfWidth - offsetX;
                double moveY = detectY - offsetY;

                // 1. 发送 LOOK_3DIMAGE 指令给 VM（切换为预览模式）
                await _vision3DClient.SendCommandAsync("LOOK_3DIMAGE");
                await Task.Delay(100);

                // 2. 移动到3D扫描起点
                UpdateStatus("扫描此处：移动到扫描起点...");
                bool moved = await _motionIO.MoveToAndWaitOK(x: startX, y: moveY, timeoutMs: (int)_sysParam.Data.MoveToScanStartTimeout);
                if (!moved)
                {
                    Log.Warning("扫描此处：移动到起点失败");
                    return;
                }

                // 3. 执行 SCAN3D 扫描
                UpdateStatus("扫描此处：扫描中...");
                Log.Info($"[扫描此处] SCAN3D X{endX:F3}, Y{moveY:F3}");
                await _motionIO.Scan3DToAndWaitStop(x: endX, y: moveY, timeoutMs: (int)_sysParam.Data.Scan3DTimeout);

                UpdateStatus("扫描此处完成");
                Log.Info("扫描此处完成");
            }
            catch (Exception ex)
            {
                Log.Error($"扫描此处异常: {ex.Message}");
            }
            finally
            {
                isWorkFlowRun[WorkFlowType.FovGrab] = false;
            }
        }
        #endregion
        #region 帮助
        public void ShowUserManual() => MessageBox.Show("使用说明\n\n1. 连接相机\n2. 连接视觉软件\n3. 开始检测", "帮助", MessageBoxButton.OK, MessageBoxImage.Information);
        public void ShowAbout() => MessageBox.Show("AOI 视觉检测系统\n版本: V1.0\n\n© 2024 All Rights Reserved", "关于", MessageBoxButton.OK, MessageBoxImage.Information);
        #endregion
        #endregion
    }
}
