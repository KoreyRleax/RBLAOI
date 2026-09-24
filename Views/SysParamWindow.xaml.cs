using RBLAOI.Core;
using RBLAOI.Core.LightSource;
using RBLAOI.Core.Managers;
using RBLAOI.Core.Motion;
using RBLAOI.Core.Vision;
using RBLAOI.ViewModels;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using static System.Windows.Forms.VisualStyles.VisualStyleElement.Window;

namespace RBLAOI.Views
{
    public partial class SysParamWindow : Window
    {
        private SysParamViewModel _viewModel;
        private Vision2DClient _vision2DClient;
        private Vision3DClient _vision3DClient;
        private MotionIO _motionIO;
        private SysParam _sysParam;
        private LightSourceController _light;

        public SysParamWindow()
        {
            InitializeComponent();
            _viewModel = new SysParamViewModel();
            _motionIO = MotionIO.Instance;
            _vision2DClient = Vision2DClient.Instance;
            _vision3DClient = Vision3DClient.Instance;
            _light = LightSourceController.Instance;
            _sysParam = SysParam.Instance;


            _motionIO.ErrorOccurred += _motionIO_ErrorOccurred;
            _vision2DClient.OnConnectionError += _visionClient_OnConnectionError;

            _viewModel.UpdateSerialTextAndButton(_motionIO.IsConnected);// 初始化串口状态
            _viewModel.UpdateVisionTextAndButton(_vision2DClient.IsConnected);// 初始化视觉状态
            _viewModel.UpdateVision3DTextAndButton(_vision3DClient.IsConnected);// 初始化视觉状态
            _viewModel.UpdateLightTextAndButton(_light.IsOpen);// 初始化光源状态

            DataContext = _viewModel;
        }


        private void Dialog()
        {
            // 检查是否有修改
            if (_viewModel.HasChanges())
            {
                var result = MessageBox.Show(
                    "参数已被修改，是否保存？",
                    "提示",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    _viewModel.Save();
                    DialogResult = true;
                }
                else if (result == MessageBoxResult.No)
                {
                    _viewModel.Revert();  // 恢复原始值
                    DialogResult = false;
                }
            }
            else
            {
                // 没有修改，直接退出
                DialogResult = false;
            }
        }

        private void BtnExit_Click(object sender, RoutedEventArgs e)
        {
            Dialog();
            Close();
        }

        private void BtnTestSerial_Click(object sender, RoutedEventArgs e)
        {
            if (_motionIO.IsConnected)
            {
                _motionIO.Close();
                _viewModel.UpdateSerialTextAndButton(false);
                return;
            }
            try
            {
                string portName = _viewModel.SelectedSerialPort;
                int baudRate = _viewModel.SelectedBaudRate;
                bool isConnected = _motionIO.Open(portName, baudRate);
                _viewModel.UpdateSerialTextAndButton(isConnected);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"连接异常：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }

        }

        /// <summary>光源控制连接/断开（串口号/波特率/通道数为系统参数）</summary>
        private void BtnTestLight_Click(object sender, RoutedEventArgs e)
        {
            if (_light.IsOpen)
            {
                _light.Close();
                _viewModel.UpdateLightTextAndButton(false);
                return;
            }
            try
            {
                string portName = _viewModel.LightSourceSerialPort;
                int baudRate = _viewModel.LightSourceBaudRate;
                bool isConnected = _light.Open(portName, baudRate);
                _viewModel.UpdateLightTextAndButton(isConnected);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"连接异常：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void BtnTestVision_Click(object sender, RoutedEventArgs e)
        {
            if (_vision2DClient != null && _vision2DClient.IsConnected)
            {
                _vision2DClient.Disconnect();
                _viewModel.UpdateVisionTextAndButton(false);
                return;
            }

            try
            {
                string ip = _viewModel.VisionIp;
                int port = _viewModel.VisionPort;

                if (string.IsNullOrEmpty(ip))
                {
                    MessageBox.Show("请先输入IP地址", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                bool isConnected = await _vision2DClient.ConnectAsync(ip, port);
                _viewModel.UpdateVisionTextAndButton(isConnected);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"连接异常：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void BtnTestVision3D_Click(object sender, RoutedEventArgs e)
        {
            if (_vision3DClient != null && _vision3DClient.IsConnected)
            {
                _vision3DClient.Disconnect();
                _viewModel.UpdateVision3DTextAndButton(false);
                return;
            }

            try
            {
                string ip = _viewModel.Vision3DIp;
                int port = _viewModel.Vision3DPort;

                if (string.IsNullOrEmpty(ip))
                {
                    MessageBox.Show("请先输入IP地址", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                bool isConnected = await _vision3DClient.ConnectAsync(ip, port);
                _viewModel.UpdateVision3DTextAndButton(isConnected);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"连接异常：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnCalibrate_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new CalibrationWindow(_viewModel)
            {
                Owner = this
            };
            dlg.ShowDialog();
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            var viewModel = DataContext as SysParamViewModel;
            viewModel?.Save();
            MessageBox.Show("参数已保存", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void AxisSpeed_LostFocus(object sender, RoutedEventArgs e)
        {
            var viewModel = DataContext as SysParamViewModel;
            if (viewModel != null && viewModel.MaxSpeed > 0 && viewModel.AxisSpeed > viewModel.MaxSpeed)
            {
                MessageBox.Show(
                    $"轴速度不能超过最大速度 {viewModel.MaxSpeed} mm/s",
                    "警告",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        private void _visionClient_OnConnectionError(string obj)
        {
            MessageBox.Show($"连接异常：{obj}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        private void _motionIO_ErrorOccurred(string obj)
        {
            MessageBox.Show($"连接异常：{obj}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        /// <summary>
        /// 重写关闭事件，在窗口关闭时清理事件订阅，避免内存泄漏
        /// </summary>
        protected override void OnClosed(EventArgs e)
        {
            // 取消事件订阅
            if (_motionIO != null)
            {
                _motionIO.ErrorOccurred -= _motionIO_ErrorOccurred;
            }

            if (_vision2DClient != null)
            {
                _vision2DClient.OnConnectionError -= _visionClient_OnConnectionError;
            }

            // 清理 ViewModel
            _viewModel?.Dispose();

            base.OnClosed(e);
        }

        /// <summary>
        /// 重写关闭命令，处理窗口右上角 X 按钮点击
        /// </summary>
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            Dialog();
            base.OnClosing(e);
        }

    }
}