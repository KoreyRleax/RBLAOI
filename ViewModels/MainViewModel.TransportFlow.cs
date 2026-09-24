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
        #region 功能
        /// <summary>检查 XYZ 轴是否已回零，若未回零则弹窗询问是否执行回零（返回 true=可继续流程）</summary>
        private async Task<bool> CheckAndHomeAxesAsync(bool skipReentryCheck = false)
        {
            if (_motionIO.IsXyzHomed) return true;

            var result = MessageBox.Show(Application.Current.MainWindow,
                "X/Y/Z 轴尚未回零，为了确保操作安全，请先执行回零操作。\n\n点击「确定」立即执行回零。\n点击「取消」取消当前操作。",
                "轴未回零",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.OK) return false;

            if (!skipReentryCheck && isWorkFlowRun.Any())
            {
                MessageBox.Show(Application.Current.MainWindow, "当前有其他操作正在运行，无法执行回零。\n请等待当前操作完成后重试。", "无法回零", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            isWorkFlowRun[WorkFlowType.ServoHome] = true;

            try
            {
                ActionBusyStateChanged?.Invoke("servo_home", true);
                UpdateStatus("伺服归零中...");

                if (await _motionIO.HomeXYZ())
                {
                    UpdateStatus("伺服归零完成");
                    Log.Info("伺服归零完成");
                    return true;
                }
                else
                {
                    UpdateStatus("伺服归零超时");
                    Log.Warning("伺服归零超时");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log.Error($"伺服归零异常: {ex.Message}");
                UpdateStatus("伺服归零异常");
                return false;
            }
            finally
            {
                ActionBusyStateChanged?.Invoke("servo_home", false);
                isWorkFlowRun[WorkFlowType.ServoHome] = false;
            }
        }

        public async void ServoHome()
        {
            if (isWorkFlowRun.Any())
            {
                MessageBox.Show(Application.Current.MainWindow, "当前有其他操作正在运行");
                return;
            }
            isWorkFlowRun[WorkFlowType.ServoHome] = true;
            if (!CheckMotionConnected()) return;

            ActionBusyStateChanged?.Invoke("servo_home", true);
            UpdateStatus("伺服归零中...");

            try
            {
                if (await _motionIO.HomeXYZ())
                {
                    UpdateStatus("伺服归零完成");
                    Log.Info("伺服归零完成");
                    DeviceMonitor.Instance.ClearNeedHomeFlags(); // 轴报警回零标志清除
                }
                else
                {
                    UpdateStatus("伺服归零超时");
                    Log.Warning("伺服归零超时");
                }
            }
            catch (Exception ex)
            {
                Log.Error($"伺服归零异常: {ex.Message}");
                UpdateStatus("伺服归零异常");
            }
            finally
            {
                ActionBusyStateChanged?.Invoke("servo_home", false);
                isWorkFlowRun[WorkFlowType.ServoHome] = false;
            }
        }

        /// <summary>归零前检查传送带链路是否有板（工作位/入口/出口任一有板即拒绝回零）。返回 true=有板（已弹窗提示）</summary>
        private async Task<bool> BoardBlockingHome()
        {
            bool work = await _motionIO.GetIOState(InputSignal.WorkSensor);
            bool entry = await _motionIO.GetIOState(InputSignal.WaitSensor);
            bool exit = await _motionIO.GetIOState(InputSignal.OutputSensor);
            if (work || entry || exit)
            {
                MessageBox.Show(Application.Current.MainWindow, "传送带上有板（工作位/入口/出口），请移走板后再回零", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return true;
            }
            return false;
        }

        /// <summary>归零全部轴（含H轴）：HomeXYZH。固定按钮区「归零」入口</summary>
        public async void HomeAllAxes()
        {
            if (isWorkFlowRun.Any())
            {
                MessageBox.Show(Application.Current.MainWindow, "当前有其他操作正在运行");
                return;
            }
            if (!CheckMotionConnected()) return;

            // 回零前检查传送带链路是否有板（工作位/入口/出口）
            if (await BoardBlockingHome()) return;

            isWorkFlowRun[WorkFlowType.ServoHome] = true;

            ActionBusyStateChanged?.Invoke("home_all", true);
            UpdateStatus("全轴归零中...");

            try
            {
                if (await _motionIO.HomeXYZH((int)_sysParam.Data.AxisHomeTimeout))
                {
                    UpdateStatus("全轴归零完成");
                    Log.Info("全轴归零完成");
                    DeviceMonitor.Instance.ClearNeedHomeFlags();
                }
                else
                {
                    UpdateStatus("全轴归零超时");
                    Log.Warning("全轴归零超时");
                }
            }
            catch (Exception ex)
            {
                Log.Error($"全轴归零异常: {ex.Message}");
                UpdateStatus("全轴归零异常");
            }
            finally
            {
                ActionBusyStateChanged?.Invoke("home_all", false);
                isWorkFlowRun[WorkFlowType.ServoHome] = false;
            }
        }

        /// <summary>轨道归零：仅 H 轴回零。功能栏「轨道归零」入口</summary>
        public async void OrbitHome()
        {
            if (isWorkFlowRun.Any())
            {
                MessageBox.Show(Application.Current.MainWindow, "当前有其他操作正在运行");
                return;
            }
            if (!CheckMotionConnected()) return;

            // 回零前检查传送带链路是否有板（工作位/入口/出口）
            if (await BoardBlockingHome()) return;

            isWorkFlowRun[WorkFlowType.ServoHome] = true;

            ActionBusyStateChanged?.Invoke("orbit_home", true);
            UpdateStatus("轨道归零中...");

            try
            {
                if (await _motionIO.Home("H", (int)_sysParam.Data.AxisHomeTimeout))
                {
                    UpdateStatus("轨道归零完成");
                    Log.Info("轨道归零完成");
                    DeviceMonitor.Instance.ClearNeedHomeH(); // 仅清H轴标志，不影响XYZ
                }
                else
                {
                    UpdateStatus("轨道归零超时");
                    Log.Warning("轨道归零超时");
                }
            }
            catch (Exception ex)
            {
                Log.Error($"轨道归零异常: {ex.Message}");
                UpdateStatus("轨道归零异常");
            }
            finally
            {
                ActionBusyStateChanged?.Invoke("orbit_home", false);
                isWorkFlowRun[WorkFlowType.ServoHome] = false;
            }
        }

        /// <summary>
        /// 启动回零检查：延迟等待其他初始化完成后，弹窗询问是否回零
        /// </summary>
        public async Task StartupHomeCheck()
        {
            if (_startupHomeCheckDone) return;
            _startupHomeCheckDone = true;
            try
            {
                // 此时已在初始化任务链末尾（RunPostInitTasksAsync），
                // 图片加载和轨宽检查已完成，无需额外延迟
                if (!_motionIO.IsConnected) return;

                // 如果已经回零过了（上次运行已回零且程序没关过），跳过
                if (_motionIO.IsXyzHomed) return;

                var result = MessageBox.Show(
                    Application.Current.MainWindow,
                    "机器尚未回零，是否立即执行回零操作？\n\n点击「确定」执行全轴回零\n点击「否」跳过（全图采集和检测流程将无法执行，需手动回零）",
                    "启动回零",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    // 回零前检查传送带链路是否有板（工作位/入口/出口）
                    if (await BoardBlockingHome())
                    {
                        UpdateStatus("回零取消：传送带有板");
                        ActionBusyStateChanged?.Invoke("servo_home", false);
                        isWorkFlowRun[WorkFlowType.ServoHome] = false;
                        return;
                    }

                    isWorkFlowRun[WorkFlowType.ServoHome] = true;
                    ActionBusyStateChanged?.Invoke("servo_home", true);
                    UpdateStatus("回零中...");

                    var homeResult = await _motionIO.HomeXYZH((int)_sysParam.Data.AxisHomeTimeout);
                    if (homeResult)
                    {
                        UpdateStatus("回零完成，等待复位...");
                        await ResetMachine();
                        UpdateStatus("复位完成");
                    }
                    else
                    {
                        UpdateStatus("启动回零超时，请稍后手动回零");
                        Log.Warning("启动回零超时");
                    }

                    ActionBusyStateChanged?.Invoke("servo_home", false);
                    isWorkFlowRun[WorkFlowType.ServoHome] = false;
                }
                // 用户点「否」：XYZ保持未回零状态，全图采集和检测流程会因 IsXyzHomed=false 而被阻止
            }
            catch (Exception ex)
            {
                Log.Error($"启动回零异常: {ex.Message}");
            }
        }

        #region ========== 轨宽检查 ==========

        /// <summary>
        /// 检查当前H轴位置(轨宽)与方案中板高对应的H位置是否一致，
        /// 不一致则弹窗提示是否自动调整
        /// </summary>
        private async Task CheckAndNotifyRailWidthAsync()
        {
            if (!_motionIO.IsConnected) return;
            var project = ProjectManager.Instance.CurrentProject;
            if (project == null) return;

            double targetH = HAxisMaxPosition - project.BoardHeight;  // 与 ShowEditProjectDialog 中的公式一致
            var pos = await _motionIO.GetCurrentPosition(timeoutMs: 200);
            double currentH = pos.Hmm;

            // Hmm=0 说明获取位置失败，跳过检查

            if (Math.Abs(currentH - targetH) > 1.0)
            {
                var result = MessageBox.Show(
                    Application.Current.MainWindow,
                    $"该方案板高({project.BoardHeight}mm)与实际轨道宽度({HAxisMaxPosition - currentH:F1}mm)不一致，\n" +
                    $"是否自动调整轨道至 {project.BoardHeight}mm？",
                    "轨宽不一致", MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    if (await _motionIO.GetIOState(InputSignal.WorkSensor))
                    {
                        MessageBox.Show(Application.Current.MainWindow, "轨道有板，不允许调整轨道", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                    UpdateStatus($"正在调整轨宽: {project.BoardHeight}mm");
                    await _motionIO.SetIO(OutSignal.Clamp1, false);
                    await _motionIO.SetIO(OutSignal.Clamp2, false);
                    bool success = await _motionIO.MoveToAndWaitOK(h: targetH, timeoutMs: (int)_sysParam.Data.AdjustRailWidthTimeout);
                    if (success)
                        UpdateStatus($"轨宽调整成功: {project.BoardHeight}mm");
                    else
                        UpdateStatus("轨宽调整超时");
                    Thread.Sleep(500);
                }
            }
        }

        #endregion

        public async void AdjustBoardHeight()
        {
            if (!CheckSafeOperation()) return;
            if (!CheckMotionConnected()) return;

            // 检查工作位是否有板
            if (await _motionIO.GetIOState(InputSignal.WorkSensor))
            {
                MessageBox.Show(Application.Current.MainWindow, "轨道有板，不允许调整轨道", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var project = ProjectManager.Instance.CurrentProject;
            double boardLength = project.BoardHeight;
            double boardWidth = project.BoardWidth;

            var adjustWindow = new AdjustBoardHeightWindow(boardLength, boardWidth);
            adjustWindow.Owner = Application.Current.MainWindow;

            if (adjustWindow.ShowDialog() == true)
            {
                if (isWorkFlowRun.Any())
                {
                    MessageBox.Show(Application.Current.MainWindow, "当前有其他操作正在运行");
                    return;
                }
                isWorkFlowRun[WorkFlowType.AdjustBoardHeight] = true;

                // 顶板下降
                await _motionIO.SetIO(OutSignal.Clamp1, false);
                await _motionIO.SetIO(OutSignal.Clamp2, false);

                double height = adjustWindow.BoardHeight;
                project.BoardHeight = height;
                BoardHeight = height;

                ProjectManager.Instance.SaveCurrentProject();
                double railHeight = HAxisMaxPosition - BoardHeight;

                UpdateStatus($"正在调整轨宽: {railHeight}mm");

                try
                {
                    bool success = await _motionIO.MoveToAndWaitOK(h: railHeight, timeoutMs: (int)_sysParam.Data.AdjustRailWidthTimeout);

                    if (success)
                    {
                        UpdateStatus($"轨宽调整成功: {railHeight}mm");
                    }
                    else
                    {
                        UpdateStatus("轨宽调整超时");
                        MessageBox.Show(Application.Current.MainWindow, "轨宽调整超时，请检查下位机", "超时", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
                finally
                {
                    isWorkFlowRun[WorkFlowType.AdjustBoardHeight] = false;
                }
            }
        }

        /// <summary>
        /// 运送基板状态机（循环驱动 + Stopwatch 计时器，超时参数见 系统参数→其他）：
        /// 去入口（入口有料→直接去基板；无料→MOTOR REV 反向找板）→ 等待到入口（每轮读入口传感器，计时超时→Err）
        /// → 去基板（开阻挡+MOTOR FWD）→ 等待到基板（计时到→MOTOR_STOP）→ 顶板 → End / Err
        /// </summary>
        private enum TransportState
        {
            Init,        // 准备阶段：初始化/检查（ResetIO、延时）
            GoToEntry,   // 去入口：入口有料 → 直接去基板；入口无料 → MOTOR REV + 计时开始
            WaitEntry,   // 等待到入口：每轮读入口传感器；计时超时 → Err（传送带中无板）
            GoToBoard,   // 去基板：打开阻挡气缸 + MOTOR FWD + 计时开始
            WaitBoard,   // 等待到基板：计时到 → MOTOR_STOP → 顶板
            LiftPlate,   // 顶板1/2 顶起
            End,         // 完成（终态）
            Err,         // 异常（终态：超时 / 指令无回复）
        }

        public async Task<bool> TransportBoard(bool isSubFlow = false, bool waitEntryOnly = false)
        {
            // 安全操作检查（isSubFlow=true 跳过互斥——连续模式外壳已持 ContinuousRun 标志）
            if (!CheckSafeOperation(isSubFlow)) return false;
            isWorkFlowRun[WorkFlowType.TransportBoard] = true;
            if (!CheckMotionConnected()) return false;

            ActionBusyStateChanged?.Invoke("transport_board", true);

            try
            {
                // waitEntryOnly（单板规则d/连续模式共用）：入口无板时纯等待放板（不 REV 反向，避免与前机送板冲突），超时=入口等板超时
                int waitEntryMs = waitEntryOnly
                    ? (int)_sysParam.Data.ContinuousEntryWaitTimeout
                    : (int)_sysParam.Data.ConveyorToEntryTimeout;
                int waitBoardMs = (int)_sysParam.Data.ConveyorWaitBoardTimeout;

                var sw = new Stopwatch();
                var state = TransportState.Init;
                string err = null;

                // 状态机单步：处理当前状态一轮判断并转移，到达 End/Err 终态返回 true（闭包直接读写 sw/state/err）
                async Task<bool> Step()
                {
                    // 报警：立即停传送带并转 Err（传送带是"发后持续运转"指令，须 MOTOR_STOP 兜底停带；补 AlarmBlocked 只防"开始"不防"进行中"的洞）
                    if (_alarmStopping || DeviceMonitor.Instance.State == DeviceState.Alarm)
                    {
                        await _motionIO.StopMotorAsync();
                        err = "设备报警，运送终止";
                        state = TransportState.Err;
                        return true;
                    }
                    // 连续模式复位：立即终止（外壳取消时子流程一并收尾，不等传感器/超时；单板模式调用时此标志恒 false 不受影响）
                    if (_continuousAbort)
                    {
                        await _motionIO.StopMotorAsync();
                        err = "复位终止，运送取消";
                        state = TransportState.Err;
                        return true;
                    }
                    // 暂停响应（单板：检测暂停 _pauseCheckEvent；连续：_continuousPaused）：
                    // 停传送带防板继续流动 + 冻结超时计时；恢复后从当前状态继续（2026-08-14 用户要求"运送基板时就暂停"）
                    if (_continuousPaused || !_pauseCheckEvent.IsSet)
                    {
                        await _motionIO.StopMotorAsync();
                        sw.Stop();
                        while (_continuousPaused || !_pauseCheckEvent.IsSet)
                        {
                            await Task.Delay(200);
                        }
                        sw.Start();
                        return false;
                    }
                    switch (state)
                    {
                        case TransportState.Init:
                            // 准备阶段：初始化IO（降下阻挡放行）、检查状态
                            await _motionIO.ResetIO();
                            await Task.Delay(200);
                            Log.Info("[Conveyor] 运送基板: 初始化完成", "Conveyor");
                            state = TransportState.GoToEntry;
                            break;

                        case TransportState.GoToEntry:
                            if (await _motionIO.GetIOState(InputSignal.WaitSensor, 200))
                            {
                                UpdateStatus("运送基板中...");
                                Log.Info("[Conveyor] 运送基板: 入口初始有料，直接去基板", "Conveyor");
                                state = TransportState.GoToBoard;
                            }
                            else if (waitEntryOnly)
                            {
                                // 连续模式：入口无板时纯等待前机放板（不 REV，避免与前机送板冲突）
                                UpdateStatus("等待入口放板...");
                                Log.Info("[Conveyor] 运送基板: 入口无料，等待前机放板（连续模式）", "Conveyor");
                                sw.Restart(); // 计时开始：等待入口有料
                                state = TransportState.WaitEntry;
                            }
                            else
                            {
                                UpdateStatus("运送基板中...");
                                Log.Info("[Conveyor] 运送基板: 去入口（MOTOR REV 反向找板）", "Conveyor");
                                if (!await _motionIO.StartMotorReverseAsync()) { err = "MOTOR REV 无回复"; state = TransportState.Err; break; }
                                sw.Restart(); // 计时开始：等待板到入口
                                state = TransportState.WaitEntry;
                            }
                            break;

                        case TransportState.WaitEntry:
                            if (await _motionIO.GetIOState(InputSignal.WaitSensor, 200))
                            {
                                await _motionIO.StopMotorAsync(); // 板已到入口，停止反向
                                UpdateStatus("运送基板中...");
                                Log.Info("[Conveyor] 运送基板: 板已到入口", "Conveyor");
                                state = TransportState.GoToBoard;
                            }
                            else if (sw.ElapsedMilliseconds >= waitEntryMs)
                            {
                                await _motionIO.StopMotorAsync(); // 超时先停传送，防板被带走
                                err = "等待入口超时，传送带中无板";
                                state = TransportState.Err;
                            }
                            else await Task.Delay(50); // 循环节拍
                            break;

                        case TransportState.GoToBoard:
                            await _motionIO.SetIO(OutSignal.Block, true); // 打开阻挡气缸（板到位后挡在加工位）
                            await Task.Delay(200);
                            Log.Info("[Conveyor] 运送基板: 去基板（MOTOR FWD，等待工作位信号持续ON）", "Conveyor");
                            if (!await _motionIO.StartMotorForwardAsync()) { err = "MOTOR FWD 无回复"; state = TransportState.Err; break; }
                            sw.Restart(); // 计时开始：等待工作位信号稳定
                            state = TransportState.WaitBoard;
                            break;

                        case TransportState.WaitBoard:
                            // 工作位信号由 DeviceMonitor 后台自动去抖喂入（GET_STATE 轮询每 ~20ms 更新），
                            // ON 持续稳定时长即认为板到位（未到位计时持续增长，中途抖动自动重置）
                            if (DeviceMonitor.InputWork.IsOnFor((int)_sysParam.Data.ConveyorBoardStableMs))
                            {
                                if (!await _motionIO.StopMotorAsync()) { err = "MOTOR_STOP 无回复"; state = TransportState.Err; break; }
                                Log.Info("[Conveyor] 运送基板: 工作位信号持续ON，停止传送", "Conveyor");
                                state = TransportState.LiftPlate;
                            }
                            else if (sw.ElapsedMilliseconds >= waitBoardMs)
                            {
                                await _motionIO.StopMotorAsync(); // 超时先停传送
                                err = "等待工作位信号超时";
                                state = TransportState.Err;
                            }
                            else await Task.Delay(50); // 循环节拍
                            break;

                        case TransportState.LiftPlate:
                            await _motionIO.SetIO(OutSignal.Clamp1, true); // 顶板1/2 顶起，把板顶到工作位
                            await _motionIO.SetIO(OutSignal.Clamp2, true);
                            state = TransportState.End;
                            break;
                    }
                    return state == TransportState.End || state == TransportState.Err;
                }

                await FlowLoopAsync(Step);

                if (state == TransportState.Err)
                {
                    UpdateStatus($"运送基板异常: {err}");
                    Log.Error($"运送基板异常: {err}");
                    return false;
                }
                else
                {
                    UpdateStatus("基板运送完成");
                    Log.Info("运送基板完成");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.Error($"运送基板异常: {ex.Message}");
                UpdateStatus("运送基板异常");
                return false;
            }
            finally
            {
                ActionBusyStateChanged?.Invoke("transport_board", false);
                isWorkFlowRun[WorkFlowType.TransportBoard] = false;
            }
        }

        /// <summary>
        /// 送到出口状态机（循环驱动 + Stopwatch 计时器，超时参数见 系统参数→其他）：
        /// 阻挡降下 → MOTOR FWD 正向等待出口有料（每轮读出口传感器，计时超时→Err）→ End / Err
        /// </summary>
        private enum ExitState
        {
            Init,         // 准备阶段：初始化/检查（ResetIO、延时）
            BlockOff,     // 降下阻挡放行
            ForwardWait,  // MOTOR FWD 正向，等待出口有料（计时开始）
            End,          // 完成（终态）
            Err,          // 异常（终态：超时 / 指令无回复）
        }

        public async Task<bool> SendToExit(bool isSubFlow = false)
        {
            // 安全操作检查（isSubFlow=true 跳过互斥——连续模式外壳已持 ContinuousRun 标志）
            if (!CheckSafeOperation(isSubFlow)) return false;
            isWorkFlowRun[WorkFlowType.SendToExit] = true;
            if (!CheckMotionConnected()) return false;

            ActionBusyStateChanged?.Invoke("send_exit", true);
            UpdateStatus("送出到出口...");

            try
            {
                int timeoutMs = (int)_sysParam.Data.ConveyorToExitTimeout;

                var sw = new Stopwatch();
                var state = ExitState.Init;
                string err = null;

                // 状态机单步（闭包读写 sw/state/err）
                async Task<bool> Step()
                {
                    // 报警：立即停传送带并转 Err（传送带是"发后持续运转"指令，须 MOTOR_STOP 兜底停带）
                    if (_alarmStopping || DeviceMonitor.Instance.State == DeviceState.Alarm)
                    {
                        await _motionIO.StopMotorAsync();
                        err = "设备报警，出板终止";
                        state = ExitState.Err;
                        return true;
                    }
                    // 连续模式复位：立即终止（外壳取消时子流程一并收尾，不等出口传感器/超时）
                    if (_continuousAbort)
                    {
                        await _motionIO.StopMotorAsync();
                        err = "复位终止，出板取消";
                        state = ExitState.Err;
                        return true;
                    }
                    switch (state)
                    {
                        case ExitState.Init:
                            // 准备阶段：初始化IO（降下阻挡放行）、检查状态
                            await _motionIO.ResetIO();
                            await Task.Delay(200);
                            Log.Info("[Conveyor] 送到出口: 初始化完成", "Conveyor");
                            state = ExitState.BlockOff;
                            break;

                        case ExitState.BlockOff:
                            Log.Info("[Conveyor] 送到出口: MOTOR FWD 正向传送", "Conveyor");
                            if (!await _motionIO.StartMotorForwardAsync()) { err = "MOTOR FWD 无回复"; state = ExitState.Err; break; }
                            sw.Restart(); // 计时开始：等待出口有料
                            state = ExitState.ForwardWait;
                            break;

                        case ExitState.ForwardWait:
                            if (await _motionIO.GetIOState(InputSignal.OutputSensor, 200))
                            {
                                await _motionIO.StopMotorAsync(); // 出口有料，停止
                                state = ExitState.End;
                            }
                            else if (sw.ElapsedMilliseconds >= timeoutMs)
                            {
                                await _motionIO.StopMotorAsync(); // 超时先停传送
                                err = "等待出口有料超时";
                                state = ExitState.Err;
                            }
                            else await Task.Delay(50); // 循环节拍
                            break;
                    }
                    return state == ExitState.End || state == ExitState.Err;
                }

                await FlowLoopAsync(Step);

                if (state == ExitState.Err)
                {
                    UpdateStatus($"送出到出口异常: {err}");
                    Log.Error($"送出到出口异常: {err}");
                    return false;
                }
                else
                {
                    UpdateStatus("已送到出口");
                    Log.Info("送到出口完成");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.Error($"送出到出口异常: {ex.Message}");
                UpdateStatus("送出到出口异常");
                return false;
            }
            finally
            {
                ActionBusyStateChanged?.Invoke("send_exit", false);
                isWorkFlowRun[WorkFlowType.SendToExit] = false;
            }
        }

        /// <summary>
        /// 送到入口状态机（循环驱动 + Stopwatch 计时器，超时参数见 系统参数→其他）：
        /// 阻挡降下 → MOTOR REV 反向等待入口有料（每轮读入口传感器，计时超时→Err）→ End / Err
        /// </summary>
        private enum EntranceState
        {
            Init,         // 准备阶段：初始化/检查（ResetIO、延时）
            BlockOff,     // 降下阻挡放行
            ReverseWait,  // MOTOR REV 反向，等待入口有料（计时开始）
            End,          // 完成（终态）
            Err,          // 异常（终态：超时 / 指令无回复）
        }

        public async void SendToEntrance()
        {
            if (!CheckSafeOperation()) return;
            isWorkFlowRun[WorkFlowType.SendToEntrance] = true;
            if (!CheckMotionConnected()) return;

            ActionBusyStateChanged?.Invoke("send_entrance", true);
            UpdateStatus("送到入口中...");

            try
            {
                int timeoutMs = (int)_sysParam.Data.ConveyorToEntryTimeout;

                var sw = new Stopwatch();
                var state = EntranceState.Init;
                string err = null;

                // 状态机单步（闭包读写 sw/state/err）
                async Task<bool> Step()
                {
                    // 报警：立即停传送带并转 Err（传送带是"发后持续运转"指令，须 MOTOR_STOP 兜底停带）
                    if (_alarmStopping || DeviceMonitor.Instance.State == DeviceState.Alarm)
                    {
                        await _motionIO.StopMotorAsync();
                        err = "设备报警，入料终止";
                        state = EntranceState.Err;
                        return true;
                    }
                    // 连续模式复位：立即终止（外壳取消时子流程一并收尾，不等入口传感器/超时）
                    if (_continuousAbort)
                    {
                        await _motionIO.StopMotorAsync();
                        err = "复位终止，入料取消";
                        state = EntranceState.Err;
                        return true;
                    }
                    switch (state)
                    {
                        case EntranceState.Init:
                            // 准备阶段：初始化IO（降下阻挡放行）、检查状态
                            await _motionIO.ResetIO();
                            await Task.Delay(200);
                            Log.Info("[Conveyor] 送到入口: 初始化完成", "Conveyor");
                            state = EntranceState.BlockOff;
                            break;

                        case EntranceState.BlockOff:
                            Log.Info("[Conveyor] 送到入口: MOTOR REV 反向传送", "Conveyor");
                            if (!await _motionIO.StartMotorReverseAsync()) { err = "MOTOR REV 无回复"; state = EntranceState.Err; break; }
                            sw.Restart(); // 计时开始：等待入口有料
                            state = EntranceState.ReverseWait;
                            break;

                        case EntranceState.ReverseWait:
                            if (await _motionIO.GetIOState(InputSignal.WaitSensor, 200))
                            {
                                await _motionIO.StopMotorAsync(); // 入口有料，停止
                                state = EntranceState.End;
                            }
                            else if (sw.ElapsedMilliseconds >= timeoutMs)
                            {
                                await _motionIO.StopMotorAsync(); // 超时先停传送
                                err = "等待入口有料超时";
                                state = EntranceState.Err;
                            }
                            else await Task.Delay(50); // 循环节拍
                            break;
                    }
                    return state == EntranceState.End || state == EntranceState.Err;
                }

                await FlowLoopAsync(Step);

                if (state == EntranceState.Err)
                {
                    UpdateStatus($"送到入口异常: {err}");
                    Log.Error($"送到入口异常: {err}");
                }
                else
                {
                    UpdateStatus("已送到入口");
                    Log.Info("送到入口完成");
                }
            }
            catch (Exception ex)
            {
                Log.Error($"送到入口异常: {ex.Message}");
                UpdateStatus("送到入口异常");
            }
            finally
            {
                ActionBusyStateChanged?.Invoke("send_entrance", false);
                isWorkFlowRun[WorkFlowType.SendToEntrance] = false;
            }
        }

        /// <summary>
        /// 状态机驱动循环：每轮调用单步方法直到其返回 true（到达 End/Err 终态）。
        /// 极薄，只消 while 样板——互斥/状态栏/日志仍在各流程外壳处理。
        /// </summary>
        private async Task FlowLoopAsync(Func<Task<bool>> stepOne)
        {
            while (!await stepOne()) { }
        }

        /// <summary>
        /// 流程启动前安全操作检查（对齐插针机 isSafeOperation + 前置检查，统一顺序）：
        /// ① 互斥流程（isSubFlow=true 跳过——连续模式外壳已持标志）② 报警状态 ③ 需回零（急停/轴报警区分文案）。
        /// 返回 true=通过可继续；false=已弹窗提示，调用方直接 return。
        /// </summary>
        private bool CheckSafeOperation(bool isSubFlow = false)
        {
            if (!isSubFlow && isWorkFlowRun.Any())
            {
                MessageBox.Show(Application.Current.MainWindow, "当前有其他操作正在运行");
                return false;
            }
            if (DeviceMonitor.Instance.State == DeviceState.Alarm)
            {
                MessageBox.Show(Application.Current.MainWindow, "设备处于报警状态，请先复位报警后再操作",
                    "禁止操作", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            if (DeviceMonitor.Instance.NeedHomeAny)
            {
                MessageBox.Show(Application.Current.MainWindow,
                    DeviceMonitor.Instance.NeedHomeByEmergency ? "急停后需先回零，请先执行伺服回零再操作" : "轴报警后需先回零，请先执行伺服回零再操作",
                    "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            return true;
        }

        /// <summary>「采集全图」按钮：未运行=启动新采集；已暂停=恢复采集（从当前点位重做）；运行中=仅提示（按钮应已被 busy 禁用，此为兜底）</summary>
        public void GrabFullImage()
        {
            if (isWorkFlowRun[WorkFlowType.FullImageGrab])
            {
                if (!_pauseGrabEvent.IsSet)
                {
                    ResumeFullImageGrab();
                }
                else
                {
                    UpdateStatus("全图采集运行中");
                }
                return;
            }
            // 2026-08-27 需求：全图采集开始前也走基板入口前置流程（规则a-d，与检测一致）——
            // 工作位有料→直接开始采集；入口有料→运送→采集；入口无料→等待放板→采集。
            // StartFullImageGrabFlow 内部仍有 CheckSafeOperation/回零等安全前置，双重校验安全。
            // StartFullImageGrabFlow 为 async void（内部自驱流程），委托包装为 Task 立即返回即可，流程由自身状态机推进。
            _ = RunBoardEntryGateAsync(() => { StartFullImageGrabFlow(); return Task.CompletedTask; });
        }
        public void GrabFOV() => ReGrabDetectPositionsAsync();

        public void StartAutoCheck() => MessageBox.Show("开始自动检测", "功能", MessageBoxButton.OK, MessageBoxImage.Information);
        public void StartManualCheck() => MessageBox.Show("手动检测", "功能", MessageBoxButton.OK, MessageBoxImage.Information);
        public void StartSpotCheck() => MessageBox.Show("点检检测", "功能", MessageBoxButton.OK, MessageBoxImage.Information);
        public void StartMarkCheck() => MessageBox.Show("MARK检测", "功能", MessageBoxButton.OK, MessageBoxImage.Information);
        public void EndBatch() => MessageBox.Show("结束批次", "功能", MessageBoxButton.OK, MessageBoxImage.Information);
        public void AutoFocus() => MessageBox.Show("自动聚焦", "功能", MessageBoxButton.OK, MessageBoxImage.Information);

        public void StopAction() => StopFullImageGrabFlow();
        #endregion
    }
}
