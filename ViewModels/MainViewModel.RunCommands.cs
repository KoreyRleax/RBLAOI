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
        #region 运行
        public void StartCheck() { _ = StartCheckFlow(); }
        public void StopCheck() => StopCheckFlow();

        /// <summary>开始/继续（固定按钮区「开始」入口，检测专用）：全图采集进行中→提示互斥不碰采集（恢复走「采集全图」按钮）；否则按工作模式分发——连续模式→启动/继续连续流程；单板模式→检测已暂停则继续，否则启动检测</summary>
        public void StartOrResumeCheck()
        {
            // 全图采集进行中（含暂停中）→ 提示互斥；采集的暂停/恢复由「采集全图」按钮承担
            if (isWorkFlowRun[WorkFlowType.FullImageGrab])
            {
                UpdateStatus(_pauseGrabEvent.IsSet ? "全图采集运行中" : "全图采集已暂停，请点「采集全图」继续");
                return;
            }

            if (IsContinuousMode)
            {
                if (IsContinuousFlowRunning)
                {
                    if (_continuousPaused)
                    {
                        // 连续模式暂停中 → 继续
                        ResumeContinuousFlow();
                        return;
                    }
                    // 连续模式运行中：检测段暂停则继续，否则提示已在运行
                    if (isWorkFlowRun[WorkFlowType.Check] && !_pauseCheckEvent.IsSet)
                    {
                        _pauseCheckEvent.Set();
                        UpdateStatus("检测恢复中...");
                        PauseStateChanged?.Invoke();
                    }
                    else
                    {
                        UpdateStatus("连续模式运行中");
                    }
                    return;
                }
                _ = ContinuousRunInternal();
                return;
            }

            // 单板模式
            if (_isDemoMode)
            {
                // 演示模式选中：开始=启动/继续演示循环；检测/运送/出板已暂停则继续（演示模式同样运板，
                // 暂停可能发生在运送/出板阶段，恢复判定须与单板一致——2026-08-19）
                bool demoPaused = !_pauseCheckEvent.IsSet &&
                    (isWorkFlowRun[WorkFlowType.Check] || isWorkFlowRun[WorkFlowType.TransportBoard] || isWorkFlowRun[WorkFlowType.SendToExit]);
                if (demoPaused)
                {
                    _pauseCheckEvent.Set();
                    UpdateStatus("演示继续，检测恢复中...");
                    PauseStateChanged?.Invoke();
                    return;
                }
                if (_demoCts == null)
                {
                    _ = DemoLoopAsync();   // 选中未启动 → 点开始启动演示循环
                    return;
                }
                UpdateStatus("演示模式运行中");
                return;
            }
            // 单板模式（非演示）：检测/运送/出板已暂停则继续（运送阶段暂停后点「开始」=恢复，而不是并发启动新前置流程），
            // 否则按入口/工作位状态走前置流程（规则a-d）
            bool boardFlowPaused = !_pauseCheckEvent.IsSet &&
                (isWorkFlowRun[WorkFlowType.Check] || isWorkFlowRun[WorkFlowType.TransportBoard] || isWorkFlowRun[WorkFlowType.SendToExit]);
            if (boardFlowPaused)
            {
                _pauseCheckEvent.Set();
                UpdateStatus("检测恢复中...");
                PauseStateChanged?.Invoke();
                return;
            }
            _ = SingleBoardCheckFlowAsync();
        }

        /// <summary>
        /// 单板模式开始流程（规则a-d）：
        /// a. 入口无料，工作位有料 → 跳过运送，直接开始检测
        /// b. 入口有料，工作位无料 → 从运送基板开始
        /// c. 入口无料且等待超时 → 弹窗提示
        /// d. 入口无料但等待期间有料 → 从等待入口放板开始
        /// ct：可选取消令牌（演示模式传 _demoCts.Token）——运板完成后、检测启动前检查，取消选中/复位后不启动新的检测（2026-08-19）
        /// </summary>
        private async Task SingleBoardCheckFlowAsync(CancellationToken ct = default)
        {
            await RunBoardEntryGateAsync(() => StartCheckFlow(), ct);
        }

        /// <summary>
        /// 基板入口前置流程（规则a-d，2026-08-27 从 SingleBoardCheckFlowAsync 抽出，检测/全图采集共用）：
        /// a. 工作位有料 → 直接开始（不流板）
        /// b. 入口有料，工作位无料 → 运送基板 → 开始
        /// c. 入口无料且等待超时 → 弹窗提示
        /// d. 入口无料但等待期间有料 → 运送基板 → 开始
        /// startFlow：到达可开始条件后执行的动作（检测=StartCheckFlow；全图采集=StartFullImageGrabFlow）
        /// </summary>
        private async Task RunBoardEntryGateAsync(Func<Task> startFlow, CancellationToken ct = default)
        {
            try
            {
                // 安全操作检查：报警/未回零/互斥时直接拦截（否则前置流程会误报"等待入口放板超时"）
                if (!CheckSafeOperation()) return;
                if (ct.IsCancellationRequested) return;

                // a. 工作位有料 → 直接开始（板已在加工位）
                if (await _motionIO.GetIOState(InputSignal.WorkSensor, 1000))
                {
                    UpdateStatus("工作位已有基板，直接开始");
                    await startFlow();
                    return;
                }

                // 工作位无料：检查入口
                if (ct.IsCancellationRequested) return;
                if (await _motionIO.GetIOState(InputSignal.WaitSensor, 1000))
                {
                    // b. 入口有料 → 运送基板 → 开始
                    UpdateStatus("入口有基板，开始运送");
                    if (await TransportBoard())
                    {
                        if (ct.IsCancellationRequested) return;   // 运板完成、开始前：演示已取消 → 不启动
                        await startFlow();
                    }
                    return;
                }

                // d. 入口无料 → 等待入口放板（TransportBoard waitEntryOnly 纯等待，有料后自动运送）
                UpdateStatus("等待入口放板...");
                if (await TransportBoard(true, true))
                {
                    if (ct.IsCancellationRequested) return;
                    await startFlow();
                }
                else
                {
                    // c. 等待超时 → 弹窗
                    MessageBox.Show(Application.Current.MainWindow,
                        "等待入口放板超时，请放入基板后重新开始",
                        "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"基板入口前置流程异常: {ex.Message}");
            }
        }

        /// <summary>暂停/继续检测</summary>
        public void PauseCheck()
        {
            // 检测运行中，或单板模式前置的运送/出板阶段（检测未启动）——都要能暂停（2026-08-19：单板运送阶段暂停无效修复）
            if (!isWorkFlowRun[WorkFlowType.Check] &&
                !isWorkFlowRun[WorkFlowType.TransportBoard] &&
                !isWorkFlowRun[WorkFlowType.SendToExit])
                return;

            if (_pauseCheckEvent.IsSet)
            {
                // 当前运行中 → 暂停（立即中断在途动作：PauseAwareWaitAsync 50ms 轮询使当前动作等待马上返回，
                // 检测状态机下一检查点转 Paused；3D扫描由状态机保证扫完；不用 Flush——会误伤运送/出板/采集在途指令）
                _pauseCheckEvent.Reset();
                UpdateStatus("检测已暂停，点【开始】继续");
            }
            else
            {
                // 当前已暂停 → 继续
                _pauseCheckEvent.Set();
                UpdateStatus("检测恢复中...");
            }
            PauseStateChanged?.Invoke();
        }

        /// <summary>纯暂停检测（固定按钮区「停止」）：与 PauseCheck 的区别——不切换，已暂停时无动作；仍发 PauseStateChanged 同步子菜单按钮文字；恢复只能点「开始」</summary>
        public void PauseCheckOnly()
        {
            // 检测运行中，或单板模式前置的运送/出板阶段（检测未启动）——都要能暂停（2026-08-19：单板运送阶段暂停无效修复）
            if (!isWorkFlowRun[WorkFlowType.Check] &&
                !isWorkFlowRun[WorkFlowType.TransportBoard] &&
                !isWorkFlowRun[WorkFlowType.SendToExit])
                return;
            if (_pauseCheckEvent.IsSet)
            {
                _pauseCheckEvent.Reset();
                UpdateStatus("检测已暂停，点【开始】继续");
                PauseStateChanged?.Invoke();
            }
        }

        /// <summary>在检测循环中检查暂停和取消。返回 true 表示需要退出循环</summary>
        private async Task<bool> CheckPauseAndCancelAsync(CancellationToken token)
        {
            if (token.IsCancellationRequested || _alarmStopping) return true;   // 报警终止同样拦截（含报警与启动竞态兜底）
            if (!_pauseCheckEvent.IsSet)
            {
                UpdateStatus("检测已暂停，点【开始】继续");
                // 异步等待，不阻塞 UI 线程（Task.Delay 会 yield 让出 UI 线程）
                while (!_pauseCheckEvent.IsSet)
                {
                    try
                    {
                        await Task.Delay(200, token);
                    }
                    catch (OperationCanceledException)
                    {
                        return true;
                    }
                }
                UpdateStatus("检测进行中...");
            }
            return false;
        }
        public void ContinuousRun() { _ = ContinuousRunInternal(); }
        public void StepRun() => MessageBox.Show("单步运行", "功能", MessageBoxButton.OK, MessageBoxImage.Information);

        // ==================== 连续模式(入板→检测→出板循环) ====================

        /// <summary>连续模式循环取消源（非空=连续模式运行中；volatile 保证复位线程/外壳线程跨线程可见——复位读引用判断外壳是否在跑）</summary>
        private volatile CancellationTokenSource _continuousCts;
        public bool IsContinuousFlowRunning => _continuousCts != null;

        /// <summary>连续模式暂停标志（停止按钮=暂停当前动作，点开始继续；检测段由 _pauseCheckEvent 暂停）</summary>
        private bool _continuousPaused;

        /// <summary>连续模式复位终止标志（ResetMachine 置位）：检测段立即终止不等当前板出板；区别于优雅停止（StopContinuousFlow 仍等检测完出板）</summary>
        private volatile bool _continuousAbort;

        /// <summary>连续模式当前板号（进行中=boardCount+1；外壳启动置1、每块完成++、结束清0）——状态栏文字统一带"(第 N 块)"括号</summary>
        private int _continuousBoardNo;

        // ==================== 全图采集运行状态（壳层） ====================

        /// <summary>全图采集是否运行中（含暂停中）——固定按钮区「停止」分发用</summary>
        public bool IsFullImageGrabRunning => isWorkFlowRun[WorkFlowType.FullImageGrab];

        /// <summary>纯暂停全图采集（固定「停止」按钮）：已暂停时重复点击无动作；恢复只能点「采集全图」按钮。暂停时解除按钮禁用（可点=恢复）</summary>
        public void PauseFullImageGrab()
        {
            if (!isWorkFlowRun[WorkFlowType.FullImageGrab]) return;
            if (_pauseGrabEvent.IsSet)
            {
                _pauseGrabEvent.Reset();
                UpdateStatus("全图采集已暂停（点「采集全图」继续）");
                ActionBusyStateChanged?.Invoke("grab_full", false);   // 暂停中按钮可点（点击=恢复采集）
            }
        }

        /// <summary>恢复全图采集（「采集全图」按钮点击时）：从当前点位重做（重新 MoveToAndWaitOK→GRAB）。恢复后按钮重新禁用</summary>
        public void ResumeFullImageGrab()
        {
            if (!isWorkFlowRun[WorkFlowType.FullImageGrab]) return;
            if (!_pauseGrabEvent.IsSet)
            {
                _pauseGrabEvent.Set();
                UpdateStatus("全图采集继续");
                ActionBusyStateChanged?.Invoke("grab_full", true);    // 恢复运行中，按钮重新禁用
            }
        }

        /// <summary>暂停连续模式：检测段立即暂停（现有机制），传送带段完成当前动作后段间等待</summary>
        public void PauseContinuousFlow()
        {
            if (!IsContinuousFlowRunning) { PauseCheckOnly(); return; }
            _continuousPaused = true;
            if (isWorkFlowRun[WorkFlowType.Check]) PauseCheckOnly();   // 检测段立即暂停（纯暂停，不切换）
            UpdateStatus("连续模式已暂停（点开始继续）");
        }

        /// <summary>继续连续模式（开始按钮）：解除暂停 + 恢复检测段</summary>
        public void ResumeContinuousFlow()
        {
            _continuousPaused = false;
            if (isWorkFlowRun[WorkFlowType.Check] && !_pauseCheckEvent.IsSet)
            {
                _pauseCheckEvent.Set();
                PauseStateChanged?.Invoke();
            }
            UpdateStatus("连续模式继续");
        }

        /// <summary>
        /// 切换工作模式(单板/连续)。任一流程运行中（检测/连续/全图采集等）禁止切换——运行中切换会与正在跑的状态机并发导致卡死，
        /// 须先复位清除（ResetMachine 清 isWorkFlowRun 与 _continuousCts）后再切换。
        /// </summary>
        public void SetWorkMode(bool continuous)
        {
            if (continuous == IsContinuousMode) return;
            if (isWorkFlowRun.Any())
            {
                MessageBox.Show(Application.Current.MainWindow, "流程运行中，请先复位后再切换工作模式",
                    "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            IsContinuousMode = continuous;
            UpdateStatus(continuous ? "已切换为连续模式（开始=连续检测）" : "已切换为单板模式（开始=单板检测）");
        }

        /// <summary>优雅停止连续模式：取消循环，当前板检测完→出板→循环结束（不卡板）</summary>
        public void StopContinuousFlow()
        {
            _continuousCts?.Cancel();
            UpdateStatus("连续模式停止中：当前板检测完出板后结束...");
        }

        /// <summary>
        /// 连续模式状态机（仿插针机 autoRun 主状态机，循环驱动 + Stopwatch 计时）：
        /// Init → LoadBoard(入板) → WaitCheck(等检测完成) → UnloadBoard(降顶板+出板)
        /// → WaitOutletClear(出口防堵) → 循环回 LoadBoard；End / Err 终态。
        /// 停止语义：取消后 WaitCheck 不打断（当前板检测完才出板），其余段立即收尾结束。
        /// </summary>
        private enum ContinuousState
        {
            Init,            // 准备：ResetIO、延时
            LoadBoard,       // 入板（TransportBoard 子流程）
            WaitCheck,       // 等待检测完成（_checkFlowCompleted；取消不打断）
            UnloadBoard,     // 出板（降顶板 + SendToExit 子流程；取消也执行收尾）
            WaitOutletClear, // 出口防堵：等出口板被取走（超时→Err）
            End,             // 终态：结束
            Err,             // 终态：异常
        }

        private async Task ContinuousRunInternal()
        {
            if (!CheckSafeOperation()) return;
            if (!CheckMotionConnected()) return;
            if (!CheckVision2DConnected()) return;
            if (!CheckVision3DConnected()) return;

            isWorkFlowRun[WorkFlowType.ContinuousRun] = true;
            _continuousAbort = false;   // 新循环开始，清除复位终止标志（finally 也会清，双保险防复位移交竞态）
            _continuousBoardNo = 1;     // 当前第 1 块（状态栏统一带板号括号）
            _continuousCts = new CancellationTokenSource();
            var token = _continuousCts.Token;
            UpdateStatus("连续模式启动");
            ActionBusyStateChanged?.Invoke("continuous_run", true);

            try
            {
                int outletClearTimeoutMs = (int)_sysParam.Data.ContinuousOutletClearTimeout;
                int boardCount = 0;
                var sw = new Stopwatch();
                bool outletWaitStarted = false;   // 出口防堵计时是否已开始（状态内首轮启动）
                var state = ContinuousState.Init;
                string err = null;

                // 状态机单步（闭包读写 sw/state/err 等）
                async Task<bool> Step()
                {
                    // 报警穿透：任何阶段立即终止（不等当前板检测完、不强行出板；收尾由 finally StopMotorAsync+ResetIO 兜底）
                    if (_alarmStopping || DeviceMonitor.Instance.State == DeviceState.Alarm)
                    {
                        err = "设备报警，连续模式已终止";
                        state = ContinuousState.Err;
                        return true;
                    }
                    // 复位终止：任何阶段立即 End（不等检测完/出板；板留原位由复位物理动作兜底）
                    // ——区别于优雅停止（StopContinuousFlow 只 Cancel 不置 abort，仍等当前板检测完出板）
                    if (_continuousAbort && token.IsCancellationRequested) { state = ContinuousState.End; return true; }
                    // 连续模式暂停：段间等待恢复（检测段由 _pauseCheckEvent 暂停，此处跳过避免双重等待）
                    // 暂停期间冻结超时计时（出口防堵 WaitOutletClear 的 Stopwatch），恢复后不会立刻超时（2026-08-14）
                    if (_continuousPaused && state != ContinuousState.WaitCheck)
                    {
                        bool freezeSw = state == ContinuousState.WaitOutletClear && outletWaitStarted;
                        if (freezeSw) sw.Stop();
                        while (_continuousPaused) { await Task.Delay(200); }
                        if (freezeSw) sw.Start();
                        return false;
                    }

                    switch (state)
                    {
                        case ContinuousState.Init:
                            if (token.IsCancellationRequested) { state = ContinuousState.End; break; }
                            await _motionIO.ResetIO();
                            await Task.Delay(200);
                            Log.Info("[Continuous] 连续模式: 初始化完成", "Continuous");
                            state = ContinuousState.LoadBoard;
                            break;

                        case ContinuousState.LoadBoard:
                            if (token.IsCancellationRequested) { state = ContinuousState.End; break; }
                            // a. 工作位已有板（未出完/手动放入）→ 跳过入板，直接检测
                            if (await _motionIO.GetIOState(InputSignal.WorkSensor, 1000))
                            {
                                Log.Info("[Continuous] 连续模式: 工作位已有板，跳过入板直接检测", "Continuous");
                                _checkFlowCompleted.Reset();
                                if (!await StartCheckFlow(true)) { err = "检测启动失败"; state = ContinuousState.Err; break; }
                                state = ContinuousState.WaitCheck;
                                break;
                            }
                            // b/d. 工作位无料 → 入板：入口有料直接运送；入口无料等待前机放板（超时=ContinuousEntryWaitTimeout）
                            Log.Info("[Continuous] 连续模式: 入板...", "Continuous");
                            if (!await TransportBoard(true, true))
                            {
                                if (_continuousAbort) { state = ContinuousState.End; break; }   // 复位终止：不弹窗直接结束
                                err = _alarmStopping ? "设备报警，入板已终止" : "入板失败";
                                state = ContinuousState.Err;
                                break;
                            }
                            _checkFlowCompleted.Reset();
                            if (!await StartCheckFlow(true)) { err = "检测启动失败"; state = ContinuousState.Err; break; }
                            state = ContinuousState.WaitCheck;
                            break;

                        case ContinuousState.WaitCheck:
                            // 等待检测完成；取消不打断——当前板检测完才出板（优雅停止语义；复位终止由 Step 顶部统一检查）
                            if (!_checkFlowCompleted.IsSet) { await Task.Delay(200); break; }
                            boardCount++;
                            _continuousBoardNo = boardCount + 1;   // 下一块进行中（状态栏统一带板号括号）
                            UpdateStatus($"连续模式: 第 {boardCount} 块检测完成");
                            Log.Info($"[Continuous] 连续模式: 第 {boardCount} 块检测完成", "Continuous");
                            state = ContinuousState.UnloadBoard;
                            break;

                        case ContinuousState.UnloadBoard:
                            // 出板：降顶板1/2 → 送出到出口（取消也执行，板不卡在机上）
                            await _motionIO.SetIO(OutSignal.Clamp1, false);
                            await _motionIO.SetIO(OutSignal.Clamp2, false);
                            Log.Info("[Continuous] 连续模式: 出板...", "Continuous");
                            if (!await SendToExit(true))
                            {
                                if (_continuousAbort) { state = ContinuousState.End; break; }   // 复位终止：不弹窗直接结束
                                err = "出板失败"; state = ContinuousState.Err; break;
                            }
                            if (token.IsCancellationRequested) { state = ContinuousState.End; break; }
                            outletWaitStarted = false;
                            state = ContinuousState.WaitOutletClear;
                            break;

                        case ContinuousState.WaitOutletClear:
                            if (token.IsCancellationRequested) { state = ContinuousState.End; break; }
                            // 出口防堵（仿插针机"出口2堵板不入板"联锁）：出口板未取走则等待，超时结束
                            bool outletHasBoard = await _motionIO.GetIOState(InputSignal.OutputSensor, 200);
                            if (!outletHasBoard)
                            {
                                state = ContinuousState.LoadBoard;   // 出口清空，继续下一块
                                break;
                            }
                            if (!outletWaitStarted) { outletWaitStarted = true; sw.Restart(); }
                            if (sw.ElapsedMilliseconds >= outletClearTimeoutMs)
                            {
                                err = "出口板未被取走（防堵超时）";
                                state = ContinuousState.Err;
                                break;
                            }
                            UpdateStatus("连续模式: 等待取走出口板...");
                            await Task.Delay(200);
                            break;
                    }
                    return state == ContinuousState.End || state == ContinuousState.Err;
                }

                await FlowLoopAsync(Step);

                if (state == ContinuousState.Err)
                {
                    UpdateStatus($"连续模式异常: {err}");
                    Log.Error($"[Continuous] 连续模式异常: {err}");
                    if (!_alarmStopping)   // 报警终止不重复弹窗（DeviceMonitor 报警弹窗已有）
                        MessageBox.Show(Application.Current.MainWindow, $"连续模式异常: {err}", "连续模式", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                else
                {
                    UpdateStatus($"连续模式已结束，共检测 {boardCount} 块");
                    Log.Info($"[Continuous] 连续模式结束，共检测 {boardCount} 块", "Continuous");
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log.Error($"[Continuous] 连续模式异常: {ex.Message}");
            }
            finally
            {
                // 安全收尾：停传送带 + 降顶板/阻挡
                try { await _motionIO.StopMotorAsync(); } catch { }
                try { await _motionIO.ResetIO(); } catch { }
                ActionBusyStateChanged?.Invoke("continuous_run", false);
                isWorkFlowRun[WorkFlowType.ContinuousRun] = false;
                _continuousAbort = false;   // 清除复位终止标志
                _continuousBoardNo = 0;     // 清除板号（结束后状态文字不再带括号）
                _continuousCts?.Dispose();
                _continuousCts = null;
                UpdateStatus("连续模式已停止");
            }
        }

        /// <summary>切换演示模式：点击后循环检测，再次点击停止</summary>
        /// <summary>
        /// 演示模式选中/取消（按钮语义=模式选项，仿单板/连续模式，**选中不启动检测**——点「开始」才启动演示循环）：
        /// 再点=取消选中（回退单板模式，运行中的单板检测不打断、自然执行完）；选中须单板模式（连续模式弹窗拦截）+ 无流程运行。
        /// </summary>
        public void SetDemoMode(bool enable)
        {
            if (enable)
            {
                // 已选中 → 取消选中（回退单板模式）：演示循环终止；运行中的单板检测不打断（_checkCts 独立，自然执行完）
                if (_isDemoMode)
                {
                    _isDemoMode = false;
                    _demoCts?.Cancel();
                    UpdateStatus("演示模式已取消，回到单板模式（当前检测执行完）");
                    DemoModeStateChanged?.Invoke();
                    return;
                }
                if (IsContinuousMode)
                {
                    MessageBox.Show(Application.Current.MainWindow, "连续模式下不允许演示模式，请先切换回单板模式",
                        "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (isWorkFlowRun.Any())
                {
                    MessageBox.Show(Application.Current.MainWindow, "流程运行中，请先复位后再启用演示模式",
                        "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                // 选中（仅选项，不启动检测——「开始」按钮负责启动）
                _isDemoMode = true;
                UpdateStatus("演示模式已开启，点「开始」开始演示检测");
                DemoModeStateChanged?.Invoke();
            }
            else
            {
                if (!_isDemoMode) return;
                _isDemoMode = false;
                _demoCts?.Cancel();
                StopCheckFlow();
                UpdateStatus("演示模式已停止");
                DemoModeStateChanged?.Invoke();
            }
        }

        /// <summary>
        /// 演示模式循环（点「开始」启动）：单板模式下循环检测；停止按钮=暂停检测（检测段暂停门）、
        /// 复位=_demoCts.Cancel 终止、取消选中（再点演示）=当前块检测完成后不再下一轮（检测本身不打断）。
        /// </summary>
        private async Task DemoLoopAsync()
        {
            _demoCts = new CancellationTokenSource();
            var token = _demoCts.Token;
            UpdateStatus("演示检测启动");
            try
            {
            while (!token.IsCancellationRequested)
            {
                if (!_isDemoMode) break;   // 取消选中（回退单板）：当前块检测完成后不再下一轮
                // 演示模式复用单板前置流程（规则a-d）：工作位有料→直接检测；入口有料→运板→检测；
                // 入口无料→等待放板→运板→检测（2026-08-19：原来直接 StartCheckFlow 跳过入口/基板位判断与运板，改为与单板一致）
                _ = SingleBoardCheckFlowAsync(token);

                // 200ms 缓冲：让本轮流程置位（GetIOState/CheckSafeOperation 异步前置阶段 isWorkFlowRun.Any() 可能仍为 false，防重复启动下一轮）
                if (!token.IsCancellationRequested)
                    await Task.Delay(200, token);

                // 非阻塞等待本轮前置+检测流程结束（暂停时流程挂起，循环自然等待；取消选中/复位时立即退出）
                while (!token.IsCancellationRequested && isWorkFlowRun.Any())
                {
                    await Task.Delay(200, token);
                }

                // 循环间隔，避免空转
                if (!token.IsCancellationRequested)
                    await Task.Delay(500, token);
            }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log.Error($"演示模式异常: {ex.Message}");
            }
            finally
            {
                // 仅当本循环仍是当前循环时清理（防取消选中后快速重选演示的竞态——新循环持有新 CTS）
                if (_demoCts != null && _demoCts.Token == token)
                {
                    _demoCts.Dispose();
                    _demoCts = null;
                    _isDemoMode = false;
                    DemoModeStateChanged?.Invoke();
                }
            }
        }

        #region 精度检测

        /// <summary>精度检测：重复检测 N 次，通过坐标匹配统计每针的重复性指标</summary>
        public async void StartPrecisionCheck()
        {
            if (isWorkFlowRun.Any())
            {
                MessageBox.Show(Application.Current.MainWindow, "当前有其他操作正在运行");
                return;
            }
            if (!CheckMotionConnected()) return;

            if (DetectPositions == null || DetectPositions.Count == 0)
            {
                MessageBox.Show(Application.Current.MainWindow, "请先在拍照位列表中勾选检测位", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (ProjectManager.Instance.CurrentProject == null)
            {
                MessageBox.Show(Application.Current.MainWindow, "请先加载方案", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_imageManager.ImageCount == 0)
            {
                MessageBox.Show(Application.Current.MainWindow, "当前方案没有图片数据，请先加载方案或采集全图", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            int repeatCount = _sysParam.Data.PrecisionRepeatCount;
            if (repeatCount < 2) repeatCount = 2;

            if (MessageBox.Show(Application.Current.MainWindow, $"精度检测将连续执行 {repeatCount} 次完整检测流程，是否继续？",
                "精度检测", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            _precisionRuns = new List<List<PinResult>>();
            _isPrecisionMode = true;
            _precisionCts = new CancellationTokenSource();
            var startTime = DateTime.Now;
            int completedRuns = 0;

            try
            {
                for (int i = 0; i < repeatCount; i++)
                {
                    if (_precisionCts.Token.IsCancellationRequested) break;

                    UpdateStatus($"精度检测 ({i + 1}/{repeatCount})");

                    // 启动单次检测（完成后 _checkFlowCompleted 会被 Set）
                    _checkFlowCompleted.Reset();
                    StartCheckFlow();

                    // 非阻塞等待检测完成
                    while (!_checkFlowCompleted.IsSet && !_precisionCts.Token.IsCancellationRequested)
                    {
                        await Task.Delay(200);
                    }

                    completedRuns++;

                    if (_precisionCts.Token.IsCancellationRequested) break;

                    // 快照本次检测结果
                    _precisionRuns.Add(PinResults.ToList());
                    Log.Info($"[精度检测] 第{i + 1}轮完成，捕获 {PinResults.Count} 个针脚");

                    // 2026-08-27 (c)：每一次检测结果也要保存到历史结果（Result/Result_{ts}_Run{n}/，
                    // 时间戳前置：历史窗口按时间排序/解析正常；Run{n} 区分轮次；沿用 Result_* 前缀）
                    try
                    {
                        _currentResultFolder = $"Result_{DateTime.Now:yyyyMMdd_HHmmss}_Run{i + 1}";
                        string runDir = GetResultDir();
                        if (runDir != null)
                        {
                            string json = JsonConvert.SerializeObject(PinResults.ToList(), Formatting.Indented);
                            File.WriteAllText(Path.Combine(runDir, "DetectResults.json"), json);
                            if (_vmOverlays != null && _vmOverlays.Count > 0)
                            {
                                string vmJson = JsonConvert.SerializeObject(_vmOverlays, Formatting.Indented);
                                File.WriteAllText(Path.Combine(runDir, "VmOverlays.json"), vmJson);
                            }
                            SaveDetectPositionsToHistory();
                            Log.Info($"[精度检测] 第{i + 1}轮结果已保存到历史: {runDir}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warning($"[精度检测] 保存第{i + 1}轮历史结果失败: {ex.Message}");
                    }
                }

                // 坐标匹配 + 统计分析
                if (_precisionRuns.Count >= 2)
                {
                    var stats = ComputePrecisionStats(_precisionRuns);
                    // 2026-08-27 优化：剔除 dx/dy 全为 0 的无效 ID（σdx==0 且 σdy==0，该针 2D 偏差未采到/匹配失败默认 0），
                    // 否则 0 值混入统计 → σ 均值虚低、极差"优秀"占比虚高（桌面 精度报告_20260827_114606 复现：348→328 ID，σdx 0.0080→0.0085、σdy 0.0092→0.0098）
                    var validStats = stats.Where(s => !(s.DxStdDev == 0 && s.DyStdDev == 0)).ToList();
                    if (validStats.Count != stats.Count)
                    {
                        Log.Warning($"[精度检测] 剔除 {stats.Count - validStats.Count} 个 dx/dy 全为0的无效ID（σdx=0且σdy=0），有效样本 {validStats.Count} 个");
                        foreach (var s in stats.Where(s => s.DxStdDev == 0 && s.DyStdDev == 0))
                            Log.Warning($"  [剔除] Grid={s.GridIndex} PinIdx={s.PinIndex} kind={s.PinType} (X={s.X:F3},Y={s.Y:F3})");
                    }
                    stats = validStats;
                    double avgDxStd = stats.Count > 0 ? stats.Average(s => s.DxStdDev) : 0;
                    double avgDyStd = stats.Count > 0 ? stats.Average(s => s.DyStdDev) : 0;
                    double avgHStd = stats.Count > 0 ? stats.Average(s => s.HStdDev) : 0;
                    double composite = stats.Count > 0 ? stats.Average(s => Math.Sqrt(s.DxStdDev * s.DxStdDev + s.DyStdDev * s.DyStdDev)) : 0;

                    // 极差分布统计（全体，良好→严重）
                    int rExcellent = 0, rNormal = 0, rSlightly = 0, rLarge = 0, rSevere = 0;
                    foreach (var s in stats)
                    {
                        double maxRange = Math.Max(s.DxRange, s.DyRange);
                        if (maxRange <= 0.01) rExcellent++;
                        if (maxRange <= 0.02) rNormal++;      // ≤0.02包含≤0.01
                        else if (maxRange <= 0.03) rSlightly++;
                        else if (maxRange <= 0.04) rLarge++;
                        else rSevere++;
                    }

                    // 2026-08-27 (f)：良好=>严重 再按 kind（A/B/C/D...）细分
                    var kindRanges = new List<Models.KindRangeStat>();
                    foreach (var g in stats.GroupBy(s => string.IsNullOrEmpty(s.PinType) ? "A" : s.PinType))
                    {
                        int kExl = 0, kNor = 0, kSli = 0, kLar = 0, kSev = 0;
                        foreach (var s in g)
                        {
                            double maxRange = Math.Max(s.DxRange, s.DyRange);
                            if (maxRange <= 0.01) kExl++;
                            if (maxRange <= 0.02) kNor++;
                            else if (maxRange <= 0.03) kSli++;
                            else if (maxRange <= 0.04) kLar++;
                            else kSev++;
                        }
                        kindRanges.Add(new Models.KindRangeStat
                        {
                            Kind = g.Key,
                            Total = g.Count(),
                            Excellent = kExl,
                            Normal = kNor,
                            SlightlyLarge = kSli,
                            Large = kLar,
                            Severe = kSev,
                        });
                    }

                    var summary = new Models.PrecisionReportSummary
                    {
                        TotalRuns = completedRuns,
                        TotalPins = stats.Count,
                        StartTime = startTime,
                        Duration = DateTime.Now - startTime,
                        AvgDxStdDev = avgDxStd,
                        AvgDyStdDev = avgDyStd,
                        AvgHStdDev = avgHStd,
                        CompositeRepeatability = composite,
                        RangeExcellent = rExcellent,
                        RangeNormal = rNormal,
                        RangeSlightlyLarge = rSlightly,
                        RangeLarge = rLarge,
                        RangeSevere = rSevere,
                        KindRanges = kindRanges,
                    };

                    // 保存精度报告到 Report/ 目录
                    string reportName = $"Report_{startTime:yyyyMMdd_HHmmss}";
                    string reportDir = Path.Combine(ProjectManager.Instance.CurrentProjectPath, "Report", reportName);
                    try
                    {
                        if (!Directory.Exists(reportDir)) Directory.CreateDirectory(reportDir);
                        string jsonPath = Path.Combine(reportDir, "report.json");
                        var reportData = new { summary, stats };
                        string json = Newtonsoft.Json.JsonConvert.SerializeObject(reportData, Newtonsoft.Json.Formatting.Indented);
                        File.WriteAllText(jsonPath, json);
                        Log.Info($"精度报告已保存: {jsonPath}");
                    }
                    catch (Exception ex)
                    {
                        Log.Warning($"保存精度报告失败: {ex.Message}");
                    }

                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        var reportWindow = new Views.PrecisionReportWindow(summary, stats, reportName);
                        reportWindow.Owner = Application.Current.MainWindow;
                        reportWindow.ShowDialog();
                    });
                }
                else
                {
                    _ = MessageBox.Show(Application.Current.MainWindow, "精度检测数据不足，无法生成报告", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"精度检测异常: {ex.Message}");
                MessageBox.Show(Application.Current.MainWindow, $"精度检测失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _isPrecisionMode = false;
                _precisionCts?.Dispose();
                _precisionCts = null;
                UpdateStatus("精度检测完成");
            }
        }

        /// <summary>通过坐标（X,Y）匹配同一针脚，计算每针的重复性统计</summary>
        private List<Models.PrecisionPinStat> ComputePrecisionStats(List<List<PinResult>> runs)
        {
            if (runs == null || runs.Count < 2) return new List<Models.PrecisionPinStat>();

            var refRun = runs[0];
            var stats = new List<Models.PrecisionPinStat>();

            // ====== 诊断日志：每轮针数 ======
            Log.Info("========== 精度匹配开始 ==========");
            for (int i = 0; i < runs.Count; i++)
                Log.Info($"  第{i + 1}轮: {runs[i].Count} 个针脚");

            Log.Info($"  参考轮(第1轮): {refRun.Count} 个针脚");
            int matchedCount = 0, failedCount = 0;

            foreach (var refPin in refRun)
            {
                var dxList = new List<double>();
                var dyList = new List<double>();
                var hList = new List<double>();
                // 2026-08-27 每轮明细：参考轮(第1轮) 必在，后续轮匹配成功则记录；漏检/超阈值失败 → Matched=false（明细表显示空）
                var samples = new List<Models.PrecisionSample>();
                // 按 轮次序号(1-based) 记录：初始填充 1..runs.Count 全部为未匹配，匹配时回填
                for (int r = 0; r < runs.Count; r++)
                    samples.Add(new Models.PrecisionSample { RunIndex = r + 1, Matched = false });

                // 解析参考针的 dx/dy（2026-08-27：同一针以 X/Y 最近距离判定，不以 PinId 判定——漏检时 PinId 会漂移）
                double refDx = TryParseDouble(refPin.DiffX);
                double refDy = TryParseDouble(refPin.DiffY);
                dxList.Add(refDx);
                dyList.Add(refDy);
                hList.Add(refPin.H);
                samples[0].Dx = refDx; samples[0].Dy = refDy; samples[0].H = refPin.H; samples[0].Matched = true;

                bool anyFail = false;
                double minDistAllRuns = double.MaxValue; // 所有轮次中的最小距离

                // 在后续轮次中匹配同一针脚（以 X/Y 距离判定，非 PinId）
                for (int r = 1; r < runs.Count; r++)
                {
                    var run = runs[r];
                    var nearest = run
                        .Select(p => new
                        {
                            Pin = p,
                            Dist = Math.Sqrt(Math.Pow(p.X - refPin.X, 2) + Math.Pow(p.Y - refPin.Y, 2))
                        })
                        .OrderBy(x => x.Dist)
                        .FirstOrDefault();

                    if (nearest != null)
                    {
                        minDistAllRuns = Math.Min(minDistAllRuns, nearest.Dist);

                        if (nearest.Dist < 1.0) // 匹配阈值 1mm
                        {
                            double dx = TryParseDouble(nearest.Pin.DiffX);
                            double dy = TryParseDouble(nearest.Pin.DiffY);
                            double h = nearest.Pin.H;
                            dxList.Add(dx);
                            dyList.Add(dy);
                            hList.Add(h);
                            samples[r].Dx = dx; samples[r].Dy = dy; samples[r].H = h; samples[r].Matched = true;
                        }
                        else
                        {
                            anyFail = true;
                            Log.Warning($"  [匹配失败] 第{r + 1}轮: Grid={refPin.GridIndex} " +
                                $"PinIdx={refPin.PinIndex} 参考(X={refPin.X:F3},Y={refPin.Y:F3}) " +
                                $"最近针(X={nearest.Pin.X:F3},Y={nearest.Pin.Y:F3}) " +
                                $"距离={nearest.Dist:F4}mm > 1mm阈值");
                        }
                    }
                    else
                    {
                        anyFail = true;
                        Log.Warning($"  [匹配失败] 第{r + 1}轮: Grid={refPin.GridIndex} " +
                            $"PinIdx={refPin.PinIndex} 参考(X={refPin.X:F3},Y={refPin.Y:F3}) " +
                            $"无任何针脚数据（该轮为空）");
                    }
                }

                // 2026-08-27 (b)：高度统计排除 H==0 的样本（系统偶发返回0，混入会导致极差/σ异常偏大）；
                // dx/dy 统计与每轮明细保留原始值（明细表如实展示每轮结果）
                var hValid = hList.Where(h => h > 0).ToList();
                double hMean = hValid.Count > 0 ? hValid.Average() : 0;
                double hStd = hValid.Count > 0 ? SampleStdDev(hValid) : 0;
                double hMin = hValid.Count > 0 ? hValid.Min() : 0;
                double hMax = hValid.Count > 0 ? hValid.Max() : 0;
                double hRange = hValid.Count > 0 ? hMax - hMin : 0;

                if (dxList.Count >= 2)
                {
                    matchedCount++;
                    stats.Add(new Models.PrecisionPinStat
                    {
                        PINID = refPin.PINID,
                        DetectIndex = refPin.DetectIndex,
                        GridIndex = refPin.GridIndex,
                        PinIndex = refPin.PinIndex,
                        PinType = string.IsNullOrEmpty(refPin.PinType) ? "A" : refPin.PinType,   // 2026-08-27 (d/f)：kind
                        X = refPin.X,
                        Y = refPin.Y,
                        SampleCount = dxList.Count,
                        Samples = samples,   // 2026-08-27 (d)：每轮明细

                        DxMean = dxList.Average(),
                        DxStdDev = SampleStdDev(dxList),
                        DxMin = dxList.Min(),
                        DxMax = dxList.Max(),
                        DxRange = dxList.Max() - dxList.Min(),

                        DyMean = dyList.Average(),
                        DyStdDev = SampleStdDev(dyList),
                        DyMin = dyList.Min(),
                        DyMax = dyList.Max(),
                        DyRange = dyList.Max() - dyList.Min(),

                        HMean = hMean,
                        HStdDev = hStd,
                        HMin = hMin,
                        HMax = hMax,
                        HRange = hRange,
                    });
                }
                else
                {
                    failedCount++;
                    // 匹配失败但至少参考轮有数据 → 跨轮无匹配
                    Log.Warning($"  [排除] Grid={refPin.GridIndex} PinIdx={refPin.PinIndex} " +
                        $"(X={refPin.X:F3},Y={refPin.Y:F3}) 仅有{dxList.Count}轮匹配成功，被排除，" +
                        $"所有轮次中最近距离={minDistAllRuns:F4}mm");
                }
            }

            Log.Info($"========== 精度匹配结束 ==========");
            Log.Info($"  参考轮针数: {refRun.Count}");
            Log.Info($"  匹配成功(≥2轮): {matchedCount}");
            Log.Info($"  匹配失败(仅参考轮): {failedCount}");
            Log.Info($"  报告针数: {stats.Count}");

            // 生成序号（从1开始）
            for (int i = 0; i < stats.Count; i++)
                stats[i].Seq = i + 1;

            return stats;
        }

        private static double TryParseDouble(string s)
        {
            double.TryParse(s, out double v);
            return v;
        }

        private static double SampleStdDev(List<double> values)
        {
            if (values.Count < 2) return 0;
            double mean = values.Average();
            double sumSq = values.Sum(v => (v - mean) * (v - mean));
            return Math.Sqrt(sumSq / (values.Count - 1));
        }

        #endregion

        // ==================== 重新采集所有检测位图片（替换采集FOV） ====================

        /// <summary>重新采集所有检测位的图片（原采集FOV功能），每位移到中心→GRAB→替换瓦片</summary>
        public async void ReGrabDetectPositionsAsync()
        {
            if (!CheckSafeOperation()) return;
            if (!CheckMotionConnected()) return;
            if (!_vision2DClient.IsConnected)
            {
                MessageBox.Show(Application.Current.MainWindow, "2D相机未连接", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (DetectPositions == null || DetectPositions.Count == 0)
            {
                MessageBox.Show(Application.Current.MainWindow, "请先在拍照位列表中勾选检测位", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var project = ProjectManager.Instance.CurrentProject;
            if (project == null)
            {
                MessageBox.Show(Application.Current.MainWindow, "请先加载方案", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (_imageManager.ImageCount == 0)
            {
                MessageBox.Show(Application.Current.MainWindow, "当前方案没有图片数据，请先加载方案或采集全图", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            isWorkFlowRun[WorkFlowType.FovGrab] = true;
            string projectImagesDir = ProjectManager.Instance.GetOriginPath();
            int totalPositions = DetectPositions.Count;
            int successCount = 0;

            try
            {
                await SetVmCameraExposure();

                // 调焦至板面焦距
                double focusTarget = project.BoardFocus;
                string focusName = "板面";
                if (focusTarget > 0)
                {
                    UpdateStatus($"调Z轴至{focusName}焦距({focusTarget}mm)...");
                    bool zOk = await RetryAsync(
                        () => _motionIO.MoveToAndWaitOK(z: focusTarget, timeoutMs: (int)_sysParam.Data.MoveZTimeout),
                        maxRetries: 3, delayMs: 200);
                    if (!zOk)
                        Log.Warning($"调Z轴至{focusName}焦距失败，可能影响图像质量");
                    else
                        await Task.Delay(100);
                }

                for (int idx = 0; idx < totalPositions; idx++)
                {
                    var pos = DetectPositions[idx];
                    UpdateStatus($"重新采集 {idx + 1}/{totalPositions} (GrabIndex={pos.GrabIndex})...");

                    string result = await GrabAndReplaceTileAsync(pos, idx, totalPositions, projectImagesDir, CancellationToken.None);
                    if (result != null)
                    {
                        // 清空该检测位的旧检测结果
                        _vmOverlays.RemoveAll(o => o.GridIndex == pos.GrabIndex);
                        await Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            var toRemove = PinResults.Where(p => p.GridIndex == pos.GrabIndex).ToList();
                            foreach (var r in toRemove) PinResults.Remove(r);
                        });
                        UpdateStatistics();
                        successCount++;
                    }
                }

                UpdateStatus($"重新采集完成：成功 {successCount}/{totalPositions}");
            }
            finally
            {
                isWorkFlowRun[WorkFlowType.FovGrab] = false;
            }
        }

        /// <summary>移动到检测位中心 → GRAB(重试×3) → 动态等文件 → 替换瓦片（token 取消时 GRAB 中断抛 OCE 由调用方状态机处理）</summary>
        private async Task<string> GrabAndReplaceTileAsync(DetectPosition pos, int positionIndex,
            int totalPositions, string projectImagesDir, CancellationToken token)
        {

            UpdateStatus($"检测位 {positionIndex + 1}/{totalPositions} 移动到中心点");
            // 2026-08-24 优化：删除移动前固定 Task.Delay(200)——MoveToAndWaitOK 内部已等轴完成，
            // 200ms 纯白等（单检测位确定性省 200ms）；UpdateStatus 为属性绑定不依赖此延迟
            // 暂停感知：移动中暂停→立即返回（状态机转 Paused，恢复后当前位整拍重做；轴物理走完当前段，安全）（2026-08-19）
            bool movedToCenter = await PauseAwareWaitAsync(RetryAsync(
                () => _motionIO.MoveToAndWaitOK(x: pos.X, y: pos.Y, timeoutMs: (int)_sysParam.Data.MoveToCenterTimeout),
                maxRetries: 3, delayMs: 200), token);
            if (!_pauseCheckEvent.IsSet) return null;   // 暂停中断：返回 null，外层 GrabTile 转 Paused（不按失败处理）
            if (!movedToCenter || token.IsCancellationRequested)
            {
                if (!token.IsCancellationRequested)
                    Log.Error($"检测位 {positionIndex} 移动到中心点失败，已重试3次");
                return null;
            }
            //await Task.Delay(200);
            UpdateStatus($"检测位 {positionIndex + 1}/{totalPositions} 重新采集中...");
            // 先发 SET_PATH（内部等回执=VM已接收，2026-08-25），再 GRAB（只发一次，不再重试）
            await SetVmSavePathToOrigin();
            // 2026-08-25：SET_PATH 后保留短延时（SetVmSavePathDelay 参数化，默认100可调小）——
            // 回执只确认"VM已设置变量"，保存模块（订阅 ImageSavePath）应用新路径有异步传播窗口，延时兜底
            await Task.Delay((int)_sysParam.Data.SetVmSavePathDelay, token);
            // 暂停感知：拍照等待中暂停→立即返回（在途 GRAB 后台收尾，恢复后整拍重做；不打断已发出的拍照）
            string grabResult = await PauseAwareWaitAsync(
                _vision2DClient.SendCommandAndWaitAsync("GRAB", "GRAB_OK", (int)_sysParam.Data.GrabTimeout, token), token);
            if (!_pauseCheckEvent.IsSet) return null;
            if (grabResult == "TIMEOUT")
            {
                // 拍照超时不弹窗（Err 收尾统一弹窗），返回 null → 检测状态机转异常终止（2026-08-14）
                Log.Error($"检测位 {positionIndex} 拍照超时");
                return null;
            }
            if (!grabResult.StartsWith("GRAB_OK")) return null;

            // 等待 VM 文件就绪（使用系统参数超时）
            string vmFileName = grabResult;
            string vmSourcePath = Path.Combine(projectImagesDir, vmFileName);
            // 等待文件就绪（确保VM写入完成）；暂停感知：暂停→立即返回（2026-08-19）
            int vmTimeout = (int)_sysParam.Data.WaitForVmSaveTimeout;
            if (!await PauseAwareWaitAsync(WaitForFileReadyAsync(vmSourcePath, vmTimeout), token))
            {
                if (!_pauseCheckEvent.IsSet) return null;   // 暂停中断
                if (token.IsCancellationRequested) return null;   // 取消优先：不等兜底
                // 2026-08-25 主题② A：等待超时 ≠ 文件不存在——快查分流（阶段2 独占打不开/尺寸未稳、
                // 文件在超时边缘写完等"存图了但上位机未感知"场景，文件其实已在目标路径；原实现不复查
                // vmSourcePath 直接判失败，导致位0 首拍偶发 15s 空等，手动停止/重启后才继续（实机复现）。
                // VM 无兜底存图目录（只存 ImageSavePath 指定路径），D:\GrabImage 旧兜底已确认不存在，删除。
                if (!File.Exists(vmSourcePath))
                {
                    // 路径未生效诊断：文件若落在上次 SET_PATH 目录 → 保存模块未应用新路径（订阅异步传播问题），
                    // 该位由板末重扫重拍重试（重扫重新 SET_PATH，路径生效后自愈）
                    if (!string.IsNullOrEmpty(_lastVmSavePath)
                        && !string.Equals(_lastVmSavePath, projectImagesDir, StringComparison.OrdinalIgnoreCase)
                        && File.Exists(Path.Combine(_lastVmSavePath, vmFileName)))
                        Log.Error($"[路径未生效] {vmFileName} 落在上次SET_PATH目录({_lastVmSavePath})，本次期望({projectImagesDir})——VM保存模块未应用新路径，请检查ImageSavePath订阅/延时");
                    Log.Warning($"[替换] VM 文件等待超时且文件不存在: {vmFileName}（该位记录板末延迟重扫）");
                    return null;
                }
                Log.Warning($"[替换] VM 文件就绪判定超时但文件已存在（存图成功未感知），按就绪继续: {vmFileName}");
            }

            // 找到当前 GrabIndex 对应的目标文件
            string targetFile = null;
            var existPaths = _imageManager.GetImagePaths();
            var existIndices = _imageManager.GetGridIndices();
            for (int j = 0; j < existIndices.Count; j++)
            {
                if (existIndices[j] == pos.GrabIndex)
                {
                    targetFile = Path.GetFileName(existPaths[j]);
                    break;
                }
            }
            if (targetFile == null)
            {
                Log.Warning($"[替换] 未找到 GrabIndex={pos.GrabIndex} 对应的原图");
                return null;
            }

            // 执行替换（删除旧文件 → 拷贝新文件 → 删除源文件），带重试
            string targetPath = Path.Combine(projectImagesDir, targetFile);
            // 源==目标（GRAB 文件名与瓦片同名，如模拟器固定文件名）：文件已就位，跳过删除/复制，防止"删掉源再复制失败"（2026-08-14 修复）
            if (string.Equals(vmSourcePath, targetPath, StringComparison.OrdinalIgnoreCase))
            {
                return vmSourcePath;
            }
            bool replaced = await RetryAsync(
                () =>
                {
                    // 步骤1：删除旧目标文件
                    try
                    {
                        if (File.Exists(targetPath))
                        {
                            File.SetAttributes(targetPath, FileAttributes.Normal);
                            File.Delete(targetPath);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warning($"[替换] 删除旧文件失败 target={targetPath}: {ex.Message}");
                        return Task.FromResult(false);
                    }

                    // 步骤2：复制新文件到目标
                    try
                    {
                        File.Copy(vmSourcePath, targetPath);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning($"[替换] 复制失败 source={vmSourcePath} → target={targetPath}: {ex.Message}");
                        return Task.FromResult(false);
                    }

                    // 步骤3：删除源文件
                    try
                    {
                        if (File.Exists(vmSourcePath))
                            File.Delete(vmSourcePath);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning($"[替换] 源文件删除失败(文件可能滞留) source={vmSourcePath}: {ex.Message}");
                    }

                    return Task.FromResult(true);
                },
                maxRetries: 5, delayMs: 100);
            if (!replaced)
            {
                Log.Error($"[替换] 替换失败，已重试5次");
                return null;
            }

            // 更新拼接图显示
            var project = ProjectManager.Instance.CurrentProject;
            _imageManager.ReplaceTile(pos.GrabIndex, targetPath,
                stitched => _vision.DisplayImageByImage(stitched),
                _sysParam.ImageDisplayRows, _sysParam.ImageDisplayCols,
                project.FovWidth, project.FovHeight, project.Overlap);

            return targetPath;
        }

        /// <summary>
        /// 单次抓图：移动到中心点 → 调焦 → SET_PATH → GRAB → 等待文件就绪 → 替换瓦片(板面图) → 返回路径
        /// </summary>
        private async Task<string> GrabOneAsync(double focusZ, string saveSubDir,
            DetectPosition pos, int positionIndex, int totalPositions, string projectImagesDir, CancellationToken token)
        {
            UpdateStatus($"检测位 {positionIndex + 1}/{totalPositions} 移动到中心点并调焦");
            // 2026-08-24 优化（1a Blob 路径补删）：删除移动前固定 Task.Delay(200)——与 GrabAndReplaceTileAsync 同型，
            // MoveToAndWaitOK 内部已等轴完成，200ms 纯白等（Blob 模式每张图一个，板面+针尖每拍 400ms）
            // 2026-08-24 优化（8 合并）：移动中心(X/Y)与调Z 合并为一次 MoveToAndWaitOK(x,y,z)——多轴并发运动，
            // 省一次指令往返与串行等待（原 XY 与 Z 两段串行）；调Z 仅 focusZ>0 时参与；状态栏文字同步合并为一条。
            // 暂停感知：移动中暂停→立即返回（状态机转 Paused，恢复后当前位整拍重做）（2026-08-19）
            bool movedToCenter = await PauseAwareWaitAsync(RetryAsync(
                () => _motionIO.MoveToAndWaitOK(
                    x: pos.X, y: pos.Y, z: focusZ > 0 ? (double?)focusZ : null,
                    timeoutMs: (int)_sysParam.Data.MoveToCenterTimeout),
                maxRetries: 3, delayMs: 200), token);
            if (!_pauseCheckEvent.IsSet) return null;   // 暂停中断
            if (!movedToCenter || token.IsCancellationRequested)
            {
                if (!token.IsCancellationRequested)
                    Log.Error($"检测位 {positionIndex} 移动到中心点/调焦失败，已重试3次");
                return null;
            }
            // 调焦后的稳定等待（曝光前光学稳定保险）；2026-08-25 参数化：用系统参数"到位拍照延时"
            // PositionSnapshotDelay（默认200，可界面调参找最合适值；token 使复位/停止可立即中断）
            if (focusZ > 0) await Task.Delay((int)_sysParam.Data.PositionSnapshotDelay, token);

            // 设置保存路径（状态文字区分板面图/针尖图：采集中(Origin) → 采集针尖中(PinX)）
            UpdateStatus(saveSubDir == "Origin"
                ? $"检测位 {positionIndex + 1}/{totalPositions} 采集中(Origin)..."
                : $"检测位 {positionIndex + 1}/{totalPositions} 采集针尖中({saveSubDir})...");
            string fullDir;
            if (saveSubDir == "Origin")
            {
                await SetVmSavePathToOrigin();
                fullDir = projectImagesDir;
            }
            else
            {
                string pinDir = ProjectManager.Instance.GetPinPath();
                string saveDir = Path.Combine(pinDir, saveSubDir);
                if (!Directory.Exists(saveDir)) Directory.CreateDirectory(saveDir);
                await SetVmSavePath(saveDir);
                fullDir = saveDir;
            }
            // 2026-08-25：SET_PATH 后保留短延时（SetVmSavePathDelay 参数化）——回执仅确认VM已接收，
            // 保存模块（订阅 ImageSavePath）应用传播由延时兜底，见 GrabAndReplaceTileAsync 同型注释
            await Task.Delay((int)_sysParam.Data.SetVmSavePathDelay, token);

            // GRAB（只发一次，不再重试；token 取消时抛 OCE 由调用方状态机统一处理，不按超时弹窗）
            // 暂停感知：拍照等待中暂停→立即返回（在途 GRAB 后台收尾，恢复后整拍重做；不打断已发出的拍照）（2026-08-19）
            string grabResult = await PauseAwareWaitAsync(
                _vision2DClient.SendCommandAndWaitAsync("GRAB", "GRAB_OK", (int)_sysParam.Data.GrabTimeout, token), token);
            if (!_pauseCheckEvent.IsSet) return null;   // 暂停中断

            if (grabResult == "TIMEOUT" || !grabResult.StartsWith("GRAB_OK"))
            {
                // 拍照超时不弹窗（Err 收尾统一弹窗），返回 null → 检测状态机转异常终止（2026-08-14）
                Log.Error($"检测位 {positionIndex} 拍照超时 (subDir={saveSubDir})");
                return null;
            }

            // 等待文件就绪；暂停感知：暂停→立即返回（2026-08-19）
            string vmFileName = grabResult;
            string sourcePath = Path.Combine(fullDir, vmFileName);
            int vmTimeout = (int)_sysParam.Data.WaitForVmSaveTimeout;
            if (!await PauseAwareWaitAsync(WaitForFileReadyAsync(sourcePath, vmTimeout), token))
            {
                if (!_pauseCheckEvent.IsSet) return null;   // 暂停中断
                if (token.IsCancellationRequested) return null;   // 取消优先：不等兜底
                // 2026-08-25 主题② A：同 GrabAndReplaceTileAsync——等待超时 ≠ 文件不存在，快查分流
                // （存图成功未感知场景继续，防 15s 空等）；VM 无兜底存图目录，删除 D:\GrabImage fallback
                if (!File.Exists(sourcePath))
                {
                    // 路径未生效诊断：文件若落在上次 SET_PATH 目录 → 保存模块未应用新路径，该位板末重扫重试
                    if (!string.IsNullOrEmpty(_lastVmSavePath)
                        && !string.Equals(_lastVmSavePath, fullDir, StringComparison.OrdinalIgnoreCase)
                        && File.Exists(Path.Combine(_lastVmSavePath, vmFileName)))
                        Log.Error($"[路径未生效] {vmFileName} 落在上次SET_PATH目录({_lastVmSavePath})，本次期望({fullDir})——VM保存模块未应用新路径，请检查ImageSavePath订阅/延时");
                    Log.Warning($"[GrabOne] 文件等待超时且文件不存在: {vmFileName}（该位记录板末延迟重扫）");
                    return null;
                }
                Log.Warning($"[GrabOne] 文件就绪判定超时但文件已存在（存图成功未感知），按就绪继续: {vmFileName}");
            }

            // 板面图：替换瓦片
            if (saveSubDir == "Origin")
            {
                string targetFile = null;
                var existPaths = _imageManager.GetImagePaths();
                var existIndices = _imageManager.GetGridIndices();
                for (int j = 0; j < existIndices.Count; j++)
                {
                    if (existIndices[j] == pos.GrabIndex)
                    {
                        targetFile = Path.GetFileName(existPaths[j]);
                        break;
                    }
                }
                if (targetFile == null)
                {
                    Log.Warning($"[GrabOne] 未找到 GrabIndex={pos.GrabIndex} 对应的原图");
                    // 返回源文件路径供调用者使用
                    return sourcePath;
                }

                string targetPath = Path.Combine(projectImagesDir, targetFile);
                // 源==目标（GRAB 文件名与瓦片同名，如模拟器固定文件名）：文件已就位，跳过删除/复制，防止"删掉源再复制失败"（2026-08-14 修复）
                if (string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase))
                {
                    return sourcePath;
                }
                // 目标文件可能被针尖图层/PinPriority替换为Pin路径，实际不在Origin目录
                // 此时如果复制会创建不存在的路径名导致文件数+1，直接保留新GRAB文件
                if (!File.Exists(targetPath))
                {
                    Log.Warning($"[GrabOne] 目标文件不在Origin目录(被针尖图层替换)，保留新GRAB文件: {sourcePath}");
                    // 更新ImageManager路径指向新GRAB文件（后续检测会用正确路径）
                    var proj = ProjectManager.Instance.CurrentProject;
                    if (proj != null)
                        _imageManager.ReplaceTile(pos.GrabIndex, sourcePath, null,
                            _sysParam.ImageDisplayRows, _sysParam.ImageDisplayCols,
                            proj.FovWidth, proj.FovHeight, proj.Overlap);
                    return sourcePath;
                }
                bool replaced = await RetryAsync(
                    () =>
                    {
                        // 步骤1：删除旧目标文件
                        try
                        {
                            if (File.Exists(targetPath))
                            {
                                File.SetAttributes(targetPath, FileAttributes.Normal);
                                File.Delete(targetPath);
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Warning($"[GrabOne] 删除旧文件失败 target={targetPath}: {ex.Message}");
                            return Task.FromResult(false);
                        }

                        // 步骤2：复制新文件到目标
                        try
                        {
                            File.Copy(sourcePath, targetPath);
                        }
                        catch (Exception ex)
                        {
                            Log.Warning($"[GrabOne] 复制失败 source={sourcePath} → target={targetPath}: {ex.Message}");
                            return Task.FromResult(false);
                        }

                        // 步骤3：删除源文件
                        try
                        {
                            if (File.Exists(sourcePath))
                                File.Delete(sourcePath);
                        }
                        catch (Exception ex)
                        {
                            Log.Warning($"[GrabOne] 源文件删除失败(文件可能滞留) source={sourcePath}: {ex.Message}");
                        }

                        return Task.FromResult(true);
                    },
                    maxRetries: 5, delayMs: 100);
                if (!replaced)
                {
                    Log.Error($"[GrabOne] 替换失败，已重试5次");
                    return null;
                }

                // 更新拼接图显示
                var project = ProjectManager.Instance.CurrentProject;
                _imageManager.ReplaceTile(pos.GrabIndex, targetPath,
                    stitched => _vision.DisplayImageByImage(stitched),
                    _sysParam.ImageDisplayRows, _sysParam.ImageDisplayCols,
                    project.FovWidth, project.FovHeight, project.Overlap);

                return targetPath;
            }
            else
            {
                // 针尖图：直接返回路径
                return sourcePath;
            }
        }

        /// <summary>
        /// 多针抓图：板面图 + 每个标记针型各抓一张
        /// </summary>
        private async Task<(string boardPath, Dictionary<char, string> pinPaths)> GrabAndReplaceMultiAsync(
            DetectPosition pos, int positionIndex, int totalPositions, string projectImagesDir, CancellationToken token)
        {
            var project = ProjectManager.Instance.CurrentProject;
            var pinPaths = new Dictionary<char, string>();

            // 1. 板面图（使用 BoardFocus 焦距）
            string boardPath = await GrabOneAsync(project.BoardFocus, "Origin", pos, positionIndex, totalPositions, projectImagesDir, token);
            if (boardPath == null) return (null, null);
            // 暂停中断：板面图已抓完、暂停到来→不再抓针尖图，由外层 GrabTile 转 Paused（2026-08-19）
            if (!_pauseCheckEvent.IsSet) return (boardPath, null);

            // 2. 遍历检测位的 PinTypes 标记，逐个抓取针尖图
            string pinTypes = pos.PinTypes ?? "";
            foreach (char marker in pinTypes)
            {
                int pinIdx = marker - 'A';
                if (pinIdx < 0 || pinIdx >= project.PinTypeParams.Length) continue;

                double focus = project.PinTypeParams[pinIdx].Focus;
                string subDir = $"Pin{marker}";
                string pinPath = await GrabOneAsync(focus, subDir, pos, positionIndex, totalPositions, projectImagesDir, token);
                if (pinPath != null)
                {
                    pinPaths[marker] = pinPath;
                    // 保存针尖图层映射（用于运行时切换），key="{GrabIndex}_{Marker}"
                    _pinImageMap[$"{pos.GrabIndex}_{marker}"] = pinPath;
                }
                // 暂停中断：当前针尖图后暂停到来→后续针型不再抓（板面图已返回，外层转 Paused）（2026-08-19）
                if (!_pauseCheckEvent.IsSet) break;
            }

            Log.Info($"检测位 {positionIndex} 多针GRAB完成: 板面={boardPath}, 针型={pinTypes} 共{pinPaths.Count}个");
            return (boardPath, pinPaths);
        }

        /// <summary>
        /// 全局去重：PadX/PadY相近的针保留一个（优先保留有3D高度的）
        /// </summary>
        public void ShowSystemParam()
        {
            var win = new SysParamWindow();
            win.Owner = Application.Current.MainWindow;
            win.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            win.ShowDialog();
        }

        /// <summary>在拼接图中定位到指定检测位并居中放大显示（带边距）</summary>
        public void NavigateToDetectIndex(int gridIndex)
        {
            var hw = _vision.HalconWindow;
            var project = ProjectManager.Instance.CurrentProject;
            if (hw == null || project == null) return;

            var tile = FullImageManager.CalculateTileDimensions(
                project.FovWidth, project.FovHeight, project.Overlap);
            int cols = _sysParam.ImageDisplayCols;
            int cropW = tile.cropW;
            int cropH = tile.cropH;

            int tileCol = gridIndex % cols;
            int tileRow = gridIndex / cols;

            double left = tileCol * cropW;
            double top = tileRow * cropH;
            double right = (tileCol + 1) * cropW;
            double bottom = (tileRow + 1) * cropH;

            // 留 10% 边距
            double marginX = cropW * 0.1;
            double marginY = cropH * 0.1;

            try
            {
                HOperatorSet.SetPart(hw, top - marginY, left - marginX, bottom + marginY, right + marginX);
                _vision.RefreshDisplay();

                // 刷新叠加层和选区框
                var si = _imageManager.GetCurrentStitchedImage();
                if (si != null && si.IsInitialized())
                {
                    HOperatorSet.ClearWindow(hw);
                    HOperatorSet.DispObj(si, hw);
                    DrawSelectionRectangles(hw);
                    if (_isOverlayVisible)
                    {
                        if (_vmOverlays.Count > 0) { DrawVmRects(hw); DrawVmText(hw); }
                    }
                    si.Dispose();
                    _vision.RefreshDisplay();
                }
            }
            catch { }
        }

        public void ShowPersonalization()
        {
            var win = new PersonalizationWindow();
            win.Owner = Application.Current.MainWindow;
            win.WindowStartupLocation = WindowStartupLocation.CenterOwner;

            if (win.ShowDialog() == true)
            {
                var hw = _vision.HalconWindow;
                if (hw == null) return;

                try
                {
                    // 刷新选择框样式（如果在选择模式）
                    if (_vision.HalconControl != null && _vision.HalconControl.IsInSelectionMode)
                    {
                        var si = _imageManager.GetCurrentStitchedImage();
                        if (si != null && si.IsInitialized())
                        {
                            HOperatorSet.ClearWindow(hw);
                            HOperatorSet.DispObj(si, hw);
                            DrawSelectionRectangles(hw);
                            si.Dispose();
                            _vision.RefreshDisplay();
                        }
                    }

                    // 刷新叠加层颜色/字体/偏移等设置
                    if (_isOverlayVisible && (_vmOverlays.Count > 0))
                    {
                        var si = _imageManager.GetCurrentStitchedImage();
                        if (si != null && si.IsInitialized())
                        {
                            HOperatorSet.ClearWindow(hw);
                            HOperatorSet.DispObj(si, hw);
                            if (_vmOverlays.Count > 0) { DrawVmRects(hw); DrawVmText(hw); }
                            si.Dispose();
                            _vision.RefreshDisplay();
                        }
                    }
                }
                catch { }
            }
        }

        public void ShowCameraParam() { }

        public void ShowMotionParam() => MessageBox.Show("运动参数", "功能", MessageBoxButton.OK, MessageBoxImage.Information);

        public void ShowIOConfig()
        {
            if (_axisIOWindow != null && _axisIOWindow.IsLoaded)
            {
                _axisIOWindow.Activate();
                return;
            }

            _axisIOWindow = new AxisIOControlWindow();
            _axisIOWindow.Owner = Application.Current.MainWindow;
            _axisIOWindow.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            _axisIOWindow.Closed += (s, e) => _axisIOWindow = null;
            _axisIOWindow.Show();
        }

        public async void RunUnitTest1()
        {
            bool isStop = await _motionIO.MoveToAndWaitOK(x: 270, y: 104);
            if (isStop)
                GrabImage();
            else
                MessageBox.Show("移动到右下角失败");
        }

        public async void RunUnitTest2()
        {
            bool success = await _motionIO.MoveToAndWaitOK(x: 270, y: 104);
            if (success)
                GrabImage();
            else
                MessageBox.Show("移动到右下角失败");
        }

        public void RunUnitTest3()
        {
        }

        public void ThemeSetting()
        {
            ResourceDictionary newTheme;
            string newThemeName;

            if (_sysParam.CurrentTheme == "Dark")
            {
                newTheme = new ResourceDictionary { Source = new Uri("Styles/LightTheme.xaml", UriKind.Relative) };
                newThemeName = "Light";
            }
            else
            {
                newTheme = new ResourceDictionary { Source = new Uri("Styles/DarkTheme.xaml", UriKind.Relative) };
                newThemeName = "Dark";
            }

            Application.Current.Resources.MergedDictionaries.Clear();
            Application.Current.Resources.MergedDictionaries.Add(newTheme);
            _sysParam.CurrentTheme = newThemeName;
            OnThemeChanged?.Invoke();
        }
        #endregion
    }
}
