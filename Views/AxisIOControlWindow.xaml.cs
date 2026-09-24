using RBLAOI.Core;
using RBLAOI.Core.Device;
using RBLAOI.Core.Managers;
using RBLAOI.Core.Motion;
using RBLAOI.ViewModels;
using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace RBLAOI.Views
{
    public partial class AxisIOControlWindow : Window
    {
        private MotionIO _motionIO;

        public AxisIOControlWindow()
        {
            InitializeComponent();
            InitializeMotionIO();
            LoadPortList();
            EnableControls(_motionIO.IsConnected);

            // 窗口加载后同步状态
            this.Loaded += (s, e) => UpdateUIByConnectionState();

            // 窗口关闭时取消订阅
            this.Closed += (s, e) =>
            {
                _motionIO.ConnectionStateChanged -= OnConnectionStateChanged;
                _motionIO.DataReceived -= OnDataReceived;
                _motionIO.ErrorOccurred -= OnErrorOccurred;
                _motionIO.IOStateUpdated -= OnIOStateUpdated;
                _motionIO.StateSnapshotUpdated -= OnStateSnapshotUpdated;
            };
        }

        #region H+/H- 按钮禁止（工作位有板时）

        /// <summary>
        /// 根据工作位是否有板更新 H+/H- 按钮禁用状态
        /// </summary>
        private void UpdateHButtonsState()
        {
            if (!_motionIO.IsConnected)
            {
                btnLineAdd.IsEnabled = false;
                btnLineDel.IsEnabled = false;
                return;
            }

            bool hasBoard = _lastState?.InputWork ?? false;
            btnLineAdd.IsEnabled = !hasBoard;
            btnLineDel.IsEnabled = !hasBoard;
        }

        #endregion


        /// <summary>
        /// 根据当前连接状态更新UI
        /// </summary>
        private void UpdateUIByConnectionState()
        {
            bool connected = _motionIO.IsConnected;

            // InvokeAsync：若连接状态从后台线程触发（如 RESTART 流程），同步 Invoke 会阻塞该线程
            Dispatcher.InvokeAsync(() =>
            {
                if (connected)
                {
                    btnConnect.Content = "断开";
                    btnConnect.Background = new SolidColorBrush(Color.FromRgb(231, 76, 60));
                    EnableControls(true);
                    UpdateHButtonsState();  // 根据工作位是否有板进一步限制 H+/H-

                    // 同步端口和波特率显示
                    if (!string.IsNullOrEmpty(_motionIO.PortName))
                    {
                        cmbPort.Text = _motionIO.PortName;
                        cmbBaud.Text = _motionIO.BaudRate.ToString();
                    }

                    // 连接后发送当前滑条 JOG 速度到控制器
                    double initPct = Math.Round(AxisSpeedSD.Value);
                    double initMmPerSec = initPct / 100.0 * SysParam.Instance.Data.MaxSpeed;
                    _ = _motionIO.SetSpeedJog(initMmPerSec);
                }
                else
                {
                    btnConnect.Content = "连接";
                    btnConnect.Background = new SolidColorBrush(Color.FromRgb(39, 174, 96));
                    EnableControls(false);
                    txtRefreshRate.Text = "--";
                    txtRefreshRate.Foreground = (Brush)FindResource("StatusRunning");
                }
            });
        }

        private void InitializeMotionIO()
        {
            _motionIO = MotionIO.Instance;
            _motionIO.ConnectionStateChanged += OnConnectionStateChanged;
            _motionIO.DataReceived += OnDataReceived;
            _motionIO.ErrorOccurred += OnErrorOccurred;
            _motionIO.IOStateUpdated += OnIOStateUpdated;
            _motionIO.StateSnapshotUpdated += OnStateSnapshotUpdated;
        }

        /// <summary>
        /// UI 绘制节流：后台 GET_STATE ~50Hz，UI 显示合并到 ~20Hz（50ms），避免每快照触发 WPF 重绘。
        /// 注意：IO 指示灯与位置快照必须各自独立节流——StatePollLoop 同一帧先触发快照事件再触发 IO 事件，
        /// 若共用节流器，IO 事件永远被快照事件挤掉（指示灯不更新而位置正常）
        /// </summary>
        private const int UiRefreshIntervalMs = 50;
        private DateTime _lastIoUiRefresh = DateTime.MinValue;
        private DateTime _lastSnapshotUiRefresh = DateTime.MinValue;
        private bool UiThrottlePass(ref DateTime last)
        {
            var now = DateTime.UtcNow;
            if ((now - last).TotalMilliseconds < UiRefreshIntervalMs) return false;
            last = now;
            return true;
        }

        private void OnIOStateUpdated(IOState state)
        {
            if (Dispatcher.HasShutdownStarted || !UiThrottlePass(ref _lastIoUiRefresh)) return;
            Dispatcher.InvokeAsync(() => UpdateIOIndicators(state));
        }

        /// <summary>
        /// GET_STATE 综合快照事件：一次快照同时更新位置和运动状态（不再各自发 GET_STATUS / GET_POS，统一走 GET_STATE）
        /// </summary>
        private void OnStateSnapshotUpdated(StateSnapshot snapshot)
        {
            if (Dispatcher.HasShutdownStarted || !UiThrottlePass(ref _lastSnapshotUiRefresh)) return;
            Dispatcher.InvokeAsync(() => UpdatePositionAndMotion(snapshot));
        }

        private void UpdatePositionAndMotion(StateSnapshot snapshot)
        {
            // 超时/坏帧的过期快照（IsFresh=false）：保留最后已知位置显示，
            // 但刷新率明确标注"过期"，与正常状态可区分，设备恢复后自动恢复正常
            if (!snapshot.IsFresh)
            {
                txtRefreshRate.Text = "过期";
                txtRefreshRate.Foreground = Brushes.OrangeRed;
                txtRtt.Text = "--";
                return;
            }

            // 右上角：数据刷新率 + RTT 实时显示（GET_STATE 实际到达频率与单次往返耗时）
            txtRefreshRate.Text = _motionIO.CurrentStateRateHz > 0
                ? $"{_motionIO.CurrentStateRateHz:F1} Hz"
                : "--";
            txtRefreshRate.Foreground = (Brush)FindResource("StatusRunning");
            txtRtt.Text = $"{snapshot.RttMs} ms";
            txtRtt.Foreground = (Brush)FindResource("StatusRunning");

            double x = _motionIO.PulseToMm(snapshot.X, "X");
            double y = _motionIO.PulseToMm(snapshot.Y, "Y");
            double z = _motionIO.PulseToMm(snapshot.Z, "Z");
            double h = _motionIO.PulseToMm(snapshot.H, "H");

            txtXPos.Text = x.ToString("F3");
            txtYPos.Text = y.ToString("F3");
            txtZPos.Text = z.ToString("F3");
            txtHPos.Text = h.ToString("F3");

            // 3D相机位置 = 2D相机位置 + 系统参数中的3D相机偏移
            double offsetX = SysParam.Instance.Data.Camera3DOffsetX;
            double offsetY = SysParam.Instance.Data.Camera3DOffsetY;
            txt3DCamX.Text = (x + offsetX).ToString("F3");
            txt3DCamY.Text = (y + offsetY).ToString("F3");

            // 轴运动状态（忙=红，空闲=绿）
            rectTestMovingX.Fill = snapshot.XBusy ? Brushes.Red : Brushes.Green;
            rectTestMovingY.Fill = snapshot.YBusy ? Brushes.Red : Brushes.Green;
            rectTestMovingZ.Fill = snapshot.ZBusy ? Brushes.Red : Brushes.Green;
            rectTestMovingH.Fill = snapshot.HBusy ? Brushes.Red : Brushes.Green;
        }

        private IOState _lastState;
        private void UpdateIOIndicators(IOState state)
        {
            // 第一次调用，初始化所有
            if (_lastState == null)
            {
                _lastState = state;
                UpdateAllIO(state);
                return;
            }

            // 逐个比较，只更新变化的
            UpdateIfChanged(rectClamp1State, _lastState.OutputClamp1, state.OutputClamp1, "顶板1");
            UpdateIfChanged(rectClamp2State, _lastState.OutputClamp2, state.OutputClamp2, "顶板2");
            UpdateIfChanged(rectBlockState, _lastState.OutputBlock, state.OutputBlock, "阻挡气缸");
            UpdateIfChanged(rectMotorState, _lastState.OutputMotor, state.OutputMotor, "马达");
            UpdateIfChanged(rectRedLightState, _lastState.OutputRedLight, state.OutputRedLight, "红灯");
            UpdateIfChanged(rectGreenLightState, _lastState.OutputGreenLight, state.OutputGreenLight, "绿灯");
            UpdateIfChanged(rectYellowLightState, _lastState.OutputYellowLight, state.OutputYellowLight, "黄灯");
            UpdateIfChanged(rectBuzzerState, _lastState.OutputBuzzer, state.OutputBuzzer, "蜂鸣器");
            UpdateIfChanged(rectCamera3DTriggerState, _lastState.OutputCamera3DTrigger, state.OutputCamera3DTrigger, "3D相机触发");

            UpdateIfChanged(rectWaitSensor, _lastState.InputWait, state.InputWait, "待料位");
            UpdateIfChanged(rectWorkSensor, _lastState.InputWork, state.InputWork, "工作位");
            UpdateIfChanged(rectOutputSensor, _lastState.InputOutput, state.InputOutput, "出料位");
            UpdateIfChanged(rectLiftUpSensor, _lastState.InputLiftUp, state.InputLiftUp, "顶升上限");
            UpdateIfChanged(rectLiftDownSensor, _lastState.InputLiftDown, state.InputLiftDown, "顶升下限");
            UpdateIfChanged(rectAirPressureSensor, _lastState.InputAirPressure, state.InputAirPressure, "气压检测");
            UpdateIfChanged(rectEmergencySensor, _lastState.InputEmergency, state.InputEmergency, "急停");
            UpdateIfChanged(rectSafetyLightSensor, _lastState.InputSafetyLight, state.InputSafetyLight, "安全光幕");
            UpdateIfChanged(rectDoorSwitchSensor, _lastState.InputDoorSwitch, state.InputDoorSwitch, "门禁开关");

            // 限位原点（四轴）
            UpdateIfChanged(rectOrgX, _lastState.OrgX, state.OrgX, "X轴原点");
            UpdateIfChanged(rectOrgY, _lastState.OrgY, state.OrgY, "Y轴原点");
            UpdateIfChanged(rectOrgZ, _lastState.OrgZ, state.OrgZ, "Cam轴原点");
            UpdateIfChanged(rectOrgH, _lastState.OrgH, state.OrgH, "H轴原点");

            UpdateIfChanged(rectLimitXPos, _lastState.LimitXPos, state.LimitXPos, "X轴正限位");
            UpdateIfChanged(rectLimitXNeg, _lastState.LimitXNeg, state.LimitXNeg, "X轴负限位");
            UpdateIfChanged(rectLimitYPos, _lastState.LimitYPos, state.LimitYPos, "Y轴正限位");
            UpdateIfChanged(rectLimitYNeg, _lastState.LimitYNeg, state.LimitYNeg, "Y轴负限位");
            UpdateIfChanged(rectLimitZPos, _lastState.LimitZPos, state.LimitZPos, "Z轴正限位");
            UpdateIfChanged(rectLimitZNeg, _lastState.LimitZNeg, state.LimitZNeg, "Z轴负限位");
            UpdateIfChanged(rectLimitHPos, _lastState.LimitHPos, state.LimitHPos, "H轴正限位");
            UpdateIfChanged(rectLimitHNeg, _lastState.LimitHNeg, state.LimitHNeg, "H轴负限位");

            // ✅ 报警信号（四轴）
            UpdateIfChanged(rectAlarmX, _lastState.AlarmX, state.AlarmX, "X轴报警");
            UpdateIfChanged(rectAlarmY, _lastState.AlarmY, state.AlarmY, "Y轴报警");
            UpdateIfChanged(rectAlarmZ, _lastState.AlarmZ, state.AlarmZ, "Z轴报警");
            UpdateIfChanged(rectAlarmH, _lastState.AlarmH, state.AlarmH, "H轴报警");

            // ✅ 到位信号（四轴 INP）
            UpdateIfChanged(rectAxisXDone, _lastState.AxisXDone, state.AxisXDone, "X轴到位");
            UpdateIfChanged(rectAxisYDone, _lastState.AxisYDone, state.AxisYDone, "Y轴到位");
            UpdateIfChanged(rectAxisZDone, _lastState.AxisZDone, state.AxisZDone, "Z轴到位");
            UpdateIfChanged(rectAxisHDone, _lastState.AxisHDone, state.AxisHDone, "H轴到位");
            // 更新最后状态
            _lastState = state;
            // 工作位传感器状态变化时同步更新 H+/H- 按钮
            UpdateHButtonsState();
        }

        /// <summary>
        /// 只有当值变化时才更新 UI
        /// </summary>
        private void UpdateIfChanged(Rectangle rect, bool oldValue, bool newValue, string name)
        {
            if (oldValue != newValue)
            {
                rect.Fill = newValue ? Brushes.Red : Brushes.Green;
                rect.ToolTip = newValue ? $"{name}: ON" : $"{name}: OFF";
            }
        }

        /// <summary>
        /// 首次加载，更新所有 IO
        /// </summary>
        private void UpdateAllIO(IOState state)
        {
            rectClamp1State.Fill = state.OutputClamp1 ? Brushes.Red : Brushes.Green;
            rectClamp2State.Fill = state.OutputClamp2 ? Brushes.Red : Brushes.Green;
            rectBlockState.Fill = state.OutputBlock ? Brushes.Red : Brushes.Green;
            rectMotorState.Fill = state.OutputMotor ? Brushes.Red : Brushes.Green;
            rectRedLightState.Fill = state.OutputRedLight ? Brushes.Red : Brushes.Green;
            rectGreenLightState.Fill = state.OutputGreenLight ? Brushes.Red : Brushes.Green;
            rectYellowLightState.Fill = state.OutputYellowLight ? Brushes.Red : Brushes.Green;
            rectBuzzerState.Fill = state.OutputBuzzer ? Brushes.Red : Brushes.Green;
            rectCamera3DTriggerState.Fill = state.OutputCamera3DTrigger ? Brushes.Red : Brushes.Green;

            rectWaitSensor.Fill = state.InputWait ? Brushes.Red : Brushes.Green;
            rectWorkSensor.Fill = state.InputWork ? Brushes.Red : Brushes.Green;
            rectOutputSensor.Fill = state.InputOutput ? Brushes.Red : Brushes.Green;
            rectLiftUpSensor.Fill = state.InputLiftUp ? Brushes.Red : Brushes.Green;
            rectLiftDownSensor.Fill = state.InputLiftDown ? Brushes.Red : Brushes.Green;
            rectAirPressureSensor.Fill = state.InputAirPressure ? Brushes.Red : Brushes.Green;
            rectEmergencySensor.Fill = state.InputEmergency ? Brushes.Red : Brushes.Green;
            rectSafetyLightSensor.Fill = state.InputSafetyLight ? Brushes.Red : Brushes.Green;
            rectDoorSwitchSensor.Fill = state.InputDoorSwitch ? Brushes.Red : Brushes.Green;

            // 限位原点（四轴）
            rectOrgX.Fill = state.OrgX ? Brushes.Red : Brushes.Green;
            rectOrgY.Fill = state.OrgY ? Brushes.Red : Brushes.Green;
            rectOrgZ.Fill = state.OrgZ ? Brushes.Red : Brushes.Green;
            rectOrgH.Fill = state.OrgH ? Brushes.Red : Brushes.Green;

            rectLimitXPos.Fill = state.LimitXPos ? Brushes.Red : Brushes.Green;
            rectLimitXNeg.Fill = state.LimitXNeg ? Brushes.Red : Brushes.Green;
            rectLimitYPos.Fill = state.LimitYPos ? Brushes.Red : Brushes.Green;
            rectLimitYNeg.Fill = state.LimitYNeg ? Brushes.Red : Brushes.Green;
            rectLimitZPos.Fill = state.LimitZPos ? Brushes.Red : Brushes.Green;
            rectLimitZNeg.Fill = state.LimitZNeg ? Brushes.Red : Brushes.Green;
            rectLimitHPos.Fill = state.LimitHPos ? Brushes.Red : Brushes.Green;
            rectLimitHNeg.Fill = state.LimitHNeg ? Brushes.Red : Brushes.Green;

            // 报警信号（四轴）
            rectAlarmX.Fill = state.AlarmX ? Brushes.Red : Brushes.Green;
            rectAlarmY.Fill = state.AlarmY ? Brushes.Red : Brushes.Green;
            rectAlarmZ.Fill = state.AlarmZ ? Brushes.Red : Brushes.Green;
            rectAlarmH.Fill = state.AlarmH ? Brushes.Red : Brushes.Green;

            // 到位信号（四轴 INP，触发=红 与限位/报警一致）
            rectAxisXDone.Fill = state.AxisXDone ? Brushes.Red : Brushes.Green;
            rectAxisYDone.Fill = state.AxisYDone ? Brushes.Red : Brushes.Green;
            rectAxisZDone.Fill = state.AxisZDone ? Brushes.Red : Brushes.Green;
            rectAxisHDone.Fill = state.AxisHDone ? Brushes.Red : Brushes.Green;
        }
        private void LoadPortList()
        {
            var ports = MotionIO.GetAvailablePorts();
            cmbPort.Items.Clear();
            foreach (var port in ports)
                cmbPort.Items.Add(port);

            if (cmbPort.Items.Count > 0)
            {
                // 默认选中系统参数配置的串口（与系统参数窗口一致），而不是列表第一项——
                // GetPortNames() 返回顺序不保证 COM1 在前，选错端口会导致握手失败
                string preferred = SysParam.Instance.Data.SelectedSerialPort;
                if (!string.IsNullOrEmpty(preferred) && cmbPort.Items.Contains(preferred))
                {
                    cmbPort.SelectedItem = preferred;
                }
                else
                {
                    cmbPort.SelectedIndex = 0;
                }
                // 如果已经连接，显示当前端口
                if (_motionIO.IsConnected && !string.IsNullOrEmpty(_motionIO.PortName))
                {
                    cmbPort.Text = _motionIO.PortName;
                }
            }
            else
            {
                cmbPort.Items.Add("无串口");
            }

            // 如果已经连接，显示当前波特率
            if (_motionIO.IsConnected && _motionIO.BaudRate > 0)
            {
                cmbBaud.Text = _motionIO.BaudRate.ToString();
            }
        }

        #region 事件处理

        private void OnConnectionStateChanged(bool connected)
        {
            // 窗口可能已关闭，需要检查
            if (!this.IsLoaded) return;
            UpdateUIByConnectionState();
        }

        private void OnDataReceived(string data)
        {
            // 必须用 InvokeAsync（非阻塞）：握手时 UI 线程被 Open() 同步等待占用，
            // 若此处 Dispatcher.Invoke 会卡死 ParseDataTask 线程，导致 PONG 匹配延迟超时（握手失败）
            if (Dispatcher.HasShutdownStarted) return;
            Dispatcher.InvokeAsync(() =>
            {
                txtRxCount.Text = $"📡 接收: {_motionIO.RxCount} 字节";
                // MessageBox.Show($"收到数据: {data}", "数据接收", MessageBoxButton.OK, MessageBoxImage.Information);
            });
        }

        private void OnErrorOccurred(string error)
        {
            if (Dispatcher.HasShutdownStarted) return;
            Dispatcher.InvokeAsync(() =>
            {
                txtStatus.Text = $"❌ 错误: {error}";
            });
        }

        private void EnableControls(bool e)
        {
            btnXAdd.IsEnabled = e; btnXDel.IsEnabled = e; btnYAdd.IsEnabled = e; btnYDel.IsEnabled = e;
            btnCamAdd.IsEnabled = e; btnCamDel.IsEnabled = e; btnLineAdd.IsEnabled = e; btnLineDel.IsEnabled = e;
            btnHome.IsEnabled = e; btnStop.IsEnabled = e; btnReset.IsEnabled = e; btnRestart.IsEnabled = e;
            btnHomeX.IsEnabled = e; btnHomeY.IsEnabled = e; btnHomeZ.IsEnabled = e; btnHomeH.IsEnabled = e;
            btnClamp1.IsEnabled = e; btnClamp2.IsEnabled = e; btnBlock.IsEnabled = e;
            btnMotor.IsEnabled = e; btnBuzzer.IsEnabled = e;
            btnRedLight.IsEnabled = e; btnGreenLight.IsEnabled = e; btnYellowLight.IsEnabled = e;
            btnCamera3DTrigger.IsEnabled = e;
            AxisSpeedSD.IsEnabled = e; txAxisSpeed.IsEnabled = e;

            // 启用时需额外根据工作位传感器状态限制 H+/H-
            if (e) UpdateHButtonsState();
        }

        #endregion

        #region 按钮事件

        private void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            if (_motionIO.IsConnected)
            {
                _motionIO.Close();
                txtStatus.Text = "串口已关闭";
            }
            else
            {
                if (cmbPort.SelectedItem == null || cmbPort.SelectedItem.ToString() == "无串口")
                {
                    MessageBox.Show("请选择有效的串口", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 容错：波特率文本非法时回退系统参数配置值（与系统参数窗口一致），避免 FormatException 静默中断连接
                int baud = int.TryParse(cmbBaud.Text, out int parsedBaud) ? parsedBaud : SysParam.Instance.Data.SelectedBaudRate;
                bool success = _motionIO.Open(cmbPort.SelectedItem.ToString(), baud);
                if (!success)
                {
                    MessageBox.Show("连接失败，请检查串口设置", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                else
                {
                    txtStatus.Text = $"串口连接成功 {cmbPort.SelectedItem.ToString()}";
                }
            }
        }
        private async void BtnClamp1_Click(object sender, RoutedEventArgs e)
        {
            if (!_motionIO.IsConnected) return;
            btnClamp1.IsEnabled = false;

            bool isUp = await _motionIO.GetIOState(OutSignal.Clamp1);
            string cmd = isUp ? "CLAMP1_OFF" : "CLAMP1_ON";
            string expectedResponse = isUp ? "CLAMP1_OFF_OK" : "CLAMP1_ON_OK";
            string rs = await _motionIO.SendCommandAndWaitAsync(cmd, 500);

            if (rs.Contains("CLAMP1_ON_OK"))
                txtStatus.Text = "顶板1成功";
            else if (rs.Contains("CLAMP1_OFF_OK"))
                txtStatus.Text = "放板1成功";
            else if (rs == "TIMEOUT")
            {
                txtStatus.Text = "操作超时";
                MessageBox.Show("操作超时");
            }

            else
                txtStatus.Text = $"操作失败: {rs}";

            btnClamp1.IsEnabled = true;
        }
        private async void BtnClamp2_Click(object sender, RoutedEventArgs e)
        {
            if (!_motionIO.IsConnected) return;
            btnClamp2.IsEnabled = false;

            bool isUp = await _motionIO.GetIOState(OutSignal.Clamp2);
            string cmd = isUp ? "CLAMP2_OFF" : "CLAMP2_ON";
            string expectedResponse = isUp ? "CLAMP2_OFF_OK" : "CLAMP2_ON_OK";
            string rs = await _motionIO.SendCommandAndWaitAsync(cmd, expectedResponse, 500);

            if (rs.Contains("CLAMP2_ON_OK"))
                txtStatus.Text = "顶板2成功";
            else if (rs.Contains("CLAMP2_OFF_OK"))
                txtStatus.Text = "放板2成功";
            else if (rs == "TIMEOUT")
            {
                txtStatus.Text = "操作超时";
                //MessageBox.Show("操作超时");
            }
            else
                txtStatus.Text = $"操作失败: {rs}";

            btnClamp2.IsEnabled = true;
        }
        private async void BtnBlock_Click(object sender, RoutedEventArgs e)
        {
            if (!_motionIO.IsConnected) return;
            btnBlock.IsEnabled = false;

            bool hasBoard = await _motionIO.GetIOState(OutSignal.Block);
            string cmd = hasBoard ? "BLOCK_OFF" : "BLOCK_ON";
            string expectedResponse = hasBoard ? "BLOCK_OFF_OK" : "BLOCK_ON_OK";

            string rs = await _motionIO.SendCommandAndWaitAsync(cmd, expectedResponse, 500);

            if (rs == "BLOCK_ON_OK") txtStatus.Text = "阻挡升起成功";
            else if (rs == "BLOCK_OFF_OK") txtStatus.Text = "阻挡降下成功";
            else if (rs == "TIMEOUT") txtStatus.Text = "操作超时";
            else txtStatus.Text = $"操作失败: {rs}";

            btnBlock.IsEnabled = true;
        }
        private async void BtnMotor_Click(object sender, RoutedEventArgs e)
        {
            if (!_motionIO.IsConnected) return;
            btnMotor.IsEnabled = false;

            bool isOn = await _motionIO.GetIOState(OutSignal.Motor);
            string cmd = isOn ? "MOTOR_OFF" : "MOTOR_ON";
            string expectedResponse = isOn ? "MOTOR_OFF_OK" : "MOTOR_ON_OK";
            string rs = await _motionIO.SendCommandAndWaitAsync(cmd, expectedResponse, 500);

            if (rs.Contains("MOTOR_ON_OK"))
                txtStatus.Text = "马达已开启";
            else if (rs.Contains("MOTOR_OFF_OK"))
                txtStatus.Text = "马达已关闭";
            else if (rs == "TIMEOUT")
            {
                txtStatus.Text = "操作超时";
                //MessageBox.Show("操作超时");
            }
            else
                txtStatus.Text = $"操作失败: {rs}";

            btnMotor.IsEnabled = true;
        }
        private async void BtnBuzzer_Click(object sender, RoutedEventArgs e)
        {
            if (!_motionIO.IsConnected) return;
            btnBuzzer.IsEnabled = false;

            bool isOn = await _motionIO.GetIOState(OutSignal.Buzzer);
            string cmd = isOn ? "BEEP_OFF" : "BEEP_ON";
            string expectedResponse = isOn ? "BEEP_OFF_OK" : "BEEP_ON_OK";
            string rs = await _motionIO.SendCommandAndWaitAsync(cmd, expectedResponse, 500);

            if (rs.Contains("BEEP_ON_OK"))
                txtStatus.Text = "蜂鸣器已开启";
            else if (rs.Contains("BEEP_OFF_OK"))
                txtStatus.Text = "蜂鸣器已关闭";
            else if (rs == "TIMEOUT")
                txtStatus.Text = "操作超时";
            else
                txtStatus.Text = $"操作失败: {rs}";

            btnBuzzer.IsEnabled = true;
        }
        private async void BtnRedLight_Click(object sender, RoutedEventArgs e)
        {
            if (!_motionIO.IsConnected) return;
            btnRedLight.IsEnabled = false;

            bool isOn = await _motionIO.GetIOState(OutSignal.RedLight);
            string cmd = isOn ? "REDLED_OFF" : "REDLED_ON";
            string expectedResponse = isOn ? "REDLED_OFF_OK" : "REDLED_ON_OK";
            string rs = await _motionIO.SendCommandAndWaitAsync(cmd, expectedResponse, 500);

            if (rs.Contains("REDLED_ON_OK"))
                txtStatus.Text = "红灯已开启";
            else if (rs.Contains("REDLED_OFF_OK"))
                txtStatus.Text = "红灯已关闭";
            else if (rs == "TIMEOUT")
                txtStatus.Text = "操作超时";
            else
                txtStatus.Text = $"操作失败: {rs}";

            btnRedLight.IsEnabled = true;
        }
        private async void BtnGreenLight_Click(object sender, RoutedEventArgs e)
        {
            if (!_motionIO.IsConnected) return;
            btnGreenLight.IsEnabled = false;

            bool isOn = await _motionIO.GetIOState(OutSignal.GreenLight);
            string cmd = isOn ? "GREENLED_OFF" : "GREENLED_ON";
            string expectedResponse = isOn ? "GREENLED_OFF_OK" : "GREENLED_ON_OK";

            string rs = await _motionIO.SendCommandAndWaitAsync(cmd, expectedResponse, 500);

            if (rs.Contains("GREENLED_ON_OK"))
                txtStatus.Text = "绿灯已开启";
            else if (rs.Contains("GREENLED_OFF_OK"))
                txtStatus.Text = "绿灯已关闭";
            else if (rs == "TIMEOUT")
                txtStatus.Text = "操作超时";
            else
                txtStatus.Text = $"操作失败: {rs}";

            btnGreenLight.IsEnabled = true;
        }
        private async void BtnYellowLight_Click(object sender, RoutedEventArgs e)
        {
            if (!_motionIO.IsConnected) return;
            btnYellowLight.IsEnabled = false;

            bool isOn = await _motionIO.GetIOState(OutSignal.YellowLight);
            string cmd = isOn ? "YELLOWLED_OFF" : "YELLOWLED_ON";
            string expectedResponse = isOn ? "YELLOWLED_OFF_OK" : "YELLOWLED_ON_OK";

            string rs = await _motionIO.SendCommandAndWaitAsync(cmd, expectedResponse, 500);

            if (rs.Contains("YELLOWLED_ON_OK"))
                txtStatus.Text = "黄灯已开启";
            else if (rs.Contains("YELLOWLED_OFF_OK"))
                txtStatus.Text = "黄灯已关闭";
            else if (rs == "TIMEOUT")
                txtStatus.Text = "操作超时";
            else
                txtStatus.Text = $"操作失败: {rs}";

            btnYellowLight.IsEnabled = true;
        }
        private async void BtnCamera3DTrigger_Click(object sender, RoutedEventArgs e)
        {
            if (!_motionIO.IsConnected) return;
            btnCamera3DTrigger.IsEnabled = false;

            bool isOn = await _motionIO.GetIOState(OutSignal.Camera3DTrigger);
            string cmd = isOn ? "CAMERA3D_OFF" : "CAMERA3D_ON";
            string expectedResponse = isOn ? "CAMERA3D_OFF_OK" : "CAMERA3D_ON_OK";
            string rs = await _motionIO.SendCommandAndWaitAsync(cmd, expectedResponse, 500);

            if (rs.Contains("CAMERA3D_ON_OK"))
                txtStatus.Text = "3D相机触发已开启";
            else if (rs.Contains("CAMERA3D_OFF_OK"))
                txtStatus.Text = "3D相机触发已关闭";
            else if (rs == "TIMEOUT")
                txtStatus.Text = "操作超时";
            else
                txtStatus.Text = $"操作失败: {rs}";

            btnCamera3DTrigger.IsEnabled = true;
        }
        #endregion

        #region 速度控制

        private bool _isSyncingSpeed = false;

        private async void AxisSpeedSD_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isSyncingSpeed) return;
            if (_motionIO == null || !_motionIO.IsConnected) return;

            double pct = Math.Round(e.NewValue);
            double mmPerSec = pct / 100.0 * SysParam.Instance.Data.MaxSpeed;
            await _motionIO.SetSpeedJog(mmPerSec);
        }

        private async void BtnSyncSpeed_Click(object sender, RoutedEventArgs e)
        {
            await SyncSpeedFromController();
        }

        private async Task SyncSpeedFromController()
        {
            if (!_motionIO.IsConnected) return;

            int pulse = await _motionIO.GetJogSpeed();
            if (pulse >= 0)
            {
                double mmPerSec = _motionIO.PulseToMm(pulse, "X");
                double pct = mmPerSec / SysParam.Instance.Data.MaxSpeed * 100.0;
                _isSyncingSpeed = true;
                AxisSpeedSD.Value = Math.Round(pct);
                _isSyncingSpeed = false;
            }
        }

        #endregion

        #region 轴控制
        /// <summary>手动 XYZH 移动按钮：设备报警状态禁止（返回 true=已拦截）</summary>
        private bool ManualMoveBlocked()
        {
            if (DeviceMonitor.Instance.State == DeviceState.Alarm)
            {
                txtStatus.Text = "报警状态，禁止手动移动";
                return true;
            }
            return false;
        }

        // X轴（按下点动，松开停止；报警状态拦截按下）
        private async void BtnXAdd_PreviewMouseDown(object s, MouseButtonEventArgs e) { if (ManualMoveBlocked()) return; await _motionIO.SendCommandAsync("X-"); }
        private async void BtnXAdd_PreviewMouseUp(object s, MouseButtonEventArgs e) { await _motionIO.SendCommandAsync("X STOP"); GetPosAndDisplay(); }
        private async void BtnXDel_PreviewMouseDown(object s, MouseButtonEventArgs e) { if (ManualMoveBlocked()) return; await _motionIO.SendCommandAsync("X+"); }
        private async void BtnXDel_PreviewMouseUp(object s, MouseButtonEventArgs e) { await _motionIO.SendCommandAsync("X STOP"); GetPosAndDisplay(); }

        // Y轴
        private async void BtnYAdd_PreviewMouseDown(object s, MouseButtonEventArgs e) { if (ManualMoveBlocked()) return; await _motionIO.SendCommandAsync("Y+"); }
        private async void BtnYAdd_PreviewMouseUp(object s, MouseButtonEventArgs e) { await _motionIO.SendCommandAsync("Y STOP"); GetPosAndDisplay(); }
        private async void BtnYDel_PreviewMouseDown(object s, MouseButtonEventArgs e) { if (ManualMoveBlocked()) return; await _motionIO.SendCommandAsync("Y-"); }
        private async void BtnYDel_PreviewMouseUp(object s, MouseButtonEventArgs e) { await _motionIO.SendCommandAsync("Y STOP"); GetPosAndDisplay(); }

        // Cam轴 (Z)
        private async void BtnCamAdd_PreviewMouseDown(object s, MouseButtonEventArgs e) { if (ManualMoveBlocked()) return; await _motionIO.SendCommandAsync("Z-"); }
        private async void BtnCamAdd_PreviewMouseUp(object s, MouseButtonEventArgs e) { await _motionIO.SendCommandAsync("Z STOP"); GetPosAndDisplay(); }
        private async void BtnCamDel_PreviewMouseDown(object s, MouseButtonEventArgs e) { if (ManualMoveBlocked()) return; await _motionIO.SendCommandAsync("Z+"); }
        private async void BtnCamDel_PreviewMouseUp(object s, MouseButtonEventArgs e) { await _motionIO.SendCommandAsync("Z STOP"); GetPosAndDisplay(); }

        // Line轴 (H)
        private async void BtnLineAdd_PreviewMouseDown(object s, MouseButtonEventArgs e) { if (ManualMoveBlocked()) return; await _motionIO.SendCommandAsync("H-"); }
        private async void BtnLineAdd_PreviewMouseUp(object s, MouseButtonEventArgs e) { await _motionIO.SendCommandAsync("H STOP"); GetPosAndDisplay(); }
        private async void BtnLineDel_PreviewMouseDown(object s, MouseButtonEventArgs e) { if (ManualMoveBlocked()) return; await _motionIO.SendCommandAsync("H+"); }
        private async void BtnLineDel_PreviewMouseUp(object s, MouseButtonEventArgs e) { await _motionIO.SendCommandAsync("H STOP"); GetPosAndDisplay(); }

        private async void GetPosAndDisplay()
        {
            PositionInfo position = await _motionIO.GetCurrentPosition();

            txtXPos.Text = position.Xmm.ToString("F3");
            txtYPos.Text = position.Ymm.ToString("F3");
            txtZPos.Text = position.Zmm.ToString("F3");
            txtHPos.Text = position.Hmm.ToString("F3");

            // 3D相机位置 = 2D相机位置 + 系统参数中的3D相机偏移
            double offsetX = SysParam.Instance.Data.Camera3DOffsetX;
            double offsetY = SysParam.Instance.Data.Camera3DOffsetY;
            txt3DCamX.Text = (position.Xmm + offsetX).ToString("F3");
            txt3DCamY.Text = (position.Ymm + offsetY).ToString("F3");
        }

        //快捷指令
        private async void BtnHome_Click(object s, RoutedEventArgs e)
        {
            if (!_motionIO.IsConnected) return;

            // H轴回零前检查传送带链路是否有板（工作位/入口/出口）
            if (await _motionIO.GetIOState(InputSignal.WorkSensor, 1000) ||
                await _motionIO.GetIOState(InputSignal.WaitSensor, 1000) ||
                await _motionIO.GetIOState(InputSignal.OutputSensor, 1000))
            {
                MessageBox.Show("传送带上有板（工作位/入口/出口），请移走板后再回零", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            btnHome.IsEnabled = false;

            CommandResult result = await _motionIO.HomeXYZH();

            txtStatus.Text = result.Message;

            if (result.IsTimeout) MessageBox.Show("操作超时");

            btnHome.IsEnabled = true;
            GetPosAndDisplay();
        }
        /// <summary>重启：统一走会话生命周期（停轮询→RESTART→新一代握手→恢复），按钮禁用防重复点击</summary>
        private async void BtnRestart_Click(object s, RoutedEventArgs e)
        {
            if (!btnRestart.IsEnabled) return;
            btnRestart.IsEnabled = false;
            txtStatus.Text = "主板重启中，等待重新握手...";
            try
            {
                bool ok = await _motionIO.RestartAndRehandshakeAsync();
                txtStatus.Text = ok ? "重启完成，会话已恢复" : "重启失败：重新握手无响应，请检查供电/串口";
            }
            catch (Exception ex)
            {
                txtStatus.Text = $"重启异常: {ex.Message}";
            }
            finally
            {
                btnRestart.IsEnabled = true;
            }
        }
        private async void BtnReset_Click(object s, RoutedEventArgs e)
        {
            if (!_motionIO.IsConnected) return;
            btnReset.IsEnabled = false;
            txtStatus.Text = "复位中...";

            try
            {
                DeviceMonitor.Instance.ResetAlarm(); // 报警复位：清锁存+关蜂鸣器（信号仍在则主界面状态栏兜底提示）
                await _motionIO.ResetIO();

                // 工作流重置
                var mainVM = Application.Current.MainWindow?.DataContext as MainViewModel;
                mainVM?.ResetWorkFlowStates();

                // X/Y移动到待机位
                double standbyX = SysParam.Instance.Data.StandbyX;
                double standbyY = SysParam.Instance.Data.StandbyY;
                await _motionIO.MoveToAndWaitOK(standbyX, standbyY);

                txtStatus.Text = "复位完成";
                GetPosAndDisplay();
            }
            catch (Exception ex)
            {
                txtStatus.Text = $"复位异常: {ex.Message}";
            }
            finally
            {
                btnReset.IsEnabled = true;
            }
        }

        #region 单轴回零

        private async void BtnHomeX_Click(object sender, RoutedEventArgs e)
        {
            if (!_motionIO.IsConnected) return;
            btnHomeX.IsEnabled = false;
            txtStatus.Text = "X轴回零中...";
            bool ok = await _motionIO.Home("X", 5000);
            txtStatus.Text = ok ? "X轴回零完成" : "X轴回零超时";
            btnHomeX.IsEnabled = true;
            GetPosAndDisplay();
        }

        private async void BtnHomeY_Click(object sender, RoutedEventArgs e)
        {
            if (!_motionIO.IsConnected) return;
            btnHomeY.IsEnabled = false;
            txtStatus.Text = "Y轴回零中...";
            bool ok = await _motionIO.Home("Y", 5000);
            txtStatus.Text = ok ? "Y轴回零完成" : "Y轴回零超时";
            btnHomeY.IsEnabled = true;
            GetPosAndDisplay();
        }

        private async void BtnHomeZ_Click(object sender, RoutedEventArgs e)
        {
            if (!_motionIO.IsConnected) return;
            btnHomeZ.IsEnabled = false;
            txtStatus.Text = "Z轴回零中...";
            bool ok = await _motionIO.Home("Z", 5000);
            txtStatus.Text = ok ? "Z轴回零完成" : "Z轴回零超时";
            btnHomeZ.IsEnabled = true;
            GetPosAndDisplay();
        }

        private async void BtnHomeH_Click(object sender, RoutedEventArgs e)
        {
            if (!_motionIO.IsConnected) return;

            // H轴回零前检查传送带链路是否有板（工作位/入口/出口）
            if (await _motionIO.GetIOState(InputSignal.WorkSensor, 1000) ||
                await _motionIO.GetIOState(InputSignal.WaitSensor, 1000) ||
                await _motionIO.GetIOState(InputSignal.OutputSensor, 1000))
            {
                MessageBox.Show("传送带上有板（工作位/入口/出口），请移走板后再回零", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            btnHomeH.IsEnabled = false;
            try
            {
                txtStatus.Text = "H轴回零中...";
                bool ok = await _motionIO.Home("H", (int)SysParam.Instance.Data.AxisHomeTimeout);
                txtStatus.Text = ok ? "H轴回零完成" : "H轴回零超时";
            }
            catch (Exception ex)
            {
                txtStatus.Text = $"H轴回零异常: {ex.Message}";
            }
            finally
            {
                btnHomeH.IsEnabled = true;
                GetPosAndDisplay();
            }
        }

        #endregion
        private void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            // 急停按钮已禁用（F407 无 PC ASCII 全局 STOP，RESTART 不是停止命令）：
            // 真正的急停走独立硬件安全链（直接切断伺服驱动器电源）；点击兜底提示，不再发送任何命令
            MessageBox.Show("软件急停未提供。\n真正的急停请使用独立硬件安全链（直接切断伺服驱动器电源）。",
                "急停", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        #endregion

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
        }


    }
}