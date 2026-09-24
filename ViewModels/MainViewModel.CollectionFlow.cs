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
        #region ===================================全图采集流程===================================

        private void InitialFullImage()
        {
            _imageManager.Clear();
            _vision.ClearHalconWindow();
            _imageManager.ClearProjectImagesFolder();

            GrabPositions?.Clear();
            DetectPositions?.Clear();
            _vision.HalconControl?.FitToWindow();
        }

        /// <summary>
        /// 流程运行状态（壳层，通用）：Init → Running ⇄ Paused → Terminated。
        /// 本次由全图采集接入（检测/连续模式后续可迁移）。
        /// 暂停/恢复信号由各流程自行提供（全图采集 = _pauseGrabEvent），壳层只负责等待与终止。
        /// </summary>
        private enum FlowRunState
        {
            Init,        // 初始化（壳层预留通用准备位；全图采集的具体准备在业务层 GrabState.Init）
            Running,     // 运行：驱动业务层单步
            Paused,      // 暂停：等待暂停信号（Set→恢复 Running；Cancel→Terminated）
            Terminated,  // 终态：流程结束（正常完成或终止）
        }

        /// <summary>
        /// 检测流程业务状态机（仿 GrabState）：
        /// Init(准备) → CapturePos(游标定位) → GrabTile(移动+拍照) → Start2D(启动2D任务)
        /// → MoveScanStart(移3D扫描起点) → StartScan3D(启动3D扫描) → Wait2D(等2D结果) →
        /// MergePending(处理上一位挂起Merge，与3D扫描并行) → WaitScan3D(等3D扫描完成) → Merge(解析合并) → NextPos(游标推进) → End / Err。
        /// 暂停检查点=现有 7 处（①~⑦，位置对齐原 CheckPauseAndCancelAsync 调用点）；游标 i 仅 NextPos 推进，恢复从当前状态重做。
        /// 2D/3D 并行任务句柄（_vmDetectTask/_3dScanTask/_3dDataTask）为闭包变量跨状态传递。
        /// 2026-08-25 异步化：WaitScan3D 不等数据 → MergePending(⑦) 恢复挂起上下文+取数 → Merge；A段期间数据后台到达，等待不再阻塞轴。
        /// 2026-08-25 v2：MergePending 移至 Wait2D 之后（Merge 与 3D 扫描并行，不再占用 B+C 段）；重扫时 _rescanFlag 驱动 NextPos 重发 2D。
        /// </summary>
        private enum CheckState
        {
            Init,          // 准备：清理缓存/针尖图层恢复/pin目录/结果目录/CTS创建/并行IO（顶板+阻挡+路径+曝光+速度）
            CapturePos,    // 检查点①：取当前位数据（坐标/模式/行程）→ GrabTile
            GrabTile,      // A段：移动+拍照（GrabAndReplaceTile/Multi）+ 清旧结果 + 2D图像路径准备 → Start2D
            Start2D,       // 检查点②：启动 _vmDetectTask（不 await，与3D并行）→ MoveScanStart
            MoveScanStart, // 移动到3D扫描起点（Retry×3）+ 检查点③（mid-case，移动后失败判定前）；失败→Err → StartScan3D
            StartScan3D,   // 检查点④：_3dScanTask = Scan3DToAndWaitOK 重试(总2次)（不 await）→ Wait2D
            Wait2D,        // await 2D结果 + 解析 + 有效区过滤 + 构建叠加层 + Dispatcher刷新 → MergePending
            MergePending,  // 检查点⑦：Wait2D后/End前——恢复上一位快照上下文 + 取数(剩余窗口) → Merge（与3D扫描并行，2026-08-25 新增）
            WaitScan3D,    // 检查点⑤：快照当前位上下文+创建3D数据监听（先于await扫描）→ await 扫描 → NextPos（不等数据，2026-08-25 优化）
            Merge,         // 检查点⑥：scan3DTimeout? 纯2D兜底 : 完整3D解析合并（匈牙利匹配+逐针判定+Dispatcher提交）→ NextPos
            NextPos,       // 游标推进：i++（整拍完成才 ++）→ MergePending(末位) / 跳过A段(_skipAForNext→WaitScan3D或重发2D) / CapturePos
            End,           // 终态标记：循环后统一收尾（去重/保存/汇总）
            Err,           // 终态：业务错误（移动失败）或报警（MarkAlarmCancel）
        }

        /// <summary>
        /// 全图采集业务状态机（仿 TransportState/ContinuousState）：
        /// Init(准备) → MoveToPos(移动) → GrabImage(拍照) → SaveStitch(保存拼接) → NextPos(游标推进) → End(完成收尾) / Err。
        /// 暂停检查点 = 每点位顶部（MoveToPos 入口）；游标 i 仅整拍完成后 ++，恢复后从当前点位重做。
        /// 拍边界（MoveToPos 入口/取消检查）发现暂停/终止时，将 flowState 交给壳层处理。
        /// </summary>
        private enum GrabState
        {
            Init,        // 准备：清缓存/计算拍照位/设置VM路径/曝光/速度/焦距
            MoveToPos,   // 移动：MoveToAndWaitOK 重试3次（= 暂停检查点，每点位顶部）
            GrabImage,   // 拍照：GRAB 一次（不再重试）
            SaveStitch,  // 保存拼接：ParseAndSaveImageAsync + StitchAndDisplay
            NextPos,     // 游标推进：i++（整拍完成才 ++）；到末尾 → End
            End,         // 终态：完成收尾（保存拍照位/网格索引/方案 + HomeXY 回原点）
            Err,         // 终态：异常（移动失败/拍照超时/解析失败）
        }

        public async void StartFullImageGrabFlow()
        {
            if (!CheckMotionConnected()) return;
            if (!CheckVision2DConnected()) return;

            if (ProjectManager.Instance.CurrentProject == null)
            {
                MessageBox.Show(Application.Current.MainWindow, "请先新建或打开一个方案", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!CheckSafeOperation()) return;
            isWorkFlowRun[WorkFlowType.FullImageGrab] = true;

            if (!await CheckAndHomeAxesAsync(true))
            {
                isWorkFlowRun[WorkFlowType.FullImageGrab] = false;
                return;
            }

            // CTS 外壳创建（仿 ContinuousRunInternal）；busy 通知
            _grabCts = new CancellationTokenSource();
            var token = _grabCts.Token;
            ActionBusyStateChanged?.Invoke("grab_full", true);

            try
            {
                // ===== 闭包变量（跨壳层/业务层共享）=====
                var project = ProjectManager.Instance.CurrentProject;
                List<GrabPosition> grabPositions = null;
                int totalCount = 0;
                int i = 0;                       // 游标：仅整拍完成后 ++（"当前点位重做"的关键）
                string projectImagesDir = ProjectManager.Instance.GetOriginPath();
                GrabPosition currentPos = null;  // 当前点位（跨 MoveToPos/GrabImage/SaveStitch 共享）
                string grabResult = null;        // 当前拍 GRAB 结果（GrabImage→SaveStitch 传递）
                string err = null;
                var flowState = FlowRunState.Init;   // 壳层状态
                var bizState = GrabState.Init;       // 业务层状态
                bool endDone = false;                // End 收尾完成标志（NextPos 置 End 后需再走一轮真正执行 End 分支）

                // 报警终止辅助：报警中取消 → 业务层直接转 Err（终态收尾走 Err 分支；弹窗由 DeviceMonitor 报警弹窗承担，本处只 Log+状态栏）
                bool MarkAlarmCancel()
                {
                    if (!_alarmStopping) return false;
                    err = "设备报警，采集已终止";
                    bizState = GrabState.Err;
                    return true;
                }

                // ===== 业务层单步：完成一个点位动作并推进；拍边界向壳层上报暂停/终止 =====
                async Task<bool> BizStep()
                {
                    switch (bizState)
                    {
                        case GrabState.Init:
                            if (token.IsCancellationRequested) { MarkAlarmCancel(); flowState = FlowRunState.Terminated; break; }
                            InitialFullImage();
                            // 采集前确保 Fov 与相机参数一致
                            SyncFovFromCameraParams();
                            Log.Info("计算拍照位...");
                            grabPositions = CalculateGrabPositions(
                                project.BoardWidth, project.BoardHeight,
                                _sysParam.RightBottomX, _sysParam.RightBottomY,
                                project.FovWidth, project.FovHeight, project.Overlap);
                            // 立即填充列表，让用户看到所有拍照位
                            UpdateGrabPositionLists(grabPositions);
                            totalCount = grabPositions.Count;
                            Log.Info($"需要拍摄 {totalCount} 个位置 ({_sysParam.ImageDisplayRows} x {_sysParam.ImageDisplayCols})");
                            UpdateStatus($"开始全图采集，共 {totalCount} 个拍照位");
                            // 通知VM设置拍照路径（TCP → GlobalScript → ImageSavePath）
                            await SetVmSavePath(projectImagesDir);
                            // 采集前设置 VM 全局相机曝光时间
                            await SetVmCameraExposure();
                            // 顶板1/2 顶起固定板 + 阻挡气缸升起（防止移动拍照时板位移）
                            // 2026-08-27 需求：顶板执行完后延时100ms再执行阻挡气缸，防止阻挡气缸把板移位（原并行 Task.WhenAll 同时动作）
                            await Task.WhenAll(
                                _motionIO.SetIO(OutSignal.Clamp1, true),
                                _motionIO.SetIO(OutSignal.Clamp2, true));
                            await Task.Delay(100, token);
                            await _motionIO.SetIO(OutSignal.Block, true);
                            // 采集前发送速度指令，确保下位机速度设置生效
                            await SendSpeedPtpAsync();
                            // 全图采集前调到板面焦距（只需调一次）
                            double boardFocus = project.BoardFocus;
                            if (boardFocus > 0)
                            {
                                UpdateStatus($"调Z轴至板面焦距({boardFocus}mm)");
                                bool zOk = await RetryAsync(
                                    () => _motionIO.MoveToAndWaitOK(z: boardFocus, timeoutMs: (int)_sysParam.Data.MoveZTimeout),
                                    maxRetries: 3, delayMs: 200);
                                if (!zOk)
                                    Log.Warning($"调Z轴至板面焦距失败，全图采集可能在错误焦距下进行");
                                else
                                    // 2026-08-25 参数化：调Z后稳定延时改用"到位拍照延时"PositionSnapshotDelay，与移动到位后 L215 用法统一，可界面调参
                                    await Task.Delay((int)_sysParam.Data.PositionSnapshotDelay, token);
                            }
                            i = 0;
                            bizState = GrabState.MoveToPos;
                            break;

                        case GrabState.MoveToPos:
                            // 暂停检查点（每点位顶部）：暂停→壳层转 Paused，恢复后从当前点位重做
                            if (!_pauseGrabEvent.IsSet) { flowState = FlowRunState.Paused; break; }
                            if (token.IsCancellationRequested) { MarkAlarmCancel(); flowState = FlowRunState.Terminated; break; }
                            currentPos = grabPositions[i];
                            Log.Info($"移动中 ....（{currentPos.Row},{currentPos.Col}] X:{currentPos.X:F1} Y:{currentPos.Y:F1} ({i + 1}/{totalCount})");
                            UpdateStatus($"移动中 .... X:{currentPos.X:F1} Y:{currentPos.Y:F1}，({i + 1}/{totalCount})");
                            // 移动重试（暂停感知：移动中暂停→立即转 Paused，恢复后当前点位重做；取消优先）
                            bool moved = await PauseAwareWaitAsync(
                                RetryAsync(
                                    () => _motionIO.MoveToAndWaitOK(currentPos.X, y: currentPos.Y, timeoutMs: (int)_sysParam.Data.MoveToScanStartTimeout),
                                    maxRetries: 3, delayMs: 50),
                                token, () => !_pauseGrabEvent.IsSet);
                            if (token.IsCancellationRequested) { MarkAlarmCancel(); flowState = FlowRunState.Terminated; break; }
                            if (!_pauseGrabEvent.IsSet) { flowState = FlowRunState.Paused; break; }
                            if (moved)
                            {
                                Log.Info("移动完成");
                                await Task.Delay((int)_sysParam.Data.PositionSnapshotDelay, token);
                            }
                            if (!moved)
                            {
                                err = $"移动到拍照位失败: [{currentPos.Row},{currentPos.Col}]";
                                Log.Error(err);
                                bizState = GrabState.Err;
                                break;
                            }
                            bizState = GrabState.GrabImage;
                            break;

                        case GrabState.GrabImage:
                            if (token.IsCancellationRequested) { MarkAlarmCancel(); flowState = FlowRunState.Terminated; break; }
                            // 暂停检查点（拍照前）：暂停→壳层转 Paused，恢复后当前点位重做
                            if (!_pauseGrabEvent.IsSet) { flowState = FlowRunState.Paused; break; }
                            UpdateStatus($"拍照中 ({i + 1}/{totalCount})");
                            // 暂停感知 + 取消感知（原 GRAB 不传 token，复位/停止时须等拍照超时——2026-08-19 修复）
                            var grabTask = _vision2DClient.SendCommandAndWaitAsync("GRAB", "GRAB_OK", (int)_sysParam.Data.GrabTimeout, token);
                            string lastGrab = await PauseAwareWaitAsync(grabTask, token, () => !_pauseGrabEvent.IsSet);
                            if (token.IsCancellationRequested) { MarkAlarmCancel(); flowState = FlowRunState.Terminated; break; }
                            if (!_pauseGrabEvent.IsSet) { flowState = FlowRunState.Paused; break; }
                            if (lastGrab == "TIMEOUT")
                            {
                                err = "拍照超时";
                                Log.Error(err);
                                bizState = GrabState.Err;
                                break;
                            }
                            grabResult = lastGrab;
                            bizState = GrabState.SaveStitch;
                            break;

                        case GrabState.SaveStitch:
                            if (token.IsCancellationRequested) { MarkAlarmCancel(); flowState = FlowRunState.Terminated; break; }
                            int gridIndex = currentPos.Row * _sysParam.ImageDisplayCols + currentPos.Col;
                            if (!await ParseAndSaveImageAsync(grabResult, projectImagesDir, gridIndex))
                            {
                                err = $"解析图片失败: {grabResult}";
                                Log.Error(err);
                                bizState = GrabState.Err;
                                break;
                            }
                            // 文件由VM直接保存到方案目录，保持原始文件名，检测时再按需定位
                            var sw = Stopwatch.StartNew();
                            _imageManager.StitchAndDisplay(
                                stitched => _vision.DisplayImageByImage(stitched),
                                _sysParam.ImageDisplayRows,
                                _sysParam.ImageDisplayCols,
                                project.FovWidth,
                                project.FovHeight,
                                project.Overlap
                            );
                            sw.Stop();
                            if (i == 0) _vision.HalconControl?.FitToWindow();
                            Log.Info($"完成拍照位 [{currentPos.Row},{currentPos.Col}]，已采集 {_imageManager.ImageCount}/{totalCount} 张 (拼接: {sw.ElapsedMilliseconds}ms)");
                            bizState = GrabState.NextPos;
                            break;

                        case GrabState.NextPos:
                            // 游标推进：整拍完成才 ++；到末尾 → End（暂停恢复从当前点位重做即依赖此点）
                            i++;
                            if (i >= totalCount) { bizState = GrabState.End; break; }
                            bizState = GrabState.MoveToPos;
                            break;

                        case GrabState.End:
                            // 完成收尾
                            UpdateStatus($"全图采集完成，共采集 {_imageManager.ImageCount} 张图片");
                            _vision.HalconControl?.FitToWindow();
                            SaveGrabPositions(grabPositions);
                            SaveGridIndexMap(ProjectManager.Instance.CurrentProjectPath);
                            ProjectManager.Instance.SaveCurrentProject();
                            if (await _motionIO.HomeXY()) UpdateStatus("就绪");
                            endDone = true;   // 收尾完成才返回终态（否则 NextPos 置 End 后 End 分支被跳过，永远执行不到）
                            break;

                        case GrabState.Err:
                            break;   // 静默终态（文本/弹窗由壳层循环外统一处理）
                    }
                    return endDone || bizState == GrabState.Err
                        || flowState == FlowRunState.Terminated;   // 业务层内取消也在本拍立即收束
                }

                // ===== 壳层单步：生命周期 + 暂停等待；驱动业务层 =====
                async Task<bool> RunStep()
                {
                    switch (flowState)
                    {
                        case FlowRunState.Init:
                            // 壳层初始化：预留通用准备位（全图采集准备在 GrabState.Init）→ 直接进入运行
                            flowState = FlowRunState.Running;
                            break;

                        case FlowRunState.Running:
                            if (token.IsCancellationRequested) { MarkAlarmCancel(); flowState = FlowRunState.Terminated; break; }
                            // 业务层单步：返回 true=到达终态（End/Err/业务内取消）→ 壳层收束
                            if (await BizStep()) flowState = FlowRunState.Terminated;
                            break;

                        case FlowRunState.Paused:
                            // 暂停等待（仿 CheckPauseAndCancelAsync）：复位先 Cancel 再 Set → 等待立即退出。
                            // 状态栏不刷文字（暂停/恢复反馈由按钮点击时一次性提示，恢复后业务层自然显示移动中/拍照中）
                            while (!_pauseGrabEvent.IsSet)
                            {
                                try { await Task.Delay(200, token); }
                                catch (OperationCanceledException) { MarkAlarmCancel(); flowState = FlowRunState.Terminated; break; }
                            }
                            if (flowState == FlowRunState.Terminated) break;
                            flowState = FlowRunState.Running;
                            break;

                        case FlowRunState.Terminated:
                            break;   // 静默终态（结果判定在循环外）
                    }
                    return flowState == FlowRunState.Terminated;
                }

                await FlowLoopAsync(RunStep);

                // ===== 终态收尾（按业务层结果区分）=====
                if (bizState == GrabState.Err)
                {
                    UpdateStatus($"全图采集异常: {err}");
                    Log.Error($"[Grab] 全图采集异常: {err}");
                    if (!_alarmStopping)   // 报警终止不重复弹窗（DeviceMonitor 报警弹窗已有）
                        MessageBox.Show(Application.Current.MainWindow, $"{err}，采集终止", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                else if (bizState == GrabState.End)
                {
                    Log.Info($"[Grab] 全图采集完成，共采集 {_imageManager.ImageCount} 张图片");
                }
                else
                {
                    // 中途终止（复位）：业务层未到达 End/Err
                    Log.Info("[Grab] 全图采集已终止");
                    UpdateStatus("全图采集已终止");
                }
            }
            catch (OperationCanceledException)
            {
                Log.Info(_alarmStopping ? "[Grab] 全图采集因设备报警终止" : "全图采集已取消");
                UpdateStatus(_alarmStopping ? "全图采集异常: 设备报警，采集已终止" : "全图采集已取消");
            }
            catch (Exception ex)
            {
                Log.Error($"全图采集异常: {ex.Message}");
                MessageBox.Show(Application.Current.MainWindow, $"全图采集失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _pauseGrabEvent.Set();                          // 恢复运行状态，避免下次进入仍为暂停
                ActionBusyStateChanged?.Invoke("grab_full", false);
                isWorkFlowRun[WorkFlowType.FullImageGrab] = false;
                _grabCts?.Dispose();
                _grabCts = null;
            }
        }

        public List<GrabPosition> CalculateGrabPositions(double boardWidth, double boardHeight,
            double rightBottomX, double rightBottomY, double fovWidth, double fovHeight, double overlap = 0)
        {
            var positions = new List<GrabPosition>();

            double stepX = fovWidth * (1 - overlap);
            double stepY = fovHeight * (1 - overlap);

            int cols = (int)Math.Ceiling(boardWidth / stepX);
            int rows = (int)Math.Ceiling(boardHeight / stepY);

            _sysParam.ImageDisplayRows = rows;
            _sysParam.ImageDisplayCols = cols;

            double totalCoverWidth = (cols - 1) * stepX + fovWidth;
            double totalCoverHeight = (rows - 1) * stepY + fovHeight;

            double leftTopX = rightBottomX - boardWidth;
            double leftTopY = rightBottomY + boardHeight;

            double startX = leftTopX + (boardWidth - totalCoverWidth) / 2.0 + fovWidth / 2.0;
            double startY = leftTopY - (boardHeight - totalCoverHeight) / 2.0 - fovHeight / 2.0;



            // 遍历顺序固定为 Z 型（默认模式，自上而下逐行从左到右）——采集设置已删除，便于维护
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    double offsetX = (_sysParam.ImageDisplayRows - r) * _sysParam.StitchOffsetX;
                    double offsetY = (_sysParam.ImageDisplayCols - c) * _sysParam.StitchOffsetY;
                    positions.Add(new GrabPosition
                    {
                        Index = r * cols + c, // 网格位置，0=0行0列，不随采集顺序变化
                        Row = r,
                        Col = c,
                        X = startX + c * stepX - offsetX,
                        Y = startY - r * stepY - offsetY,
                        IsDetectPosition = false
                    });
                }
            }

            Log.Info($"计算拍照位: {rows}行 x {cols}列 = {positions.Count}个位置 (Z型)");
            Log.Info($"实际重叠率: X方向={1 - stepX / fovWidth:P1}, Y方向={1 - stepY / fovHeight:P1}");
            return positions;
        }

        private async Task<bool> ParseAndSaveImageAsync(string result, string baseDirectory, int gridIndex)
        {
            if (result.StartsWith("GRAB_OK"))
            {
                string fileName = result;
                if (!string.IsNullOrEmpty(fileName))
                {
                    string filePath = Path.Combine(baseDirectory, fileName);

                    // 如果主路径找不到，尝试 D:\GrabImage 兜底（脱机测试用）
                    if (!File.Exists(filePath))
                    {
                        string fallbackPath = Path.Combine(@"D:\GrabImage", fileName);
                        if (File.Exists(fallbackPath))
                        {
                            Log.Warning($"文件在主路径不存在，从 D:\\GrabImage 复制: {fileName}");
                            Directory.CreateDirectory(Path.GetDirectoryName(filePath));
                            File.Copy(fallbackPath, filePath, overwrite: true);
                        }
                    }

                    if (await WaitForFileReadyAsync(filePath, (int)_sysParam.Data.WaitForVmSaveTimeout))
                    {
                        _imageManager.AddImage(filePath, gridIndex);
                        Log.Info($"已加载图片 {fileName}，当前共 {_imageManager.ImageCount} 张");
                        return true;
                    }
                    else
                    {
                        Log.Warning($"图片文件不存在: {filePath}");
                        return false;
                    }
                }
                else
                {
                    string latestImage = GetLatestImageFile(baseDirectory);
                    if (!string.IsNullOrEmpty(latestImage))
                    {
                        _imageManager.AddImage(latestImage, gridIndex);
                        Log.Info($"已加载最新图片，当前共 {_imageManager.ImageCount} 张");
                        return true;
                    }
                }
            }
            else if (result == "TIMEOUT")
            {
                Log.Error("拍照超时");
                return false;
            }
            return false;
        }

        public void StopFullImageGrabFlow()
        {
            if (!isWorkFlowRun[WorkFlowType.FullImageGrab]) return;
            _grabCts?.Cancel();
            _pauseGrabEvent.Set();   // 唤醒暂停等待 → 立即进入 Terminated 退出
            UpdateStatus("全图采集已终止");
        }

        public async void GrabImage()
        {
            if (isWorkFlowRun.Any())
            {
                MessageBox.Show(Application.Current.MainWindow, "当前有其他操作正在运行");
                return;
            }
            isWorkFlowRun[WorkFlowType.FovGrab] = true;
            if (!CheckVision2DConnected()) return;

            try
            {
                string result = await _vision2DClient.SendCommandAndWaitAsync("GRAB", "GRAB_OK", (int)_sysParam.Data.GrabTimeout);

                if (result.StartsWith("GRAB_OK"))
                {
                    // 先确保VM路径指向默认抓图目录，再读取图片
                    string defaultGrabDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GrabTemp");
                    Directory.CreateDirectory(defaultGrabDir);
                    await SetVmSavePath(defaultGrabDir);
                    result = await _vision2DClient.SendCommandAndWaitAsync("GRAB", "GRAB_OK", (int)_sysParam.Data.GrabTimeout);
                    if (!result.StartsWith("GRAB_OK")) return;

                    string fileName = result;
                    string filePath = Path.Combine(defaultGrabDir, fileName);
                    if (await WaitForFileReadyAsync(filePath, (int)_sysParam.Data.WaitForVmSaveTimeout))
                    {
                        HObject image = new HObject();
                        HObject scaled = new HObject();
                        HOperatorSet.ReadImage(out image, filePath);
                        var tile = FullImageManager.CalculateTileDimensions(
                            ProjectManager.Instance.CurrentProject.FovWidth,
                            ProjectManager.Instance.CurrentProject.FovHeight, 0);
                        HOperatorSet.ZoomImageSize(image, out scaled, tile.displayW, tile.displayH, "constant");

                        _vision.DisplayImageByImage(scaled);
                    }
                }
                else if (result == "TIMEOUT")
                {
                    UpdateStatus("拍照超时");
                }
            }
            finally
            {
                isWorkFlowRun[WorkFlowType.FovGrab] = false;
            }
        }

        #endregion
    }
}
