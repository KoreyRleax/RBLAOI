using RBLAOI.Core.LightSource;
using RBLAOI.Core.Managers;
using RBLAOI.Core.Utility;
using RBLAOI.Core.Vision;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace RBLAOI.Views
{
    /// <summary>
    /// 光源控制对话框（4 通道亮度 0-255 + 亮灭开关；串口号/波特率/通道数为系统参数，
    /// 在 系统参数→连接管理 中配置，所有方案共用）。
    /// 串口连接由 MainViewModel 在程序启动时统一建立（与下位机/网口检查同时，失败弹窗），
    /// 此处未连接时兜底重连。
    /// 拖动滑块/勾选开关/输入数值时实时下发完整多通道帧（ZVD 协议，帧段数 = 系统参数通道数）。
    /// 连接失败时仅提示，参数保存功能不受影响。
    /// </summary>
    public partial class LightControlWindow : Window, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        private readonly LightSourceController _light = LightSourceController.Instance;
        private readonly EditProjectWindow _owner; // 非模态打开：参数实时回写

        // 实时下发：UI 线程只写"最新待发送状态"，后台线程 10ms 轮询发送，丢弃中间帧、不阻塞 UI
        private readonly object _pendingLock = new object();
        private LightChannelState[] _pendingChannels;
        private Thread _sendThread;
        private volatile bool _closing;

        private void RaisePropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private int _channelCount = 4; // 控制器实际通道数（系统参数），多通道帧段数必须与之匹配

        // ========== 训练拍照：曝光时间(0-30000μs, 整数显示, 实时下发VM) ==========
        private double _exposureValue = 10000;
        public double ExposureValue
        {
            get => _exposureValue;
            set
            {
                _exposureValue = Math.Round(ClampExposure(value));
                RaisePropertyChanged();
                if (_owner != null) _owner.CameraExposureValue = _exposureValue; // 非模态：实时同步回方案编辑窗口
                NotifyExposureChanged();
            }
        }
        private static double ClampExposure(double v) => v < 0 ? 0 : (v > 30000 ? 30000 : v);

        private string _trainDir = "";
        public string TrainDir { get => _trainDir; set { _trainDir = value; RaisePropertyChanged(); } }

        // 曝光实时下发防抖：拖动高频变化只发最新值
        private CancellationTokenSource _exposureCts;
        private async void NotifyExposureChanged()
        {
            _exposureCts?.Cancel();
            _exposureCts = new CancellationTokenSource();
            try
            {
                await Task.Delay(300, _exposureCts.Token);
                await Vision2DClient.Instance.SendCommandAsync($"SET_EXPOSURE:{(int)_exposureValue}");
            }
            catch (TaskCanceledException) { }
            catch (Exception ex) { Log.Warning($"设置曝光失败: {ex.Message}", "TrainCapture"); }
        }

        private double _channel1;
        public double Channel1
        {
            get => _channel1;
            set { _channel1 = Math.Round(Clamp(value)); RaisePropertyChanged(); if (_owner != null) _owner.LightChannel1 = _channel1; NotifyLightChanged(); } // 只保留整数亮度
        }

        private double _channel2;
        public double Channel2
        {
            get => _channel2;
            set { _channel2 = Math.Round(Clamp(value)); RaisePropertyChanged(); if (_owner != null) _owner.LightChannel2 = _channel2; NotifyLightChanged(); }
        }

        private double _channel3;
        public double Channel3
        {
            get => _channel3;
            set { _channel3 = Math.Round(Clamp(value)); RaisePropertyChanged(); if (_owner != null) _owner.LightChannel3 = _channel3; NotifyLightChanged(); }
        }

        private double _channel4;
        public double Channel4
        {
            get => _channel4;
            set { _channel4 = Math.Round(Clamp(value)); RaisePropertyChanged(); if (_owner != null) _owner.LightChannel4 = _channel4; NotifyLightChanged(); }
        }

        private bool _lightOn1 = true;
        public bool LightOn1 { get => _lightOn1; set { _lightOn1 = value; RaisePropertyChanged(); if (_owner != null) _owner.LightOn1 = value; NotifyLightChanged(); } }

        private bool _lightOn2 = true;
        public bool LightOn2 { get => _lightOn2; set { _lightOn2 = value; RaisePropertyChanged(); if (_owner != null) _owner.LightOn2 = value; NotifyLightChanged(); } }

        private bool _lightOn3 = true;
        public bool LightOn3 { get => _lightOn3; set { _lightOn3 = value; RaisePropertyChanged(); if (_owner != null) _owner.LightOn3 = value; NotifyLightChanged(); } }

        private bool _lightOn4 = true;
        public bool LightOn4 { get => _lightOn4; set { _lightOn4 = value; RaisePropertyChanged(); if (_owner != null) _owner.LightOn4 = value; NotifyLightChanged(); } }

        private static double Clamp(double v) => v < 0 ? 0 : (v > 255 ? 255 : v);

        /// <summary>亮度 0-255 直接换算为硬件值；亮灭由复选框决定（T/F 必须走多通道帧）</summary>
        private static LightChannelState ToChannelState(double brightness, bool on)
            => new LightChannelState((int)System.Math.Round(brightness), on);

        public LightControlWindow(EditProjectWindow owner, double exposure, double ch1, double ch2, double ch3, double ch4,
            bool on1, bool on2, bool on3, bool on4)
        {
            InitializeComponent();
            _owner = owner;
            _exposureValue = ClampExposure(exposure); // 直接设字段：初始化不触发实时下发
            TrainDir = SysParam.Instance.LightTrainDir; // 持久化的训练存储路径（选一次长期有效）
            Channel1 = ch1;
            Channel2 = ch2;
            Channel3 = ch3;
            Channel4 = ch4;
            LightOn1 = on1;
            LightOn2 = on2;
            LightOn3 = on3;
            LightOn4 = on4;

            DataContext = this;

            // 串口号/波特率/通道数为系统参数（在 系统参数→连接管理 中配置），由启动时的 ConnectLightSource 统一连接
            string port = SysParam.Instance.LightSourceSerialPort;

            // 按系统参数通道数初始化：发送帧段数必须与控制器实际通道数一致（2通道控制器收到4段帧无法解析）
            ApplyChannelCount(SysParam.Instance.LightSourceChannelCount);

            // 连接光源控制器串口：启动时已建立连接则复用；否则在此兜底重连（含波特率）。
            // 失败仅提示，亮度参数仍可编辑保存
            bool connected = _light.IsOpen
                ? string.Equals(_light.PortName, port, StringComparison.OrdinalIgnoreCase)
                : _light.Open(port, SysParam.Instance.LightSourceBaudRate);
            if (connected)
            {
                StartSendLoop();
                NotifyLightChanged(); // 打开后立即按当前值点亮一次
            }
            else
            {
                MessageBox.Show($"光源串口 {port} 连接失败，无法实时调光。\n请确认接线与串口号后重新打开本窗口。",
                    "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>应用系统参数通道数：同步控制器帧结构、隐藏多余通道行、按新帧结构重新下发</summary>
        private void ApplyChannelCount(int count)
        {
            _channelCount = count == 2 ? 2 : 4;
            _light.SetChannelCount(_channelCount);
            ChRow3.Visibility = _channelCount >= 3 ? Visibility.Visible : Visibility.Collapsed;
            ChRow4.Visibility = _channelCount >= 4 ? Visibility.Visible : Visibility.Collapsed;
            NotifyLightChanged();
        }

        /// <summary>通道值变化：更新待发送快照，由后台线程下发（拖动滑块时高频调用，仅更新内存）。
        /// 快照长度 = 控制器实际通道数，保证多通道帧段数匹配</summary>
        private void NotifyLightChanged()
        {
            if (_sendThread == null || _closing) return;
            lock (_pendingLock)
            {
                var states = new LightChannelState[_channelCount];
                for (var i = 0; i < states.Length; i++)
                {
                    switch (i)
                    {
                        case 0: states[i] = ToChannelState(_channel1, _lightOn1); break;
                        case 1: states[i] = ToChannelState(_channel2, _lightOn2); break;
                        case 2: states[i] = ToChannelState(_channel3, _lightOn3); break;
                        case 3: states[i] = ToChannelState(_channel4, _lightOn4); break;
                    }
                }
                _pendingChannels = states;
            }
        }

        /// <summary>启动后台发送线程（仅一次）</summary>
        private void StartSendLoop()
        {
            if (_sendThread != null) return;
            _sendThread = new Thread(SendLoop) { IsBackground = true };
            _sendThread.Start();
        }

        /// <summary>后台发送循环：每 10ms 检查一次待发送快照，只发最新值（丢弃中间帧）</summary>
        private void SendLoop()
        {
            while (!_closing)
            {
                LightChannelState[] snapshot = null;
                lock (_pendingLock)
                {
                    if (_pendingChannels != null)
                    {
                        snapshot = _pendingChannels;
                        _pendingChannels = null;
                    }
                }

                if (snapshot != null)
                {
                    try
                    {
                        if (!_light.SetAll(snapshot))
                            Log.Warning("光源指令无回复，请检查串口连接", "LightControl");
                    }
                    catch (Exception ex)
                    {
                        Log.Exception(ex, "LightControl");
                        Thread.Sleep(500); // 串口异常时降频重试，避免刷屏
                    }
                }
                else
                {
                    Thread.Sleep(10);
                }
            }
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            // 输入框手动输入越界时收敛到 0-255（非模态：数据已实时回写，此处仅收尾）
            Channel1 = Clamp(Channel1);
            Channel2 = Clamp(Channel2);
            Channel3 = Clamp(Channel3);
            Channel4 = Clamp(Channel4);
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        // ========== 训练拍照 ==========

        private void BtnBrowseDir_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = "选择训练图片存储目录" };
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                TrainDir = dlg.SelectedPath;
                SysParam.Instance.LightTrainDir = dlg.SelectedPath; // 持久化：下次打开自动带出
            }
        }

        /// <summary>
        /// 训练拍照：按当前光源值+曝光拍照，存到用户指定目录（不影响瓦片）。
        /// 流程：SET_EXPOSURE → SET_PATH(训练目录) → GRAB → 等VM存图 → 改名为 曝光-通道1-通道2.bmp → 恢复VM路径
        /// </summary>
        private async void BtnTrainCapture_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(TrainDir))
            {
                MessageBox.Show("请先选择训练图片存储路径。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!_light.IsOpen)
            {
                MessageBox.Show("光源串口未连接，无法按当前光源值拍照。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                BtnTrainCapture.IsEnabled = false;

                // 1. 按当前通道值点亮光源（完整帧，后台线程10ms内下发）
                NotifyLightChanged();
                // 2. 设置曝光并拍照（VM 直接存图到训练目录）
                Directory.CreateDirectory(TrainDir);
                await Vision2DClient.Instance.SendCommandAsync($"SET_EXPOSURE:{(int)_exposureValue}");
                await Vision2DClient.Instance.SendCommandAsync($"SET_PATH:{TrainDir}");
                await Task.Delay(100); 
                string grabResult = await Vision2DClient.Instance.SendCommandAndWaitAsync("GRAB", "GRAB_OK",
                    (int)SysParam.Instance.Data.GrabTimeout);
                if (grabResult == "TIMEOUT")
                {
                    MessageBox.Show("拍照超时。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 3. 解析 VM 返回的文件名并定位实际保存位置
                //    （实测 SET_PATH 在 VM 端可能不生效，图片存到方案 Images/Pin 下，需候选目录查找）
                string fileName = grabResult;
                string srcPath = null;
                for (int i = 0; i < 50 && srcPath == null; i++)
                {
                    srcPath = LocateVmImage(fileName);
                    if (srcPath == null) await Task.Delay(100);
                }

                if (srcPath == null || !IsFileReady(srcPath))
                {
                    //MessageBox.Show($"VM 保存的图片未找到: {fileName}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // 4. 重命名为 曝光-通道1-通道2.bmp（同名文件先删后移）
                string target = Path.Combine(TrainDir, BuildTrainFileName());
                if (File.Exists(target))
                {
                    File.SetAttributes(target, FileAttributes.Normal);
                    File.Delete(target);
                }
                File.Move(srcPath, target);
                Log.Info($"训练拍照: {Path.GetFileName(target)}", "TrainCapture");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"拍照失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                await Task.Delay(500);
                BtnTrainCapture.IsEnabled = true;
            }
        }

        /// <summary>文件名: 曝光-通道1值-通道2值.bmp（按控制器实际通道数拼接）</summary>
        private string BuildTrainFileName()
        {
            var channels = new[] { _channel1, _channel2, _channel3, _channel4 };
            var parts = new List<string> { ((int)Math.Round(_exposureValue)).ToString() };
            for (var i = 0; i < _channelCount; i++)
                parts.Add(((int)Math.Round(channels[i])).ToString());
            return string.Join("-", parts) + ".bmp";
        }

        /// <summary>
        /// 在候选目录中定位 VM 保存的图片（SET_PATH 在 VM 端可能不生效，
        /// 实测图片存到方案 Images/Pin 下，可能按针型建子目录）。
        /// </summary>
        private string LocateVmImage(string fileName)
        {
            if (string.IsNullOrEmpty(TrainDir)) return null;
            var p = Path.Combine(TrainDir, fileName);
            return File.Exists(p) ? p : null;
        }

        /// <summary>文件是否已写完（独占打开成功即视为就绪）</summary>
        private static bool IsFileReady(string path)
        {
            try
            {
                using (var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None)) { }
                return true;
            }
            catch
            {
                return false;
            }
        }

        protected override void OnClosed(System.EventArgs e)
        {
            _closing = true;
            _sendThread?.Join(300);
            // 串口连接生命周期归 MainViewModel：程序启动时建立、退出时关闭，窗口关闭不释放
            base.OnClosed(e);
        }

        /// <summary>输入框只允许数字和小数点</summary>
        private void NumberOnly_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            foreach (char c in e.Text)
            {
                if (!char.IsDigit(c) && c != '.')
                {
                    e.Handled = true;
                    return;
                }
            }
        }
    }
}
