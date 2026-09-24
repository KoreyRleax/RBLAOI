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
        #region ===================================方案管理===================================

        /// <summary>
        /// 连接光源控制器串口（串口号/波特率/通道数为系统参数，所有方案共用）：
        /// 已连接且端口一致则复用；否则尝试连接。失败不弹窗，由 CheckDeviceConnectionsAsync 统一提示。
        /// </summary>
        private void ConnectLightSource()
        {
            var port = _sysParam.LightSourceSerialPort;
            if (string.IsNullOrEmpty(port)) return;

            var light = LightSourceController.Instance;
            if (light.IsOpen && string.Equals(light.PortName, port, StringComparison.OrdinalIgnoreCase))
                return;

            // 按系统参数通道数初始化帧结构（多通道帧段数必须与控制器一致）
            try { light.SetChannelCount(_sysParam.LightSourceChannelCount); } catch { }

            if (!light.Open(port, _sysParam.LightSourceBaudRate))
                Log.Warning($"光源串口 {port} 连接失败", "LightControl");
        }

        private void AutoLoadLastProject()
        {
            var lastProject = ProjectManager.Instance.GetLastLoadedProject();
            if (lastProject == null) return;

            var (projectName, projectPath) = lastProject.Value;
            string parentDir = Directory.GetParent(projectPath).FullName;
            ProjectManager.Instance.SetWorkingDirectory(parentDir);

            if (!ProjectManager.Instance.LoadProject(projectName)) return;

            var project = ProjectManager.Instance.CurrentProject;
            if (string.IsNullOrEmpty(project.ProjectPath))
            {
                project.ProjectPath = ProjectManager.Instance.CurrentProjectPath;
            }
            CurrentProjectPath = ProjectManager.Instance.CurrentProjectPath;
            LoadProjectToUI(ProjectManager.Instance.CurrentProject);
            ProjectManager.Instance.SaveCurrentProject();
            _imageManager.Clear();
            _vision.ClearHalconWindow();

            UpdateStatus($"已加载方案: {projectName}");

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
        }

        /// <summary>
        /// 窗口加载后执行初始化任务链：图片加载 → 轨宽检查 → 启动回零弹窗
        /// 串行执行避免多个模态对话框叠加冲突
        /// </summary>
        public async void StartPostInitTasks()
        {
            try
            {
                await CheckDeviceConnectionsAsync();        // 0. 设备连接统一检查（聚合一次弹窗）
                await StartupHomeCheck();                   // 1. 回零弹窗
                await CheckAndNotifyRailWidthAsync();       // 2 轨宽检查（可能有弹窗）
                await LoadProjectImagesAndDisplayAsync();   // 3. 加载图片（可能有弹窗）
            }
            catch (Exception ex)
            {
                Log.Error($"启动后初始化任务异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 启动后统一检查设备连接（下位机/光源/2D/3D 相机）：未连接的设备聚合为一次模态提示，
        /// 不逐个弹窗、不在构造函数中弹窗（窗口就绪后调用，Owner 正常）。
        /// 2D/3D 连接由 MainWindow.Loaded 发起，此处等待其共享 Task 完成后判定。
        /// </summary>
        private async Task CheckDeviceConnectionsAsync()
        {
            try { await _visionConnectTask; } catch { }

            var failed = new List<string>();
            if (!_motionIO.IsConnected) failed.Add("下位机串口");
            if (!LightSourceController.Instance.IsOpen) failed.Add("光源串口");
            if (!_vision2DClient.IsConnected) failed.Add("2D相机");
            if (!_vision3DClient.IsConnected) failed.Add("3D相机");
            if (failed.Count == 0) return;

            Log.Warning("设备连接检查: " + string.Join(", ", failed), "DeviceCheck");
            MessageBox.Show(Application.Current.MainWindow,
                "以下设备未连接，相关功能不可用：\n  • " + string.Join("\n  • ", failed) +
                "\n\n请到 系统参数→连接管理 中连接。",
                "设备连接提示", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        public async Task LoadProjectImagesAndDisplayAsync()
        {
            // 切换方案时重置右键菜单状态
            ExitSelectInspectionMode();

            var project = ProjectManager.Instance.CurrentProject;
            if (project == null) return;

            var (isValid, expected, actual, _) = ValidateImageCount(project);
            if (!isValid)
            {
                _vision.ClearHalconWindow();
                MessageBox.Show(Application.Current.MainWindow, $"图片数量不一致！预期 {expected} 张，实际仅 {actual} 张。请重新采集全图。", "图片数量不一致", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string imagesDir = ResolveImageDir();
            var files = Directory.GetFiles(imagesDir, "*.*")
                .Where(f => f.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => Path.GetFileName(f))
                .ToList();

            _imageManager.LoadImages(files);

            // 尝试加载已保存的网格索引映射，匹配加载时的拼图顺序
            string projectDir = ProjectManager.Instance.CurrentProjectPath;
            if (!string.IsNullOrEmpty(projectDir))
            {
                var gridIndices = LoadGridIndexMap(projectDir);
                if (gridIndices != null)
                    _imageManager.SetGridIndices(gridIndices);
                // 加载针尖图片路径映射（查看 ABC 功能用）
                var pinMap = LoadPinImageMap(projectDir);
                if (pinMap != null)
                {
                    _pinImageMap.Clear();
                    foreach (var kvp in pinMap)
                        _pinImageMap[kvp.Key] = kvp.Value;
                }
            }

            (_sysParam.ImageDisplayRows, _sysParam.ImageDisplayCols) = _imageManager.CalculateDisplayRowsCols
                (project.BoardWidth, project.BoardHeight, project.FovWidth, project.FovHeight, project.Overlap);

            int totalImages = files.Count;

            var dialog = new ProgressDialog();
            dialog.Owner = Application.Current.MainWindow;

            var progress = new Progress<int>(value =>
            {
                dialog.UpdateProgress(value, totalImages);
            });

            Task bgTask = Task.Run(() =>
            {
                _imageManager.StitchAndDisplay(stitched =>
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        _vision.DisplayImageByImage(stitched);
                        _vision.HalconControl?.FitToWindow();
                    });
                }, _sysParam.ImageDisplayRows, _sysParam.ImageDisplayCols,
                   project.FovWidth, project.FovHeight, project.Overlap, progress);
            });

            // 后台任务完成后自动关闭对话框
            _ = bgTask.ContinueWith(_ =>
            {
                if (dialog.IsVisible)
                    dialog.MarkCompleted();
            }, TaskScheduler.FromCurrentSynchronizationContext());

            dialog.ShowDialog();

            await bgTask;

            // 重载方案后若叠加层之前已开启，自动恢复叠加层显示
            if (_isOverlayVisible)
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (_vmOverlays.Count == 0)
                        LoadVmOverlays();

                    if (_vmOverlays.Count > 0)
                    {
                        var hw = _vision.HalconWindow;
                        if (hw != null)
                        {
                            var stitched = _imageManager.GetCurrentStitchedImage();
                            if (stitched != null && stitched.IsInitialized())
                            {
                                HOperatorSet.ClearWindow(hw);
                                HOperatorSet.DispObj(stitched, hw);
                                if (_vmOverlays.Count > 0) { DrawVmRects(hw); DrawVmText(hw); }
                                stitched.Dispose();
                                _vision.RefreshDisplay();
                            }
                        }
                    }
                });
            }
        }

        /// <summary>获取项目图片目录（优先 Origin 子目录，兼容旧版 Images/ 根目录）</summary>
        private string ResolveImageDir()
        {
            string originDir = ProjectManager.Instance.GetOriginPath();
            if (Directory.Exists(originDir) && Directory.GetFiles(originDir, "*.bmp").Concat(
                Directory.GetFiles(originDir, "*.png")).Concat(
                Directory.GetFiles(originDir, "*.jpg")).Any())
                return originDir;
            string imagesDir = ProjectManager.Instance.GetImagePath("Images");
            if (Directory.GetFiles(imagesDir, "*.bmp").Concat(
                Directory.GetFiles(imagesDir, "*.png")).Concat(
                Directory.GetFiles(imagesDir, "*.jpg")).Any())
                return imagesDir;
            return originDir;
        }

        /// <summary>
        /// 校验并规整 Origin 图片数量（2026-08-24 容错化）：
        /// - actual &lt; expected（图片不足）→ isValid=false，调用方弹窗"图片数量不一致"
        /// - actual == expected → isValid=true
        /// - actual &gt; expected（图片多余，如文件操作失败残留）→ 按文件创建时间删除最新的
        ///   (actual - expected) 张多余图片，isValid=true，保证方案能正常加载并显示底图
        /// </summary>
        private (bool isValid, int expected, int actual, int removed) ValidateImageCount(ProjectData project)
        {
            string imagesDir = ResolveImageDir();
            var files = Directory.GetFiles(imagesDir, "*.*")
                .Where(f => f.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
                .ToList();
            int actual = files.Count;

            double stepX = project.FovWidth * (1 - project.Overlap);
            double stepY = project.FovHeight * (1 - project.Overlap);
            int expected = (int)Math.Ceiling(project.BoardWidth / stepX) * (int)Math.Ceiling(project.BoardHeight / stepY);

            // 图片多于预期：按"文件创建时间"取最新的多余图片删除（创建时间相同时按文件名降序保证确定性）
            if (actual > expected)
            {
                var toDelete = files
                    .OrderByDescending(f => SafeFileCreationTime(f))
                    .ThenByDescending(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                    .Take(actual - expected)
                    .ToList();

                int removed = 0;
                foreach (var file in toDelete)
                {
                    try
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                        File.Delete(file);
                        removed++;
                    }
                    catch (Exception ex)
                    {
                        Log.Warning($"删除多余Origin图片失败: {file}: {ex.Message}");
                    }
                }
                if (removed > 0)
                    Log.Warning($"Origin图片数量容错: 预期 {expected} 张，实际 {actual} 张，已按创建时间自动删除最新多余 {removed} 张");
                return (true, expected, actual - removed, removed);
            }

            return (actual == expected, expected, actual, 0);
        }

        /// <summary>安全获取文件创建时间；读取失败返回 DateTime.MinValue（排在删除序列最前，不会被优先删）</summary>
        private static DateTime SafeFileCreationTime(string file)
        {
            try { return File.GetCreationTime(file); }
            catch { return DateTime.MinValue; }
        }

        /// <summary>从相机分辨率×像素当量推算 FovWidth/FovHeight，写入 project 并同步到 UI 绑定</summary>
        private void SyncFovFromCameraParams()
        {
            var project = ProjectManager.Instance.CurrentProject;
            if (project == null) return;

            double pxEq = _sysParam.Data.CameraPixelEquivalent;
            double newFovW = _sysParam.Data.CameraResolutionW * pxEq;
            double newFovH = _sysParam.Data.CameraResolutionH * pxEq;

            project.FovWidth = Math.Round(newFovW, 4);
            project.FovHeight = Math.Round(newFovH, 4);

            FovWidth = project.FovWidth;
            FovHeight = project.FovHeight;

            Log.Info($"Fov已同步: {_sysParam.Data.CameraResolutionW}×{_sysParam.Data.CameraResolutionH} px × {pxEq:F6} mm/px " +
                     $"→ Fov {project.FovWidth:F4}×{project.FovHeight:F4} mm");
        }

        private void LoadProjectToUI(ProjectData project)
        {
            SyncFovFromCameraParams();
            CurrentProjectName = project.ProjectName;
            CurrentProjectPath = project.ProjectPath;
            Remark = project.Remark ?? "";
            BoardWidth = project.BoardWidth;
            BoardHeight = project.BoardHeight;
            FovWidth = project.FovWidth;
            FovHeight = project.FovHeight;
            Overlap = project.Overlap * 100;
            SubBoardCount = project.SubBoardCount;
            CurrentSide = project.CurrentSide;
            CurrentTrack = project.CurrentTrack;
            MainBarcode = project.MainBarcode;
            SubBarcode = project.SubBarcode;
            CurrentBatch = project.CurrentBatch;
            BatchCount = project.BatchCount;
            PassRate = project.PassRate;
            InspectTime = project.InspectTime;
            BoardTime = project.BoardTime;
            FalseRate = project.FalseRate;
            DefectRate = project.DefectRate;
            BatchPassRate = project.BatchPassRate;
            GoodCount = project.GoodCount;
            NgCount = project.NgCount;

            // 检测参数兼容：确保 PinTypeParams 存在（旧方案加载时自动生成默认值）
            EnsurePinTypeParams(project);
            UpdateDetectionParamModifiedFlags();
        }

        private void SaveUIToProject()
        {
            var project = ProjectManager.Instance.CurrentProject;
            if (project == null) return;

            project.ProjectName = CurrentProjectName;
            project.ProjectPath = CurrentProjectPath;
            project.BoardWidth = Math.Round(BoardWidth, 2);
            project.BoardHeight = BoardHeight;
            project.FovWidth = FovWidth;
            project.FovHeight = FovHeight;
            project.Overlap = Overlap / 100.0;
            project.SubBoardCount = SubBoardCount;
            project.CurrentSide = CurrentSide;
            project.CurrentTrack = CurrentTrack;
            project.MainBarcode = MainBarcode;
            project.SubBarcode = SubBarcode;
            project.CurrentBatch = CurrentBatch;
            project.BatchCount = BatchCount;
            project.Remark = Remark;
        }

        private void UpdateDetectionParamModifiedFlags()
        {
            var sys = _sysParam.Data;
            var project = ProjectManager.Instance.CurrentProject;
            if (project?.PinTypeParams == null || project.PinTypeParams.Length == 0)
            {
                IsXDevModified = false;
                IsYDevModified = false;
                IsHeightModified = false;
                IsHeightDevModified = false;
                IsRelOffsetXModified = false;
                IsRelOffsetYModified = false;
                return;
            }
            // 系统参数中已无全局检测参数，PinTypeParams 即方案自有参数，不涉及"被修改"
            IsXDevModified = false;
            IsYDevModified = false;
            IsHeightModified = false;
            IsHeightDevModified = false;
            IsRelOffsetXModified = false;
            IsRelOffsetYModified = false;
        }

        /// <summary>
        /// 确保方案的 PinTypeParams 存在且数量匹配 PluginFeatureCount。
        /// 旧方案加载时自动生成默认值。
        /// </summary>
        private void EnsurePinTypeParams(ProjectData project)
        {
            if (project == null) return;
            if (project.PinTypeParams != null && project.PinTypeParams.Length > 0)
            {
                // 已存在，检查数量是否匹配
                if (project.PinTypeParams.Length == project.PluginFeatureCount)
                    return;
                // 数量不匹配时调整
                var list = project.PinTypeParams.ToList();
                while (list.Count < project.PluginFeatureCount)
                {
                    char label = (char)('A' + list.Count);
                    list.Add(new PinTypeParameter
                    {
                        Label = label.ToString(),
                        Focus = 0,
                        XyTolerance = 0.1,
                        IdealOffsetX = 0,
                        IdealOffsetY = 0,
                        HeightTolerance = 0.1,
                        IdealHeight = 2.0
                    });
                }
                project.PinTypeParams = list.Take(project.PluginFeatureCount).ToArray();
                return;
            }

            // 为空：生成默认值
            int count = project.PluginFeatureCount > 0 ? project.PluginFeatureCount : 1;
            var newList = new List<PinTypeParameter>();
            for (int i = 0; i < count; i++)
            {
                char label = (char)('A' + i);
                newList.Add(new PinTypeParameter
                {
                    Label = label.ToString(),
                    Focus = 0,
                    XyTolerance = 0.1,
                    IdealOffsetX = 0,
                    IdealOffsetY = 0,
                    HeightTolerance = 0.1,
                    IdealHeight = 2.0
                });
            }
            project.PinTypeParams = newList.ToArray();
        }

        /// <summary>
        /// 获取指定针型的检测参数
        /// </summary>
        private PinTypeParameter GetPinTypeParam(string pinType)
        {
            var project = ProjectManager.Instance.CurrentProject;
            if (project?.PinTypeParams == null) return null;
            return project.PinTypeParams.FirstOrDefault(p => p.Label == pinType);
        }

        /// <summary>获取检测参数默认值（系统参数中已无全局检测参数，使用硬编码默认）</summary>
        private (double xyTol, double idealH, double hTol, double offX, double offY) GetDefaultDetectParams()
        {
            return (0.1, 2.0, 0.1, 0, 0);
        }

        private void SaveGrabPositions(List<GrabPosition> positions)
        {
            string dir = ProjectManager.Instance.CurrentProjectPath;
            if (string.IsNullOrEmpty(dir)) return;

            string posDir = Path.Combine(dir, "Positions");
            if (!Directory.Exists(posDir)) Directory.CreateDirectory(posDir);

            string filePath = Path.Combine(posDir, "GrabPositions.json");
            string json = JsonConvert.SerializeObject(positions, Formatting.Indented);
            File.WriteAllText(filePath, json);
        }

        private List<GrabPosition> LoadGrabPositions()
        {
            string dir = ProjectManager.Instance.CurrentProjectPath;
            if (string.IsNullOrEmpty(dir)) return null;

            string filePath = Path.Combine(dir, "Positions", "GrabPositions.json");
            if (!File.Exists(filePath)) return null;

            string json = File.ReadAllText(filePath);
            return JsonConvert.DeserializeObject<List<GrabPosition>>(json);
        }

        private void SaveDetectPositions()
        {
            string dir = ProjectManager.Instance.CurrentProjectPath;
            if (string.IsNullOrEmpty(dir) || DetectPositions == null || DetectPositions.Count == 0) return;

            string posDir = Path.Combine(dir, "Positions");
            if (!Directory.Exists(posDir)) Directory.CreateDirectory(posDir);

            string filePath = Path.Combine(posDir, "DetectPositions.json");
            string json = JsonConvert.SerializeObject(DetectPositions.ToList(), Formatting.Indented);
            File.WriteAllText(filePath, json);
        }

        /// <summary>保存检测位到历史结果目录</summary>
        private void SaveDetectPositionsToHistory()
        {
            if (DetectPositions == null || DetectPositions.Count == 0) return;
            string resultDir = GetResultDir();
            if (resultDir == null) return;
            string filePath = Path.Combine(resultDir, "DetectPositions.json");
            string json = JsonConvert.SerializeObject(DetectPositions.ToList(), Formatting.Indented);
            File.WriteAllText(filePath, json);
        }

        private List<DetectPosition> LoadDetectPositions()
        {
            string dir = ProjectManager.Instance.CurrentProjectPath;
            if (string.IsNullOrEmpty(dir)) return null;

            string filePath = Path.Combine(dir, "Positions", "DetectPositions.json");
            if (!File.Exists(filePath)) return null;

            string json = File.ReadAllText(filePath);
            return JsonConvert.DeserializeObject<List<DetectPosition>>(json);
        }

        private void SaveGridIndexMap(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return;
            string posDir = Path.Combine(dir, "Positions");
            if (!Directory.Exists(posDir)) Directory.CreateDirectory(posDir);
            string filePath = Path.Combine(posDir, "GridIndexMap.json");
            var indices = _imageManager.GetGridIndices();
            string json = JsonConvert.SerializeObject(indices, Formatting.Indented);
            File.WriteAllText(filePath, json);
        }

        private void SavePinImageMap(string dir)
        {
            if (string.IsNullOrEmpty(dir) || _pinImageMap.Count == 0) return;
            string posDir = Path.Combine(dir, "Positions");
            if (!Directory.Exists(posDir)) Directory.CreateDirectory(posDir);
            string filePath = Path.Combine(posDir, "PinImageMap.json");
            string json = JsonConvert.SerializeObject(_pinImageMap, Formatting.Indented);
            File.WriteAllText(filePath, json);
        }

        private Dictionary<string, string> LoadPinImageMap(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return null;
            string filePath = Path.Combine(dir, "Positions", "PinImageMap.json");
            if (!File.Exists(filePath)) return null;
            string json = File.ReadAllText(filePath);
            return JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
        }

        private List<int> LoadGridIndexMap(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return null;
            string filePath = Path.Combine(dir, "Positions", "GridIndexMap.json");
            if (!File.Exists(filePath)) return null;
            string json = File.ReadAllText(filePath);
            return JsonConvert.DeserializeObject<List<int>>(json);
        }

        private void UpdateGrabPositionLists(List<GrabPosition> positions)
        {
            if (GrabPositions != null)
            {
                foreach (var pos in GrabPositions)
                    pos.DetectPositionChanged -= OnGrabPositionDetectChanged;
            }

            GrabPositions = new ObservableCollection<GrabPosition>(positions);

            foreach (var pos in GrabPositions)
                pos.DetectPositionChanged += OnGrabPositionDetectChanged;

            UpdateDetectPositions();
        }

        #endregion
        #region ===================================VM管理===================================

        /// <summary>
        /// 刷新 VM 方案状态栏显示
        /// </summary>
        private void UpdateVmSolutionStatus()
        {
            try
            {
                string path = VmSolution.Instance?.SolutionPath;
                if (!string.IsNullOrEmpty(path))
                    VmSolutionStatusText = $"VM: {path}";
                else
                    VmSolutionStatusText = "VM: 未加载";
            }
            catch
            {
                VmSolutionStatusText = "VM: 未加载";
            }
        }

        /// <summary>
        /// 检查VM方案是否已加载
        /// </summary>
        private bool IsVmSolutionLoaded_NOP() { return true; }

        /// <summary>
        /// 从当前方案加载VM方案（VMSolution/Check.sol），返回是否加载成功
        /// </summary>
        //public bool LoadVmSolutionFromCurrentProject()
        //{
        //try
        //{
        //    string solPath = ProjectManager.Instance.GetVmSolutionPath();

        //    if (string.IsNullOrEmpty(solPath) || !File.Exists(solPath))
        //    {
        //        VmSolutionStatusText = "VM: 未加载";
        //        return false;
        //    }

        //    // 如果当前已加载的就是这个方案，跳过重载（VM方案加载很慢）
        //    try
        //    {
        //        string currentPath = VmSolution.Instance?.SolutionPath;
        //        if (!string.IsNullOrEmpty(currentPath) && currentPath == solPath && IsVmSolutionLoaded())
        //        {
        //            UpdateVmSolutionStatus();
        //            return true;
        //        }
        //    }
        //    catch { }

        //    // 先断开 TCP 连接，再切方案，防止旧方案服务端未释放导致连错
        //    _vision2DClient.Disconnect();
        //    _vision3DClient.Disconnect();

        //    try { VmSolution.Instance.CloseSolution(); } catch { }

        //    VmSolution.Load(solPath, "");
        //    Log.Info($"VM方案已加载: {solPath}");
        //    UpdateVmSolutionStatus();

        //    // VM方案加载完成后，再连接2D/3D相机网口
        //    ConnectVision();

        //    return true;
        //}
        //catch (Exception ex)
        //{
        //    Log.Warning($"加载VM方案失败: {ex.Message}");
        //    VmSolutionStatusText = "VM: 加载失败";
        //return false;
        //}
        //}

        /// <summary>
        /// 卸载当前VM方案
        /// </summary>
        public void UnloadVmSolution()
        {
            try { VmSolution.Instance.CloseSolution(); } catch { }
            VmSolutionStatusText = "VM: 未加载";
        }

        /// <summary>
        /// 确保VM方案已加载（尝试从当前方案目录加载 Check.sol）
        /// </summary>
        private bool EnsureVmSolutionLoaded()
        {
            // if (IsVmSolutionLoaded()) return true;

            string solPath = ProjectManager.Instance.GetVmSolutionPath();
            if (string.IsNullOrEmpty(solPath) || !File.Exists(solPath))
                return false;

            try
            {
                VmSolution.Load(solPath, "");
                Log.Info("VM方案已重新加载");
                UpdateVmSolutionStatus();
                return true;
            }
            catch (Exception ex)
            {
                Log.Warning($"重新加载VM方案失败: {ex.Message}");
                VmSolutionStatusText = "VM: 加载失败";
                return false;
            }
        }

        /// <summary>
        /// 发送 TCP 指令通知 VM 设置相机曝光时间（避免 SDK 直接操作相机与 VM 冲突）。
        /// 优先使用当前方案的曝光时间（>0），未设置时回退系统参数。
        /// </summary>
        private async Task SetVmCameraExposure()
        {
            try
            {
                var project = ProjectManager.Instance.CurrentProject;
                bool useProject = project != null && project.CameraExposureTime > 0;
                double exposure = useProject ? project.CameraExposureTime : _sysParam.Data.CameraExposureTime;
                await _vision2DClient.SendCommandAsync($"SET_EXPOSURE:{(int)exposure}");
                Log.Info($"已发送SET_EXPOSURE指令: {exposure:F0} μs (来源: {(useProject ? "方案" : "系统参数")})");
            }
            catch (Exception ex)
            {
                Log.Warning($"设置相机曝光时间失败: {ex.Message}");
            }
        }

        /// <summary>上次 SET_PATH 的目录（路径未生效诊断用：文件就绪失败时对比文件落点）</summary>
        private string _lastVmSavePath = null;

        /// <summary>
        /// 通过 TCP 通知 VM 设置拍照路径（经 GlobalScript → SetGlobalVariableStringValue → 绑定到输出图像1）
        /// 注：不能直接用 SDK 设置 OriginImgPath，因为 VM 方案中该参数绑定了全局变量，会被覆盖
        /// </summary>
        private async Task SetVmSavePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                // 2026-08-25：fire-and-forget（不做回执）——回执只确认"全局变量已写"，不确认保存模块（订阅
                // ImageSavePath）已应用新路径（异步传播窗口，属假确认）；路径传播由调用点 SetVmSavePathDelay
                // 延时兜底，最终确认靠 GRAB 后"目标目录出现文件"，失败时 [路径未生效] 诊断显性化（RunCommands.cs）。
                await _vision2DClient.SendCommandAsync($"SET_PATH:{path}");
                _lastVmSavePath = path;
                Log.Info($"已设置VM拍照路径: {path}");
            }
            catch (Exception ex)
            {
                Log.Warning($"设置VM拍照路径失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 通知VM将图片直接保存到 Origin 目录
        /// </summary>
        private async Task SetVmSavePathToOrigin()
        {
            string projectImagesDir = ProjectManager.Instance.GetOriginPath();
            if (string.IsNullOrEmpty(projectImagesDir)) return;
            await SetVmSavePath(projectImagesDir);
        }

        /// <summary>
        /// 通知VM将图片保存到 Pin 目录（针尖图，用于Blob分析）
        /// </summary>
        private async Task SetVmSavePathToPin()
        {
            string pinDir = ProjectManager.Instance.GetPinPath();
            if (string.IsNullOrEmpty(pinDir)) return;
            await SetVmSavePath(pinDir);
        }

        #endregion
    }
}
