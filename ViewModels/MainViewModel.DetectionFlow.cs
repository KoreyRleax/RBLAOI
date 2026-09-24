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
        #region ========== VM 3D结果解析与坐标转换 ==========

        /// <summary>
        /// 解析VM格式的3D检测结果: DETECT_3D_OK:x,y,z;x,y,z;...
        /// VM物理坐标µm → mm，返回VM3DPoint列表
        /// </summary>
        private List<VM3DPoint> ParseVMDetect3DResponse(string response)
        {
            var results = new List<VM3DPoint>();
            try
            {
                if (string.IsNullOrWhiteSpace(response)) return results;

                string dataPart = response;
                int prefixIdx = dataPart.IndexOf("DETECT_3D_OK:");
                if (prefixIdx >= 0)
                    dataPart = dataPart.Substring(prefixIdx + "DETECT_3D_OK:".Length);
                else if (dataPart.Contains("DETECT3D_OK"))
                {
                    prefixIdx = dataPart.IndexOf("DETECT3D_OK");
                    dataPart = dataPart.Substring(prefixIdx + "DETECT3D_OK".Length);
                    if (dataPart.StartsWith(":")) dataPart = dataPart.Substring(1);
                }

                dataPart = dataPart.Trim();

                // ---- 新格式: 包含 || 分隔符（针尖||板面） ----
                int sepIdx = dataPart.IndexOf("||");
                if (sepIdx >= 0)
                {
                    string tipsPart = dataPart.Substring(0, sepIdx);
                    string boardsPart = dataPart.Substring(sepIdx + 2);

                    List<VM3DPoint> tips = ParseVM3DPointsPart(tipsPart);
                    List<VM3DPoint> boards = ParseVM3DPointsPart(boardsPart);

                    Log.Info($"[ParseVM3D] 新格式: 针尖={tips.Count}个, 板面={boards.Count}个");

                    if (tips.Count > 0)
                    {
                        results = MatchTipToBoard(tips, boards);
                        string detail = string.Join("; ",
                            results.Select(p => string.Format("#ID vx={0:F4},vy={1:F4},vz(h)={2:F4}", p.VmX, p.VmY, p.VmZ)));
                        Log.Debug($"[ParseVM3D] 匹配后坐标(mm): [{detail}]");
                    }
                    return results;
                }

                // ---- 旧格式: 无 ||，单个点集 ----
                var entries = dataPart.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var entry in entries)
                {
                    var parts = entry.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 3) continue;

                    double x, y, z;
                    if (double.TryParse(parts[0], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out x) &&
                        double.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out y) &&
                        double.TryParse(parts[2], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out z))
                    {
                        results.Add(new VM3DPoint
                        {
                            VmX = x / 1000.0,
                            VmY = y / 1000.0,
                            VmZ = z / 1000.0
                        });
                    }
                }

                Log.Info($"[ParseVM3D] 解析到 {results.Count} 个针");
                if (results.Count > 0)
                {
                    string detail = string.Join("; ",
                        results.Select(p => string.Format("#ID vx={0:F4},vy={1:F4},vz={2:F4}", p.VmX, p.VmY, p.VmZ)));
                    Log.Debug($"[ParseVM3D] 坐标详情(mm): [{detail}]");
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[ParseVM3D] 解析失败: {ex.Message}");
            }
            return results;
        }

        /// <summary>
        /// 解析 "x,y,z;x,y,z;..." 格式字符串为 VM3DPoint 列表（µm→mm）
        /// </summary>
        private List<VM3DPoint> ParseVM3DPointsPart(string dataPart)
        {
            var results = new List<VM3DPoint>();
            var entries = dataPart.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var entry in entries)
            {
                var parts = entry.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3) continue;

                double x, y, z;
                if (double.TryParse(parts[0], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out x) &&
                    double.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out y) &&
                    double.TryParse(parts[2], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out z))
                {
                    results.Add(new VM3DPoint
                    {
                        VmX = x / 1000.0,
                        VmY = y / 1000.0,
                        VmZ = z / 1000.0
                    });
                }
            }
            return results;
        }

        /// <summary>
        /// 最近邻匹配针尖↔板面，计算 h = |z1 - z2|
        /// 对每个针尖找xy距离最近的板面点，距离 < 阈值则计算针高
        /// 未匹配到板面的针尖 h = 0（fallback）
        /// </summary>
        private List<VM3DPoint> MatchTipToBoard(List<VM3DPoint> tips, List<VM3DPoint> boards)
        {
            if (tips == null || tips.Count == 0) return tips ?? new List<VM3DPoint>();
            if (boards == null || boards.Count == 0)
            {
                foreach (var tip in tips) tip.VmZ = 0;
                return tips;
            }

            double threshold = _sysParam.Data.MatchThreshold3D;
            int matchCount = 0;

            for (int t = 0; t < tips.Count; t++)
            {
                double bestDist = double.MaxValue;
                int bestIdx = -1;

                for (int b = 0; b < boards.Count; b++)
                {
                    double dx = tips[t].VmX - boards[b].VmX;
                    double dy = tips[t].VmY - boards[b].VmY;
                    double dist = Math.Sqrt(dx * dx + dy * dy);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        bestIdx = b;
                    }
                }

                if (bestIdx >= 0 && bestDist <= threshold)
                {
                    double zTip = tips[t].VmZ;
                    double zBoard = boards[bestIdx].VmZ;
                    double h = Math.Abs(zTip - zBoard);
                    tips[t].VmZ = h;
                    matchCount++;
                    Log.Debug($"[MatchTipToBoard] 针尖#{t}({tips[t].VmX:F3},{tips[t].VmY:F3}) " +
                        $"↔板面#{bestIdx}({boards[bestIdx].VmX:F3},{boards[bestIdx].VmY:F3}) " +
                        $"dist={bestDist:F3} zTip={zTip:F3} zBoard={zBoard:F3} h={h:F3}");
                }
                else
                {
                    tips[t].VmZ = 0;
                    Log.Debug($"[MatchTipToBoard] 针尖#{t}({tips[t].VmX:F3},{tips[t].VmY:F3}) " +
                        $"无匹配(最近距离={bestDist:F3}>{threshold}mm), h=0");
                }
            }

            Log.Info($"[MatchTipToBoard] 针尖{tips.Count}个↔板面{boards.Count}个: 匹配{matchCount}个, 阈值{threshold}mm");
            return tips;
        }

        #endregion
        #region  ===================================检测流程 ===================================
        public async Task<bool> StartCheckFlow(bool isSubFlow = false)
        {
            // 安全操作检查（isSubFlow=true 跳过互斥——连续模式外壳已持 ContinuousRun 标志）
            if (!CheckSafeOperation(isSubFlow)) return false;
            if (!CheckMotionConnected()) return false;
            if (!CheckVision2DConnected()) return false;
            if (!CheckVision3DConnected()) return false;

            // 防重入：在第一个异步操作之前就设置标志位，避免 await 期间用户二次点击重入
            isWorkFlowRun[WorkFlowType.Check] = true;

            // 回零检测（传入 true 表示由检测流程调用，跳过内部的 isWorkFlowRun 检查）
            if (!await CheckAndHomeAxesAsync(true))
            {
                isWorkFlowRun[WorkFlowType.Check] = false;
                return false;
            }
            if (DetectPositions == null || DetectPositions.Count == 0)
            {
                MessageBox.Show(Application.Current.MainWindow, "请先在拍照位列表中勾选检测位", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                isWorkFlowRun[WorkFlowType.Check] = false;
                return false;
            }

            var project = ProjectManager.Instance.CurrentProject;
            if (project == null)
            {
                MessageBox.Show(Application.Current.MainWindow, "请先加载方案", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                isWorkFlowRun[WorkFlowType.Check] = false;
                return false;
            }

            if (_imageManager.ImageCount == 0)
            {
                MessageBox.Show(Application.Current.MainWindow, "当前方案没有图片数据，请先加载方案或采集全图", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                isWorkFlowRun[WorkFlowType.Check] = false;
                return false;
            }

            // ===== 闭包变量（跨壳层/业务层共享；仿全图采集 currentPos/grabResult 模式）=====
            var defaultParams = GetDefaultDetectParams();   // 检测参数默认值（方案级，逐针型优先）
            double defaultXyTol = defaultParams.xyTol;
            double defaultIdealH = defaultParams.idealH;
            double defaultHTol = defaultParams.hTol;
            // 3D相机相对于2D相机的偏移量
            double offsetX = _sysParam.Data.Camera3DOffsetX;
            double offsetY = _sysParam.Data.Camera3DOffsetY;
            int totalPositions = DetectPositions.Count;
            int globalPinId = 1;
            string projectImagesDir = ProjectManager.Instance.GetOriginPath();
            var swPos = new Stopwatch();        // 检测位计时（CapturePos 重启；MoveScanStart 写 tBC、WaitScan3D/Merge 读取）
            long tBC = 0;                       // MoveScanStart 写（2D启动+移起点耗时），WaitScan3D 读取
            DetectPosition pos = null;          // 当前检测位（CapturePos 写，后续各状态读）
            double detectX = 0, detectY = 0;    // 当前检测位中心坐标
            double scanHalfWidth = 0, startX = 0, endX = 0, moveY = 0;   // 3D扫描行程/起点/终点/移动Y
            string detectionMode = null;
            bool isBlobMode = false;
            string capturedImagePath = null;    // GrabTile 写（A段拍照结果）→ 诊断/2D路径准备读
            string imagePath = null;            // GrabTile 尾写（2D检测图像路径）→ Start2D 读
            bool canUseVM = false;              // GrabTile 尾写 → Start2D 读
            List<Pin2DData> rawPins2D = null;   // Wait2D 写（2D解析结果）→ Merge 读
            Task<string> _vmDetectTask = null;  // Start2D 写（2D任务句柄，不 await 与3D并行）→ Wait2D 读
            Task<bool> _3dScanTask = null;      // StartScan3D 写（3D扫描句柄，不 await）→ WaitScan3D 读
            string response = "TIMEOUT";        // WaitScan3D 写（3D结果）→ Merge 读
            bool scan3DTimeout = true;          // WaitScan3D 尾写（=response=="TIMEOUT"）→ Merge 分支判据（3D超时→纯2D兜底）
            bool scan3DNoData = false;          // WaitScan3D 写：3D扫描成功但未收到数据（2026-08-21，纳入重扫判定）
            int i = 0;                          // 游标：仅 NextPos ++（整拍完成才推进，暂停恢复从当前位重做）
            string err = null;
            var flowState = FlowRunState.Init;  // 壳层状态
            var bizState = CheckState.Init;     // 业务层状态
            CancellationToken token = default;  // Init case 内创建 _checkCts 后赋值
            Task pendingPauseTask = null;       // 暂停中断时留下的在途后台任务（拍照/2D检测），Paused 恢复前等待其收尾防并发写同一图片（2026-08-19）
            int retry3DPos = 0;                 // 当前检测位 3D 全匹配失败重扫计数（2026-08-21，CapturePos 每检测位重置）
            const int max3DRescanRetry = 2;     // 2026-08-27 需求：End阶段（板末重扫）重试次数 3→至多2次；每检测位 3D 重扫上限（仍全失败→按原逻辑降级显示，防死循环）

            // —— 2026-08-25 优化：3D结果等待异步化（挂起上一位上下文，A段移动期间数据后台到达，MergePending 恢复执行 Merge）——
            int _pendingMergeIdx = -1;          // 挂起的检测位号（WaitScan3D 快照写，MergePending 消费；-1=无）
            DetectPosition _pendingPos = null;  // 挂起检测位的 pos/坐标/行程（CapturePos(i+1) 会覆盖流程变量，必须先快照）
            double _pendingDetectX = 0, _pendingDetectY = 0;
            double _pendingScanHalfWidth = 0, _pendingStartX = 0, _pendingEndX = 0, _pendingMoveY = 0;
            List<Pin2DData> _pendingPins2D = null;              // 挂起位的 rawPins2D 快照（防被下一检测位 Wait2D 覆盖）
            Task<string> _pending3DDataTask = null;             // 挂起位的 3D 数据监听（WaitScan3D 创建，MergePending 消费）
            DateTime _pendingScanDoneUtc = default;             // 挂起位扫描完成时刻（MergePending 算剩余等待窗口）
            bool _skipAForNext = false;                         // 下一检测位 A 段已拍（重扫/正常流返回时跳过 CapturePos/GrabTile）
            bool _rescanFlag = false;                           // 本拍发生过重扫（Merge 重扫分支写；NextPos 回 i+1 时据此决定重发 2D 而非直接等扫描）
            List<Pin2DData> _curRawPins2D = null;               // 当前位(i+1) 的 rawPins2D（Wait2D 解析后、Merge 覆盖流程变量前保存；NextPos 恢复用）
            List<int> _rescanQueue = new List<int>();           // 2026-08-25 延迟重扫：主流程 3D 数据异常位队列（板末统一重扫，不再即时重扫打断在途 SCAN3D）
            bool _rescanPhase = false;                          // 板末重扫阶段标志（重扫位严格串行：Wait2D 后直连 WaitScan3D+同步 Merge，不走 MergePending 挂起）
            int _rescanRetries = 0;                             // 当前重扫位已重试次数（≤max3DRescanRetry；重扫位重拍重来）
            bool _endHandled = false;                           // 2026-08-25 End case 收尾完成标志（BizStep 终态判定用：End case 执行完才返回 true，否则 End 本体永不执行）

            // 报警终止辅助：报警中取消 → 业务层直接转 Err（终态收尾走 Err 分支；弹窗由 DeviceMonitor 报警弹窗承担，本处只 Log+状态栏）
            bool MarkAlarmCancel()
            {
                if (!_alarmStopping) return false;
                err = "设备报警，检测已终止";
                bizState = CheckState.Err;
                return true;
            }

            // ===== 业务层单步：每拍一个状态的一件事+迁移；到达终态（End/Err/业务内取消）返回 true =====
            async Task<bool> BizStep()
            {
                // 2026-08-25 优化：快照检测位 idx 的 3D 挂起上下文（MergePending 恢复用；CapturePos(i+1) 会覆盖流程变量）
                void SnapshotPending3D(int idx, List<Pin2DData> pins2D)
                {
                    _pendingMergeIdx = idx;
                    _pendingPos = pos;
                    _pendingDetectX = detectX; _pendingDetectY = detectY;
                    _pendingScanHalfWidth = scanHalfWidth;
                    _pendingStartX = startX; _pendingEndX = endX; _pendingMoveY = moveY;
                    _pendingPins2D = pins2D?.ToList();
                    _pendingScanDoneUtc = DateTime.UtcNow;
                }

                try
                {
                    switch (bizState)
                    {
                        case CheckState.Init:
                            {
                                if (token.IsCancellationRequested) { MarkAlarmCancel(); flowState = FlowRunState.Terminated; break; }

                                StartInspectTimer();   // ⏱ 检测耗时：开始计时（每板检测启动时归零；连续模式每板重新计时，2026-08-27）

                                // —— 原清理/准备段（:3181-3234 原样搬入，异常现走 catch 不逃逸）——
                                _vmOverlays.Clear();
                                PinResults.Clear();
                                _rescanQueue.Clear(); _rescanPhase = false; _rescanRetries = 0; _endHandled = false;   // 2026-08-25 延迟重扫：每板重置

                                // 如果当前处于针尖图层模式，先恢复板面路径再重置
                                if (_isShowingPinLayer)
                                {
                                    var restoreProj = ProjectManager.Instance.CurrentProject;
                                    if (restoreProj != null)
                                    {
                                        foreach (var kvp in _boardImageBackup)
                                        {
                                            int grabIdx = kvp.Key;
                                            string boardPath = kvp.Value;
                                            if (File.Exists(boardPath))
                                            {
                                                _imageManager.ReplaceTile(grabIdx, boardPath, null,
                                                    _sysParam.ImageDisplayRows, _sysParam.ImageDisplayCols,
                                                    restoreProj.FovWidth, restoreProj.FovHeight, restoreProj.Overlap);
                                            }
                                        }
                                    }
                                }
                                // 重置针尖图层状态
                                _pinImageMap.Clear();
                                _boardImageBackup.Clear();
                                _currentPositionPinPaths.Clear();
                                _isShowingPinLayer = false;

                                // 清理 Blob 模式的 Pin 针尖图目录
                                string pinDir = ProjectManager.Instance.GetPinPath();
                                if (pinDir != null && Directory.Exists(pinDir))
                                {
                                    try
                                    {
                                        Directory.Delete(pinDir, true);
                                        Directory.CreateDirectory(pinDir);
                                    }
                                    catch (Exception ex)
                                    {
                                        Log.Warning($"清理Pin目录失败: {ex.Message}");
                                    }
                                }

                                // 设置新的时间戳文件夹，本次检测结果将保存到 Result/Result_yyyyMMdd_HHmmss/
                                // （精度检测模式不创建结果目录）
                                if (!_isPrecisionMode)
                                {
                                    _currentResultFolder = $"Result_{DateTime.Now:yyyyMMdd_HHmmss}";
                                    string newDir = Path.Combine(ProjectManager.Instance.CurrentProjectPath, "Result", _currentResultFolder);
                                    if (!Directory.Exists(newDir)) Directory.CreateDirectory(newDir);
                                }

                                // 切换到检测结果标签页
                                SelectedTabIndex = 3;

                                // 保存当前检测位状态，确保重载后与检测结果一致
                                if (!_isPrecisionMode)
                                {
                                    SaveDetectPositions();
                                    if (GrabPositions != null && GrabPositions.Count > 0)
                                        SaveGrabPositions(GrabPositions.ToList());
                                }

                                // —— 并行IO准备：TCP指令（拍照路径、曝光时间）+ 串口指令（顶板1、顶板2顶起 + 阻挡气缸升起，防止检测时板位移）——
                                // 2026-08-27 需求：顶板执行完后延时100ms再执行阻挡气缸，防止阻挡气缸把板移位（原并行 Task.WhenAll 同时动作）
                                await Task.WhenAll(
                                    SetVmSavePathToOrigin(),
                                    SetVmCameraExposure(),
                                    _motionIO.SetIO(OutSignal.Clamp1, true),
                                    _motionIO.SetIO(OutSignal.Clamp2, true)
                                );
                                await Task.Delay(100, token);
                                await _motionIO.SetIO(OutSignal.Block, true);
                                // 串口速度指令（等顶板完成后再发，共用串口）
                                await SendSpeedPtpAsync();
                                await SendSpeedScan3DAsync();

                                _checkCts = new CancellationTokenSource();
                                token = _checkCts.Token;
                                _checkFlowCompleted.Reset(); // 重置完成信号
                                i = 0;
                                bizState = CheckState.CapturePos;
                                break;
                            }

                        case CheckState.CapturePos:
                            {
                                // 检查点①（每检测位顶部）：暂停→壳层转 Paused，恢复后从当前位重做
                                if (!_pauseCheckEvent.IsSet) { flowState = FlowRunState.Paused; break; }
                                if (token.IsCancellationRequested) { MarkAlarmCancel(); flowState = FlowRunState.Terminated; break; }

                                swPos.Restart();  // ⏱ 检测位计时
                                retry3DPos = 0;   // 每检测位重置 3D 重扫计数（2026-08-21）
                                pos = DetectPositions[i];
                                detectX = pos.X;
                                detectY = pos.Y;

                                // 3D扫描行程从系统参数读取，scanHalfWidth = 行程/2
                                scanHalfWidth = _sysParam.Data.Scan3DStroke / 2.0;
                                startX = detectX - scanHalfWidth - offsetX;
                                endX = detectX + scanHalfWidth - offsetX;
                                moveY = detectY - offsetY;

                                // 检测模式
                                detectionMode = _sysParam.Data.DetectionMode;
                                isBlobMode = detectionMode == "VM-Blob方案";
                                bizState = CheckState.GrabTile;
                                break;
                            }

                        case CheckState.GrabTile:
                            {
                                if (token.IsCancellationRequested) { MarkAlarmCancel(); flowState = FlowRunState.Terminated; break; }
                                // 暂停检查点（每检测位拍照前）：暂停→壳层转 Paused，恢复后从当前位重做
                                if (!_pauseCheckEvent.IsSet) { flowState = FlowRunState.Paused; break; }

                                // (1) 移动到检测位中心点，重新采集该检测位的图片
                                capturedImagePath = null;
                                var swA = System.Diagnostics.Stopwatch.StartNew();  // ⏱ A: 移动到中心+拍照
                                // 2026-08-24 优化：移除每拍诊断 Directory.GetFiles×2（8-14 排查 Origin 文件数问题时加的临时诊断，
                                // 该问题已关闭；全目录枚举每拍 10~400ms 且与 VM 写文件竞争磁盘，属非必要开销）
                                if (_vision2DClient.IsConnected)
                                {
                                    if (isBlobMode)
                                    {
                                        // Blob模式：多针型抓图（板面图 + 每型针尖图）
                                        // 暂停感知：拍照动作中暂停→立即转 Paused（在途拍照后台收尾，恢复后当前位整拍重做）
                                        var grabTask = GrabAndReplaceMultiAsync(
                                            pos, i, totalPositions, projectImagesDir, token);
                                        var (boardImg, pinPaths) = await PauseAwareWaitAsync(grabTask, token);
                                        if (token.IsCancellationRequested) { MarkAlarmCancel(); flowState = FlowRunState.Terminated; break; }
                                        if (!_pauseCheckEvent.IsSet) { pendingPauseTask = grabTask; flowState = FlowRunState.Paused; break; }
                                        capturedImagePath = boardImg;
                                        // 保存在变量中，后续DETECT2D_BLOB命令使用
                                        _currentPositionPinPaths.Clear();
                                        if (pinPaths != null)
                                        {
                                            foreach (var kvp in pinPaths)
                                                _currentPositionPinPaths[kvp.Key] = kvp.Value;
                                        }
                                        // 2026-08-25 主题②：针尖图部分失败（板面成功但缺针型图，如 GRAB 超时/文件未就绪）——
                                        // 该位入队板末重扫（重扫完整重拍补全）；重扫模式内重试计数防死循环（与拍照失败分支同语义）。
                                        // 实机（19:15 轮）：位0 PinC 图 15s 超时后缺失，但板面成功 → 原逻辑继续导致缺针型无补救（白等 15s）
                                        string pinTypes = pos.PinTypes ?? "";
                                        if (capturedImagePath != null && (pinPaths == null || pinPaths.Count < pinTypes.Length))
                                        {
                                            Log.Error($"检测位 {i + 1} 针尖图缺失（{pinPaths?.Count ?? 0}/{pinTypes.Length}）");
                                            if (_rescanPhase)
                                            {
                                                _rescanRetries++;
                                                if (_rescanRetries < max3DRescanRetry)
                                                {
                                                    Log.Warning($"检测位 {i} 重扫针尖图缺失第 {_rescanRetries} 次，重拍重来");
                                                    bizState = CheckState.CapturePos;
                                                    break;
                                                }
                                                Log.Warning($"检测位 {i} 重扫针尖图缺失 {_rescanRetries} 次，达到上限接受降级（缺针型图）");
                                            }
                                            else
                                            {
                                                Log.Warning($"检测位 {i} 针尖图缺失，已记录板末延迟重扫");
                                                if (!_rescanQueue.Contains(i)) _rescanQueue.Add(i);
                                            }
                                        }
                                    }
                                    else
                                    {
                                        // 暂停感知：移动/拍照/等文件中暂停→立即转 Paused（在途任务后台收尾，恢复后当前位整拍重做）
                                        var grabTask = GrabAndReplaceTileAsync(pos, i, totalPositions, projectImagesDir, token);
                                        capturedImagePath = await PauseAwareWaitAsync(grabTask, token);
                                        if (token.IsCancellationRequested) { MarkAlarmCancel(); flowState = FlowRunState.Terminated; break; }
                                        if (!_pauseCheckEvent.IsSet) { pendingPauseTask = grabTask; flowState = FlowRunState.Paused; break; }
                                    }
                                    // 复位/停止取消优先：拍照返回后立即检查（取消时拍照可能因 Flush 提前返回 null，不得转 Err 弹"检测失败"）
                                    if (capturedImagePath != null)
                                    {
                                        // 清空旧检测结果（后续2D/3D检测会重新生成）
                                        _vmOverlays.RemoveAll(o => o.GridIndex == pos.GrabIndex);
                                        // 2026-08-25 异步化：不 await（Dispatcher 队列 FIFO 保序，防 A段尾部被 UI 渲染阻塞）；
                                        // UpdateStatistics 一并移入 UI 线程——它枚举 PinResults(ObservableCollection)，跨线程与 UI 的 Add/Remove 并发会竞态拖慢流程（实机 +180ms 定位）
                                        Application.Current.Dispatcher.InvokeAsync(() =>
                                        {
                                            var toRemove = PinResults.Where(p => p.GridIndex == pos.GrabIndex).ToList();
                                            foreach (var r in toRemove) PinResults.Remove(r);
                                            UpdateStatistics();
                                        });
                                    }
                                    else
                                    {
                                        // 2026-08-25 主题②：拍照失败（VM超时/移动失败/文件未就绪）不再 Err 终止整板——
                                        // 记录板末统一重扫（重扫位完整重拍 A 段自愈）；该位无图跳过 2D/3D（板末重扫补全）
                                        Log.Error($"检测位 {i + 1} 拍照失败（VM超时/移动失败/文件未就绪）");
                                        if (_rescanPhase)
                                        {
                                            // 重扫模式内重拍失败：不入队（防死循环——重扫位来自队列，再入队会无限重扫），
                                            // 重试计数后接受降级（与 Merge 重扫失败同语义）
                                            _rescanRetries++;
                                            if (_rescanRetries < max3DRescanRetry)
                                            {
                                                Log.Warning($"检测位 {i} 重扫重拍失败第 {_rescanRetries} 次，重拍重来");
                                                bizState = CheckState.CapturePos;
                                                break;
                                            }
                                            Log.Warning($"检测位 {i} 重扫重拍失败 {_rescanRetries} 次，达到上限接受降级（该位无图）");
                                            bizState = CheckState.NextPos;
                                            break;
                                        }
                                        Log.Error($"检测位 {i + 1} 拍照失败，已记录板末延迟重扫（原 Err 终止整板）");
                                        if (!_rescanQueue.Contains(i)) _rescanQueue.Add(i);
                                        bizState = CheckState.NextPos;
                                        break;
                                    }
                                }
                                Log.Info($"[⏱] 检测位{i} A(移动+拍照)={swA.ElapsedMilliseconds}ms | 总={swPos.ElapsedMilliseconds}ms");

                                // (2)+(3) 并行：2D检测与移动到3D扫描起点同时执行
                                canUseVM = (detectionMode == "VM方案" || isBlobMode) && _vision2DClient.IsConnected;
                                rawPins2D = null;
                                imagePath = capturedImagePath;
                                // 2026-08-25 清理：原"拍照失败用瓦片库原图兜底"残留死代码已删（imagePath==null 时上方
                                // L465-472 已 Err 终止，此段不可达；采集失败统一按 Err 处理，不再用旧图继续 2D/3D）
                                // 2026-08-25 优化：A段完成 → 直接启动本检测位2D；上一位挂起3D结果延后到
                                // Wait2D 解析完成后处理（MergePending 与 3D 扫描并行，Merge 不再占用 B+C 段）
                                bizState = CheckState.Start2D;
                                break;
                            }

                        case CheckState.Start2D:
                            {
                                // 检查点②（启动2D前）
                                if (!_pauseCheckEvent.IsSet) { flowState = FlowRunState.Paused; break; }
                                if (token.IsCancellationRequested) { MarkAlarmCancel(); flowState = FlowRunState.Terminated; break; }
                                _vmDetectTask = null;

                                if (imagePath != null)
                                {
                                    if (canUseVM)
                                    {
                                        UpdateStatus($"检测位 {i + 1}/{totalPositions} 2D检测中...");
                                        string vmCommand;
                                        string vmOkPrefix;
                                        if (isBlobMode)
                                        {
                                            // Blob模式：多针型格式 DETECT2D_BLOB:{boardPath}|A:{pinPathA}|B:{pinPathB}
                                            var sb = new System.Text.StringBuilder($"DETECT2D_BLOB:{imagePath}");
                                            var pinTypes = pos.PinTypes ?? "";
                                            foreach (char marker in pinTypes)
                                            {
                                                if (_currentPositionPinPaths.TryGetValue(marker, out string pPath))
                                                {
                                                    sb.Append($"|{marker}:{pPath}");
                                                }
                                            }
                                            vmCommand = sb.ToString();
                                            vmOkPrefix = "DETECT2D_BLOB_OK";
                                        }
                                        else
                                        {
                                            vmCommand = $"DETECT2D_RECT:{imagePath}";
                                            vmOkPrefix = "DETECT2D_RECT_OK";
                                        }
                                        _vmDetectTask = _vision2DClient.SendCommandAndWaitAsync(
                                            vmCommand, vmOkPrefix, (int)_sysParam.Data.GrabTimeout, token);
                                    }
                                }
                                bizState = CheckState.MoveScanStart;
                                break;
                            }

                        case CheckState.MoveScanStart:
                            {
                                if (token.IsCancellationRequested) { MarkAlarmCancel(); flowState = FlowRunState.Terminated; break; }
                                // 同时移动到3D扫描起点（与2D检测并行），带重试
                                UpdateStatus($"检测位 {i + 1}/{totalPositions} 移动到3D扫描起点");
                                Log.Info($"检测位 {i}: 起点({startX:F3}, {moveY:F3}) → 终点({endX:F3}, {moveY:F3})");
                                // 暂停感知：移动中暂停→立即转 Paused（轴自然走完当前段停在目标点，恢复后重新移动；取消优先）
                                var moveTask = RetryAsync(
                                    () => _motionIO.MoveToAndWaitOK(x: startX, y: moveY, timeoutMs: (int)_sysParam.Data.MoveToScanStartTimeout),
                                    maxRetries: 3, delayMs: 200);
                                bool movedToStart = await PauseAwareWaitAsync(moveTask, token);
                                // 检查点③（移动后、失败判定前，位置精确对应原 :3401）
                                if (token.IsCancellationRequested) { MarkAlarmCancel(); flowState = FlowRunState.Terminated; break; }
                                if (!_pauseCheckEvent.IsSet) { pendingPauseTask = moveTask; flowState = FlowRunState.Paused; break; }
                                if (!movedToStart)
                                {
                                    Log.Error($"移动到检测位 {i} 起点失败，已重试3次");
                                    // 原 MessageBox+break → 业务 Err（收尾弹窗+红灯+状态栏"检测异常"，返回值仍 true 不影响连续模式）
                                    err = $"移动到检测位 {i + 1} 起点失败";
                                    bizState = CheckState.Err;
                                    break;
                                }

                                tBC = swPos.ElapsedMilliseconds;
                                // B(2D启动) 和 C(移到3D起点) 是在 tBC 内并行完成的
                                Log.Info($"[⏱] 检测位{i} B+C(2D启动+移到起点)={tBC}ms");
                                bizState = CheckState.StartScan3D;
                                break;
                            }

                        case CheckState.StartScan3D:
                            {
                                // 检查点④（启动3D扫描前）
                                if (!_pauseCheckEvent.IsSet) { flowState = FlowRunState.Paused; break; }
                                if (token.IsCancellationRequested) { MarkAlarmCancel(); flowState = FlowRunState.Terminated; break; }
                                _3dScanTask = null;

                                // 移动完成，立即开始3D扫描（与2D检测完全并行）；失败重试1次（总尝试2次，SCAN3D偶发超时/触发失败兜底）
                                // 第一次用 Scan3DTimeout（正常扫描运动），重试用 Scan3DRetryTimeout（轴已到位，短超时快速判定真失败）
                                UpdateStatus($"检测位 {i + 1}/{totalPositions} 3D扫描中...");
                                Log.Info($"检测位 {i}: SCAN3D X{endX:F3}, Y{moveY:F3}");
                                _3dScanTask = Scan3DWithRetryAsync(endX, moveY);

                                // 此时2D检测可能还在跑，3D扫描也在跑，状态显示等待哪个
                                if (_vmDetectTask != null && !_vmDetectTask.IsCompleted)
                                    UpdateStatus($"检测位 {i + 1}/{totalPositions} 2D检测中 + 3D扫描中...");
                                else
                                    UpdateStatus($"检测位 {i + 1}/{totalPositions} 3D扫描中...");
                                bizState = CheckState.Wait2D;
                                break;
                            }

                        case CheckState.Wait2D:
                            {
                                if (token.IsCancellationRequested) { MarkAlarmCancel(); flowState = FlowRunState.Terminated; break; }
                                // 等2D检测完成（与3D扫描重叠，不额外耗时）
                                if (_vmDetectTask != null)
                                {
                                    // 暂停感知：2D检测等待中暂停→立即转 Paused（在途2D任务后台收尾，恢复后整拍重做；取消优先）
                                    string response2D = await PauseAwareWaitAsync(_vmDetectTask, token);
                                    if (token.IsCancellationRequested) { MarkAlarmCancel(); flowState = FlowRunState.Terminated; break; }
                                    if (!_pauseCheckEvent.IsSet) { pendingPauseTask = _vmDetectTask; flowState = FlowRunState.Paused; break; }
                                    if (response2D != "TIMEOUT")
                                    {
                                        if (response2D.StartsWith("DETECT2D_BLOB_OK"))
                                            rawPins2D = ParseDetect2DBlobResponse(response2D);
                                        else
                                            rawPins2D = ParseDetect2DResponse(response2D);

                                        // 构建VM叠加层数据（像素坐标→物理mm）
                                        _vmOverlays.RemoveAll(o => o.GridIndex == pos.GrabIndex);
                                        double mmPerPx = _sysParam.Data.CameraPixelEquivalent;
                                        double actFovW = _sysParam.Data.CameraResolutionW * mmPerPx;
                                        double actFovH = _sysParam.Data.CameraResolutionH * mmPerPx;
                                        double fovLeft = detectX - actFovW / 2;
                                        double fovTop = detectY + actFovH / 2;
                                        var vmConverted = new List<Pin2DData>();
                                        foreach (var pin in rawPins2D)
                                        {
                                            double pinMmX = fovLeft + pin.X * mmPerPx;
                                            double pinMmY = fovTop - pin.Y * mmPerPx;
                                            double padMmX = fovLeft + pin.PadX * mmPerPx;
                                            double padMmY = fovTop - pin.PadY * mmPerPx;

                                            // 有效区过滤：剔除落在重叠区的检测结果（全局去重兜底，此处简单过滤）
                                            // 使用焊盘坐标(padMmX/padMmY)判断，确保只保留当前FOV有效区内的针
                                            // 不用针尖坐标，因为针尖偏移可能导致针误入相邻FOV重叠区
                                            // EffAreaLeftShrink/RightShrink 可单独收缩左右边界，补偿标定误差
                                            double effHalfBase = actFovW * (1 - project.Overlap) / 2;
                                            double effHalfH = actFovH * (1 - project.Overlap) / 2;
                                            double effLeft = detectX - effHalfBase + _sysParam.Data.EffAreaLeftShrink;
                                            double effRight = detectX + effHalfBase - _sysParam.Data.EffAreaRightShrink;
                                            if (i == 0) Log.Debug($"[有效区] detectX={detectX:F3} base={effHalfBase:F3} "
                                                + $"L收缩={_sysParam.Data.EffAreaLeftShrink:F2} R收缩={_sysParam.Data.EffAreaRightShrink:F2} "
                                                + $"eff=[{effLeft:F3}, {effRight:F3}] effHalfH={effHalfH:F3}");
                                            if (padMmX < effLeft || padMmX > effRight || Math.Abs(padMmY - detectY) > effHalfH)
                                            {
                                                string reason = padMmX < effLeft ? "X超左" :
                                                                padMmX > effRight ? "X超右" :
                                                                $"Y偏差{Math.Abs(padMmY - detectY):F3} > {effHalfH:F3}";
                                                Log.Debug($"[有效区过滤] 针型{pin.PinType} PinIdx={pin.PinIndex} " +
                                                    $"padMm({padMmX:F3},{padMmY:F3}) → {reason}");
                                                continue;
                                            }

                                            // 按针型读取检测参数（从解析结果取针型）
                                            string pinTypeStr = pin.PinType ?? "A";
                                            var pinParam = project.PinTypeParams?.FirstOrDefault(p => p.Label == pinTypeStr);
                                            double offX = pinParam?.IdealOffsetX ?? defaultParams.offX;
                                            double offY = pinParam?.IdealOffsetY ?? defaultParams.offY;
                                            double xyTol = pinParam?.XyTolerance ?? defaultXyTol;

                                            double idealPinMmX = padMmX + offX;
                                            double idealPinMmY = padMmY + offY;
                                            double dxMm = pinMmX - idealPinMmX;
                                            double dyMm = pinMmY - idealPinMmY;

                                            bool isEmptyOrCrooked = pin.IsEmptyPin || pin.IsCrookedPin;

                                            // 空针/歪针加入叠加层（无实际针尖框）
                                            _vmOverlays.Add(new VmOverlayItem
                                            {
                                                GridIndex = pos.GrabIndex,
                                                PinIndex = pin.PinIndex,
                                                PinType = pinTypeStr,
                                                PinMmX = isEmptyOrCrooked ? 0 : Math.Round(pinMmX, 3),
                                                PinMmY = isEmptyOrCrooked ? 0 : Math.Round(pinMmY, 3),
                                                PadMmX = Math.Round(padMmX, 3),
                                                PadMmY = Math.Round(padMmY, 3),
                                                IdealPinMmX = Math.Round(idealPinMmX, 3),
                                                IdealPinMmY = Math.Round(idealPinMmY, 3),
                                                Dx = Math.Round(dxMm, 3),
                                                Dy = Math.Round(dyMm, 3),
                                                IsOK = isEmptyOrCrooked ? false : Math.Abs(dxMm) <= xyTol && Math.Abs(dyMm) <= xyTol,
                                                IsEmptyPin = pin.IsEmptyPin,
                                                IsCrookedPin = pin.IsCrookedPin,
                                                // VM匹配框参数（像素→mm转换）
                                                PadW = Math.Round(pin.PadW * mmPerPx, 3),
                                                PadH = Math.Round(pin.PadH * mmPerPx, 3),
                                                PadAng = Math.Round(pin.PadAng, 3),
                                                PinW = Math.Round(pin.PinW * mmPerPx, 3),
                                                PinH = Math.Round(pin.PinH * mmPerPx, 3),
                                                PinAng = Math.Round(pin.PinAng, 3)
                                            });

                                            // 空针/歪针改用焊盘位置显示
                                            double displayX = isEmptyOrCrooked ? padMmX : pinMmX;
                                            double displayY = isEmptyOrCrooked ? padMmY : pinMmY;

                                            vmConverted.Add(new Pin2DData
                                            {
                                                PinIndex = pin.PinIndex,
                                                Dx = pin.IsCrookedPin ? 0 : Math.Round(dxMm, 3),
                                                Dy = pin.IsCrookedPin ? 0 : Math.Round(dyMm, 3),
                                                X = Math.Round(displayX, 3),
                                                Y = Math.Round(displayY, 3),
                                                PadX = Math.Round(padMmX, 3),
                                                PadY = Math.Round(padMmY, 3),
                                                IsEmptyPin = pin.IsEmptyPin,
                                                IsCrookedPin = pin.IsCrookedPin,
                                                PadW = Math.Round(pin.PadW * mmPerPx, 3),
                                                PadH = Math.Round(pin.PadH * mmPerPx, 3),
                                                PadAng = Math.Round(pin.PadAng, 3),
                                                PinW = Math.Round(pin.PinW * mmPerPx, 3),
                                                PinH = Math.Round(pin.PinH * mmPerPx, 3),
                                                PinAng = Math.Round(pin.PinAng, 3),
                                                PinType = pinTypeStr
                                            });
                                        }
                                        rawPins2D = vmConverted;
                                        Log.Info($"检测位 {i} 2D检测完成，解析到 {rawPins2D.Count} 个针");
                                    }
                                    else
                                    {
                                        // 2026-08-25 主题②：2D 检测超时（VM 无响应）不再仅降级空结果——记录板末统一重扫
                                        // （重扫位重拍 A 段重新 2D，自愈）；主流程降级继续（3D 照扫，板末补 2D）
                                        Log.Warning($"检测位 {i} 2D检测超时");
                                        if (_rescanPhase)
                                        {
                                            // 重扫模式内 2D 超时：不在此重拍——该位 SCAN3D 已在途，立即重拍会双发交错（串位回归）；
                                            // 照常推进 WaitScan3D → Merge 的"3D数据异常"分支统一重试（此时 3D 已扫完，严格串行安全）
                                            Log.Warning($"检测位 {i} 重扫 2D 检测超时（Merge 统一重试处理）");
                                        }
                                        else
                                        {
                                            Log.Warning($"检测位 {i} 2D检测超时，已记录板末延迟重扫");
                                            if (!_rescanQueue.Contains(i)) _rescanQueue.Add(i);
                                        }
                                    }
                                }

                                // (3.5) 2D检测完成后刷新叠加层显示（在等待3D前让用户看到2D结果）
                                // 2026-08-21：改为异步刷新（fire-and-forget），不阻塞检测流程推进；
                                // 渲染由 UI 线程在流程 await 间隙执行，本帧按快照绘制（见 RefreshOverlayDisplayAsync）
                                if (rawPins2D != null && rawPins2D.Count > 0)
                                {
                                    RefreshOverlayDisplayAsync();
                                }
                                // 2026-08-25 优化：2D解析完成 → 先处理上一位挂起的3D结果（MergePending，此时本检测位
                                // 3D 扫描已在 StartScan3D 发出并后台运行，Merge 计算被扫描时间掩盖，不再占用 B+C 段）
                                // 2026-08-25 延迟重扫：重扫模式跳过 MergePending（板末重扫严格串行，无在途扫描/挂起上下文），
                                // WaitScan3D 内同步等扫描+数据后直接 Merge
                                bizState = _rescanPhase ? CheckState.WaitScan3D : CheckState.MergePending;
                                break;
                            }

                        case CheckState.WaitScan3D:
                            {
                                // 检查点⑤（等3D扫描前）
                                if (!_pauseCheckEvent.IsSet) { flowState = FlowRunState.Paused; break; }
                                if (token.IsCancellationRequested) { MarkAlarmCancel(); flowState = FlowRunState.Terminated; break; }

                                // 2026-08-25 延迟重扫：重扫模式严格串行——同步等扫描完成+等数据，直接 Merge（无挂起/无在途交错）
                                if (_rescanPhase)
                                {
                                    if (_3dScanTask != null)
                                    {
                                        bool scanOk = await WaitOrCancelAsync(_3dScanTask, token);
                                        if (token.IsCancellationRequested) { MarkAlarmCancel(); flowState = FlowRunState.Terminated; break; }
                                        response = "TIMEOUT";
                                        scan3DNoData = false;
                                        if (scanOk && _vision3DClient.IsConnected)
                                        {
                                            int waitMs = Math.Min((int)_sysParam.Data.Wait3DResultTimeout, 2000);
                                            int capMs = (int)(_sysParam.Data.Scan3DTimeout * 2 + _sysParam.Data.Wait3DResultTimeout);
                                            var dataTask = _vision3DClient.WaitForPrefixNonEmptyAsync("DETECT_3D_OK:", capMs, token);
                                            if (await Task.WhenAny(dataTask, Task.Delay(waitMs, token)) == dataTask)
                                                response = await dataTask;
                                            else
                                            {
                                                Log.Warning($"检测位 {i} 重扫 3D数据超时，扫描成功但未收到数据");
                                                scan3DNoData = true;
                                            }
                                        }
                                        else if (!scanOk)
                                        {
                                            Log.Error($"检测位 {i} 重扫 3D扫描失败");
                                        }
                                    }
                                    else
                                    {
                                        response = "TIMEOUT";
                                        scan3DNoData = false;
                                    }
                                    scan3DTimeout = response == "TIMEOUT";
                                    Log.Info($"[⏱] 检测位{i} 重扫 D(3D扫描完成)={swPos.ElapsedMilliseconds - tBC}ms");
                                    Log.Info($"[⏱] 检测位{i} 重扫 总耗时={swPos.ElapsedMilliseconds}ms ========================================");
                                    bizState = CheckState.Merge;
                                    break;
                                }

                                // (4) 2026-08-25 优化：只等扫描完成（不等待数据）——
                                // 快照本检测位上下文 + 创建3D数据监听（早开防漏），立即推进 NextPos；
                                // A 段移动/拍照期间数据后台到达，MergePending 状态取数+Merge（数据等待不再阻塞轴）。
                                if (_3dScanTask != null)
                                {
                                    // 监听早开防漏VM提前推送（数据可能在扫描完成前已到，2026-08-21 实测 23ms~1.5s）；
                                    // 内部超时给大上限（覆盖重试最坏耗时+结果窗口），真正的结果窗口在 MergePending 用剩余时间 WhenAny 判定
                                    int _3dDataCapMs = (int)(_sysParam.Data.Scan3DTimeout * 2 + _sysParam.Data.Wait3DResultTimeout);
                                    _pending3DDataTask = _vision3DClient.IsConnected
                                        ? _vision3DClient.WaitForPrefixNonEmptyAsync("DETECT_3D_OK:", _3dDataCapMs, token)
                                        : null;

                                    // 3D扫描不重试（重试会导致CAM3D_ON/OFF信号异常）；取消感知：复位/停止立即退出，不等扫描超时
                                    bool scanOk = await WaitOrCancelAsync(_3dScanTask, token);
                                    if (token.IsCancellationRequested) { MarkAlarmCancel(); flowState = FlowRunState.Terminated; break; }

                                    if (scanOk)
                                    {
                                        Log.Info($"[⏱] 检测位{i} D(3D扫描完成)={swPos.ElapsedMilliseconds - tBC}ms");
                                        Log.Info($"[⏱] 检测位{i} 总耗时={swPos.ElapsedMilliseconds}ms ========================================");
                                        SnapshotPending3D(i, rawPins2D);
                                    }
                                    else
                                    {
                                        Log.Error($"检测位 {i} 3D扫描失败");
                                        // 2026-08-25 延迟重扫：去掉阻塞弹窗（实测阻塞 ~5.3s，位23 实证），扫描失败改为记录板末统一重扫
                                        // （偶发轴/超时失败重扫可自愈）；丢弃监听防 MergePending 误判 scan3DNoData，快照照做走 TIMEOUT 降级
                                        _pending3DDataTask = null;
                                        if (!_rescanQueue.Contains(i))
                                        {
                                            _rescanQueue.Add(i);
                                            Log.Warning($"检测位 {i} 3D扫描失败（轴/超时），已记录板末延迟重扫");
                                        }
                                        SnapshotPending3D(i, rawPins2D);
                                    }
                                }
                                else
                                {
                                    Log.Info($"检测位 {i} 3D未配置，跳过3D检测");
                                    SnapshotPending3D(i, rawPins2D);
                                    _pending3DDataTask = null;
                                }
                                bizState = CheckState.NextPos;
                                break;
                            }

                        case CheckState.MergePending:
                            {
                                // 检查点⑦（2026-08-25 新增）：Wait2D 解析完成后/End前——处理上一位挂起的3D结果；
                                // 此时本检测位 3D 扫描已在 StartScan3D 发出并后台运行，Merge 计算被扫描时间掩盖，不占 B+C 段。
                                if (!_pauseCheckEvent.IsSet) { flowState = FlowRunState.Paused; break; }
                                if (token.IsCancellationRequested) { MarkAlarmCancel(); flowState = FlowRunState.Terminated; break; }

                                if (_pendingMergeIdx >= 0)
                                {
                                    // 先保存当前位(i+1) 的 2D 结果——Merge 会覆盖流程变量 rawPins2D，NextPos 恢复上下文时用
                                    _curRawPins2D = rawPins2D;

                                    // 恢复上一位上下文（WaitScan3D 快照；A段期间 CapturePos(i+1) 已覆盖流程变量）
                                    i = _pendingMergeIdx;
                                    pos = _pendingPos;
                                    detectX = _pendingDetectX; detectY = _pendingDetectY;
                                    scanHalfWidth = _pendingScanHalfWidth;
                                    startX = _pendingStartX; endX = _pendingEndX; moveY = _pendingMoveY;
                                    rawPins2D = _pendingPins2D;
                                    _pendingMergeIdx = -1;

                                    // 取数：数据应已在 A 段期间到达；剩余窗口兜底（总窗口从扫描完成起算仍压 2s，语义不变）
                                    response = "TIMEOUT";
                                    if (_pending3DDataTask != null)
                                    {
                                        int elapsedMs = (int)(DateTime.UtcNow - _pendingScanDoneUtc).TotalMilliseconds;
                                        int waitMs = Math.Max(0, Math.Min((int)_sysParam.Data.Wait3DResultTimeout, 2000) - elapsedMs);
                                        var resultWait = Task.WhenAny(_pending3DDataTask, Task.Delay(waitMs, token));
                                        if (await resultWait == _pending3DDataTask) response = await _pending3DDataTask;
                                        else if (token.IsCancellationRequested) { MarkAlarmCancel(); flowState = FlowRunState.Terminated; break; }  // 复位/停止立即终止
                                        else
                                        {
                                            Log.Warning($"检测位 {i} 3D数据超时，扫描成功但未收到数据");
                                            scan3DNoData = true;
                                            response = "TIMEOUT";
                                        }
                                        _pending3DDataTask = null;
                                    }
                                    else
                                    {
                                        // 3D未配置或扫描失败（轴失败不重扫）
                                        scan3DNoData = false;
                                        Log.Warning($"检测位 {i} 3D检测超时");
                                    }
                                    scan3DTimeout = response == "TIMEOUT";

                                    _skipAForNext = true;   // 本检测位（i+1）A段已拍且 2D/3D 已启动，NextPos 时跳过 CapturePos/GrabTile（不重拍）
                                    bizState = CheckState.Merge;
                                    break;
                                }

                                // 无挂起 → 本检测位 2D 已检、3D 扫描已启动，直接等扫描完成（首拍/正常流）
                                bizState = i >= totalPositions ? CheckState.End : CheckState.WaitScan3D;
                                break;
                            }

                        case CheckState.Merge:
                            {
                                // 检查点⑥（解析合并前）
                                if (!_pauseCheckEvent.IsSet) { flowState = FlowRunState.Paused; break; }
                                if (token.IsCancellationRequested) { MarkAlarmCancel(); flowState = FlowRunState.Terminated; break; }

                                // (6) 解析并合并2D+3D检测数据
                                // 新VM格式: DETECT_3D_OK:x,y,z;x,y,z;... (逗号+分号)
                                // 旧格式: (x&y&z)(x&y&z)... 或 pinIdx,val;...
                                List<PinResult> newResults = null;
                                List<VM3DPoint> vmRaw3D = null;
                                bool isNewVMFormat = false;

                                if (!scan3DTimeout)
                                {
                                    // ====== 第一步：解析3D数据 ======
                                    List<(int PinIndex, double X, double Y, double H)> filteredPins3D = null;
                                    bool has3DCoordinates = false;

                                    // 先尝试解析新VM格式
                                    vmRaw3D = ParseVMDetect3DResponse(response);
                                    if (vmRaw3D != null && vmRaw3D.Count > 0)
                                    {
                                        isNewVMFormat = true;
                                        has3DCoordinates = true;
                                        double minX = vmRaw3D.Min(p => p.VmX);
                                        double maxX = vmRaw3D.Max(p => p.VmX);
                                        double minY = vmRaw3D.Min(p => p.VmY);
                                        double maxY = vmRaw3D.Max(p => p.VmY);
                                        Log.Info($"[3D匹配] 检测位 {i}: 新VM格式 {vmRaw3D.Count}针 " +
                                            $"x_vm=[{minX:F3}~{maxX:F3}] y_vm=[{minY:F3}~{maxY:F3}]");
                                    }
                                    else
                                    {
                                        // 旧格式兼容
                                        var rawPins3D = ParseDetect3DResponse(response);
                                        if (rawPins3D != null && rawPins3D.Count > 0)
                                        {
                                            has3DCoordinates = rawPins3D.Any(p => p.X != 0 || p.Y != 0);
                                            if (has3DCoordinates)
                                            {
                                                // ===== 重叠区过滤（与2D同一逻辑）=====
                                                // 坐标系转换与匹配段统一：均值减法将3D坐标中心对齐到 detectX/detectY
                                                // p.X 以传感器线中心为原点（±13mm），p.Y 以扫描起点为原点（0→-26mm）
                                                double sensorHalfWidth = scanHalfWidth;
                                                double overlapRatio = project.Overlap;
                                                double meanRawX = rawPins3D.Average(p => p.X);
                                                double meanRawY = rawPins3D.Average(p => p.Y);
                                                // 有效区半宽/半高：3D传感器宽度=26mm，居中去除重叠比例
                                                double effHalfW = sensorHalfWidth * (1 - overlapRatio);
                                                double effHalfH = sensorHalfWidth * (1 - overlapRatio);
                                                double effLeft = detectX - effHalfW;
                                                double effRight = detectX + effHalfW;
                                                double exclusiveRight;

                                                if (i < totalPositions - 1)
                                                {
                                                    double nextDetectX = DetectPositions[i + 1].X;
                                                    double nextEffLeft = nextDetectX - effHalfW;
                                                    // 分界点 = (本有效区右边界 + 下个有效区左边界) / 2
                                                    exclusiveRight = (effRight + nextEffLeft) / 2;
                                                }
                                                else
                                                {
                                                    exclusiveRight = effRight;
                                                }

                                                filteredPins3D = rawPins3D
                                                    .Where(p =>
                                                    {
                                                        // 参考匹配段：detectX + (p.X - meanX) 统一坐标系
                                                        double absPinX = detectX + (p.X - meanRawX);
                                                        double absPinY = detectY + (p.Y - meanRawY);
                                                        // XY双向有效区判断（与2D一致）
                                                        return absPinX >= effLeft && absPinX < exclusiveRight &&
                                                               Math.Abs(absPinY - detectY) <= effHalfH;
                                                    })
                                                    .ToList();

                                                Log.Info($"检测位 {i}: 3D原始{rawPins3D.Count}个针，" +
                                                         $"重叠率={overlapRatio * 100:F0}% 有效区=[{effLeft:F1}, {exclusiveRight:F1}) " +
                                                         $"Y有效半高={effHalfH:F1} " +
                                                         $"→ 过滤后{filteredPins3D.Count}个针");

                                                // 3D原始/过滤坐标明细
                                                if (rawPins3D.Count > 0)
                                                {
                                                    string rawDetail = string.Join(" | ",
                                                        rawPins3D.Select(p => $"#{p.PinIndex}(px={p.X:F3},py={p.Y:F3},h={p.H:F3})"));
                                                    Log.Debug($"检测位 {i} 3D原始坐标: [{rawDetail}]");
                                                }
                                                if (filteredPins3D.Count > 0 && filteredPins3D.Count != rawPins3D.Count)
                                                {
                                                    string filtDetail = string.Join(" | ",
                                                        filteredPins3D.Select(p =>
                                                        {
                                                            double ax = detectX + p.X, ay = detectY + p.Y;
                                                            return $"#{p.PinIndex} abs({ax:F3},{ay:F3}) h={p.H:F3}";
                                                        }));
                                                    Log.Debug($"检测位 {i} 3D过滤后坐标: [{filtDetail}]");
                                                }
                                            }
                                            else
                                            {
                                                // 旧格式（无坐标，按PinIndex匹配）
                                                filteredPins3D = rawPins3D;
                                            }
                                        }
                                        else
                                        {
                                            Log.Warning($"检测位 {i} 3D未解析到有效数据: {response}");
                                        }
                                    }

                                    // ====== 第二步：2D-3D 匹配合并 ======
                                    var validPins2D = rawPins2D?.Where(p => !p.IsEmptyPin).ToList();
                                    // Blob模式排除空针（不参与3D匹配校准）；矩形模式空针可用于吸收歪针3D点
                                    var allPins2D = isBlobMode
                                        ? rawPins2D?.Where(p => !p.IsEmptyPin).ToList()
                                        : rawPins2D?.ToList();

                                    int mergeTotal2D = validPins2D?.Count ?? 0;
                                    int mergeMatched3D = 0;
                                    int mergeValidH3D = 0;   // 匹配成功且高度有效(h>0)的针数（2026-08-21：VM可能返回全0高度，须视为异常）
                                    int mergeNoMatch3D = 0;
                                    int mergeUnmatched3DSkipped = 0;

                                    // 2026-08-25 修复：匹配分支加 validPins2D 判空——"2D 检测超时（rawPins2D=null）+ 3D 数据正常"时
                                    // 原条件只看 vmRaw3D，进分支后 L1180 validPins2D.Count 会 NRE 崩溃；null 时走下方纯2D容错
                                    // （validPins2D 为空 → 无结果落表 → 该位由板末重扫补，2D 超时已入队）
                                    if (isNewVMFormat && vmRaw3D != null && vmRaw3D.Count > 0
                                        && validPins2D != null && validPins2D.Count > 0)
                                    {
                                        // ====== 新VM格式匹配（分列排序配对 + 中位数偏移 + 欧氏距离匹配）======
                                        //   1. 按X分列（左/右两排）
                                        //   2. 每列按Y排序后顺序配对，计算每对 constX/constY
                                        //   3. 取所有对 constX/constY 中位数 → 校准偏移
                                        //   4. 用校准偏移把全部3D点转成board坐标
                                        //   5. Pass2 精匹配（3mm阈值）

                                        // ---- 步骤0：3D点过滤已移除（原始数据全部保留）----
                                        /* 如需开启过滤，取消下方注释
                                        double overlapRatio = project.Overlap;
                                        double effHalfW = project.FovWidth * (1 - overlapRatio) / 2.0;
                                        double effHalfH = project.FovHeight * (1 - overlapRatio) / 2.0;
                                        double centerX3D = _sysParam.Data.SensorFovWidth / 2 * 11;
                                        double centerY3D = _sysParam.Data.SensorFovHeight / 2 * -11 - 16317.994;
                                        var beforeFilter = vmRaw3D.Select((p, j) => new { p, j }).ToList();
                                        var afterFilter = beforeFilter.Where(x =>
                                            //Math.Abs(x.p.VmX * 1000 - centerX3D) <= effHalfW*1000 &&
                                            Math.Abs(x.p.VmY * 1000 - centerY3D) <= effHalfH * 1000
                                        ).ToList();
                                        string filterDetail = string.Join("; ", beforeFilter.Select(x =>
                                        {
                                            double devX = Math.Abs(x.p.VmX * 1000 - centerX3D);
                                            double devY = Math.Abs(x.p.VmY * 1000 - centerY3D);
                                            bool outX = devX > effHalfW * 1000;
                                            bool outY = devY > effHalfH * 1000;
                                            string status = (outX || outY) ? "✗" : "✓";
                                            string reason = (outX && outY) ? "X+Y超" : outX ? "X超" : outY ? "Y超" : "";
                                            return $"#{x.j}{status}(vmX={x.p.VmX:F4},vmY={x.p.VmY:F4},H={x.p.VmZ:F4}) " +
                                                   $"X偏移={devX:F1}/{effHalfW * 1000:F0}um " +
                                                   $"Y偏移={devY:F1}/{effHalfH * 1000:F0}um" +
                                                   (reason != "" ? $" [{reason}]" : "");
                                        }));
                                        Log.Info($"[3D过滤] 检测位{i}: {beforeFilter.Count}→{afterFilter.Count}个通过 " +
                                                 $"| 排除{beforeFilter.Count - afterFilter.Count}个 | {filterDetail}");
                                        vmRaw3D = afterFilter.Select(x => x.p).ToList();
                                        */

                                        // ---- 步骤1：从系统参数读取VM→板坐标转换偏移（固定值，所有检测位复用）----
                                        // Camera3DOffset 用于调3D扫描位置(物理); VmToBoardOffset 用于坐标变换(数学)
                                        double calibOffsetX = _sysParam.Data.VmToBoardOffsetX;
                                        double calibOffsetY = _sysParam.Data.VmToBoardOffsetY;
                                        Log.Info($"[3D校准] 检测位{i}: 使用系统参数偏移({calibOffsetX:F1},{calibOffsetY:F1})");
                                        /*
                                        // ─── 逐FOV投票校准（备用，取消注释//即可启用）───
                                        var voteBins = new Dictionary<string, int>();
                                        const double binSize = 0.5;
                                        foreach (var p2 in allPins2D)
                                        {
                                            foreach (var p3 in vmRaw3D)
                                            {
                                                double ox = Math.Round((p2.X - detectX - p3.VmX) / binSize) * binSize;
                                                double oy = Math.Round((p2.Y - detectY - p3.VmY) / binSize) * binSize;
                                                string key = $"{ox:F1},{oy:F1}";
                                                voteBins.TryGetValue(key, out int cnt);
                                                voteBins[key] = cnt + 1;
                                            }
                                        }
                                        calibOffsetX = 0; calibOffsetY = 0;
                                        if (voteBins.Count > 0)
                                        {
                                            var bestVote = voteBins.OrderByDescending(kv => kv.Value).First();
                                            var bestParts = bestVote.Key.Split(',');
                                            calibOffsetX = double.Parse(bestParts[0]);
                                            calibOffsetY = double.Parse(bestParts[1]);
                                            Log.Info($"[3D校准] 检测位{i}: 总投票{voteBins.Count}个格子 " +
                                                $"最佳偏移({calibOffsetX:F1},{calibOffsetY:F1}) 得票{bestVote.Value}");
                                        }
                                        else Log.Warning($"[3D校准] 检测位{i}: 无有效配对，跳过3D匹配");
                                        */

                                        // ---- 步骤5：全部原始3D点用校准偏移转board坐标 ----
                                        var candidatesAll = new List<VM3DCandidate>();
                                        for (int j = 0; j < vmRaw3D.Count; j++)
                                        {
                                            var p = vmRaw3D[j];
                                            double bx = detectX + p.VmX + calibOffsetX;
                                            double by = detectY + p.VmY + calibOffsetY;
                                            candidatesAll.Add(new VM3DCandidate
                                            {
                                                Index = j,
                                                BoardX = bx,
                                                BoardY = by,
                                                Z = p.VmZ,
                                                VmX = p.VmX,
                                                VmY = p.VmY
                                            });
                                            Log.Debug($"[3D转换] 3D#{j}: vm({p.VmX:F4},{p.VmY:F4}) " +
                                                $"=> board({bx:F3},{by:F3}) H={p.VmZ:F3}");
                                        }

                                        // ---- 步骤6：Pass2精匹配（匈牙利算法：全局最小总距离）----
                                        // 真正的全局最优，适配行/列/环/散乱所有排列
                                        newResults = new List<PinResult>();
                                        double matchThreshold = _sysParam.Data.MatchThreshold3D;

                                        // 匈牙利算法：构建代价矩阵 rows=validPins2D, cols=candidatesAll
                                        int n2 = validPins2D.Count;
                                        int n3 = candidatesAll.Count;
                                        int size = Math.Max(n2, n3);
                                        double[,] hungCost = new double[size, size];
                                        double INF = 1e9;
                                        double maxMatchDist = 15.0; // 超过15mm视为不可能匹配（覆盖同组间距，过滤异组）
                                        for (int pi = 0; pi < size; pi++)
                                        {
                                            for (int ci = 0; ci < size; ci++)
                                            {
                                                if (pi < n2 && ci < n3)
                                                {
                                                    var p2 = validPins2D[pi];
                                                    var c = candidatesAll[ci];
                                                    double dist = Math.Sqrt(
                                                        Math.Pow(p2.X - c.BoardX, 2) +
                                                        Math.Pow(p2.Y - c.BoardY, 2));
                                                    // 距离过远的3D点不参与匹配（防止另一组3D点干扰）
                                                    hungCost[pi, ci] = dist <= maxMatchDist ? dist : INF;
                                                }
                                                // 虚拟行/列代价=0：允许2D无3D匹配、允许多余3D点被跳过
                                                // （2026-08-21 修复：原INF=1e9导致虚拟行(多余3D)抢占可匹配点、真实针被挤到远距INF边，
                                                //   例：PinIdx=8 被配到20.41mm的3D#16而错失1.50mm的3D#8 → 误判无匹配NG）
                                                else hungCost[pi, ci] = 0;
                                            }
                                        }

                                        // Hungarian 算法（O(n³)）
                                        double[] u = new double[size + 1];
                                        double[] v = new double[size + 1];
                                        int[] hungP = new int[size + 1];
                                        int[] way = new int[size + 1];

                                        for (int hi = 1; hi <= size; hi++)
                                        {
                                            hungP[0] = hi;
                                            int j0 = 0;
                                            double[] minv = new double[size + 1];
                                            bool[] used = new bool[size + 1];
                                            for (int j = 1; j <= size; j++) minv[j] = INF;

                                            do
                                            {
                                                used[j0] = true;
                                                int i0 = hungP[j0];
                                                double delta = INF;
                                                int j1 = 0;
                                                for (int j = 1; j <= size; j++)
                                                {
                                                    if (used[j]) continue;
                                                    double cur = hungCost[i0 - 1, j - 1] - u[i0] - v[j];
                                                    if (cur < minv[j])
                                                    {
                                                        minv[j] = cur;
                                                        way[j] = j0;
                                                    }
                                                    if (minv[j] < delta)
                                                    {
                                                        delta = minv[j];
                                                        j1 = j;
                                                    }
                                                }
                                                for (int j = 0; j <= size; j++)
                                                {
                                                    if (used[j])
                                                    {
                                                        u[hungP[j]] += delta;
                                                        v[j] -= delta;
                                                    }
                                                    else minv[j] -= delta;
                                                }
                                                j0 = j1;
                                            } while (hungP[j0] != 0);

                                            do { int j1 = way[j0]; hungP[j0] = hungP[j1]; j0 = j1; } while (j0 != 0);
                                        }

                                        int[] hungAssign = new int[n2];
                                        double[] hungDist = new double[n2];
                                        for (int pi = 0; pi < n2; pi++) { hungAssign[pi] = -1; hungDist[pi] = 0; }
                                        for (int j = 1; j <= size; j++)
                                        {
                                            if (hungP[j] <= n2 && j <= n3)
                                            {
                                                int pi = hungP[j] - 1;
                                                int ci = j - 1;
                                                hungAssign[pi] = ci;
                                                var p2 = validPins2D[pi];
                                                var c = candidatesAll[ci];
                                                hungDist[pi] = Math.Sqrt(
                                                    Math.Pow(p2.X - c.BoardX, 2) +
                                                    Math.Pow(p2.Y - c.BoardY, 2));
                                            }
                                        }

                                        for (int pi = 0; pi < n2; pi++)
                                        {
                                            var pin2D = validPins2D[pi];
                                            double h = 0;
                                            string matchInfo;
                                            if (hungAssign[pi] >= 0 && hungDist[pi] <= matchThreshold)
                                            {
                                                var match = candidatesAll[hungAssign[pi]];
                                                h = match.Z;
                                                mergeMatched3D++;
                                                if (h > 0) mergeValidH3D++;   // 高度有效才计数（VM异常返回0时不计）
                                                matchInfo = $"OK 匹配3D#{match.Index} dist={hungDist[pi]:F3} " +
                                                    $"board({match.BoardX:F3},{match.BoardY:F3}) h={h:F3}";
                                            }
                                            else
                                            {
                                                mergeNoMatch3D++;
                                                // 区分两种无匹配：有分配但距离超阈值 / 未分配到任何3D点（3D点数不足，分到虚拟列）
                                                string noMatchReason = hungAssign[pi] >= 0
                                                    ? $"匈牙利距离={hungDist[pi]:F2}mm >阈值{matchThreshold}mm"
                                                    : "3D点数不足，无点可配";
                                                matchInfo = $"NO 无匹配({noMatchReason}) " +
                                                    $"pin2D({pin2D.X:F1},{pin2D.Y:F1})";
                                            }

                                            // 按针型读取检测参数
                                            string pt = pin2D.PinType ?? "A";
                                            var pParam = project.PinTypeParams?.FirstOrDefault(p => p.Label == pt);
                                            double xyTol = pParam?.XyTolerance ?? defaultXyTol;
                                            double iH = pParam?.IdealHeight ?? defaultIdealH;
                                            double hTol = pParam?.HeightTolerance ?? defaultHTol;

                                            bool xOk = Math.Abs(pin2D.Dx) <= xyTol;
                                            bool yOk = Math.Abs(pin2D.Dy) <= xyTol;
                                            bool hOk = Math.Abs(h - iH) <= hTol;
                                            string verdict = pin2D.IsCrookedPin ? "NG" : ((xOk && yOk && hOk) ? "OK" : "NG");

                                            Log.Info($"[3D匹配] PinIdx={pin2D.PinIndex} " +
                                                $"2D({pin2D.X:F3},{pin2D.Y:F3}) " +
                                                $"dx={pin2D.Dx:F3} dy={pin2D.Dy:F3} | " +
                                                matchInfo + " | " +
                                                $"判{xOk}+{yOk}+{hOk}={verdict}");

                                            newResults.Add(new PinResult
                                            {
                                                PINID = globalPinId++,
                                                DetectIndex = i,
                                                GridIndex = pos.GrabIndex,
                                                PinIndex = pin2D.PinIndex,
                                                PinType = pt,
                                                DiffX = (pin2D.IsEmptyPin || pin2D.IsCrookedPin) ? "NAN" : pin2D.Dx.ToString("F3"),
                                                DiffY = (pin2D.IsEmptyPin || pin2D.IsCrookedPin) ? "NAN" : pin2D.Dy.ToString("F3"),
                                                Result = verdict,
                                                X = pin2D.X,
                                                Y = pin2D.Y,
                                                H = Math.Round(h, 3),
                                                PadX = pin2D.PadX,
                                                PadY = pin2D.PadY,
                                                IsEmptyPin = pin2D.IsEmptyPin,
                                                IsCrookedPin = pin2D.IsCrookedPin
                                            });
                                        }

                                        mergeUnmatched3DSkipped = n3 - mergeMatched3D;
                                        Log.Info($"[3D匹配] 检测位 {i}: 2D有效={mergeTotal2D} " +
                                            $"匹配3D={mergeMatched3D} " +
                                            $"无匹配={mergeNoMatch3D} " +
                                            $"多余3D跳过={mergeUnmatched3DSkipped}");

                                        // 空针不参与3D匹配（h=0），歪针已通过validPins2D正常参与3D匹配
                                    }
                                    else
                                    {
                                        // ─── 新VM格式未满足时的容错：无3D匹配，纯2D结果 ───
                                        if (validPins2D != null && validPins2D.Count > 0)
                                        {
                                            newResults = new List<PinResult>();
                                            foreach (var pin2D in validPins2D)
                                            {
                                                mergeNoMatch3D++;
                                                newResults.Add(new PinResult
                                                {
                                                    PINID = globalPinId++,
                                                    DetectIndex = i,
                                                    GridIndex = pos.GrabIndex,
                                                    PinIndex = pin2D.PinIndex,
                                                    PinType = pin2D.PinType ?? "A",
                                                    DiffX = (pin2D.IsEmptyPin || pin2D.IsCrookedPin) ? "NAN" : pin2D.Dx.ToString("F3"),
                                                    DiffY = (pin2D.IsEmptyPin || pin2D.IsCrookedPin) ? "NAN" : pin2D.Dy.ToString("F3"),
                                                    Result = pin2D.IsCrookedPin ? "NG" : "NG",
                                                    X = pin2D.X,
                                                    Y = pin2D.Y,
                                                    H = 0,
                                                    PadX = pin2D.PadX,
                                                    PadY = pin2D.PadY,
                                                    IsEmptyPin = pin2D.IsEmptyPin,
                                                    IsCrookedPin = pin2D.IsCrookedPin
                                                });
                                            }
                                        }
                                        else
                                        {
                                            string reason = (rawPins2D != null && rawPins2D.Any(p => p.IsEmptyPin)) ? "全部为空针" :
                                                            (rawPins2D == null) ? "2D超时且3D无效" : "3D无有效数据";
                                            Log.Warning($"检测位 {i} 无法生成完整结果（{reason}），跳过该检测位");
                                        }
                                    }

                                    // ====== 3D 全匹配失败/高度全0 处理（2026-08-21 即时重扫 → 2026-08-25 延迟重扫）======
                                    // 3D 扫描成功但解析后所有针高度均无效（全部匹配失败 / 匹配成功但 VM 返回高度全0），
                                    // 判定 3D 扫描图像源或 VM 检测异常。
                                    // 2026-08-25 延迟重扫：主流程不再即时重扫（即时重扫会打断"下一检测位在途 SCAN3D"，
                                    //   双 SCAN3D 交错 → VM 3D 数据串位 → 后续位静默全 NG，位31-34 实证），
                                    //   只记录不合格位到 _rescanQueue，板末统一重扫；重扫模式（_rescanPhase）内严格串行重试。
                                    // 条件含"3D 有数据但 2D 全无"（位2 实证：3D 20点/2D 0针 = 2D 检测失败，也需重扫）
                                    if (!scan3DTimeout && mergeValidH3D == 0 &&
                                        (mergeTotal2D > 0 || (vmRaw3D?.Count ?? 0) > 0))
                                    {
                                        if (_rescanPhase)
                                        {
                                            _rescanRetries++;
                                            Log.Warning($"检测位 {i} 重扫第 {_rescanRetries} 次仍 3D 数据{vmRaw3D?.Count ?? 0}点但 2D 有效{mergeTotal2D}针全部无有效高度" +
                                                        $"（匹配{mergeMatched3D}个但高度全0或全匹配失败），" +
                                                        $"{(_rescanRetries < max3DRescanRetry ? "重拍重来" : "达到上限，接受降级结果")}");
                                            if (_rescanRetries < max3DRescanRetry)
                                            {
                                                _3dScanTask = null;
                                                swPos.Restart();
                                                bizState = CheckState.GrabTile;   // 重扫位重拍重来（严格串行，无在途扫描）
                                                break;
                                            }
                                            // 达到上限：接受降级结果（继续往下落表）
                                        }
                                        else
                                        {
                                            // 2026-08-25 修复：加 Contains 防重——位可能已由 GrabTile/2D超时/针尖缺失等先行入队，
                                            // 直接 Add 会重复重扫（19:36 轮实证：位0/位6 各入队两次 → 队列 Count=5 [0,0,6,6,25] → 重复重扫 ~8s）
                                            if (!_rescanQueue.Contains(i)) _rescanQueue.Add(i);
                                            retry3DPos++;
                                            Log.Warning($"检测位 {i} 3D 数据{vmRaw3D?.Count ?? 0}点但 2D 有效{mergeTotal2D}针全部无有效高度" +
                                                        $"（匹配{mergeMatched3D}个但高度全0或全匹配失败，疑似 3D 图像源/VM 异常），已记录板末延迟重扫");
                                        }
                                        // 继续往下：降级落表（H=0/NG），板末重扫成功后替换
                                    }

                                    // ====== 每检测位合并明细日志 ======
                                    if (newResults != null && newResults.Count > 0)
                                    {
                                        int posOK = newResults.Count(p => p.Result == "OK");
                                        int posNG = newResults.Count(p => p.Result == "NG");
                                        Log.Info($"[检测位 {i} 合并明细] " +
                                                 $"2D有效={mergeTotal2D}, " +
                                                 $"匹配3D={mergeMatched3D}, " +
                                                 $"有效高度={mergeValidH3D}, " +
                                                 $"仅2D(无3D)={mergeNoMatch3D}, " +
                                                 $"3D未匹配2D(NG空针跳过)={mergeUnmatched3DSkipped}, " +
                                                 $"合并结果={newResults.Count}针 (OK={posOK}, NG={posNG})");
                                    }

                                    if (newResults != null && newResults.Count > 0)
                                    {
                                        // UI线程更新检测结果列表 + 统计（UpdateStatistics 枚举 PinResults，必须在 UI 线程与 Add 同批，
                                        // 否则与流程线程枚举竞态——2026-08-25 实机定位 A/B+C 段 +180/+240ms）
                                        // 2026-08-25 延迟重扫：重扫模式先删该位旧降级结果再插新（板末重扫成功后替换）
                                        Application.Current.Dispatcher.InvokeAsync(() =>
                                        {
                                            if (_rescanPhase)
                                            {
                                                var stale = PinResults.Where(p => p.GridIndex == pos.GrabIndex).ToList();
                                                foreach (var s in stale) PinResults.Remove(s);
                                            }
                                            foreach (var pin in newResults)
                                                PinResults.Add(pin);
                                            UpdateStatistics();
                                        });

                                        // 更新当前检测位的叠加层高度数据（数据对象属性赋值，无需 UI 线程；
                                        // 放流程上下文执行，保证下方异步刷新的快照拍到最终 H/IsOK/PinId）
                                        int grabIdx = pos.GrabIndex;
                                        foreach (var overlay in _vmOverlays.Where(o => o.GridIndex == grabIdx))
                                        {
                                            var r = MatchOverlayPinResult(newResults, overlay);
                                            if (r != null) { overlay.H = r.H; overlay.IsOK = r.Result == "OK"; overlay.PinId = r.PINID; }
                                        }
                                    }

                                    // 检测位完成后刷新叠加层显示（VM）
                                    // 2026-08-21：改为异步刷新（fire-and-forget），检测流程不等渲染完成直接推进 NextPos；
                                    // 渲染由 UI 线程在流程 await 间隙执行，本帧按快照绘制（见 RefreshOverlayDisplayAsync）
                                    if (_vmOverlays.Count > 0)
                                    {
                                        RefreshOverlayDisplayAsync();
                                    }
                                }
                                else
                                {
                                    // ---------- 3D超时 → 纯2D结果 ----------
                                    var validPins2D = rawPins2D?.Where(p => !p.IsEmptyPin).ToList();
                                    // 3D数据超时（扫描成功但未收到数据）同为3D数据异常 → 延迟重扫（2026-08-25；
                                    // 2026-08-21 即时重扫会打断在途 SCAN3D 致数据串位，改为板末统一处理）
                                    if (scan3DNoData && validPins2D != null && validPins2D.Count > 0)
                                    {
                                        if (_rescanPhase)
                                        {
                                            _rescanRetries++;
                                            Log.Warning($"检测位 {i} 重扫第 {_rescanRetries} 次仍 3D数据超时无有效高度（扫描成功但未收到数据），" +
                                                        $"{(_rescanRetries < max3DRescanRetry ? "重拍重来" : "达到上限，接受降级结果")}");
                                            if (_rescanRetries < max3DRescanRetry)
                                            {
                                                _3dScanTask = null;
                                                swPos.Restart();
                                                bizState = CheckState.GrabTile;
                                                break;
                                            }
                                            // 达到上限：接受降级结果（继续往下落表）
                                        }
                                        else
                                        {
                                            // 2026-08-25 修复：加 Contains 防重（同 L1414，防跨路径重复入队）
                                            if (!_rescanQueue.Contains(i)) _rescanQueue.Add(i);
                                            retry3DPos++;
                                            Log.Warning($"检测位 {i} 3D数据超时无有效高度（扫描成功但未收到数据），已记录板末延迟重扫");
                                        }
                                    }
                                    if (validPins2D != null && validPins2D.Count > 0)
                                    {
                                        newResults = new List<PinResult>();
                                        foreach (var pin2D in validPins2D)
                                        {
                                            // 按针型读取检测参数
                                            string pt3 = pin2D.PinType ?? "A";
                                            var pParam3 = project.PinTypeParams?.FirstOrDefault(p => p.Label == pt3);
                                            double xyTol3 = pParam3?.XyTolerance ?? defaultXyTol;
                                            double iH3 = pParam3?.IdealHeight ?? defaultIdealH;
                                            double hTol3 = pParam3?.HeightTolerance ?? defaultHTol;

                                            bool xOk = Math.Abs(pin2D.Dx) <= xyTol3;
                                            bool yOk = Math.Abs(pin2D.Dy) <= xyTol3;
                                            bool hOk = Math.Abs(0 - iH3) <= hTol3;
                                            string verdict = pin2D.IsCrookedPin ? "NG" : ((xOk && yOk && hOk) ? "OK" : "NG");

                                            newResults.Add(new PinResult
                                            {
                                                PINID = globalPinId++,
                                                DetectIndex = i,
                                                GridIndex = pos.GrabIndex,
                                                PinIndex = pin2D.PinIndex,
                                                PinType = pt3,
                                                DiffX = (pin2D.IsEmptyPin || pin2D.IsCrookedPin) ? "NAN" : pin2D.Dx.ToString("F3"),
                                                DiffY = (pin2D.IsEmptyPin || pin2D.IsCrookedPin) ? "NAN" : pin2D.Dy.ToString("F3"),
                                                Result = verdict,
                                                X = pin2D.X,
                                                Y = pin2D.Y,
                                                H = 0,
                                                PadX = pin2D.PadX,
                                                PadY = pin2D.PadY,
                                                IsEmptyPin = pin2D.IsEmptyPin,
                                                IsCrookedPin = pin2D.IsCrookedPin
                                            });
                                        }
                                        Log.Warning($"检测位 {i} 3D超时，仅显示2D结果 ({validPins2D.Count}个针)");
                                    }

                                    // 结果显示
                                    if (newResults != null && newResults.Count > 0)
                                    {
                                        // 2026-08-25 异步化：不 await（Dispatcher 队列 FIFO 保序，防 UI 渲染阻塞轴）；
                                        // UpdateStatistics 同批移入（枚举 PinResults 防跨线程竞态）
                                        // 2026-08-25 延迟重扫：重扫模式先删该位旧降级结果再插新
                                        Application.Current.Dispatcher.InvokeAsync(() =>
                                        {
                                            if (_rescanPhase)
                                            {
                                                var stale = PinResults.Where(p => p.GridIndex == pos.GrabIndex).ToList();
                                                foreach (var s in stale) PinResults.Remove(s);
                                            }
                                            foreach (var pin in newResults)
                                                PinResults.Add(pin);

                                            // 更新叠加层 PINID
                                            int grabIdx = pos.GrabIndex;
                                            foreach (var overlay in _vmOverlays.Where(o => o.GridIndex == grabIdx))
                                            {
                                                var r = MatchOverlayPinResult(newResults, overlay);
                                                if (r != null) { overlay.H = r.H; overlay.IsOK = r.Result == "OK"; overlay.PinId = r.PINID; }
                                            }
                                            UpdateStatistics();
                                        });
                                    }
                                }
                                bizState = CheckState.NextPos;
                                break;
                            }

                        case CheckState.NextPos:
                            // 游标推进：整拍完成才 ++（暂停恢复从当前位重做依赖此点）。
                            // 2026-08-25 优化：① 末位先经 MergePending 补最后一位的挂起 Merge；② _skipAForNext 时跳过已拍的 A 段（不重拍）
                            // 不加取消检查——取消在 Merge 中途到来时现状是"合并跑完→下循环顶①拦截"，保持收尾文案计数正确
                            // 2026-08-25 延迟重扫：重扫模式不 i++，直接取队列下一个不合格位；队列空 → 结束重扫 → End
                            if (_rescanPhase)
                            {
                                if (_rescanQueue.Count > 0)
                                {
                                    i = _rescanQueue[0];
                                    _rescanQueue.RemoveAt(0);
                                    _rescanRetries = 0;
                                    Log.Info($"检测位 {i} 板末重扫（队列剩余 {_rescanQueue.Count} 位）");
                                    bizState = CheckState.CapturePos;
                                }
                                else
                                {
                                    _rescanPhase = false;
                                    Log.Info("板末重扫完成，队列已空");
                                    bizState = CheckState.End;
                                }
                                break;
                            }
                            i++;
                            if (i >= totalPositions) { bizState = CheckState.MergePending; break; }
                            if (_skipAForNext)
                            {
                                _skipAForNext = false;
                                // 恢复当前检测位上下文（CapturePos/GrabTile 已跳过，pos/行程变量还停留在 MergePending 恢复的上一位值；
                                // 否则 Start2D 会用错 PinTypes 构造 DETECT2D_BLOB → VM 无响应 → 2D 超时。2026-08-25 实机定位）
                                pos = DetectPositions[i];
                                detectX = pos.X; detectY = pos.Y;
                                scanHalfWidth = _sysParam.Data.Scan3DStroke / 2.0;
                                startX = detectX - scanHalfWidth - offsetX;
                                endX = detectX + scanHalfWidth - offsetX;
                                moveY = detectY - offsetY;
                                rawPins2D = _curRawPins2D;   // 恢复本位(i+1) 的 2D 结果（Wait2D 已解析；MergePending 覆盖流程变量前保存，2026-08-25）
                                if (_rescanFlag)
                                {
                                    // 本拍 Merge 触发过重扫：本位的 2D/3D 已被重扫流程作废（_vmDetectTask/_3dScanTask 置 null），
                                    // 需重新发 2D+3D（A 段图已拍不重拍；重扫的是上一位，本位图未动）
                                    _rescanFlag = false;
                                    bizState = CheckState.Start2D;
                                }
                                else
                                {
                                    // 正常流：本位 2D 已检、3D 扫描已启动，直接等扫描完成（Merge 已并行做完）
                                    bizState = CheckState.WaitScan3D;
                                }
                                break;
                            }
                            bizState = CheckState.CapturePos;
                            break;

                        case CheckState.End:
                            // 2026-08-25 延迟重扫：主流程结束（或重扫队列推进到此）——若有不合格位且未在重扫模式，
                            // 进入板末统一重扫（重扫模式内严格串行，不再打断主流程在途 SCAN3D）
                            Log.Info($"[延迟重扫] End 收尾检查: _rescanPhase={_rescanPhase}, 队列Count={_rescanQueue.Count}, i={i}, total={totalPositions}");
                            if (!_rescanPhase && _rescanQueue.Count > 0)
                            {
                                _rescanPhase = true;
                                i = _rescanQueue[0];
                                _rescanQueue.RemoveAt(0);
                                _rescanRetries = 0;
                                Log.Info($"主流程完成，进入板末统一重扫：检测位 {i}（队列剩余 {_rescanQueue.Count} 位）");
                                bizState = CheckState.CapturePos;
                                break;
                            }
                            // 2026-08-25：等 Dispatcher 队列清空（未执行的落表/清结果任务跑完），再进入循环外收尾统计，
                            // 防 PinResults(ObservableCollection) 跨线程枚举竞态（流程线程 vs UI 线程 Add/Remove）
                            await Application.Current.Dispatcher.InvokeAsync(() => { });
                            _endHandled = true;   // 2026-08-25 End 收尾完成（含重扫入口判断/Dispatcher 等待），BizStep 此时才返回 true（原 L1595 同轮返回导致 End 本体永不执行）
                            break;   // 终态标记：循环后统一收尾（去重/保存/汇总）

                        case CheckState.Err:
                            break;   // 静默终态：弹窗/红灯/文案在循环后统一处理
                    }
                }
                catch (OperationCanceledException)
                {
                    // 取消中断统一处理：TCP/串口等待被 token 取消抛 OCE（复位/停止/报警）→ 立即转壳层 Terminated（报警时 Err 化，不弹窗）。
                    // 若无此 catch，OCE 会冒泡到方法级 catch(Exception) 误弹"检测失败"。
                    MarkAlarmCancel();
                    flowState = FlowRunState.Terminated;
                }
                catch (Exception ex)
                {
                    // 2026-08-25 防御：业务层未预期异常（如 2D 超时+3D 正常时 validPins2D NRE）不得静默死亡——
                    // 原实现无此 catch，NRE 冒泡到外壳被吞，流程线程死亡且无日志（19:26 轮位6 2D超时→Merge NRE，看似"卡住"4.5 分钟）。
                    // 现记录异常 + 转 Err 收尾（循环外统一保存部分结果），日志可见、可恢复。
                    Log.Error($"检测流程异常（已转Err收尾）: {ex}");
                    err = "检测流程内部异常";
                    bizState = CheckState.Err;
                }
                // 2026-08-25 修复：bizState==End 还需 _endHandled（End case 已执行收尾）才返回 true——原判定在同轮
                // L896 设 End 即返回，FlowLoopAsync 直接退出，End case 本体（重扫入口/Dispatcher 等待/诊断日志）从不执行
                return (bizState == CheckState.End && _endHandled) || bizState == CheckState.Err
                    || flowState == FlowRunState.Terminated;
            }

            // ===== 壳层单步：生命周期 + 暂停等待；驱动业务层 =====
            async Task<bool> RunStep()
            {
                switch (flowState)
                {
                    case FlowRunState.Init:
                        // 壳层初始化：具体准备在 CheckState.Init → 直接进入运行
                        flowState = FlowRunState.Running;
                        break;

                    case FlowRunState.Running:
                        if (token.IsCancellationRequested) { MarkAlarmCancel(); flowState = FlowRunState.Terminated; break; }
                        // 业务层单步：返回 true=到达终态（End/Err/业务内取消）→ 壳层收束
                        if (await BizStep()) flowState = FlowRunState.Terminated;
                        break;

                    case FlowRunState.Paused:
                        PauseInspectTimer();   // ⏱ 检测耗时：暂停计时（保留当前累计值，恢复后继续累加，2026-08-27）
                        // 暂停等待（仿 CheckPauseAndCancelAsync/全图采集壳层）：复位/停止/报警先 Cancel 再 Set → 等待立即退出。
                        // 状态栏不刷文字（暂停/恢复反馈由按钮点击时一次性提示，恢复后业务层自然显示检测中）
                        while (!_pauseCheckEvent.IsSet)
                        {
                            try { await Task.Delay(200, token); }
                            catch (OperationCanceledException) { MarkAlarmCancel(); flowState = FlowRunState.Terminated; break; }
                        }
                        if (flowState == FlowRunState.Terminated) break;
                        // 恢复前等在途后台任务（暂停中断时留下的拍照/2D检测）收尾（最多3秒），防恢复后整拍重做与其并发写同一图片/TCP错配（2026-08-19）
                        if (pendingPauseTask != null)
                        {
                            try { await Task.WhenAny(pendingPauseTask, Task.Delay(3000, token)); } catch { }
                            pendingPauseTask = null;
                        }
                        ResumeInspectTimer();   // ⏱ 检测耗时：恢复计时（2026-08-27）
                        flowState = FlowRunState.Running;
                        break;

                    case FlowRunState.Terminated:
                        break;   // 静默终态（结果判定在循环外）
                }
                return flowState == FlowRunState.Terminated;
            }

            try
            {
                await FlowLoopAsync(RunStep);

                // ===== 2026-08-25 延迟重扫兜底（循环外统一入口，双保险）=====
                // 主流程结束（End 已退出循环）后，若队列仍有不合格位（End case 重扫入口未触发的情况），
                // 在此进入重扫模式再驱动一轮状态机；End case 已消费队列时此处 Count==0 自动跳过。
                // 重扫模式内部：NextPos 取队列下一个 / 队列空 → _rescanPhase=false → End → 再次退出循环。
                if (_rescanQueue.Count > 0 && !_alarmStopping && bizState != CheckState.Err)
                {
                    _rescanPhase = true;
                    i = _rescanQueue[0];
                    _rescanQueue.RemoveAt(0);
                    _rescanRetries = 0;
                    Log.Info($"[延迟重扫] 主流程结束，循环外进入板末重扫：检测位 {i}（队列剩余 {_rescanQueue.Count} 位）");
                    bizState = CheckState.CapturePos;
                    flowState = FlowRunState.Running;   // 关键：主流程结束时 flowState 已是 Terminated，不重置 RunStep 直接走 Terminated 分支退出（重扫不执行，实机验证 18:23:19 日志"未正常完成"）
                    await FlowLoopAsync(RunStep);
                }

                // ===== 终态收尾（循环外统一：中途终止/报错同样执行去重+保存=部分结果落盘，保持原"break 后走完整收尾"行为）=====

                // 2026-08-25 主题② C：孤儿图清扫（此时主流程+板末重扫全部 GRAB 已结束，无在途拍照）——
                // 正常流 GRAB 临时文件替换目标后即删；残留的 GRAB_OK_* 且不在瓦片索引内的文件=泄漏孤儿
                // （"存图了未感知"超时/停止重启场景的旧图），防瓦片排序错位与 ValidateImageCount 容错误删
                CleanupOrphanGrabbedFiles();

                // 全局去重：PadX/PadY相近的针保留一个（优先保留有3D高度的）
                if (PinResults.Count > 0)
                {
                    const double dedupThreshold = 0.5; // mm，焊盘坐标差小于此值视为同一针
                    var dedupKeep = new List<PinResult>();
                    var dedupRemove = new List<PinResult>();
                    foreach (var pin in PinResults)
                    {
                        if (dedupRemove.Contains(pin)) continue;
                        var dup = PinResults.FirstOrDefault(p =>
                            p != pin && !dedupRemove.Contains(p) &&
                            Math.Abs(p.PadX - pin.PadX) < dedupThreshold &&
                            Math.Abs(p.PadY - pin.PadY) < dedupThreshold);
                        if (dup != null)
                        {
                            // 保留有3D高度的，都有的保留第一个
                            if (pin.H > 0 && dup.H == 0) dedupRemove.Add(dup);
                            else if (dup.H > 0 && pin.H == 0) { dedupRemove.Add(pin); dedupKeep.Add(dup); continue; }
                            else dedupRemove.Add(dup); // 都无高度或都有高度，保留第一个
                        }
                        dedupKeep.Add(pin);
                    }
                    int dupCount = dedupRemove.Count;
                    if (dupCount > 0)
                    {
                        var removePinIDs = dedupRemove.Select(d => d.PINID).ToList();
                        Log.Info($"[去重] 移除{dupCount}个重复针 (阈值={dedupThreshold}mm): " +
                            string.Join(", ", removePinIDs.Select(id => $"PINID={id}")));
                        // UI线程更新PinResults和叠加层显示
                        await Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            foreach (var r in dedupRemove) PinResults.Remove(r);
                            // 只移除与被删PinResult同GridIndex且PadX/PadY相近的叠加层
                            _vmOverlays.RemoveAll(o => dedupRemove.Any(r =>
                                o.GridIndex == r.GridIndex &&
                                Math.Abs(o.PadMmX - r.PadX) < dedupThreshold &&
                                Math.Abs(o.PadMmY - r.PadY) < dedupThreshold));
                            try
                            {
                                var hww = _vision.HalconWindow;
                                if (hww != null)
                                {
                                    var si = _imageManager.GetCurrentStitchedImage();
                                    if (si != null && si.IsInitialized())
                                    {
                                        HOperatorSet.ClearWindow(hww);
                                        HOperatorSet.DispObj(si, hww);
                                        if (_vmOverlays.Count > 0) { DrawVmRects(hww); DrawVmText(hww); }
                                        si.Dispose();
                                        _vision.RefreshDisplay();
                                    }
                                }
                            }
                            catch { }
                        });
                    }

                    // 叠加层自身去重（处理空针等不在PinResults中的重复项）
                    // 必须独立执行，因为空针不在PinResults中，PinResults去重不会触发叠加层去重
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        int overlayBefore = _vmOverlays.Count;
                        for (int oi = _vmOverlays.Count - 1; oi >= 0; oi--)
                        {
                            for (int oj = 0; oj < oi; oj++)
                            {
                                var a = _vmOverlays[oi];
                                var b = _vmOverlays[oj];
                                if (Math.Abs(a.PadMmX - b.PadMmX) < dedupThreshold &&
                                    Math.Abs(a.PadMmY - b.PadMmY) < dedupThreshold)
                                {
                                    // 优先保留正常针 > 歪针 > 空针，其次保留有高度的
                                    int priority(VmOverlayItem o) => o.IsEmptyPin ? 0 : o.IsCrookedPin ? 1 : 2;
                                    if (priority(a) > priority(b)) _vmOverlays.RemoveAt(oi);
                                    else if (priority(b) > priority(a)) _vmOverlays.RemoveAt(oj);
                                    else if (a.H > 0 && b.H == 0) _vmOverlays.RemoveAt(oj);
                                    else _vmOverlays.RemoveAt(oi);
                                    break;
                                }
                            }
                        }
                        if (_vmOverlays.Count < overlayBefore)
                        {
                            try
                            {
                                var hww = _vision.HalconWindow;
                                if (hww != null)
                                {
                                    var si = _imageManager.GetCurrentStitchedImage();
                                    if (si != null && si.IsInitialized())
                                    {
                                        HOperatorSet.ClearWindow(hww);
                                        HOperatorSet.DispObj(si, hww);
                                        if (_vmOverlays.Count > 0) { DrawVmRects(hww); DrawVmText(hww); }
                                        si.Dispose();
                                        _vision.RefreshDisplay();
                                    }
                                }
                            }
                            catch { }
                        }
                    });
                }

                //UpdateStatus("检测完成，机器回零中...");
                //await _motionIO.HomeXY(10000);

                // 保存 + 汇总（非精度模式）
                if (!_isPrecisionMode)
                {
                    SavePinResults();
                    SaveVmOverlays();
                    // 同时将检测位配置保存到历史结果目录
                    SaveDetectPositionsToHistory();
                }
                SyncPinIdealHeight();
                UpdateStatistics();

                // 状态栏三分支（按业务终态区分：完成/异常/终止）
                // 2026-08-27 Bug修复：显示"共 N 个针"应为全局去重后的针数（PinResults.Count），
                // 原实现用 globalPinId-1（最后一个自增 PINID），去重后与实际针数不符
                if (bizState == CheckState.End)
                {
                    int finalPinCount = PinResults.Count;   // 去重后总数（去重 Dispatcher 已 await，Count 已生效）
                    UpdateStatus($"检测完成，共 {finalPinCount} 个针，合格率: {YieldRate:F1}%");
                }
                else if (bizState == CheckState.Err)
                    UpdateStatus($"检测异常: {err}，已保存已检测位部分结果");
                else
                    UpdateStatus($"检测已终止（复位/停止/取消），已检测 {i} 个检测位，部分结果已保存");

                // ====== 全检测流总汇总 ======
                if (bizState == CheckState.End)
                {
                    int totalPins = PinResults.Count;
                    int hasHeight = PinResults.Count(p => p.H > 0);
                    int noHeight = PinResults.Count(p => p.H == 0);
                    int totalOK = PinResults.Count(p => p.Result == "OK");
                    int totalNG = PinResults.Count(p => p.Result == "NG");
                    Log.Info($"========== 检测汇总 ==========");
                    Log.Info($"总针脚数: {totalPins}");
                    Log.Info($"2D+3D完整数据(含高度): {hasHeight} | 无3D高度数据: {noHeight}");
                    Log.Info($"检测位数量: {totalPositions}");
                    Log.Info($"OK: {totalOK} | NG: {totalNG} | 合格率: {(totalPins > 0 ? 100.0 * totalOK / totalPins : 0):F1}%");
                    Log.Info($"==============================");
                }
                else
                {
                    Log.Warning($"[Check] 流程未正常完成（{err ?? "终止"}），已保存部分结果: {PinResults.Count}针");
                }

                // 保存针尖图片路径映射（查看 ABC 功能重启后可用）
                Log.Info($"[PinMap] 检测完成，_pinImageMap 共 {_pinImageMap.Count} 项");
                SavePinImageMap(ProjectManager.Instance.CurrentProjectPath);

                // 报错收尾：红灯+弹窗（报警终止不重复弹窗——DeviceMonitor 报警弹窗已有）
                if (bizState == CheckState.Err)
                {
                    Log.Error($"[Check] 检测异常: {err}");
                    if (!_alarmStopping)
                        MessageBox.Show(Application.Current.MainWindow, $"{err}，检测终止", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }

            catch (OperationCanceledException)
            {
                // 复位/停止/报警取消：静默终止（不弹"检测失败"框）；流程标志由 finally 清理（2026-08-14）
                return false;
            }
            catch (Exception ex)
            {
                Log.Error($"检测异常: {ex.Message}");
                MessageBox.Show(Application.Current.MainWindow, $"检测失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
            finally
            {
                _pauseCheckEvent.Set();          // 恢复运行状态，避免下次进入仍为暂停
                PauseStateChanged?.Invoke();     // 通知UI更新按钮文字
                isWorkFlowRun[WorkFlowType.Check] = false;
                _checkFlowCompleted.Set();       // 通知等待方检测已完成
                _checkCts?.Dispose();
                _checkCts = null;
                // ⏱ 检测耗时收尾（2026-08-27）：正常结束(End)保留本次耗时；停止/复位/报警/异常等中断统一重置为0
                StopInspectTimer(bizState == CheckState.End);
            }
            return true;
        }
        public void StopCheckFlow()
        {
            //if (!isWorkFlowRun[WorkFlowType.Check]) return;
            _checkCts?.Cancel();
            _pauseCheckEvent.Set();                  // 恢复运行状态（可能处于暂停中）
            PauseStateChanged?.Invoke();
            isWorkFlowRun[WorkFlowType.Check] = false;
            UpdateStatus("检测已停止");
        }

        // ==================== 检测耗时计时（2026-08-27） ====================

        /// <summary>检测耗时：重置并开始计时（检测流程 Init case 调用；连续模式每板重新计时）</summary>
        private void StartInspectTimer()
        {
            _inspectSw.Restart();
            InspectElapsed = TimeSpan.Zero;
            _inspectTimer?.Start();
        }

        /// <summary>检测耗时：暂停计时（壳层 Paused 进入时；保留当前累计值，恢复后继续累加）</summary>
        private void PauseInspectTimer()
        {
            _inspectSw.Stop();
            _inspectTimer?.Stop();
        }

        /// <summary>检测耗时：恢复计时（壳层 Paused 退出时；防重复恢复）</summary>
        private void ResumeInspectTimer()
        {
            if (_inspectSw.IsRunning) return;   // 防重复恢复（边界场景兜底）
            _inspectSw.Start();
            _inspectTimer?.Start();
        }

        /// <summary>检测耗时：停止并收尾（流程 finally 统一调用；keepElapsed=false → 停止/复位/报警/异常中断，重置为0）</summary>
        private void StopInspectTimer(bool keepElapsed)
        {
            _inspectSw.Stop();
            _inspectTimer?.Stop();
            if (!keepElapsed) InspectElapsed = TimeSpan.Zero;
        }

        /// <summary>
        /// 3D扫描带重试（总尝试2次）：第一次用 Scan3DTimeout（正常扫描运动时长+余量），
        /// 失败后延迟200ms重试，第二次用 Scan3DRetryTimeout（轴已到位：假失败瞬间过，真失败短超时快速判定）。
        /// 不传 token（扫描一旦开始必须扫完，CAMERA3D_ON/OFF 信号完整性）；取消由外层 WaitOrCancelAsync 兜底。
        /// </summary>
        private async Task<bool> Scan3DWithRetryAsync(double endX, double moveY)
        {
            if (await _motionIO.Scan3DToAndWaitOK(x: endX, y: moveY, timeoutMs: (int)_sysParam.Data.Scan3DTimeout))
                return true;
            Log.Warning($"3D扫描第一次尝试失败，{_sysParam.Data.Scan3DRetryTimeout}ms 短超时重试");
            await Task.Delay(200);
            return await _motionIO.Scan3DToAndWaitOK(x: endX, y: moveY, timeoutMs: (int)_sysParam.Data.Scan3DRetryTimeout);
        }

        /// <summary>
        /// 暂停感知等待：任务完成 / token 取消 / 暂停请求时返回（暂停时立即返回 default，不等在途任务）。
        /// 调用方 await 后必须检查暂停标志（默认 _pauseCheckEvent.IsSet==false）转 Paused（否则按失败处理会误转 Err）。
        /// 在途任务不打断（后台继续执行，恢复后从当前位整拍重做；Paused 恢复前等待其收尾防并发写同一图片）。
        /// 3D 扫描（Scan3DToAndWaitOK）不用本方法——扫描一旦开始必须扫完（CAMERA3D_ON/OFF 信号与数据完整性）。
        /// isPaused 可注入其它暂停信号（如全图采集的 _pauseGrabEvent）。
        /// </summary>
        private async Task<T> PauseAwareWaitAsync<T>(Task<T> task, CancellationToken token, Func<bool> isPaused = null)
        {
            if (isPaused == null) isPaused = () => !_pauseCheckEvent.IsSet;
            if (token.IsCancellationRequested) return default;
            if (isPaused()) return default;   // 已暂停：立即返回，调用方转 Paused
            // 2026-08-24 优化：循环改 WhenAny 三路竞争——任务完成立即返回（原实现每轮 Task.Delay(50) 尾差平均 25ms/次，
            // 单检测位 6 个调用点合计 ~150ms）；暂停/取消检查仍每 50ms 一次（语义不变：≤50ms 响应暂停、取消立即返回 default）
            while (true)
            {
                if (task.IsCompleted) return await task;   // 已完成：立即返回（零尾差）
                if (token.IsCancellationRequested) return default;
                if (isPaused()) return default;   // 暂停被按下：中断等待（50ms 内响应）
                if (await Task.WhenAny(task, Task.Delay(50)) == task) return await task;
            }
        }
        /// <summary>
        /// 解析3D相机检测结果（高度+坐标）
        /// 格式1（新 - 3D轮廓仪物理坐标）:
        ///   DETECT_3D_OK:(pixel_x&pixel_y&pixel_z)...(phys_x&phys_y&phys_z)...
        ///   前半=像素坐标，后半=物理坐标（µm）；取物理坐标做转置映射:
        ///     board_X = -phys_y/1000  传感器方向→板面X(mm)
        ///     board_Y =  phys_x/1000  扫描方向→板面Y(mm)
        ///     H      =  phys_z/1000  深度→高度(mm)
        /// 格式2（旧 - 逗号分隔）:
        ///   DETECT_3D_OK:pinIdx,relXmm,relYmm,height;...  （带坐标，向后兼容）
        ///   DETECT_3D_OK:pinIdx,height;...                 （无坐标，向后兼容）
        /// </summary>
        private List<(int PinIndex, double X, double Y, double H)> ParseDetect3DResponse(string response)
        {
            var results = new List<(int, double, double, double)>();
            try
            {
                if (string.IsNullOrWhiteSpace(response)) return results;

                // 去掉前缀（兼容新推送格式 DETECT3D_OK 和旧查询格式 DETECT_3D_OK）
                string dataPart = response;
                if (dataPart.Contains("DETECT3D_OK"))
                {
                    int idx = dataPart.IndexOf("DETECT3D_OK");
                    dataPart = dataPart.Substring(idx + "DETECT3D_OK".Length);
                }
                else if (dataPart.Contains("DETECT_3D_OK"))
                {
                    int idx = dataPart.IndexOf("DETECT_3D_OK");
                    dataPart = dataPart.Substring(idx + "DETECT_3D_OK".Length);
                }
                // 去掉可能紧跟的冒号
                if (dataPart.StartsWith(":"))
                    dataPart = dataPart.Substring(1);

                // 判断格式：新格式用 () 分组，旧格式用逗号/分号
                if (dataPart.Contains('(') && dataPart.Contains(')'))
                {
                    // ===== 新格式: (x&y&z)(x&y&z)...(phys_x&phys_y&phys_z)... =====
                    var allTuples = Extract3DTuples(dataPart);
                    if (allTuples.Count == 0 || allTuples.Count % 2 != 0)
                    {
                        Log.Warning($"3D数据解析: tuple数为{allTuples.Count}，非偶数，跳过");
                        return results;
                    }

                    int pinCount = allTuples.Count / 2;
                    // 只取物理坐标（后半部分）
                    for (int i = 0; i < pinCount; i++)
                    {
                        var phys = allTuples[pinCount + i];

                        // 转置映射:
                        //   扫描方向(phys_x) → 板面 Y
                        //   传感器方向(phys_y) → 板面 X
                        double x = -phys.y / 1000.0;  // phys_y(µm) → 板面X(mm)
                        double y = -phys.x / 1000.0;   // phys_x(µm) → 板面Y(mm)（负号：扫描方向与板面Y方向相反）
                        double h = phys.z / 1000.0;   // phys_z(µm) → 高度(mm)

                        results.Add((i, x, y, h));
                    }
                }
                else if (dataPart.Contains(','))
                {
                    // ===== 旧格式: pinIdx,relXmm,relYmm,height;... 或 pinIdx,height;... =====
                    var entries = dataPart.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var entry in entries)
                    {
                        if (string.IsNullOrWhiteSpace(entry)) continue;
                        var parts = entry.Split(',');
                        if (parts.Length < 2) continue;

                        int pinIdx = int.Parse(parts[0]);
                        double x = 0, y = 0, h;

                        if (parts.Length >= 4)
                        {
                            // 带坐标: pinIdx,relXmm,relYmm,height
                            x = double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
                            y = double.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);
                            h = double.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture);
                        }
                        else
                        {
                            // 无坐标: pinIdx,height
                            h = double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
                        }

                        results.Add((pinIdx, x, y, h));
                    }
                }
                else
                {
                    Log.Warning($"3D数据格式无法识别: {(dataPart.Length > 100 ? dataPart.Substring(0, 100) + "..." : dataPart)}");
                }
            }
            catch (Exception ex)
            {
                Log.Error($"解析3D检测数据失败: {ex.Message}, 原始数据: {response}");
            }
            return results;
        }

        /// <summary>
        /// 从字符串中提取所有 (x&y&z) 三元组
        /// </summary>
        private List<(double x, double y, double z)> Extract3DTuples(string data)
        {
            var list = new List<(double, double, double)>();
            var regex = new Regex(@"\(([^)]+)\)");
            var matches = regex.Matches(data);
            foreach (Match match in matches)
            {
                var parts = match.Groups[1].Value.Split('&');
                if (parts.Length >= 3)
                {
                    double x = double.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
                    double y = double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
                    double z = double.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);
                    list.Add((x, y, z));
                }
            }
            return list;
        }

        /// <summary>
        /// 解析VM 2D检测结果
        /// 格式: DETECT2D_RECT_OK:{PadX}&{PadY}&{HasPin}&{PinX}&{PinY};...
        /// HasPin=0 → 该焊盘无针脚（空针），PinX/PinY 数据忽略，结果直接 NG
        /// HasPin=1 → 有针脚，PinX/PinY 为实际针脚坐标，正常判定
        /// 坐标单位为像素（px），上位机通过 CameraPixelEquivalent 换算为 mm
        /// </summary>
        private List<Pin2DData> ParseDetect2DResponse(string response)
        {
            var results = new List<Pin2DData>();
            try
            {
                if (!response.StartsWith("DETECT2D_RECT_OK")) return results;

                string dataPart = response.Substring("DETECT2D_RECT_OK".Length).Trim();
                if (dataPart.StartsWith(":"))
                    dataPart = dataPart.Substring(1).Trim();

                // 统一分隔符：全角→半角
                dataPart = dataPart.Replace('；', ';');

                // 记录分隔: 新格式为 \r\n, 旧格式为 ;  (向后兼容)
                string[] entries;
                if (dataPart.Contains("\n"))
                    entries = dataPart.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                else
                    entries = dataPart.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                int index = 0;
                var ci = System.Globalization.CultureInfo.InvariantCulture;

                foreach (var entry in entries)
                {
                    string line = entry.Trim().TrimEnd('&');
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    var parts = line.Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 3) continue;

                    var pin = new Pin2DData { PinIndex = index++ };
                    pin.PadX = Math.Round(double.Parse(parts[0], ci), 3);
                    pin.PadY = Math.Round(double.Parse(parts[1], ci), 3);

                    if (parts.Length >= 11)
                    {
                        // 新11字段格式（有针数据）
                        pin.PadW = Math.Round(double.Parse(parts[2], ci), 3);
                        pin.PadH = Math.Round(double.Parse(parts[3], ci), 3);
                        pin.PadAng = Math.Round(double.Parse(parts[4], ci), 3);
                        int hasPin = int.Parse(parts[5]);
                        pin.IsEmptyPin = (hasPin == 0);
                        if (!pin.IsEmptyPin)
                        {
                            pin.X = Math.Round(double.Parse(parts[6], ci), 3);
                            pin.Y = Math.Round(double.Parse(parts[7], ci), 3);
                            pin.PinW = Math.Round(double.Parse(parts[8], ci), 3);
                            pin.PinH = Math.Round(double.Parse(parts[9], ci), 3);
                            pin.PinAng = Math.Round(double.Parse(parts[10], ci), 3);
                            pin.Dx = Math.Round(pin.X - pin.PadX, 3);
                            pin.Dy = Math.Round(pin.Y - pin.PadY, 3);
                        }
                    }
                    else if (parts.Length >= 6)
                    {
                        // 新6字段格式（空针，带匹配框尺寸）
                        pin.PadW = Math.Round(double.Parse(parts[2], ci), 3);
                        pin.PadH = Math.Round(double.Parse(parts[3], ci), 3);
                        pin.PadAng = Math.Round(double.Parse(parts[4], ci), 3);
                        pin.IsEmptyPin = true;  // 6字段=HasPin=0
                    }
                    else
                    {
                        // 旧5字段格式: {PadX}&{PadY}&{HasPin}&{PinX}&{PinY}
                        int hasPin = int.Parse(parts[2]);
                        pin.IsEmptyPin = (hasPin == 0);
                        if (!pin.IsEmptyPin && parts.Length >= 5)
                        {
                            pin.X = Math.Round(double.Parse(parts[3], ci), 3);
                            pin.Y = Math.Round(double.Parse(parts[4], ci), 3);
                            pin.Dx = Math.Round(pin.X - pin.PadX, 3);
                            pin.Dy = Math.Round(pin.Y - pin.PadY, 3);
                        }
                    }

                    results.Add(pin);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"解析VM 2D检测数据失败: {ex.Message}, 原始数据: {response}");
            }
            return results;
        }

        /// <summary>
        /// 解析VM Blob 2D检测结果
        /// 格式: DETECT2D_BLOB_OK:PadX1&PadY1&PadW1&PadH1;PadX2...;...||BlobX1&BlobY1&BlobArea1;BlobX2...;...
        /// 焊盘段和Blob段用 || 分隔，C#端做最近邻匹配，计算DiffArea并判空针
        /// </summary>
        /// <summary>
        /// 解析VM Blob 2D检测结果
        /// 格式: DETECT2D_BLOB_OK:padA:x&y&w&h&ang;...|blankA:x&y&w&h&ang&area;...|pinA:x&y&w&h&ang;...
        /// 用 pad:/blank:/pin: 标签定位三段，不依赖分隔符，支持多针型标记(A/B/C...)
        /// 空针判据: BlobArea >= 焊盘W×H × 阈值（系统参数可调）
        /// 白区段BlobArea为第6字段（可选），无此字段时兼容旧格式用W×H计算
        /// </summary>
        private List<Pin2DData> ParseDetect2DBlobResponse(string response)
        {
            var results = new List<Pin2DData>();
            try
            {
                if (!response.StartsWith("DETECT2D_BLOB_OK")) return results;

                string dataPart = response.Substring("DETECT2D_BLOB_OK".Length).Trim();
                if (dataPart.StartsWith(":"))
                    dataPart = dataPart.Substring(1).Trim();

                // 统一分隔符：全角→半角
                dataPart = dataPart.Replace('；', ';').Replace('｜', '|');

                var ci = System.Globalization.CultureInfo.InvariantCulture;

                // ---- 按标记字母分组（padB/blankB/pinB一组，padC/blankC/pinC一组）----
                var groups = new Dictionary<char, (System.Text.StringBuilder pads,
                    System.Text.StringBuilder blanks, System.Text.StringBuilder pins)>();
                var reSegments = System.Text.RegularExpressions.Regex.Matches(dataPart,
                    @"(pad|blank|pin)([A-Z]?):(.*?)(?=(?:pad|blank|pin)[A-Z]?:|$)",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase
                    | System.Text.RegularExpressions.RegexOptions.Singleline);
                foreach (System.Text.RegularExpressions.Match m in reSegments)
                {
                    string kw = m.Groups[1].Value.ToLower();
                    char marker = m.Groups[2].Success && m.Groups[2].Length > 0 ? m.Groups[2].Value[0] : 'A';
                    string data = m.Groups[3].Value.TrimEnd('|', ';', ' ');
                    if (data.Length == 0) continue;
                    if (!groups.ContainsKey(marker))
                        groups[marker] = (new System.Text.StringBuilder(),
                            new System.Text.StringBuilder(), new System.Text.StringBuilder());
                    var g = groups[marker];
                    if (kw == "pad") { if (g.pads.Length > 0) g.pads.Append(';'); g.pads.Append(data); }
                    else if (kw == "blank") { if (g.blanks.Length > 0) g.blanks.Append(';'); g.blanks.Append(data); }
                    else if (kw == "pin") { if (g.pins.Length > 0) g.pins.Append(';'); g.pins.Append(data); }
                }

                // 没找到标签则回退旧格式
                if (groups.Count == 0)
                {
                    string fallback = dataPart.Split(new[] { "||" }, StringSplitOptions.None)[0];
                    if (!string.IsNullOrEmpty(fallback)) ParseBlobGroup(results, fallback + "||", "A", ci, 0);
                    return results;
                }

                double emptyThreshold = _sysParam.Data.EmptyPinThreshold;
                foreach (char marker in groups.Keys.OrderBy(c => c))
                {
                    var g = groups[marker];
                    string combined = g.pads + "||" + g.blanks + "||" + g.pins;
                    int before = results.Count;
                    ParseBlobGroup(results, combined, marker.ToString(), ci, emptyThreshold);
                    int n = results.Count - before;
                    if (n > 0) Log.Info($"[Blob2D] 标记{marker}: {n}个针");
                }
            }
            catch (Exception ex)
            {
                Log.Error($"解析VM Blob 2D检测数据失败: {ex.Message}, 原始数据: {response}");
            }
            return results;
        }

        /// <summary>解析单组标记的 Blob 数据（pad||blank||pin 格式），结果绑定指定 PinType</summary>
        private void ParseBlobGroup(List<Pin2DData> results, string dataPart,
            string marker, System.Globalization.CultureInfo ci, double emptyThreshold)
        {
            string[] sections = dataPart.Split(new[] { "||" }, StringSplitOptions.None);
            if (sections.Length < 1) return;

            // 焊盘段
            var pads = new List<(double X, double Y, double W, double H, double Ang)>();
            foreach (var e in sections[0].Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var p = e.Trim().Split('&');
                if (p.Length >= 5)
                    pads.Add((double.Parse(p[0], ci), double.Parse(p[1], ci),
                              double.Parse(p[2], ci), double.Parse(p[3], ci), double.Parse(p[4], ci)));
            }
            if (pads.Count == 0) return;

            // 白区段
            var whites = new List<(double X, double Y, double W, double H, double Ang, double Area)>();
            if (sections.Length > 1)
                foreach (var e in sections[1].Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var b = e.Trim().Split('&');
                    if (b.Length >= 5)
                    {
                        double x = double.Parse(b[0], ci), y = double.Parse(b[1], ci);
                        double w = double.Parse(b[2], ci), h = double.Parse(b[3], ci);
                        double ang = double.Parse(b[4], ci);
                        double area = b.Length >= 6 ? double.Parse(b[5], ci) : w * h;
                        whites.Add((x, y, w, h, ang, area));
                    }
                }

            // 黑区段
            var blacks = new List<(double X, double Y, double W, double H, double Ang)>();
            if (sections.Length > 2)
                foreach (var e in sections[2].Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var b = e.Trim().Split('&');
                    if (b.Length >= 5)
                        blacks.Add((double.Parse(b[0], ci), double.Parse(b[1], ci),
                                    double.Parse(b[2], ci), double.Parse(b[3], ci), double.Parse(b[4], ci)));
                }

            double emptyTh = emptyThreshold > 0 ? emptyThreshold : _sysParam.Data.EmptyPinThreshold;

            // 黑区匹配到焊盘
            var matchedBlack = new Dictionary<int, (double X, double Y, double W, double H, double Ang)>();
            var usedBlack = new bool[blacks.Count];
            foreach (var (pad, oi) in pads.Select((p, i) => (p, i)))
            {
                double hw = pad.W / 2, hh = pad.H / 2, bestD = double.MaxValue;
                int bestJ = -1;
                for (int j = 0; j < blacks.Count; j++)
                {
                    if (usedBlack[j]) continue;
                    double dx = Math.Abs(blacks[j].X - pad.X), dy = Math.Abs(blacks[j].Y - pad.Y);
                    if (dx < hw && dy < hh)
                    { double d = dx * dx + dy * dy; if (d < bestD) { bestD = d; bestJ = j; } }
                }
                if (bestJ >= 0) { usedBlack[bestJ] = true; matchedBlack[oi] = blacks[bestJ]; }
            }

            for (int i = 0; i < pads.Count; i++)
            {
                var pad = pads[i];
                double whiteArea = (i < whites.Count) ? whites[i].Area : 0;
                double ratio = (pad.W * pad.H) > 0 ? whiteArea / (pad.W * pad.H) : 0;
                bool isEmptyPin = whiteArea >= (pad.W * pad.H) * emptyTh;
                bool hasBlack = matchedBlack.ContainsKey(i);
                bool isCrooked = !isEmptyPin && !hasBlack;

                double pinX = pad.X, pinY = pad.Y, pinW = 0, pinH = 0, pinAng = 0;
                if (hasBlack) { var bk = matchedBlack[i]; pinX = bk.X; pinY = bk.Y; pinW = bk.W; pinH = bk.H; pinAng = bk.Ang; }

                Log.Debug($"[空针判据-{marker}] Pad#{i} ({pad.W:F1}×{pad.H:F1}) whiteRatio={ratio * 100:F1}% "
                    + $"→ {(isEmptyPin ? "空针" : isCrooked ? "歪针" : "正常针")}");

                results.Add(new Pin2DData
                {
                    PinIndex = i,
                    PinType = marker,
                    PadX = Math.Round(pad.X, 3),
                    PadY = Math.Round(pad.Y, 3),
                    PadW = Math.Round(pad.W, 3),
                    PadH = Math.Round(pad.H, 3),
                    PadAng = Math.Round(pad.Ang, 3),
                    X = Math.Round(isCrooked ? pad.X : pinX, 3),
                    Y = Math.Round(isCrooked ? pad.Y : pinY, 3),
                    Dx = Math.Round(isCrooked ? 0 : pinX - pad.X, 3),
                    Dy = Math.Round(isCrooked ? 0 : pinY - pad.Y, 3),
                    IsEmptyPin = isEmptyPin,
                    IsCrookedPin = isCrooked,
                    PinW = Math.Round(pinW, 3),
                    PinH = Math.Round(pinH, 3),
                    PinAng = Math.Round(pinAng, 3)
                });
            }
        }

        #region ===================================检测动作===================================
        private void UpdateStatistics()
        {
            GoodCount = PinResults.Count(r => r.Result == "OK");
            NgCount = PinResults.Count(r => r.Result == "NG");
            int total = GoodCount + NgCount;
            PassRate = total > 0 ? (double)GoodCount / total * 100 : 0;
            RaisePropertyChanged(nameof(YieldRate));
        }

        /// <summary>
        /// 当用户修改检测参数（允许DX/DY、理想高度等）后重新计算检测结果
        /// </summary>
        private void RecalculatePinResults()
        {
            if (PinResults == null || PinResults.Count == 0) return;

            var project = ProjectManager.Instance.CurrentProject;
            var defaultParams = GetDefaultDetectParams();

            foreach (var pin in PinResults)
            {
                // 按针型读取检测参数
                string pt = pin.PinType ?? "A";
                var pParam = project?.PinTypeParams?.FirstOrDefault(p => p.Label == pt);
                double xyTol = pParam?.XyTolerance ?? defaultParams.xyTol;
                double iH = pParam?.IdealHeight ?? defaultParams.idealH;
                double hTol = pParam?.HeightTolerance ?? defaultParams.hTol;

                pin.SetIdealHeight(iH);

                if (pin.IsEmptyPin)
                {
                    pin.Result = "NG";
                    pin.DiffX = "NAN";
                    pin.DiffY = "NAN";
                    pin.H = 0;
                    continue;
                }

                if (pin.IsCrookedPin)
                {
                    pin.Result = "NG";
                    pin.DiffX = "NAN";
                    pin.DiffY = "NAN";
                    // 歪针保留3D高度用于显示，但结果强制NG
                    continue;
                }

                if (!double.TryParse(pin.DiffX, out double dx)) continue;
                if (!double.TryParse(pin.DiffY, out double dy)) continue;

                bool xOk = Math.Abs(dx) <= xyTol;
                bool yOk = Math.Abs(dy) <= xyTol;
                bool hOk = Math.Abs(pin.H - iH) <= hTol;

                pin.Result = (xOk && yOk && hOk) ? "OK" : "NG";
            }

            UpdateStatistics();
            SavePinResults();
        }

        /// <summary>
        /// 当相对偏移量（IdealOffsetX/Y）改变时，重新计算所有叠加层的理想针位置和 dx/dy，
        /// 同时更新 PinResults 的 DiffX/DiffY 和 Result。
        /// </summary>
        private void RecalculateOffsetDxDy()
        {
            var project = ProjectManager.Instance.CurrentProject;
            var defaultParams = GetDefaultDetectParams();

            // 使用第一个针型的偏移量作为叠加层的基准（VM叠加层没有PinType信息）
            double offsetX = project?.PinTypeParams?.Length > 0 ? project.PinTypeParams[0].IdealOffsetX : defaultParams.offX;
            double offsetY = project?.PinTypeParams?.Length > 0 ? project.PinTypeParams[0].IdealOffsetY : defaultParams.offY;
            double baseXyTol = project?.PinTypeParams?.Length > 0 ? project.PinTypeParams[0].XyTolerance : defaultParams.xyTol;

            // 1. 更新 VM 叠加层
            foreach (var item in _vmOverlays)
            {
                if (item.IsEmptyPin) continue;
                double idealX = item.PadMmX + offsetX;
                double idealY = item.PadMmY + offsetY;
                item.IdealPinMmX = Math.Round(idealX, 3);
                item.IdealPinMmY = Math.Round(idealY, 3);
                item.Dx = Math.Round(item.PinMmX - idealX, 3);
                item.Dy = Math.Round(item.PinMmY - idealY, 3);
                item.IsOK = Math.Abs(item.Dx) <= baseXyTol && Math.Abs(item.Dy) <= baseXyTol;
            }

            // 2. 更新 PinResults（按针型读取参数）
            foreach (var pin in PinResults)
            {
                if (pin.IsEmptyPin)
                {
                    pin.Result = "NG";
                    continue;
                }

                string pt = pin.PinType ?? "A";
                var pParam = project?.PinTypeParams?.FirstOrDefault(p => p.Label == pt);
                double xyTol = pParam?.XyTolerance ?? defaultParams.xyTol;
                double offX = pParam?.IdealOffsetX ?? defaultParams.offX;
                double offY = pParam?.IdealOffsetY ?? defaultParams.offY;
                double iH = pParam?.IdealHeight ?? defaultParams.idealH;
                double hTol = pParam?.HeightTolerance ?? defaultParams.hTol;

                double idealX = pin.PadX + offX;
                double idealY = pin.PadY + offY;
                double dx = pin.X - idealX;
                double dy = pin.Y - idealY;
                pin.DiffX = dx.ToString("F3");
                pin.DiffY = dy.ToString("F3");

                pin.SetIdealHeight(iH);
                bool xOk = Math.Abs(dx) <= xyTol;
                bool yOk = Math.Abs(dy) <= xyTol;
                bool hOk = Math.Abs(pin.H - iH) <= hTol;
                pin.Result = (xOk && yOk && hOk) ? "OK" : "NG";
            }

            UpdateStatistics();
        }

        /// <summary>将 VM 叠加层的 IsOK 与 PinResults 的最新 OK/NG 同步</summary>
        private void SyncVmOverlayResults()
        {
            if (_vmOverlays.Count == 0 || PinResults == null) return;
            foreach (var overlay in _vmOverlays)
            {
                // VM 用 GridIndex 匹配（针型+位内索引联合区分，见 MatchOverlayPinResult）
                var pin = MatchOverlayPinResult(PinResults.Where(p => p.GridIndex == overlay.GridIndex), overlay);
                if (pin != null)
                    overlay.IsOK = pin.Result == "OK";
            }
        }

        /// <summary>
        /// 叠加层 ↔ PinResult 匹配：不同针型的 PinIndex 由 VM 各针型模块独立编号会重复，
        /// 必须以 针型+PinIndex 联合匹配；旧存档叠加层无 PinType 时按焊盘坐标兜底。
        /// 调用方应先按 GridIndex 过滤结果集（同一FOV内焊盘坐标唯一）。
        /// </summary>
        private static PinResult MatchOverlayPinResult(IEnumerable<PinResult> results, VmOverlayItem overlay)
        {
            // 旧存档无 PinType：直接按焊盘坐标匹配，避免把 B 针误配到同 PinIndex 的 A 针
            if (string.IsNullOrEmpty(overlay.PinType))
                return MatchOverlayByPad(results, overlay);

            var r = results.FirstOrDefault(n => n.PinIndex == overlay.PinIndex && (n.PinType ?? "A") == overlay.PinType);
            if (r != null) return r;
            return MatchOverlayByPad(results, overlay);
        }

        /// <summary>按焊盘坐标匹配（同一FOV内焊盘坐标唯一；兼容旧存档无 PinType 数据）</summary>
        private static PinResult MatchOverlayByPad(IEnumerable<PinResult> results, VmOverlayItem overlay)
        {
            return results.FirstOrDefault(n =>
                Math.Abs(n.PadX - overlay.PadMmX) < 0.2 &&
                Math.Abs(n.PadY - overlay.PadMmY) < 0.2);
        }

        private void SyncPinIdealHeight()
        {
            if (PinResults == null) return;
            var project = ProjectManager.Instance.CurrentProject;
            if (project?.PinTypeParams == null)
            {
                double h = 2.0;
                foreach (var pin in PinResults)
                    pin.SetIdealHeight(h);
                return;
            }
            foreach (var pin in PinResults)
            {
                var param = project.PinTypeParams.FirstOrDefault(p => p.Label == (pin.PinType ?? "A"));
                pin.SetIdealHeight(param?.IdealHeight ?? 2.0);
            }
        }

        // ==================== 历史结果管理 ====================

        /// <summary>获取当前结果目录路径</summary>
        private string GetResultDir()
        {
            string dir = ProjectManager.Instance.CurrentProjectPath;
            if (string.IsNullOrEmpty(dir)) return null;
            string path;
            if (!string.IsNullOrEmpty(_currentResultFolder))
                path = Path.Combine(dir, "Result", _currentResultFolder);
            else
                path = Path.Combine(dir, "Result");
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
            return path;
        }

        /// <summary>查找最新的结果目录</summary>
        private string FindLatestResultDir()
        {
            string dir = ProjectManager.Instance.CurrentProjectPath;
            if (string.IsNullOrEmpty(dir)) return null;

            string resultDir = Path.Combine(dir, "Result");
            if (!Directory.Exists(resultDir)) return null;

            var tsDirs = Directory.GetDirectories(resultDir, "Result_*")
                .OrderByDescending(d => d).ToList();
            if (tsDirs.Any())
            {
                _currentResultFolder = Path.GetFileName(tsDirs.First());
                return tsDirs.First();
            }

            if (Directory.Exists(resultDir)) return resultDir;
            return null;
        }

        /// <summary>列出所有历史结果目录</summary>
        private List<string> ListHistoryResultDirs()
        {
            string dir = ProjectManager.Instance.CurrentProjectPath;
            if (string.IsNullOrEmpty(dir)) return new List<string>();
            string resultDir = Path.Combine(dir, "Result");
            if (!Directory.Exists(resultDir)) return new List<string>();
            return Directory.GetDirectories(resultDir, "Result_*")
                .OrderByDescending(d => d).ToList();
        }

        /// <summary>加载指定目录的检测结果</summary>
        public bool LoadResultFromDir(string resultDir)
        {
            if (string.IsNullOrEmpty(resultDir) || !Directory.Exists(resultDir)) return false;

            _currentResultFolder = Path.GetFileName(resultDir);

            // 加载检测结果
            PinResults = new ObservableCollection<PinResult>();
            string resultPath = Path.Combine(resultDir, "DetectResults.json");
            if (File.Exists(resultPath))
            {
                string json = File.ReadAllText(resultPath);
                var results = JsonConvert.DeserializeObject<List<PinResult>>(json);
                if (results != null && results.Count > 0)
                {
                    results = results.Where(r => !r.IsEmptyPin).ToList();
                    PinResults = new ObservableCollection<PinResult>(results.OrderBy(r => r.DetectIndex));
                    UpdateStatistics();
                }
            }

            // 2026-08-27 Bug修复2：读取检测总数（旧历史无 ResultMeta.json → 回退为结果数）
            TotalPinCount = PinResults?.Count ?? 0;
            string metaPath = Path.Combine(resultDir, "ResultMeta.json");
            if (File.Exists(metaPath))
            {
                try
                {
                    var meta = JsonConvert.DeserializeObject<Dictionary<string, int>>(File.ReadAllText(metaPath));
                    if (meta != null && meta.TryGetValue("PinCounts", out int pc) && pc > 0)
                        TotalPinCount = pc;
                }
                catch (Exception ex)
                {
                    Log.Warning($"读取检测总数失败: {ex.Message}");
                }
            }

            // 加载叠加层
            _vmOverlays.Clear();
            string vmPath = Path.Combine(resultDir, "VmOverlays.json");
            if (File.Exists(vmPath))
            {
                string json = File.ReadAllText(vmPath);
                var results = JsonConvert.DeserializeObject<List<VmOverlayItem>>(json);
                if (results != null) _vmOverlays = results.Where(r => !r.IsEmptyPin).ToList();
            }

            // 加载检测位选择
            string detectPosPath = Path.Combine(resultDir, "DetectPositions.json");
            if (File.Exists(detectPosPath))
            {
                string json = File.ReadAllText(detectPosPath);
                var dp = JsonConvert.DeserializeObject<List<DetectPosition>>(json);
                if (dp != null && dp.Count > 0)
                {
                    DetectPositions = new ObservableCollection<DetectPosition>(dp);
                    // 同步到 GrabPositions 的 IsDetectPosition 状态
                    if (GrabPositions != null)
                    {
                        var dpSet = new HashSet<int>(dp.Select(d => d.GrabIndex));
                        foreach (var gp in GrabPositions)
                            gp.IsDetectPosition = dpSet.Contains(gp.Index);
                    }
                }
            }

            SyncPinIdealHeight();
            Log.Info($"已加载历史结果: {resultDir}");

            // 切换到检测结果标签页
            SelectedTabIndex = 3;

            // 刷新 Halcon 窗口显示（叠加层+检测位红框）
            RefreshOverlayDisplay();

            return true;
        }

        private void SavePinResults()
        {
            try
            {
                string resultDir = GetResultDir();
                if (string.IsNullOrEmpty(resultDir) || PinResults == null || PinResults.Count == 0) return;

                string filePath = Path.Combine(resultDir, "DetectResults.json");
                string json = JsonConvert.SerializeObject(PinResults.ToList(), Formatting.Indented);
                File.WriteAllText(filePath, json);
                Log.Info($"检测结果已保存: {filePath}");

                // 2026-08-27 Bug修复2：总针数（去重后）随历史结果持久化，供打开历史结果后显示
                TotalPinCount = PinResults.Count;
                try
                {
                    string metaPath = Path.Combine(resultDir, "ResultMeta.json");
                    File.WriteAllText(metaPath, JsonConvert.SerializeObject(new { PinCounts = TotalPinCount }, Formatting.Indented));
                    Log.Info($"检测总数已保存: {metaPath} (PinCounts={TotalPinCount})");
                }
                catch (Exception ex)
                {
                    Log.Warning($"保存检测总数失败: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                Log.Error($"保存检测结果失败: {ex.Message}");
            }
        }

        private void LoadPinResults()
        {
            try
            {
                PinResults = new ObservableCollection<PinResult>();

                string resultDir = FindLatestResultDir();
                if (resultDir == null) return;

                string filePath = Path.Combine(resultDir, "DetectResults.json");
                if (!File.Exists(filePath)) return;

                string json = File.ReadAllText(filePath);
                var results = JsonConvert.DeserializeObject<List<PinResult>>(json);
                if (results != null && results.Count > 0)
                {
                    results = results.Where(r => !r.IsEmptyPin).ToList();
                    PinResults = new ObservableCollection<PinResult>(results.OrderBy(r => r.DetectIndex));
                    UpdateStatistics();
                    Log.Info($"已加载检测结果: {filePath}");
                }

                // 2026-08-27 Bug修复2：启动自动加载时同步检测总数（无 ResultMeta.json → 回退结果数）
                TotalPinCount = PinResults?.Count ?? 0;
                string metaPath = Path.Combine(resultDir, "ResultMeta.json");
                if (File.Exists(metaPath))
                {
                    try
                    {
                        var meta = JsonConvert.DeserializeObject<Dictionary<string, int>>(File.ReadAllText(metaPath));
                        if (meta != null && meta.TryGetValue("PinCounts", out int pc) && pc > 0)
                            TotalPinCount = pc;
                    }
                    catch (Exception ex)
                    {
                        Log.Warning($"读取检测总数失败: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error($"加载检测结果失败: {ex.Message}");
            }
        }

        // ==================== VM 叠加层数据持久化 ====================
        private void SaveVmOverlays()
        {
            try
            {
                if (_vmOverlays == null || _vmOverlays.Count == 0) return;

                string filePath = Path.Combine(GetResultDir(), "VmOverlays.json");
                string json = JsonConvert.SerializeObject(_vmOverlays, Formatting.Indented);
                File.WriteAllText(filePath, json);
                Log.Info($"VM叠加层数据已保存: {filePath}");
            }
            catch (Exception ex)
            {
                Log.Error($"保存VM叠加层数据失败: {ex.Message}");
            }
        }

        private void LoadVmOverlays()
        {
            try
            {
                string resultDir = FindLatestResultDir();
                if (resultDir == null) return;

                string filePath = Path.Combine(resultDir, "VmOverlays.json");
                if (!File.Exists(filePath)) return;

                string json = File.ReadAllText(filePath);
                var results = JsonConvert.DeserializeObject<List<VmOverlayItem>>(json);
                if (results != null && results.Count > 0)
                {
                    _vmOverlays = results.Where(r => !r.IsEmptyPin).ToList(); // 过滤掉旧数据中的空针
                    Log.Info($"已加载VM叠加层数据: {filePath}, {_vmOverlays.Count} 个针脚");
                }
            }
            catch (Exception ex)
            {
                Log.Error($"加载VM叠加层数据失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 2026-08-25 主题② C：孤儿图清扫（检测流程终态收尾调用，此时主流程+板末重扫全部 GRAB 已结束）。
        /// 正常流中 VM 每次 GRAB 的临时文件（GRAB_OK_&lt;时间戳&gt;.bmp）在替换瓦片后即被删除；
        /// 残留的 GRAB_OK_* 且不在对应索引内的文件 = 泄漏孤儿——
        /// 来源：①"存图了但上位机未感知"超时后停止/重启的旧图；②文件等待超时后晚到的图。
        /// 索引规则（2026-08-25 修正，防误删正常文件）：
        ///   Origin 板面瓦片 → _imageManager.GetImagePaths()（瓦片本身也是 GRAB_OK_ 前缀）
        ///   Pin 子目录针尖图 → _pinImageMap.Values（针尖图也是 GRAB_OK_ 前缀且不替换目标，直接以 VM 文件留存）
        /// 危害（不清理时）：混入瓦片文件名排序导致 GridIndex 错位（Project.cs:175）；ValidateImageCount 按创建时间
        /// 删多余时可能误删重扫替换后的目标文件（静默损坏）。
        /// </summary>
        private void CleanupOrphanGrabbedFiles()
        {
            try
            {
                // 索引集合：板面瓦片 + 针尖图（两部分都可能是 GRAB_OK_ 前缀）
                var indexed = new HashSet<string>(_imageManager.GetImagePaths(), StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (var v in _pinImageMap.Values) indexed.Add(v);
                }
                catch { }

                int removed = 0;
                // Origin 板面目录：一级文件，索引=GetImagePaths
                try
                {
                    string originDir = ProjectManager.Instance.GetOriginPath();
                    if (Directory.Exists(originDir))
                        removed += CleanupOrphanInDir(originDir, indexed, false);
                }
                catch { }
                // Pin 针尖目录：递归子目录（PinA/B/C...），索引=_pinImageMap.Values
                try
                {
                    string pinDir = ProjectManager.Instance.GetPinPath();
                    if (Directory.Exists(pinDir))
                        removed += CleanupOrphanInDir(pinDir, indexed, true);
                }
                catch { }

                if (removed > 0)
                    Log.Warning($"[孤儿清扫] 清理 {removed} 个 GRAB 残留孤儿文件（防瓦片错位/容错误删）");
            }
            catch (Exception ex)
            {
                Log.Warning($"[孤儿清扫] 异常: {ex.Message}");
            }
        }

        private int CleanupOrphanInDir(string dir, HashSet<string> indexed, bool recursive)
        {
            int removed = 0;
            var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            foreach (var f in Directory.GetFiles(dir, "GRAB_OK_*", option))
            {
                if (indexed.Contains(f)) continue;   // 正常瓦片/针尖图（含重扫替换后的目标文件），保留
                try
                {
                    File.SetAttributes(f, FileAttributes.Normal);
                    File.Delete(f);
                    removed++;
                }
                catch (Exception ex)
                {
                    Log.Warning($"[孤儿清扫] 删除失败: {f}: {ex.Message}");
                }
            }
            return removed;
        }
        #endregion

        #endregion
    }
}
