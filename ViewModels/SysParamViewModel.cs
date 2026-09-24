using RBLAOI.Core;
using RBLAOI.Core.Managers;
using RBLAOI.Core.Utility;
using RBLAOI.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Ports;
using System.Reflection;
using System.Windows.Media;
using VM.Core;

namespace RBLAOI.ViewModels
{
    public class SysParamViewModel : NotificationObject
    {
        private SysParam _sysParam;
        private Dictionary<string, object> _originalValues;

        // ==================== 连接状态（手动更新） ====================
        private string _serialStatusText = "未连接";
        private Brush _serialStatusColor = Brushes.Gray;
        private string _serialButtonText = "连接";
        private Brush _serialButtonColor = (Brush)new BrushConverter().ConvertFrom("#FF2D6A4F");

        private string _visionStatusText = "未连接";
        private Brush _visionStatusColor = Brushes.Gray;
        private string _visionButtonText = "连接";
        private Brush _visionButtonColor = (Brush)new BrushConverter().ConvertFrom("#FF2D6A4F");

        private string _vision3DStatusText = "未连接";
        private Brush _vision3DStatusColor = Brushes.Gray;
        private string _vision3DButtonText = "连接";
        private Brush _vision3DButtonColor = (Brush)new BrushConverter().ConvertFrom("#FF2D6A4F");

        private string _lightStatusText = "未连接";
        private Brush _lightStatusColor = Brushes.Gray;
        private string _lightButtonText = "连接";
        private Brush _lightButtonColor = (Brush)new BrushConverter().ConvertFrom("#FF2D6A4F");

        private bool _isSerialControlsEnabled = true;

        private bool _isVisionControlsEnabled = true;

        private bool _isVision3DControlsEnabled = true;

        private bool _isLightControlsEnabled = true;
        // ==================== 列表数据 ====================
        private ObservableCollection<string> _serialPorts;
        private ObservableCollection<int> _baudRates;
        public ObservableCollection<string> DetectionModes { get; set; } = new ObservableCollection<string> { "VM方案", "VM-Blob方案" };

        // ==================== 主题 ====================
        public ObservableCollection<string> Themes { get; set; } = new ObservableCollection<string> { "深色", "亮色" };

        // ==================== 构造函数 ====================
        public SysParamViewModel()
        {
            _sysParam = SysParam.Instance;
            LoadSerialPorts();
            LoadBaudRates();
            LoadData();          // 反射自动同步所有 SysParam 属性
            SaveOriginalValues();
        }

        // ==================== 连接状态属性（非序列化，手动通知） ====================
        public string SerialStatusText { get => _serialStatusText; set => SetProperty(ref _serialStatusText, value); }
        public Brush SerialTextColor { get => _serialStatusColor; set => SetProperty(ref _serialStatusColor, value); }
        public string SerialButtonText { get => _serialButtonText; set => SetProperty(ref _serialButtonText, value); }
        public Brush SerialButtonColor { get => _serialButtonColor; set => SetProperty(ref _serialButtonColor, value); }

     
        public string VisionStatusText { get => _visionStatusText; set => SetProperty(ref _visionStatusText, value); }
        public Brush VisionTextColor { get => _visionStatusColor; set => SetProperty(ref _visionStatusColor, value); }
        public string VisionButtonText { get => _visionButtonText; set => SetProperty(ref _visionButtonText, value); }
        public Brush VisionButtonColor { get => _visionButtonColor; set => SetProperty(ref _visionButtonColor, value); }

        public string Vision3DStatusText { get => _vision3DStatusText; set => SetProperty(ref _vision3DStatusText, value); }
        public Brush Vision3DTextColor { get => _vision3DStatusColor; set => SetProperty(ref _vision3DStatusColor, value); }
        public string Vision3DButtonText { get => _vision3DButtonText; set => SetProperty(ref _vision3DButtonText, value); }
        public Brush Vision3DButtonColor { get => _vision3DButtonColor; set => SetProperty(ref _vision3DButtonColor, value); }

        public string LightStatusText { get => _lightStatusText; set => SetProperty(ref _lightStatusText, value); }
        public Brush LightTextColor { get => _lightStatusColor; set => SetProperty(ref _lightStatusColor, value); }
        public string LightButtonText { get => _lightButtonText; set => SetProperty(ref _lightButtonText, value); }
        public Brush LightButtonColor { get => _lightButtonColor; set => SetProperty(ref _lightButtonColor, value); }

        public bool IsSerialControlsEnabled { get => _isSerialControlsEnabled; set => SetProperty(ref _isSerialControlsEnabled, value); }
        public bool IsVisionControlsEnabled { get => _isVisionControlsEnabled; set => SetProperty(ref _isVisionControlsEnabled, value); }
        public bool IsVision3DControlsEnabled { get => _isVision3DControlsEnabled; set => SetProperty(ref _isVision3DControlsEnabled, value); }
        public bool IsLightControlsEnabled { get => _isLightControlsEnabled; set => SetProperty(ref _isLightControlsEnabled, value); }

        public ObservableCollection<string> SerialPorts { get => _serialPorts; set => SetProperty(ref _serialPorts, value); }
        public ObservableCollection<int> BaudRates { get => _baudRates; set => SetProperty(ref _baudRates, value); }

        // ==================== 系统独有参数（通过反射自动同步） ====================
        [SysParam] public double RightBottomX { get; set; }
        [SysParam] public double RightBottomY { get; set; }

        [SysParam] public string VisionIp { get; set; }
        [SysParam] public int VisionPort { get; set; }
        [SysParam] public string Vision3DIp { get; set; }
        [SysParam] public int Vision3DPort { get; set; }

        [SysParam] public string SelectedSerialPort { get; set; }
        [SysParam] public int SelectedBaudRate { get; set; }
        [SysParam] public string LightSourceSerialPort { get; set; }
        [SysParam] public int LightSourceBaudRate { get; set; }
        [SysParam] public int LightSourceChannelCount { get; set; }

        [SysParam] public double AxisSpeed { get; set; }
        [SysParam] public double MaxSpeed { get; set; }
        [SysParam] public double Scan3DSpeed { get; set; }
        [SysParam] public double HAxisMaxPosition { get; set; }

        // 新增相机/延时/开关参数（仅系统级）
        [SysParam] public double Camera3DOffsetX { get; set; }
        [SysParam] public double Camera3DOffsetY { get; set; }
        [SysParam] public double SensorFovWidth { get; set; }
        [SysParam] public double SensorFovHeight { get; set; }
        [SysParam] public double Scan3DStroke { get; set; }
        [SysParam] public double VmToBoardOffsetX { get; set; }
        [SysParam] public double VmToBoardOffsetY { get; set; }
        [SysParam] public double EffAreaLeftShrink { get; set; }
        [SysParam] public double EffAreaRightShrink { get; set; }
        [SysParam] public double CameraExposureTime { get; set; }
        [SysParam] public double SetVmSavePathDelay { get; set; }
        [SysParam] public double PositionSnapshotDelay { get; set; }
        [SysParam] public double MaterialDischargeBlockDelay { get; set; }
        [SysParam] public double MaterialDischargeTimeout { get; set; }
        [SysParam] public int CameraResolutionW { get; set; }
        [SysParam] public int CameraResolutionH { get; set; }
        private double _cameraPixelEquivalent;
        [SysParam]
        public double CameraPixelEquivalent
        {
            get => _cameraPixelEquivalent;
            set
            {
                if (SetProperty(ref _cameraPixelEquivalent, value))
                    RaisePropertyChanged(nameof(CameraPixelDensity));
            }
        }
        /// <summary>像素密度(px/mm)，界面输入用，内部转换为 CameraPixelEquivalent (mm/px)</summary>
        public double CameraPixelDensity
        {
            get => CameraPixelEquivalent > 0 ? Math.Round(1.0 / CameraPixelEquivalent, 4) : 0;
            set
            {
                if (value > 0)
                    CameraPixelEquivalent = Math.Round(1.0 / value, 12);
                RaisePropertyChanged();
            }
        }
        [SysParam] public string DetectionMode { get; set; }
        [SysParam] public double EmptyPinThreshold { get; set; }
        [SysParam] public double MatchThreshold3D { get; set; }
        [SysParam] public double MaxOverlap { get; set; }
        [SysParam] public double StitchOffsetX { get; set; }
        [SysParam] public double StitchOffsetY { get; set; }
        [SysParam] public bool EnableScanner { get; set; }
        [SysParam] public bool EnableMark { get; set; }
        [SysParam] public bool EnableAlarm { get; set; }
        [SysParam] public bool EnableSafetyDoorAlarm { get; set; }
        [SysParam] public bool EnableLightCurtainAlarm { get; set; }
        [SysParam] public double PulseEquivalentX { get; set; }
        [SysParam] public double PulseEquivalentY { get; set; }
        [SysParam] public double PulseEquivalentZ { get; set; }
        [SysParam] public double PulseEquivalentH { get; set; }
        [SysParam] public double StandbyX { get; set; }
        [SysParam] public double StandbyY { get; set; }
        [SysParam] public double StandbyZ { get; set; }
        [SysParam] public double IoPollInterval { get; set; }
        [SysParam] public int PrecisionRepeatCount { get; set; }
        [SysParam] public double MoveToCenterTimeout { get; set; }
        [SysParam] public double MoveToScanStartTimeout { get; set; }
        [SysParam] public double AdjustRailWidthTimeout { get; set; }
        [SysParam] public double GrabTimeout { get; set; }
        [SysParam] public double Wait3DResultTimeout { get; set; }
        [SysParam] public double Scan3DTimeout { get; set; }
        [SysParam] public double Scan3DRetryTimeout { get; set; }
        [SysParam] public double Camera3DTriggerTimeout { get; set; }
        [SysParam] public double MoveZTimeout { get; set; }
        [SysParam] public double AxisHomeTimeout { get; set; }
        [SysParam] public double WaitForVmSaveTimeout { get; set; }
        [SysParam] public double ConveyorToEntryTimeout { get; set; }
        [SysParam] public double ConveyorBoardStableMs { get; set; }
        [SysParam] public double ConveyorWaitBoardTimeout { get; set; }
        [SysParam] public double ConveyorToExitTimeout { get; set; }
        [SysParam] public double ContinuousOutletClearTimeout { get; set; }
        [SysParam] public double ContinuousEntryWaitTimeout { get; set; }

        // ========== 界面结果显示设置（已移至 PersonalizationViewModel） ==========

        // ==================== 连接状态更新方法 ====================
        public void UpdateSerialTextAndButton(bool isConnected)
        {
            SerialStatusText = isConnected ? "已连接" : "未连接";
            SerialTextColor = isConnected ? Brushes.Green : Brushes.Red;
            SerialButtonText = isConnected ? "断开连接" : "连接";
            SerialButtonColor = isConnected
                ? (Brush)new BrushConverter().ConvertFrom("#FFE74C3C")
                : (Brush)new BrushConverter().ConvertFrom("#FF2D6A4F");
            IsSerialControlsEnabled = !isConnected;
        }

        public void UpdateVisionTextAndButton(bool isConnected)
        {
            VisionStatusText = isConnected ? "已连接" : "未连接";
            VisionTextColor = isConnected ? Brushes.Green : Brushes.Red;
            VisionButtonText = isConnected ? "断开连接" : "连接";
            VisionButtonColor = isConnected
                ? (Brush)new BrushConverter().ConvertFrom("#FFE74C3C")
                : (Brush)new BrushConverter().ConvertFrom("#FF2D6A4F");
            IsVisionControlsEnabled = !isConnected;
        }

        public void UpdateVision3DTextAndButton(bool isConnected)
        {
            Vision3DStatusText = isConnected ? "已连接" : "未连接";
            Vision3DTextColor = isConnected ? Brushes.Green : Brushes.Red;
            Vision3DButtonText = isConnected ? "断开连接" : "连接";
            Vision3DButtonColor = isConnected
                ? (Brush)new BrushConverter().ConvertFrom("#FFE74C3C")
                : (Brush)new BrushConverter().ConvertFrom("#FF2D6A4F");
            IsVision3DControlsEnabled = !isConnected;
        }

        public void UpdateLightTextAndButton(bool isConnected)
        {
            LightStatusText = isConnected ? "已连接" : "未连接";
            LightTextColor = isConnected ? Brushes.Green : Brushes.Red;
            LightButtonText = isConnected ? "断开连接" : "连接";
            LightButtonColor = isConnected
                ? (Brush)new BrushConverter().ConvertFrom("#FFE74C3C")
                : (Brush)new BrushConverter().ConvertFrom("#FF2D6A4F");
            IsLightControlsEnabled = !isConnected;
        }

        // ==================== 端口列表加载 ====================
        private void LoadSerialPorts()
        {
            var ports = SerialPort.GetPortNames();
            SerialPorts = new ObservableCollection<string>(ports);
            if (SerialPorts.Count == 0)
                SerialPorts.Add("无串口");
        }

        private void LoadBaudRates()
        {
            BaudRates = new ObservableCollection<int> { 9600, 115200, 460800, 921600 };
        }

        // ==================== 自动同步逻辑 ====================
        /// <summary>
        /// 从 SysParam.Data 中加载所有标记了 [SysParam] 的属性
        /// </summary>
        public void LoadData()
        {
            var data = _sysParam.Data;
            if (data == null) return;

            foreach (var prop in GetType().GetProperties())
            {
                if (prop.GetCustomAttribute<SysParamAttribute>() == null) continue;
                if (!prop.CanWrite) continue;

                var srcProp = typeof(SysParamData).GetProperty(prop.Name);
                if (srcProp != null && srcProp.CanRead)
                {
                    var value = srcProp.GetValue(data);
                    prop.SetValue(this, value);
                }
            }

        }

        /// <summary>
        /// 将所有标记了 [SysParam] 的属性写回 SysParam.Data，并保存到文件
        /// </summary>
        public void Save()
        {
            var data = _sysParam.Data;
            if (data == null) return;

            foreach (var prop in GetType().GetProperties())
            {
                if (prop.GetCustomAttribute<SysParamAttribute>() == null) continue;
                if (!prop.CanRead) continue;

                var dstProp = typeof(SysParamData).GetProperty(prop.Name);
                if (dstProp != null && dstProp.CanWrite)
                {
                    var value = prop.GetValue(this);
                    dstProp.SetValue(data, value);
                }
            }

            _sysParam.Save();  // 一次性序列化整个数据对象
            SaveOriginalValues();
        }

        // ==================== 变更检测 ====================
        private void SaveOriginalValues()
        {
            _originalValues = new Dictionary<string, object>();
            foreach (var prop in GetType().GetProperties())
            {
                if (prop.GetCustomAttribute<SysParamAttribute>() != null && prop.CanRead)
                    _originalValues[prop.Name] = prop.GetValue(this);
            }
        }

        public bool HasChanges()
        {
            foreach (var prop in GetType().GetProperties())
            {
                if (prop.GetCustomAttribute<SysParamAttribute>() == null) continue;
                if (!prop.CanRead) continue;

                var current = prop.GetValue(this);
                _originalValues.TryGetValue(prop.Name, out object original);
                if (!Equals(current, original)) return true;
            }
            return false;
        }

        public void Revert()
        {
            foreach (var prop in GetType().GetProperties())
            {
                if (prop.GetCustomAttribute<SysParamAttribute>() == null) continue;
                if (!prop.CanWrite) continue;

                if (_originalValues.TryGetValue(prop.Name, out object original))
                    prop.SetValue(this, original);
            }
        }

        public void Dispose()
        {
            _originalValues?.Clear();
            _originalValues = null;
        }
    }

    // ==================== 特性标记 ====================
    [AttributeUsage(AttributeTargets.Property)]
    public class SysParamAttribute : Attribute { }
}