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
        public enum WorkFlowType
        {
            ServoHome,
            AdjustBoardHeight,
            TransportBoard,
            SendToExit,
            SendToEntrance,
            FullImageGrab,
            FovGrab,
            Check,
            BatchCheck,
            ContinuousRun,
            WorkFlowTypeCount,
        }

        /// <summary>重置所有工作流状态标志</summary>
        public void ResetWorkFlowStates()
        {
            for (int i = 0; i < (int)WorkFlowType.WorkFlowTypeCount; i++)
                isWorkFlowRun[(WorkFlowType)i] = false;
        }

        /// <summary>bitset 包装，支持直接用 WorkFlowType 索引</summary>
        private struct WorkFlowBitset
        {
            private readonly bool[] _bits;
            public WorkFlowBitset(int count) => _bits = new bool[count];
            public bool this[WorkFlowType t]
            {
                get => _bits[(int)t];
                set => _bits[(int)t] = value;
            }
            public bool Any() => _bits.Any(b => b);
        }

        private WorkFlowBitset isWorkFlowRun = new WorkFlowBitset((int)WorkFlowType.WorkFlowTypeCount);

        // =================================== 事件 ===================================
        public event Action OnThemeChanged;
        public event Action EditModeChanged;
        public event Action<string> StatusChanged;
        public event Action<string, bool> ActionBusyStateChanged;

        /// <summary>工作模式切换事件(单板/连续模式按钮高亮刷新)</summary>
        public event Action ModeChanged;

        // ==================== 标记工具栏事件 ====================
        public event Action MarkerToolbarStateChanged;

        private void UpdateMarkerToolbarState()
        {
            MarkerToolbarStateChanged?.Invoke();
        }

        // ==================== 标记模式常量、属性、颜色表 ====================
        private const int MARKER_MODE_NONE = -1;
        private const int MARKER_MODE_DELETE = -2;

        private int _activeMarkerIndex = MARKER_MODE_NONE;

        /// <summary>当前激活的标记模式索引：-1=无模式, 0=A, 1=B, ..., -2=删除模式</summary>
        public int ActiveMarkerIndex => _activeMarkerIndex;

        /// <summary>当前方案的特征个数</summary>
        public int PluginFeatureCount =>
            ProjectManager.Instance.CurrentProject?.PluginFeatureCount ?? 1;

        /// <summary>标记字母颜色表（10色，对应 A-J）</summary>
        private static readonly string[] MarkerColors = new[]
        {
            "#2196F3", // A - 蓝
            "#4CAF50", // B - 绿
            "#F44336", // C - 红
            "#FF9800", // D - 橙
            "#9C27B0", // E - 紫
            "#00BCD4", // F - 青
            "#795548", // G - 棕
            "#E91E63", // H - 粉
            "#009688", // I - 青绿
            "#607D8B", // J - 灰蓝
        };

        // =================================== 字段 ===================================

        #region 依赖服务
        private MotionIO _motionIO;
        private Vision _vision;
        private SysParam _sysParam;
        private Vision2DClient _vision2DClient;
        private Vision3DClient _vision3DClient;
        private FullImageManager _imageManager;
        #endregion

        #region ==================== 检测结果叠加层数据模型 ====================
        private bool _isOverlayVisible = true;

        // ==================== 叠加层显示过滤 ====================
        private enum OverlayResultFilter { All, OK, NG }
        private enum OverlayDisplayMode { AllInfo, NumberOnly }
        private OverlayResultFilter _overlayFilter = OverlayResultFilter.All;
        private OverlayDisplayMode _overlayDisplayMode = OverlayDisplayMode.AllInfo;

        private int _selectedTabIndex = 0;
        public int SelectedTabIndex { get => _selectedTabIndex; set => SetProperty(ref _selectedTabIndex, value); }

        /// <summary>VM 检测结果叠加层数据（检测位完成后用于 Halcon 绘制）</summary>
        private class VmOverlayItem
        {
            public int GridIndex { get; set; }
            public int PinIndex { get; set; }
            /// <summary>针型标记（A/B/C...），与 PinResult 匹配时必须联合 PinIndex 使用（不同针型的 PinIndex 各自独立编号，会重复）；旧存档为 null 时按焊盘坐标兜底匹配</summary>
            public string PinType { get; set; }
            public double PinMmX { get; set; }
            public double PinMmY { get; set; }
            public double PadMmX { get; set; }
            public double PadMmY { get; set; }
            public double IdealPinMmX { get; set; }
            public double IdealPinMmY { get; set; }
            public double Dx { get; set; }
            public double Dy { get; set; }
            public double H { get; set; }          // 3D高度(mm)，检测后从PinResult更新
            public bool IsOK { get; set; }
            public bool IsEmptyPin { get; set; }
            /// <summary>是否为歪针（无针尖坐标但有3D高度）</summary>
            public bool IsCrookedPin { get; set; }
            public int PinId { get; set; }         // 全局唯一PINID，由merge时填入
            // ==================== VM匹配框参数（像素→mm转换后） ====================
            public double PadW { get; set; }       // 焊盘框宽度(mm)
            public double PadH { get; set; }       // 焊盘框高度(mm)
            public double PadAng { get; set; }     // 焊盘框角度(度)
            public double PinW { get; set; }       // 针框宽度(mm)
            public double PinH { get; set; }       // 针框高度(mm)
            public double PinAng { get; set; }     // 针框角度(度)
        }

        /// <summary>VM 3D检测解析结果</summary>
        private class VM3DPoint
        {
            public double VmX { get; set; }
            public double VmY { get; set; }
            public double VmZ { get; set; }
        }

        /// <summary>VM 3D匹配候选（转换到板面坐标后）</summary>

        private class VM3DCandidate
        {
            public int Index { get; set; }
            public double BoardX { get; set; }
            public double BoardY { get; set; }
            public double Z { get; set; }
            public double VmX { get; set; }
            public double VmY { get; set; }
        }

        /// <summary>2D检测原始数据（从VM解析后的统一格式）</summary>
        private class Pin2DData
        {
            public int PinIndex { get; set; }
            public double Dx { get; set; }
            public double Dy { get; set; }
            public double X { get; set; }
            public double Y { get; set; }
            public double PadX { get; set; }
            public double PadY { get; set; }
            public bool IsEmptyPin { get; set; }
            /// <summary>是否为歪针（Blob检测中无黑区针尖坐标但非空针）</summary>
            public bool IsCrookedPin { get; set; }
            public double PadW { get; set; }
            public double PadH { get; set; }
            public double PadAng { get; set; }
            public double PinW { get; set; }
            public double PinH { get; set; }
            public double PinAng { get; set; }
            public int PinId { get; set; }         // 全局唯一PINID，merge时由globalPinId填入
            public string PinType { get; set; } = "A";  // 针型标记 A/B/C...
        }

        /// <summary>VM 检测结果叠加层数据（检测位完成后用于 Halcon 绘制）</summary>
        private List<VmOverlayItem> _vmOverlays = new List<VmOverlayItem>();
        #endregion

        #region 状态标志
        private bool _isSerialPortConnected;
        private bool _isVision2DConnected = false;
        private bool _isVision3DConnected = false;
        private bool _isEditMode = false;
        private CancellationTokenSource _grabCts;
        private CancellationTokenSource _checkCts;
        /// <summary>检测暂停控制（true=运行中, false=已暂停）</summary>
        private ManualResetEventSlim _pauseCheckEvent = new ManualResetEventSlim(true);
        /// <summary>全图采集暂停控制（true=运行中, false=已暂停）——与 _pauseCheckEvent 对称；复位/终止时 Set 唤醒</summary>
        private ManualResetEventSlim _pauseGrabEvent = new ManualResetEventSlim(true);
        /// <summary>报警终止中：DeviceMonitor 报警激活时置位（轮询线程写），各流程检查点读取（volatile）；报警消失后清除</summary>
        private volatile bool _alarmStopping;
        /// <summary>检测完成信号（在 finally 块中设置，用于可靠等待检测结束）</summary>
        private ManualResetEventSlim _checkFlowCompleted = new ManualResetEventSlim(false);
        /// <summary>检测耗时累计器（2026-08-27：检测流程 Init 时 Restart；暂停 Stop/恢复 Start；结束/中断由 StopInspectTimer 统一收尾）</summary>
        private readonly System.Diagnostics.Stopwatch _inspectSw = new System.Diagnostics.Stopwatch();
        /// <summary>检测耗时刷新定时器（1s 把 Stopwatch 累计值刷到 InspectElapsed；暂停时 Stop 停刷新，显示保留）</summary>
        private System.Windows.Threading.DispatcherTimer _inspectTimer;
        /// <summary>暂停/继续按钮文本</summary>
        public string PauseCheckText => _pauseCheckEvent.IsSet ? " 暂停检测" : " 继续检测";
        /// <summary>暂停状态变更事件（用于UI更新按钮文字）</summary>
        public event Action PauseStateChanged;

        #region 演示模式
        private bool _isDemoMode;
        private CancellationTokenSource _demoCts;
        /// <summary>演示模式按钮文本（选中高亮，不切换文字——停止由停止/复位按钮承担）</summary>
        public string DemoModeText => "演示模式";
        /// <summary>演示模式是否选中（设备状态机 Demo 状态 + 按钮高亮用）</summary>
        public bool IsDemoMode => _isDemoMode;
        /// <summary>演示模式状态变更事件</summary>
        public event Action DemoModeStateChanged;
        #endregion

        #region 精度检测
        private bool _isPrecisionMode;
        private CancellationTokenSource _precisionCts;
        private List<List<Models.PinResult>> _precisionRuns;
        #endregion

        /// <summary>启动回零检查已执行过（防重入）</summary>
        private bool _startupHomeCheckDone;
        /// <summary>H轴零位到固定轨道的距离 (H + BH = MAX)，从系统参数读取</summary>
        private double HAxisMaxPosition => _sysParam.Data.HAxisMaxPosition;

        /// <summary>针尖图片映射："{GrabIndex}_{Marker}" → 针尖图片路径（支持多针型）</summary>
        private Dictionary<string, string> _pinImageMap = new Dictionary<string, string>();
        /// <summary>当前检测位的多针型针尖图片路径（用于构建 DETECT2D_BLOB 命令）</summary>
        private Dictionary<char, string> _currentPositionPinPaths = new Dictionary<char, string>();
        /// <summary>当前针尖图层的原始板面路径备份</summary>
        private Dictionary<int, string> _boardImageBackup = new Dictionary<int, string>();
        /// <summary>是否正在显示针尖图层</summary>
        private bool _isShowingPinLayer = false;
        #endregion

        #region 界面绑定
        private string _statusText = "就绪";
        private string _editButtonText = "编辑";
        private string _lastOpenDirectory;
        private AxisIOControlWindow _axisIOWindow;
        #endregion

        #region 方案属性
        private string _currentProjectName = "";
        public string CurrentProjectName { get => _currentProjectName; set => SetProperty(ref _currentProjectName, value); }

        private string _currentProjectPath;
        public string CurrentProjectPath { get => _currentProjectPath; set => SetProperty(ref _currentProjectPath, value); }

        private string _remark = "";
        public string Remark { get => _remark; set => SetProperty(ref _remark, value); }

        private double _boardWidth;
        public double BoardWidth { get => _boardWidth; set => SetProperty(ref _boardWidth, value); }

        private double _boardHeight;
        public double BoardHeight { get => _boardHeight; set => SetProperty(ref _boardHeight, value); }

        private double _fovWidth;
        public double FovWidth { get => _fovWidth; set => SetProperty(ref _fovWidth, value); }

        private double _fovHeight;
        public double FovHeight { get => _fovHeight; set => SetProperty(ref _fovHeight, value); }

        private double _overlap;
        public double Overlap { get => _overlap; set { double clamped = Math.Max(0, Math.Min(100, value)); SetProperty(ref _overlap, clamped); } }

        private int _subBoardCount;
        public int SubBoardCount { get => _subBoardCount; set => SetProperty(ref _subBoardCount, value); }

        private string _currentSide = "正面";
        public string CurrentSide { get => _currentSide; set => SetProperty(ref _currentSide, value); }

        private string _currentTrack = "轨道1";
        public string CurrentTrack { get => _currentTrack; set => SetProperty(ref _currentTrack, value); }

        private string _mainBarcode = "0";
        public string MainBarcode { get => _mainBarcode; set => SetProperty(ref _mainBarcode, value); }

        private string _subBarcode = "0";
        public string SubBarcode { get => _subBarcode; set => SetProperty(ref _subBarcode, value); }

        private string _currentBatch = "0";
        public string CurrentBatch { get => _currentBatch; set => SetProperty(ref _currentBatch, value); }

        private int _batchCount;
        public int BatchCount { get => _batchCount; set => SetProperty(ref _batchCount, value); }

        // ==================== 检测参数（方案级，初始化从系统参数继承；现已移至 PinTypeParams 每针型独立） ====================
        // 以下全局检测参数属性已删除，改用 project.PinTypeParams[pTypeIdx].XyTolerance / IdealHeight / HeightTolerance 等
        // 保留 Is*Modified 标记（用于UI显示与系统参数对比）
        private bool _isXDevModified;
        public bool IsXDevModified { get => _isXDevModified; set => SetProperty(ref _isXDevModified, value); }
        private bool _isYDevModified;
        public bool IsYDevModified { get => _isYDevModified; set => SetProperty(ref _isYDevModified, value); }
        private bool _isHeightModified;
        public bool IsHeightModified { get => _isHeightModified; set => SetProperty(ref _isHeightModified, value); }
        private bool _isHeightDevModified;
        public bool IsHeightDevModified { get => _isHeightDevModified; set => SetProperty(ref _isHeightDevModified, value); }
        private bool _isRelOffsetXModified;
        public bool IsRelOffsetXModified { get => _isRelOffsetXModified; set => SetProperty(ref _isRelOffsetXModified, value); }
        private bool _isRelOffsetYModified;
        public bool IsRelOffsetYModified { get => _isRelOffsetYModified; set => SetProperty(ref _isRelOffsetYModified, value); }
        #endregion

        #region 统计信息
        private double _passRate;
        public double PassRate { get => _passRate; set => SetProperty(ref _passRate, value); }

        private double _inspectTime;
        public double InspectTime { get => _inspectTime; set => SetProperty(ref _inspectTime, value); }

        private double _boardTime;
        public double BoardTime { get => _boardTime; set => SetProperty(ref _boardTime, value); }

        private double _falseRate;
        public double FalseRate { get => _falseRate; set => SetProperty(ref _falseRate, value); }

        private double _defectRate;
        public double DefectRate { get => _defectRate; set => SetProperty(ref _defectRate, value); }

        private double _batchPassRate;
        public double BatchPassRate { get => _batchPassRate; set => SetProperty(ref _batchPassRate, value); }

        private int _goodCount;
        public int GoodCount { get => _goodCount; set => SetProperty(ref _goodCount, value); }

        private int _ngCount;
        public int NgCount { get => _ngCount; set => SetProperty(ref _ngCount, value); }

        /// <summary>检测总数（全局去重后的针数，2026-08-27 Bug修复2：历史结果保留，显示在检测结果列表标题旁）</summary>
        private int _totalPinCount;
        public int TotalPinCount { get => _totalPinCount; set => SetProperty(ref _totalPinCount, value); }

        /// <summary>检测耗时（2026-08-27：开始检测起计时，运行中每1s刷新；暂停停走；正常结束保留本次耗时；中断/异常重置为0）</summary>
        private TimeSpan _inspectElapsed = TimeSpan.Zero;
        public TimeSpan InspectElapsed { get => _inspectElapsed; set => SetProperty(ref _inspectElapsed, value); }
        #endregion

        #region 派生属性
        public double YieldRate => (GoodCount + NgCount) > 0
            ? (double)GoodCount / (GoodCount + NgCount) * 100 : 0;
        public bool IsEditMode { get => _isEditMode; set { if (SetProperty(ref _isEditMode, value)) { EditButtonText = value ? "退出编辑" : "编辑"; RaisePropertyChanged(nameof(IsEditMode)); } } }
        public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }

        /// <summary>工作模式：true=连续模式(入板→检测→出板循环)，false=单板模式(默认，检一块结束)</summary>
        private bool _isContinuousMode;
        public bool IsContinuousMode
        {
            get => _isContinuousMode;
            private set
            {
                if (_isContinuousMode != value)
                {
                    _isContinuousMode = value;
                    ModeChanged?.Invoke();
                }
            }
        }
        public bool IsVision2DConnected { get => _isVision2DConnected; set => SetProperty(ref _isVision2DConnected, value); }
        public bool IsVision3DConnected { get => _isVision3DConnected; set => SetProperty(ref _isVision3DConnected, value); }
        public string EditButtonText { get => _editButtonText; set => SetProperty(ref _editButtonText, value); }

        private string _vmSolutionStatusText = "VM: 就绪";
        public string VmSolutionStatusText { get => _vmSolutionStatusText; set => SetProperty(ref _vmSolutionStatusText, value); }
        #endregion

        /// <summary>当前检测结果文件夹名称（如 Result_20260623_143000），为空时使用 Result/</summary>
        private string _currentResultFolder = "";
        /// <summary>获取当前结果文件夹名称（供历史结果窗口调用）</summary>
        public string GetCurrentResultFolder() => _currentResultFolder;

        /// <summary>获取方案路径（供历史结果窗口调用）</summary>
        public string GetProjectPath() => ProjectManager.Instance.CurrentProjectPath;

        /// <summary>是否有拼接图像（用于菜单切换等场景判断是否需要刷新底图）</summary>
        public bool HasStitchedImage => _imageManager != null && _imageManager.ImageCount > 0;

        #region 列表数据
        private ObservableCollection<GrabPosition> _grabPositions = new ObservableCollection<GrabPosition>();
        public ObservableCollection<GrabPosition> GrabPositions { get => _grabPositions; set => SetProperty(ref _grabPositions, value); }

        private ObservableCollection<DetectPosition> _detectPositions = new ObservableCollection<DetectPosition>();
        public ObservableCollection<DetectPosition> DetectPositions
        {
            get => _detectPositions;
            set => SetProperty(ref _detectPositions, value);
        }

        private ObservableCollection<PinResult> _pinResults = new ObservableCollection<PinResult>();
        public ObservableCollection<PinResult> PinResults
        {
            get => _pinResults;
            set => SetProperty(ref _pinResults, value);
        }
        #endregion

        public MainViewModel()

        {
            _motionIO = MotionIO.Instance;
            DeviceMonitor.EnsureInit(); // 启动设备监控：订阅 IOStateUpdated（喂入全部 IO 去抖 + 报警检查 + 设备状态机，频率=IO 轮询间隔）
            DeviceMonitor.Instance.RegisterFlowStateProviders(
                flowRunning: () => isWorkFlowRun.Any(),
                flowPaused: () => !_pauseCheckEvent.IsSet || !_pauseGrabEvent.IsSet || _continuousPaused,
                flowDemo: () => _isDemoMode);
            DeviceMonitor.Instance.AlarmTriggered += OnAlarmTriggered;
            DeviceMonitor.Instance.AlarmCleared += OnAlarmCleared;
            _vision = new Vision();
            _vision2DClient = Vision2DClient.Instance;
            _vision3DClient = Vision3DClient.Instance;
            _sysParam = SysParam.Instance;
            _imageManager = new FullImageManager();

            _motionIO.ConnectionStateChanged += OnConnectionStateChanged;
            _vision2DClient.OnResultReceived += OnVisionResultReceived;
            _vision2DClient.OnConnectionChanged += OnVisionConnectionChanged;
            _vision2DClient.OnRuntimeError += OnVisionRuntimeError;
            // VM方案改为方案加载时按需加载，不再启动时全局加载

            // 下位机/光源同步尝试连接（不弹窗）；2D/3D 异步连接与全部设备聚合检查
            // 在窗口 Loaded 后的 CheckDeviceConnectionsAsync 中统一进行
            ConnectMotion();
            ConnectLightSource();
            AutoLoadLastProject();

            // 检测耗时刷新定时器：1s 把 Stopwatch 累计值刷到 UI（暂停/恢复/收尾由检测流程状态机控制，2026-08-27）
            // 无参构造绑定当前线程(UI) Dispatcher；MainViewModel 在 MainWindow 构造函数中创建，运行于 UI 线程
            _inspectTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _inspectTimer.Tick += (s, e) => InspectElapsed = _inspectSw.Elapsed;
        }

        #region ===================================串口网口通信===================================

        private Task _visionConnectTask = Task.CompletedTask;

        /// <summary>
        /// 连接 2D/3D 视觉（TCP），失败不弹窗，由 CheckDeviceConnectionsAsync 统一提示。
        /// 返回/保存为共享 Task：MainWindow 调用与启动检查 await 同一连接，避免重复 ConnectAsync。
        /// </summary>
        public Task ConnectVision()
        {
            if (_visionConnectTask.IsCompleted && _vision2DClient.IsConnected && _vision3DClient.IsConnected)
                return _visionConnectTask;
            _visionConnectTask = ConnectVisionCore();
            return _visionConnectTask;
        }

        private async Task ConnectVisionCore()
        {
            if (!_vision2DClient.IsConnected)
                await _vision2DClient.ConnectAsync(_sysParam.VisionIp, _sysParam.VisionPort);
            if (!_vision3DClient.IsConnected)
                await _vision3DClient.ConnectAsync(_sysParam.Vision3DIp, _sysParam.Vision3DPort);
        }

        /// <summary>连接下位机串口，失败不弹窗，由 CheckDeviceConnectionsAsync 统一提示</summary>
        public void ConnectMotion()
        {
            _motionIO.Open(_sysParam.SelectedSerialPort, _sysParam.SelectedBaudRate);
        }

        private void OnVisionResultReceived(string result) { }

        private void OnVisionConnectionChanged(bool connected)
        {
            IsVision2DConnected = connected;
            UpdateStatus(connected ? "视觉软件已连接" : "视觉软件已断开");
        }

        private void OnVisionRuntimeError(string error)
        {
            UpdateStatus($"错误: {error}");
        }

        private void OnConnectionStateChanged(bool connected)
        {
            _isSerialPortConnected = connected;
            if (!connected)
            {
                _motionIO.ResetHomedStatus();
                return;
            }
            // 用户后续连接串口时，如果XYZ尚未回零则弹窗询问
            // 只在窗口已加载时弹窗，否则交给 StartPostInitTasks（Window.Loaded 中执行）
            if (!_motionIO.IsXyzHomed && !_startupHomeCheckDone && Application.Current.MainWindow?.IsLoaded == true)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(300); // 等连接稳定
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        _ = StartupHomeCheck();
                    });
                });
            }
        }

        #endregion

        #region ===================================连接检查===================================

        /// <summary>
        /// 检查串口连接，未连接时弹窗提示
        /// </summary>
        private bool CheckMotionConnected()
        {
            if (!_motionIO.IsConnected)
            {
                MessageBox.Show(Application.Current.MainWindow, "请先连接串口", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            return true;
        }

        /// <summary>
        /// 检查 2D 视觉连接，未连接时弹窗提示
        /// </summary>
        private bool CheckVision2DConnected()
        {
            if (!_vision2DClient.IsConnected)
            {
                MessageBox.Show(Application.Current.MainWindow, "请先连接视觉软件", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            return true;
        }

        /// <summary>
        /// 检查 3D 视觉连接，未连接时弹窗提示
        /// </summary>
        private bool CheckVision3DConnected()
        {
            if (!_vision3DClient.IsConnected)
            {
                MessageBox.Show(Application.Current.MainWindow, " 请先连接3D相机", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            return true;
        }

        #endregion

        #region ===================================检测位管理===================================

        private void OnGrabPositionDetectChanged(GrabPosition pos)
        {
            UpdateDetectPositions();

            if (_vision.HalconControl != null && _vision.HalconControl.IsInSelectionMode)
            {
                RefreshSelectionDisplay();
                return;
            }
        }

        public void UpdateDetectPositions()
        {
            var points = GrabPositions
                .Where(p => p.IsDetectPosition)
                .Select((p, seq) => new DetectPosition
                {
                    Index = seq,
                    GrabIndex = p.Index,
                    X = p.X,
                    Y = p.Y,
                    Row = p.Row,
                    Col = p.Col,
                    IsDetectPosition = p.IsDetectPosition,
                    PinTypes = p.PinTypes
                })
                .ToList();

            DetectPositions = new ObservableCollection<DetectPosition>(points);
        }

        private void OnSelectInspectionModeRequested()
        {
            EnterSelectInspectionMode();
        }

        private void OnDetectPositionToggled(int index)
        {
            OnPositionToggled(index);
        }

        private async void OnMoveToDetectPosition(double imgRow, double imgCol)
        {
            try
            {
                var project = ProjectManager.Instance.CurrentProject;
                if (project == null) return;
                var tile = FullImageManager.CalculateTileDimensions(
                    project.FovWidth, project.FovHeight, project.Overlap);
                int cols = _sysParam.ImageDisplayCols;
                int gridCol = (int)(imgCol / tile.cropW);
                int gridRow = (int)(imgRow / tile.cropH);
                int gridIndex = gridRow * cols + gridCol;

                if (GrabPositions == null) return;
                var pos = GrabPositions.FirstOrDefault(p => p.Index == gridIndex);
                if (pos == null)
                {
                    Log.Warning($"[移动] 无拍照位 #{gridIndex}");
                    return;
                }
                Log.Info($"[移动] 移动到拍照位 #{gridIndex} (X={pos.X}, Y={pos.Y})...");
                UpdateStatus($"移动到拍照位 #{gridIndex}...");
                bool ok = await _motionIO.MoveToAndWaitOK(x: pos.X, y: pos.Y, timeoutMs: 8000);
                UpdateStatus(ok ? $"已移动到拍照位 #{gridIndex}" : $"移动失败");
            }
            catch (Exception ex)
            {
                Log.Error($"[移动] 移动到检测位失败: {ex.Message}");
            }
        }

        private async void OnScanPositionAtPosition(double imgRow, double imgCol)
        {
            var project = ProjectManager.Instance.CurrentProject;
            if (project == null) return;
            var tile = FullImageManager.CalculateTileDimensions(project.FovWidth, project.FovHeight, project.Overlap);
            int cols = _sysParam.ImageDisplayCols;
            int gridCol = (int)(imgCol / tile.cropW);
            int gridRow = (int)(imgRow / tile.cropH);
            int gridIndex = gridRow * cols + gridCol;
            if (GrabPositions == null) return;
            var pos = GrabPositions.FirstOrDefault(p => p.Index == gridIndex);
            if (pos == null) { Log.Warning("[扫描] 无拍照位"); return; }
            Log.Info($"[扫描] 扫描拍照位 #{gridIndex}");
            StartScanAtPosition(gridIndex);
        }

        private void OnExitInspectionModeRequested()
        {
            ExitSelectInspectionMode();
        }

        private void OnToggleOverlayRequested()
        {
            _isOverlayVisible = !_isOverlayVisible;
            var hw = _vision.HalconWindow;
            if (hw == null) return;

            // 同步右键菜单状态
            if (_vision.HalconControl != null)
                _vision.HalconControl.IsOverlayVisible = _isOverlayVisible;

            if (_isOverlayVisible)
            {
                // 按需加载叠加层数据
                if (_vmOverlays.Count == 0)
                    LoadVmOverlays();

                var stitched = _imageManager.GetCurrentStitchedImage();
                if (stitched != null && stitched.IsInitialized())
                {
                    HOperatorSet.ClearWindow(hw);
                    HOperatorSet.DispObj(stitched, hw);

                    if (_vmOverlays.Count > 0)
                    {
                        DrawVmRects(hw);
                        DrawVmText(hw);
                    }
                    stitched.Dispose();
                    _vision.RefreshDisplay();
                }
            }
            else
            {
                var stitched = _imageManager.GetCurrentStitchedImage();
                if (stitched != null && stitched.IsInitialized())
                {
                    HOperatorSet.ClearWindow(hw);
                    HOperatorSet.DispObj(stitched, hw);
                    stitched.Dispose();
                    _vision.RefreshDisplay();
                }
            }
        }

        // ==================== 叠加层过滤事件处理 ====================

        private void ApplyOverlayFilter(string filter, string displayMode)
        {
            _overlayFilter = (OverlayResultFilter)Enum.Parse(typeof(OverlayResultFilter), filter);
            _overlayDisplayMode = (OverlayDisplayMode)Enum.Parse(typeof(OverlayDisplayMode), displayMode);
            // 持久化到系统参数（重启后恢复）
            SysParam.Instance.OverlayFilter = filter;
            SysParam.Instance.OverlayDisplayMode = displayMode;
            if (_vision.HalconControl != null)
            {
                _vision.HalconControl.CurrentOverlayFilter = filter;
                _vision.HalconControl.CurrentOverlayDisplayMode = displayMode;
            }
            RefreshOverlayDisplay();
        }

        /// <summary>用当前过滤条件刷新叠加层显示</summary>
        private void RefreshOverlayDisplay()
        {
            var hw = _vision.HalconWindow;
            if (hw == null) return;

            var stitched = _imageManager.GetCurrentStitchedImage();
            bool hasImage = stitched != null && stitched.IsInitialized();

            if (!hasImage && !_isOverlayVisible && _vmOverlays.Count == 0)
                return;

            HOperatorSet.SetWindowParam(hw, "flush", "false");
            HOperatorSet.ClearWindow(hw);

            if (hasImage)
            {
                HOperatorSet.DispObj(stitched, hw);
                stitched.Dispose();
            }

            // 绘制检测位选区红框
            var halconControl = _vision.HalconControl;
            if (halconControl != null && halconControl.IsInSelectionMode)
                DrawSelectionRectangles(hw);
            else if (GrabPositions != null && GrabPositions.Any(p => p.IsDetectPosition))
                DrawSelectionRectangles(hw);

            if (_isOverlayVisible)
            {
                if (_vmOverlays.Count > 0) { DrawVmRects(hw); DrawVmText(hw); }
            }
            HOperatorSet.SetWindowParam(hw, "flush", "true");
            _vision.RefreshDisplay();
        }

        private void OnOverlayFilterAll() => ApplyOverlayFilter("All", _overlayDisplayMode.ToString());
        private void OnOverlayFilterOK() => ApplyOverlayFilter("OK", _overlayDisplayMode.ToString());
        private void OnOverlayFilterNG() => ApplyOverlayFilter("NG", _overlayDisplayMode.ToString());
        private void OnOverlayModeAllInfo() => ApplyOverlayFilter(_overlayFilter.ToString(), "AllInfo");
        private void OnOverlayModeNumberOnly() => ApplyOverlayFilter(_overlayFilter.ToString(), "NumberOnly");

        #region 查看模式（工具栏按钮）

        /// <summary>查看模式：显示所有检测位的板面图</summary>
        public void ShowBoardView()
        {
            if (_imageManager.ImageCount == 0) return;
            var project = ProjectManager.Instance.CurrentProject;
            if (project == null) return;

            // 遍历所有备份，恢复为板面图
            foreach (var kvp in _boardImageBackup)
            {
                int grabIdx = kvp.Key;
                string boardPath = kvp.Value;
                if (!File.Exists(boardPath)) continue;
                _imageManager.ReplaceTile(grabIdx, boardPath,
                    stitched => _vision.DisplayImageByImage(stitched),
                    _sysParam.ImageDisplayRows, _sysParam.ImageDisplayCols,
                    project.FovWidth, project.FovHeight, project.Overlap);
            }
            _boardImageBackup.Clear();
            _isShowingPinLayer = false;
            UpdateStatus("已切换至板面图层");
        }

        /// <summary>查看模式：显示指定针型的针尖图</summary>
        public void ShowPinTypeView(int pinTypeIndex)
        {
            if (_imageManager.ImageCount == 0) return;
            char marker = (char)('A' + pinTypeIndex);
            var project = ProjectManager.Instance.CurrentProject;
            if (project == null || GrabPositions == null) return;

            // 先恢复板面图（清除已有的针尖替换）
            ShowBoardView();

            // 遍历所有检测位，替换有 marker 标记的瓦片
            foreach (var grabPos in GrabPositions)
            {
                if (!grabPos.IsDetectPosition || string.IsNullOrEmpty(grabPos.PinTypes)) continue;
                if (!grabPos.PinTypes.Contains(marker)) continue;  // 没有该标记，跳过

                // 检查是否有该针型的图
                string pinImagePath = GetPinImagePath(grabPos.Index, marker);
                if (pinImagePath == null || !File.Exists(pinImagePath)) continue;

                // 备份当前板面图（仅首次）
                var existPaths = _imageManager.GetImagePaths();
                var existIndices = _imageManager.GetGridIndices();
                for (int i = 0; i < existIndices.Count; i++)
                {
                    if (existIndices[i] == grabPos.Index)
                    {
                        if (!_boardImageBackup.ContainsKey(grabPos.Index))
                            _boardImageBackup[grabPos.Index] = existPaths[i];
                        break;
                    }
                }

                // 替换瓦片为针尖图
                _imageManager.ReplaceTile(grabPos.Index, pinImagePath,
                    stitched => _vision.DisplayImageByImage(stitched),
                    _sysParam.ImageDisplayRows, _sysParam.ImageDisplayCols,
                    project.FovWidth, project.FovHeight, project.Overlap);
            }
            _isShowingPinLayer = true;
            UpdateStatus($"已切换至 {marker} 针尖图");
        }

        /// <summary>获取指定检测位指定针型的图片路径</summary>
        private string GetPinImagePath(int grabIndex, char marker)
        {
            string key = $"{grabIndex}_{marker}";
            if (_pinImageMap.TryGetValue(key, out string path))
                return path;
            return null;
        }

        #endregion

        private void EnterSelectInspectionMode()
        {
            // 默认激活标记模式 A
            if (_activeMarkerIndex == MARKER_MODE_NONE)
                _activeMarkerIndex = 0;

            if (!EnsureSelectionModeReady()) return;

            UpdateMarkerToolbarState();
            RefreshSelectionDisplay();
            _vision.RefreshDisplay();
        }

        /// <summary>
        /// 确保选择模式所需参数已初始化（GridCols/GridRows/FovWidth等）
        /// 返回 false 表示无法进入选择模式
        /// </summary>
        private bool EnsureSelectionModeReady()
        {
            var project = ProjectManager.Instance.CurrentProject;
            if (project == null) return false;

            var (isValid, expected, actual, _) = ValidateImageCount(project);
            if (!isValid)
            {
                _vision.ClearHalconWindow();
                MessageBox.Show(Application.Current.MainWindow, $"图片数量不一致！预期 {expected} 张，实际仅 {actual} 张。请重新采集全图。", "图片数量不一致", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            if (GrabPositions == null || GrabPositions.Count == 0)
            {
                MessageBox.Show(Application.Current.MainWindow, "当前方案没有拍照位，请先采集全图。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            var halconControl = _vision.HalconControl;
            if (halconControl == null) return false;

            if (halconControl.IsInSelectionMode)
                return true; // 已经初始化好了

            var tile = FullImageManager.CalculateTileDimensions(project.FovWidth, project.FovHeight, project.Overlap);
            double fovWidthPx = tile.cropW;
            double fovHeightPx = tile.cropH;

            halconControl.GridCols = _sysParam.ImageDisplayCols;
            halconControl.GridRows = _sysParam.ImageDisplayRows;
            halconControl.FovWidthPixels = fovWidthPx;
            halconControl.FovHeightPixels = fovHeightPx;
            halconControl.IsInSelectionMode = true;
            halconControl.PluginFeatureCount = PluginFeatureCount;
            return true;
        }

        private void OnPositionToggled(int gridIndex)
        {
            if (GrabPositions == null) return;
            var pos = GrabPositions.FirstOrDefault(p => p.Index == gridIndex);
            if (pos == null) return;

            if (_activeMarkerIndex == MARKER_MODE_DELETE)
            {
                // 删除模式：清除该位的所有标记
                pos.PinTypes = "";
                pos.IsDetectPosition = false;
                RefreshSelectionDisplay();
                return;
            }

            if (_activeMarkerIndex >= 0 && _activeMarkerIndex < 10)
            {
                // 标记模式：切换当前标记字母
                char marker = (char)('A' + _activeMarkerIndex);
                if (pos.PinTypes.Contains(marker))
                    pos.PinTypes = pos.PinTypes.Replace(marker.ToString(), "");
                else
                    pos.PinTypes += marker;

                pos.IsDetectPosition = !string.IsNullOrEmpty(pos.PinTypes);
                // 强制同步 DetectPositions（IsDetectPosition 未变化时不触发事件）
                UpdateDetectPositions();
            }
            else
            {
                // 无模式（_activeMarkerIndex == MARKER_MODE_NONE）
                // 保持原有行为：简单切换 IsDetectPosition（但不清除 PinTypes）
                pos.IsDetectPosition = !pos.IsDetectPosition;
                if (!pos.IsDetectPosition)
                    pos.PinTypes = ""; // 取消检测位时清空标记
                else if (string.IsNullOrEmpty(pos.PinTypes))
                    pos.PinTypes = "A"; // 默认标记A
            }

            RefreshSelectionDisplay();
        }

        // ==================== 标记模式方法 ====================

        /// <summary>设置当前标记模式</summary>
        public void SetMarkerMode(int markerIndex) // 0=A, 1=B, ...
        {
            _activeMarkerIndex = (_activeMarkerIndex == markerIndex) ? MARKER_MODE_NONE : markerIndex;
            // 如果切换到标记模式，确保进入选择模式（初始化Grid参数、刷新显示）
            if (_activeMarkerIndex >= 0)
                EnsureSelectionModeReady();
            RefreshSelectionDisplay();
            UpdateMarkerToolbarState(); // 通知UI刷新按钮状态
        }

        /// <summary>设置删除模式</summary>
        public void SetDeleteMode()
        {
            _activeMarkerIndex = (_activeMarkerIndex == MARKER_MODE_DELETE) ? MARKER_MODE_NONE : MARKER_MODE_DELETE;
            if (_activeMarkerIndex == MARKER_MODE_DELETE)
                EnsureSelectionModeReady();
            RefreshSelectionDisplay();
            UpdateMarkerToolbarState();
        }

        /// <summary>清空所有检测位的标记</summary>
        public void ClearAllMarkers()
        {
            if (GrabPositions == null) return;
            foreach (var pos in GrabPositions)
            {
                pos.PinTypes = "";
                pos.IsDetectPosition = false;
            }
            RefreshSelectionDisplay();
            UpdateMarkerToolbarState();
        }

        private void OnMarkerModeSelected(int idx)
        {
            SetMarkerMode(idx);
        }

        private void OnClearAllMarkersFromMenu()
        {
            ClearAllMarkers();
        }

        private void ExitSelectInspectionMode()
        {
            var halconControl = _vision.HalconControl;
            if (halconControl != null)
            {
                halconControl.IsInSelectionMode = false;
            }

            // 退出选择模式时重置标记模式
            _activeMarkerIndex = MARKER_MODE_NONE;
            UpdateMarkerToolbarState();

            // 清除叠加的虚线框和红色框，只显示原图
            var currentImage = _imageManager.GetCurrentStitchedImage();
            if (currentImage != null && currentImage.IsInitialized())
            {
                var halconWindow = _vision.HalconWindow;
                if (halconWindow != null)
                {
                    HOperatorSet.ClearWindow(halconWindow);
                    HOperatorSet.DispObj(currentImage, halconWindow);
                }
                currentImage.Dispose();
            }

            if (_isOverlayVisible)
            {
                var hw = _vision.HalconWindow;
                if (hw != null)
                {
                    if (_vmOverlays.Count > 0) { DrawVmRects(hw); DrawVmText(hw); }
                }
            }

            // 强制 Halcon 控件更新布局，确保其内部状态与显示一致
            _vision.RefreshDisplay();
        }

        /// <summary>在指定窗口上绘制选区虚线框/红框，不负责清窗和显示底图</summary>
        private void DrawSelectionRectangles(HWindow hw)
        {
            var project = ProjectManager.Instance.CurrentProject;
            if (project == null || GrabPositions == null) return;

            var tile = FullImageManager.CalculateTileDimensions(project.FovWidth, project.FovHeight, project.Overlap);
            double fovWidthPx = tile.cropW;
            double fovHeightPx = tile.cropH;
            int gridCols = _sysParam.ImageDisplayCols;
            int gridRows = _sysParam.ImageDisplayRows;

            // 未选中的为白色虚线框
            HOperatorSet.SetLineWidth(hw, 2);
            HOperatorSet.SetDraw(hw, "margin");
            HOperatorSet.SetLineStyle(hw, new HTuple(new int[] { 4, 4 }));

            for (int r = 0; r < gridRows; r++)
            {
                for (int c = 0; c < gridCols; c++)
                {
                    int idx = r * gridCols + c;
                    if (idx >= GrabPositions.Count) break;

                    if (!GrabPositions[idx].IsDetectPosition)
                    {
                        double row1 = r * fovHeightPx;
                        double col1 = c * fovWidthPx;
                        double row2 = (r + 1) * fovHeightPx - 1;
                        double col2 = (c + 1) * fovWidthPx - 1;

                        HOperatorSet.SetColor(hw, "white");
                        HOperatorSet.DispRectangle1(hw, row1, col1, row2, col2);
                    }
                }
            }

            // 已选中的为实线框（颜色和大小从系统参数读取）
            HOperatorSet.SetLineStyle(hw, new HTuple());
            HOperatorSet.SetColor(hw, _sysParam.Data.SelectionBoxColor);
            HOperatorSet.SetLineWidth(hw, _sysParam.Data.SelectionBoxSize);

            for (int r = 0; r < gridRows; r++)
            {
                for (int c = 0; c < gridCols; c++)
                {
                    int idx = r * gridCols + c;
                    if (idx >= GrabPositions.Count) break;

                    if (GrabPositions[idx].IsDetectPosition)
                    {
                        double row1 = r * fovHeightPx;
                        double col1 = c * fovWidthPx;
                        double row2 = (r + 1) * fovHeightPx - 1;
                        double col2 = (c + 1) * fovWidthPx - 1;

                        HOperatorSet.DispRectangle1(hw, row1, col1, row2, col2);

                        // 在选中框的左上角绘制标记字母
                        string pinTypes = GrabPositions[idx].PinTypes;
                        if (!string.IsNullOrEmpty(pinTypes))
                        {
                            // 根据当前缩放比动态计算字号（与叠加层文字相同策略），在屏幕上保持固定物理尺寸
                            int markerFontSize = _sysParam.Data.MarkerFontSize;
                            try
                            {
                                HOperatorSet.GetPart(hw, out HTuple mpr1, out HTuple mpc1,
                                                         out HTuple mpr2, out HTuple mpc2);
                                HOperatorSet.GetWindowExtents(hw, out _, out _, out HTuple mWinW, out HTuple mWinH);
                                if (mpr2.Length > 0 && mWinH.Length > 0)
                                {
                                    double imageH = Math.Abs(mpr2.D - mpr1.D) + 1;
                                    markerFontSize = Math.Max(8, (int)(markerFontSize * mWinH.D / imageH));
                                }
                            }
                            catch { }
                            HOperatorSet.SetFont(hw, $"Consolas-{markerFontSize}");
                            double labelStartX = col1 + 2;
                            double labelStartY = row1 + 2;
                            // 2026-08-27 Bug修复：针型标记文字（回退版，性能优先）
                            // ① 背景：disp_text 原生 box（非自绘）——未配置 MarkerTextBgColor 时显式 box=false（无背景）；
                            //    配置后 box=true + box_color。注：box 按字体行盒计算、比大写字母大，为 Halcon 固有行为，
                            //    自绘贴合方案(get_string_extents+矩形)实测性能差已回退，接受该观感。
                            // ② 字符间距 = 基准字号 + MarkerTextMargin（固定图像坐标值，不乘窗口缩放因子）——
                            //    缩放时不漂移，与上个版本(mi*18 固定间距)同性质，始终焊在板面图上。
                            string markerBg = _sysParam.Data.MarkerTextBgColor;
                            HTuple mgName = new HTuple(), mgValue = new HTuple();
                            if (!string.IsNullOrEmpty(markerBg))
                            {
                                mgName = new HTuple(new string[] { "box", "box_color" });
                                mgValue = new HTuple(new string[] { "true", markerBg });
                            }
                            else
                            {
                                mgName = new HTuple("box");
                                mgValue = new HTuple("false");
                            }
                            int markerMargin = _sysParam.Data.MarkerTextMargin;
                            if (markerMargin < 0) markerMargin = 0;
                            int charStep = _sysParam.Data.MarkerFontSize + markerMargin;
                            for (int mi = 0; mi < pinTypes.Length; mi++)
                            {
                                string ch = pinTypes[mi].ToString();
                                int ci = pinTypes[mi] - 'A';
                                if (ci >= 0 && ci < MarkerColors.Length)
                                {
                                    HOperatorSet.SetColor(hw, MarkerColors[ci]);
                                    HOperatorSet.DispText(hw, ch, "image", labelStartY, labelStartX + mi * charStep,
                                        MarkerColors[ci], mgName, mgValue);
                                }
                            }
                            // 恢复选中框颜色，避免状态泄漏影响后续绘制
                            HOperatorSet.SetColor(hw, _sysParam.Data.SelectionBoxColor);
                        }
                    }
                }
            }

            HOperatorSet.SetLineWidth(hw, 1);
        }

        private void RefreshSelectionDisplay()
        {
            var currentImage = _imageManager.GetCurrentStitchedImage();
            if (currentImage == null || !currentImage.IsInitialized()) return;

            var halconWindow = _vision.HalconWindow;
            if (halconWindow == null) return;

            HOperatorSet.ClearWindow(halconWindow);
            HOperatorSet.DispObj(currentImage, halconWindow);
            currentImage.Dispose();

            DrawSelectionRectangles(halconWindow);

            if (_isOverlayVisible)
            {
                if (_vmOverlays.Count > 0) { DrawVmRects(halconWindow); DrawVmText(halconWindow); }
            }
        }

        #endregion

        #region ===================================辅助方法===================================

        private void UpdateStatus(string message)
        {
            // 连续模式运行中：状态文字统一追加当前板号括号（外壳/内层检测/子流程/按钮反馈全覆盖，"始终显示第几块"）
            if (isWorkFlowRun[WorkFlowType.ContinuousRun] && _continuousBoardNo > 0)
                message = $"{message} (第 {_continuousBoardNo} 块)";
            if (Application.Current.Dispatcher.CheckAccess())
            {
                StatusText = message;
                StatusChanged?.Invoke(message);
            }
            else
            {
                // 2026-08-25：Invoke(同步) → InvokeAsync(不等待)——检测流程线程调用 UpdateStatus 时，
                // UI 线程正忙于拼接大图渲染（异步化后 Merge 移到 A 段后撞上渲染高峰），同步 Invoke 会阻塞流程 ~400ms/次
                // （实机定位：A行→DETECT2D_BLOB 间隙 429ms 即 UpdateStatus 阻塞）。状态文字延迟刷新无碍，流程不等待。
                Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    StatusText = message;
                    StatusChanged?.Invoke(message);
                });
            }
        }

        private string GetLatestImageFile(string directory)
        {
            try
            {
                if (!Directory.Exists(directory))
                {
                    Log.Warning($"目录不存在: {directory}");
                    return null;
                }

                var files = Directory.GetFiles(directory, "*.png")
                    .Concat(Directory.GetFiles(directory, "*.jpg"))
                    .Concat(Directory.GetFiles(directory, "*.bmp"))
                    .Concat(Directory.GetFiles(directory, "*.tiff"))
                    .OrderByDescending(f => File.GetLastWriteTime(f))
                    .ToList();

                return files.FirstOrDefault();
            }
            catch (Exception ex)
            {
                Log.Error($"获取最新图片失败: {ex.Message}");
                return null;
            }
        }

        private async Task<bool> WaitForFileReadyAsync(string filePath, int timeoutMs = 2000)
        {
            var sw = Stopwatch.StartNew();

            // 阶段1：等待文件出现
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (File.Exists(filePath))
                    break;
                await Task.Delay(50);
            }

            if (!File.Exists(filePath))
                return false;

            // 阶段2：等待文件尺寸稳定且独占打开成功（确保写入完成）
            long lastSize = -1;
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                try
                {
                    long currentSize = new FileInfo(filePath).Length;
                    if (currentSize == lastSize && currentSize > 0)
                    {
                        try
                        {
                            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.None))
                            {
                                Log.Debug($"文件就绪: {filePath}, 大小: {currentSize} bytes, 等待耗时: {sw.ElapsedMilliseconds}ms");
                                return true;
                            }
                        }
                        catch (IOException)
                        {
                            // 文件大小稳定但仍在被写入中，继续等待
                        }
                    }
                    lastSize = currentSize;
                    await Task.Delay(100);
                }
                catch (IOException)
                {
                    await Task.Delay(100);
                }
            }

            Log.Warning($"等待文件就绪超时 ({sw.ElapsedMilliseconds}ms): {filePath}");
            return false;
        }

        public void SetHalconWindow(CustomHSmartWindowControl halconControl)
        {
            _vision.SetHalconWindow(halconControl);

            halconControl.SelectInspectionModeRequested -= OnSelectInspectionModeRequested;
            halconControl.SelectInspectionModeRequested += OnSelectInspectionModeRequested;
            halconControl.DetectPositionToggled -= OnDetectPositionToggled;
            halconControl.DetectPositionToggled += OnDetectPositionToggled;
            halconControl.ExitInspectionModeRequested -= OnExitInspectionModeRequested;
            halconControl.ExitInspectionModeRequested += OnExitInspectionModeRequested;
            halconControl.MoveToDetectPositionRequested -= OnMoveToDetectPosition;
            halconControl.MoveToDetectPositionRequested += OnMoveToDetectPosition;
            halconControl.ScanPositionRequested -= OnScanPositionAtPosition;
            halconControl.ScanPositionRequested += OnScanPositionAtPosition;
            halconControl.ToggleOverlayRequested -= OnToggleOverlayRequested;
            halconControl.ToggleOverlayRequested += OnToggleOverlayRequested;

            // 叠加层过滤事件
            halconControl.OverlayFilterAll -= OnOverlayFilterAll;
            halconControl.OverlayFilterAll += OnOverlayFilterAll;
            halconControl.OverlayFilterOK -= OnOverlayFilterOK;
            halconControl.OverlayFilterOK += OnOverlayFilterOK;
            halconControl.OverlayFilterNG -= OnOverlayFilterNG;
            halconControl.OverlayFilterNG += OnOverlayFilterNG;
            halconControl.OverlayModeAllInfo -= OnOverlayModeAllInfo;
            halconControl.OverlayModeAllInfo += OnOverlayModeAllInfo;
            halconControl.OverlayModeNumberOnly -= OnOverlayModeNumberOnly;
            halconControl.OverlayModeNumberOnly += OnOverlayModeNumberOnly;
            // 从系统参数恢复持久化的结果图层过滤/显示模式（右键菜单切换，重启后保持）
            if (Enum.TryParse(SysParam.Instance.Data.OverlayFilter, out OverlayResultFilter savedFilter))
                _overlayFilter = savedFilter;
            if (Enum.TryParse(SysParam.Instance.Data.OverlayDisplayMode, out OverlayDisplayMode savedMode))
                _overlayDisplayMode = savedMode;
            // 同步当前状态到控件
            halconControl.CurrentOverlayFilter = _overlayFilter.ToString();
            halconControl.CurrentOverlayDisplayMode = _overlayDisplayMode.ToString();

            // 标记模式事件
            halconControl.PluginFeatureCount = PluginFeatureCount;
            halconControl.MarkerModeSelected -= OnMarkerModeSelected;
            halconControl.MarkerModeSelected += OnMarkerModeSelected;
            halconControl.ClearAllMarkersRequested -= OnClearAllMarkersFromMenu;
            halconControl.ClearAllMarkersRequested += OnClearAllMarkersFromMenu;

            halconControl.ImagePartChanged -= OnImagePartChanged;
            halconControl.ImagePartChanged += OnImagePartChanged;

            halconControl.IsOverlayVisible = _isOverlayVisible;

            // 拖拽刷新底图 + 叠加层
            halconControl.GetStitchedImage = () => _imageManager.GetCurrentStitchedImage();
            halconControl.GetRawStitchedImage = () => _imageManager.GetRawStitchedImage();
            halconControl.DrawOverlays = hw =>
            {
                if (halconControl.IsInSelectionMode)
                {
                    DrawSelectionRectangles(hw);
                }
                if (_vmOverlays.Count > 0 && _isOverlayVisible)
                {
                    DrawVmRects(hw);
                    DrawVmText(hw);
                }
            };
        }

        private void OnImagePartChanged(object sender, EventArgs e)
        {
            var hw = _vision.HalconWindow;
            if (hw == null) return;
            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    var halconControl = _vision.HalconControl;
                    if (halconControl != null && halconControl.IsInSelectionMode)
                    {
                        // 选择模式下缩放/平移后：重绘底图 + 选区虚线框/红框
                        RefreshSelectionDisplay();
                        return;
                    }

                    if ((_vmOverlays.Count == 0) || !_isOverlayVisible) return;

                    // 清窗 + 重绘拼图，清除上次缩放残留的文字，防止文字叠加累积
                    HOperatorSet.SetWindowParam(hw, "flush", "false");
                    var stitched = _imageManager.GetCurrentStitchedImage();
                    if (stitched == null || !stitched.IsInitialized()) return;
                    HOperatorSet.ClearWindow(hw);
                    HOperatorSet.DispObj(stitched, hw);
                    stitched.Dispose();
                    HOperatorSet.SetWindowParam(hw, "flush", "true");
                    // 完整重绘（清窗后拼图和矩形都丢失，须全部重绘），文字按新缩放比重新计算字号
                    if (_vmOverlays.Count > 0) { DrawVmRects(hw); DrawVmText(hw); }
                }
                catch { }
            }), DispatcherPriority.Normal);
        }

        public void RefreshDisplayAfterWindowReady()
        {
            var project = ProjectManager.Instance.CurrentProject;
            if (project == null) return;

            // 窗口就绪后重新显示图片（构造函数中首次加载时 Halcon 窗口尚未创建）
            _vision.ClearHalconWindow();
            _ = LoadProjectImagesAndDisplayAsync();

        }

        /// <summary>
        /// 通用重试：执行异步操作最多 maxRetries 次（返回bool），每次失败后延迟 delayMs 毫秒
        /// 提示信息由调用方自行处理
        /// </summary>
        private async Task<bool> RetryAsync(Func<Task<bool>> action, int maxRetries = 3, int delayMs = 200)
        {
            for (int retry = 0; retry < maxRetries; retry++)
            {
                if (retry > 0)
                    await Task.Delay(delayMs);
                if (await action())
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 取消感知等待：任务完成或 token 取消时返回（取消时立即返回 default，不等在途串口/网口指令超时；
        /// 在途指令由 Flush/自身超时收敛，流程层立即退出——复位/停止"立即终止"依赖此方法，2026-08-14）
        /// </summary>
        private static async Task<T> WaitOrCancelAsync<T>(Task<T> task, CancellationToken token)
        {
            if (token.IsCancellationRequested) return default;
            var cancelTcs = new TaskCompletionSource<T>();
            using (token.Register(() => cancelTcs.TrySetResult(default)))
            {
                return await await Task.WhenAny(task, cancelTcs.Task);
            }
        }

        /// <summary>
        /// 通用重试：执行异步操作最多 maxRetries 次（返回 string），
        /// 使用 isSuccess 判断是否成功，失败返回 TIMEOUT 字符串
        /// </summary>
        private async Task<string> RetryAsync(Func<Task<string>> action, Func<string, bool> isSuccess,
            int maxRetries = 3, int delayMs = 200)
        {
            for (int retry = 0; retry < maxRetries; retry++)
            {
                if (retry > 0)
                    await Task.Delay(delayMs);
                string result = await action();
                if (isSuccess(result))
                    return result;
            }
            return "TIMEOUT";
        }

        /// <summary>
        /// 发送 PTP 速度指令给下位机，确保速度设置生效
        /// </summary>
        private async Task SendSpeedPtpAsync()
        {
            if (!_motionIO.IsConnected) return;
            double speed = _sysParam.Data.AxisSpeed;
            if (speed <= 0) return;
            await _motionIO.SetSpeedPtp(speed);
            Log.Info($"已发送轴速度: {speed} mm/s");
        }

        private async Task SendSpeedScan3DAsync()
        {
            if (!_motionIO.IsConnected) return;
            double scan3DSpeed = _sysParam.Data.Scan3DSpeed;
            if (scan3DSpeed > 0)
            {
                await _motionIO.SetScan3DSpeed(scan3DSpeed);
                Log.Info($"已设置3D扫描速度: {scan3DSpeed} mm/s");
            }
        }

        /// <summary>关闭应用程序时清理所有资源</summary>
        public void Cleanup()
        {
            try { Log.Info("开始清理资源", "MainViewModel"); }
            catch { }

            // 取消正在运行的操作
            try
            {
                _checkCts?.Cancel();
                _checkCts?.Dispose();
                _checkCts = null;
            }
            catch { }

            try
            {
                _grabCts?.Cancel();
                _grabCts?.Dispose();
                _grabCts = null;
            }
            catch { }

            // 断开 2D/3D 相机 TCP 连接
            try { Vision2DClient.Instance?.Disconnect(); } catch { }
            try { Vision3DClient.Instance?.Disconnect(); } catch { }

            // 清理串口连接
            try { MotionIO.Cleanup(); } catch { }
            // 关闭光源串口
            try { LightSourceController.Instance.Close(); } catch { }

            try { Log.Info("清理完成", "MainViewModel"); }
            catch { }
        }

        public async Task ResetMachine()
        {
            if (!_motionIO.IsConnected) return;
            // 取消正在运行的检测/采集流程
            _checkCts?.Cancel();
            _grabCts?.Cancel();
            _pauseGrabEvent.Set();   // 唤醒全图采集暂停等待 → 立即进入 Terminated 退出
            _demoCts?.Cancel();      // 终止演示模式（DemoLoopAsync finally 清 _isDemoMode + 刷新高亮）
            // 重置连续模式状态：取消循环+置复位终止标志+清暂停标志，复位后点开始重新启动新一轮流程（而非继续）
            _continuousCts?.Cancel();
            // 外壳在跑（_continuousCts 非 null=连续模式运行中，含复位前刚结束前的瞬间）才置复位终止标志，让子流程/外壳立即响应；
            // 不在跑则清除残留——若无条件置位，外壳已结束后标志残留 true，会误触发后续单板检测的运送子流程（"复位终止，运送取消"）
            // 注意：此处【不能】把 _continuousCts 置 null（2026-08-19 修复）——外壳 finally 负责清理；
            // 若提前置 null，复位按钮双击时第二次复位读到 _continuousCts==null 会把 _continuousAbort 覆盖回 false，
            // 连续外壳 Step 顶部 abort 检查失效 → 复位无法终止（外壳继续出板/入板）。
            _continuousAbort = _continuousCts != null;
            _continuousPaused = false;
            _pauseCheckEvent.Set();
            PauseStateChanged?.Invoke();
            ResetWorkFlowStates();
            DeviceMonitor.Instance.ResetAlarm(); // 报警复位：清锁存+关蜂鸣器（信号仍在则状态栏兜底提示）
            _motionIO.Flush(); // 停止未完成指令（与报警保护同一原语）：在途 Move/GRAB 等等待者立即 ERROR 返回，流程快速收敛——复位立即停止，不等当前轮超时
            try { await _motionIO.StopMotorAsync(); } catch { } // 停传送带（外壳可能已结束，须由复位兜底；外壳 finally 也停，幂等无害）
            await _motionIO.ResetIO();
            // 速度设为300，快速移动到待机位
            double savedSpeed = SysParam.Instance.Data.AxisSpeed;
            await _motionIO.SetSpeedPtp(300);
            // X/Y/Z移动到待机位
            double standbyX = SysParam.Instance.Data.StandbyX;
            double standbyY = SysParam.Instance.Data.StandbyY;
            double standbyZ = SysParam.Instance.Data.StandbyZ;
            await _motionIO.MoveToAndWaitOK(standbyX, standbyY, standbyZ);
            // 恢复速度
            await _motionIO.SetSpeedPtp(savedSpeed);
        }

        /// <summary>
        /// 报警激活（DeviceMonitor 轮询线程触发）：只做线程安全原语——置报警终止标志 + 取消各流程 CTS + 唤醒暂停等待 + 停止未完成指令。
        /// 物理运动由下位机兜底（报警时机器不动）；在途等待指令由 Flush 以 ERROR 确定性完成（不等超时）。
        /// 不碰 UI/标志位（PauseStateChanged/isWorkFlowRun/UpdateStatus 由各流程 finally 在自身上下文清理与提示）。
        /// </summary>
        private void OnAlarmTriggered()
        {
            _alarmStopping = true;                       // 先置位：各流程检查点立刻可见
            _grabCts?.Cancel(); _pauseGrabEvent.Set();  // 全图采集：检查点转 Err；暂停等待立即唤醒
            _checkCts?.Cancel(); _pauseCheckEvent.Set(); // 单板/连续/演示/精度内层检测：下一检查点退出（finally 置 _checkFlowCompleted）
            _continuousCts?.Cancel();                        // 连续外壳：Step 顶报警穿透立即转 Err（不等检测完、不出板）
            _demoCts?.Cancel();                              // 演示循环退出
            _precisionCts?.Cancel();                         // 精度循环退出
            _motionIO.Flush();                               // 停止未完成指令：等待者 ERROR 立即返回（物理运动由下位机兜底）
        }

        /// <summary>报警解除（DeviceMonitor 轮询线程触发）：允许后续流程正常启动</summary>
        private void OnAlarmCleared() => _alarmStopping = false;

        #endregion
    }
}
