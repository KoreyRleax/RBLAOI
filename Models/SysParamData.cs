using System;

namespace RBLAOI.Models
{
    [Serializable]
    public class SysParamData
    {
        // ========== 连接参数 ==========
        public string VisionIp { get; set; } = "127.0.0.1";
        public int VisionPort { get; set; } = 8500;
        public string Vision3DIp { get; set; } = "127.0.0.1";
        public int Vision3DPort { get; set; } = 8501;
        public string SelectedSerialPort { get; set; } = "COM1";
        public int SelectedBaudRate { get; set; } = 460800;
        public string LightSourceSerialPort { get; set; } = "COM1";  // 光源控制器串口号（所有方案共用）
        public int LightSourceBaudRate { get; set; } = 19200;        // 光源控制器波特率 (默认19200-8N1)
        public int LightSourceChannelCount { get; set; } = 4;        // 光源控制器通道数（界面显示行数/训练文件名段数）
        public string LightTrainDir { get; set; } = "";              // 训练拍照存储目录（持久化，选择一次长期有效）

        // ========== 全局定位参数 ==========
        public double RightBottomX { get; set; } = 290.0;
        public double RightBottomY { get; set; } = 99.0;

        // ========== 运动参数 ==========
        public double AxisSpeed { get; set; } = 20;
        public double MoveStepMultiplier { get; set; } = 100;
        public double PulseEquivalentX { get; set; } = 500;          // X轴脉冲当量
        public double PulseEquivalentY { get; set; } = 500;          // Y轴脉冲当量
        public double PulseEquivalentZ { get; set; } = 500;          // Z轴脉冲当量
        public double PulseEquivalentH { get; set; } = 500;          // H轴脉冲当量
        public double MaxSpeed { get; set; } = 600;                 // 最大速度 (mm/s)
        public double Scan3DSpeed { get; set; } = 30;               // 3D扫描速度 (mm/s)
        public double HAxisMaxPosition { get; set; } = 300.0;       // H轴零位到固定轨道的距离 (mm)
        public double StandbyX { get; set; } = 0;                   // X轴待机位 (mm)
        public double StandbyY { get; set; } = 0;                   // Y轴待机位 (mm)
        public double StandbyZ { get; set; } = 0;                   // Z轴待机位 (mm)

        // ========== IO 参数 ==========
        public double IoPollInterval { get; set; } = 20;            // IO 状态轮询间隔 (ms)，优化后 200→20

        // ========== 相机与延时参数 ==========
        public double CameraExposureTime { get; set; } = 10000;     // 相机曝光时间 (μs)
        public double SetVmSavePathDelay { get; set; } = 100;    // 保存路径生效延时 (ms)：SET_PATH 发送后等 VM 保存模块应用新路径的传播窗口（2026-08-25 由 ImageCaptureDelay 改名）
        public double PositionSnapshotDelay { get; set; } = 200;    // 到位拍照延时 (ms)
        public double MaterialDischargeBlockDelay { get; set; } = 500; // 排料阻挡升起延时 (ms)
        public double MaterialDischargeTimeout { get; set; } = 3000;   // 排料超时报警延时 (ms)
        public double CameraPixelEquivalent { get; set; } = 0.004780825;   // 相机像素当量 (mm/px)
        public int CameraResolutionW { get; set; } = 5472;                // 相机分辨率宽 (px)
        public int CameraResolutionH { get; set; } = 3648;                // 相机分辨率高 (px)

        // ========== 相机偏移参数 ==========
        public double Camera3DOffsetX { get; set; } = 0;   // 3D相机相对于2D相机的X方向偏移 (mm)
        public double Camera3DOffsetY { get; set; } = 0;   // 3D相机相对于2D相机的Y方向偏移 (mm)

        // ========== 3D传感器参数 ==========
        public double SensorFovWidth { get; set; } = 26.44;     // 3D轮廓仪FOV宽度 (mm)
        public double SensorFovHeight { get; set; } = 19.36;    // 3D轮廓仪FOV高度 (mm)
        public double Scan3DStroke { get; set; } = 26.0;         // 3D扫描行程 (mm)
        public double VmToBoardOffsetX { get; set; } = 0;        // VM→板坐标转换偏移X (mm)
        public double VmToBoardOffsetY { get; set; } = 0;        // VM→板坐标转换偏移Y (mm)

        // ========== 有效区参数 ==========
        public double EffAreaLeftShrink { get; set; } = 0;       // 有效区左边界收缩量 (mm)
        public double EffAreaRightShrink { get; set; } = 0;      // 有效区右边界收缩量 (mm)

        // ========== 检测参数（全局默认值，每针型独立参数移至 PinTypeParameter） ==========
        public string DetectionMode { get; set; } = "VM方案";       // VM方案 / VM-Blob方案
        private double _emptyPinThreshold = 0.95;
        public double EmptyPinThreshold
        {
            get => _emptyPinThreshold;
            set => _emptyPinThreshold = Math.Max(0, Math.Min(value, 1));
        }
        public double MatchThreshold3D { get; set; } = 1.0;         // 2D/3D匹配距离阈值 (mm)
        public double MaxOverlap { get; set; } = 0.10;               // 新建方案默认最大重叠率 (10%)

        // ========== Halcon 结果图层显示（右键菜单，持久化） ==========
        public string OverlayFilter { get; set; } = "All";           // 结果图层过滤：All/OK/NG
        public string OverlayDisplayMode { get; set; } = "AllInfo";  // 结果图层显示模式：AllInfo/NumberOnly

        // ========== 功能开关 ==========
        public bool EnableScanner { get; set; } = false;            // 启用扫码
        public bool EnableMark { get; set; } = true;               // 启用Mark
        public bool EnableAlarm { get; set; } = true;              // 启用报警
        public bool EnableSafetyDoorAlarm { get; set; } = true;    // 启用安全门报警（X11 门禁，已接线；勾选才检查）
        public bool EnableLightCurtainAlarm { get; set; } = true;  // 启用光幕报警（X10 安全光幕，已接线；勾选才检查）

        // ========== 图片显示参数（隐藏，不显示在界面上） ==========
        public int ImageDisplayRows { get; set; } = 4;
        public int ImageDisplayCols { get; set; } = 4;

        // ========== 拼图补偿参数 ==========
        public double StitchOffsetX { get; set; } = 0.05;   // 拼图X补偿 (mm)，补偿相机安装角度误差
        public double StitchOffsetY { get; set; } = 0.1;    // 拼图Y补偿 (mm)

        // ========== VM 视觉 ==========
        // 已移除全局VmSolutionPath，改为方案-VM方案一一映射，路径统一为 {方案目录}/VMSolution/Check.sol

        // ========== 主题 ==========
        public string CurrentTheme { get; set; } = "Dark";

        // ========== 界面结果显示设置 ==========
        public int TextOffsetX { get; set; } = 10;           // 文字水平偏移 (像素), 正=向右
        public int TextOffsetY { get; set; } = -7;           // 文字垂直偏移 (像素), 正=向下
        public string TextDirection { get; set; } = "Horizontal"; // 文字显示方向: Horizontal=横向, Vertical=纵向旋转90度
        public int OverlayFontSize { get; set; } = 10;       // 叠加层基准字号
        public int MarkerFontSize { get; set; } = 22;        // 检测位标记字号（A/B/C...）
        public string OverlayFontName { get; set; } = "Consolas";  // 叠加层字体
        public string PadColor { get; set; } = "cyan";       // 焊盘定位框颜色
        public string PinColor { get; set; } = "yellow";     // 针尖定位框颜色
        public string IdealPinColor { get; set; } = "gold";  // 理想针框颜色
        public string TextBgColor { get; set; } = "";        // 文字背景色（空=无背景）
        public string EmptyPinColor { get; set; } = "orange";  // 空针框颜色
        public double IdealPinDotRadius { get; set; } = 3;     // 理想针圆点半径(像素)

        // ========== 标注框显示开关 ==========
        public bool ShowPadBox { get; set; } = true;         // 显示焊盘框
        public bool ShowPinBox { get; set; } = true;         // 显示针尖框
        public bool ShowIdealPinBox { get; set; } = true;    // 显示理想针框
        public bool ShowEmptyPinBox { get; set; } = true;     // 显示空针框

        // ========== 检测位选择设置 ==========
        public string SelectionBoxColor { get; set; } = "red";   // 选择框颜色
        public int SelectionBoxSize { get; set; } = 1;           // 选择框大小（像素）
        public int MarkerTextMargin { get; set; } = 6;           // 检测位标记文字边距（像素，字符间距；2026-08-27 新增，防大字号重叠）
        public string MarkerTextBgColor { get; set; } = "";      // 检测位标记文字背景色（空=无背景，2026-08-27 新增）

        // ========== 精度检测 ==========
        public int PrecisionRepeatCount { get; set; } = 10;      // 精度检测重复次数

        // ========== 超时参数 (ms) ==========
        public double MoveToCenterTimeout { get; set; } = 2000;      // 移动到检测位中心超时
        public double MoveToScanStartTimeout { get; set; } = 3000;    // 移动到3D扫描起点超时
        public double AdjustRailWidthTimeout { get; set; } = 10000;   // 调整轨宽超时
        public double GrabTimeout { get; set; } = 5000;              // GRAB拍照超时(ms)。2026-08-25：10000→5000（GRAB→回执实测正常 ~300-400ms、偶发慢 4.5s(位13)；超时后主题②入队板末重扫补救，无需 10s 干等；真超时=VM 异常，入队重扫+孤儿清扫兜底）
        public double Wait3DResultTimeout { get; set; } = 5000;       // 等待3D结果超时(仅扫描完成后的VM推送窗口, 2026-08-21解耦)
        public double Scan3DTimeout { get; set; } = 10000;            // 3D扫描超时
        public double Scan3DRetryTimeout { get; set; } = 3000;        // 3D扫描重试超时(第二次尝试: 轴已到位, 短超时快速判定, 2026-08-21)
        public double Camera3DTriggerTimeout { get; set; } = 500;     // CAMERA3D_ON/OFF 确认超时(直发版, 2026-08-25; 实测往返中位17ms/p90~40ms, 500ms覆盖排队与异常长尾)
        public double MoveZTimeout { get; set; } = 2000;              // Z轴调焦到位超时(ms)
        public double AxisHomeTimeout { get; set; } = 10000;          // 全轴/单轴回零超时 (ms)

        // ========== VM存图超时 ==========
        public double WaitForVmSaveTimeout { get; set; } = 5000;      // 等待VM存图超时(ms)。2026-08-25：2000→5000（文件就绪实测 185ms/p95=342ms/max=1.8s，5s 覆盖慢写余量）；超时后主题② A 快查分流+板末重扫补救，无需 15s 级大超时（位0 首拍 15s 空等实机复现，根因=文件晚到/未感知）

        // ========== 传送带流程超时 (ms) ==========
        public double ConveyorToEntryTimeout { get; set; } = 10000;  // 送到入口超时：反向传送等待入口传感器有料的超时 (ms)
        public double ConveyorBoardStableMs { get; set; } = 500;    // 传送到板稳定延时 (ms)：工作位信号持续ON确认板到位
        public double ConveyorWaitBoardTimeout { get; set; } = 5000; // 等待工作位信号稳定的超时 (ms)
        public double ConveyorToExitTimeout { get; set; } = 5000;     // 送到出口：正向传送等待出口有料超时 (5s)
        public double ContinuousOutletClearTimeout { get; set; } = 30000; // 出口防堵超时（连续模式）：出口板未被取走的等待超时 (30s)
        public double ContinuousEntryWaitTimeout { get; set; } = 60000;   // 入口等板超时（单板/连续共用）：单板模式规则d入口无料等待放板、连续模式等待前机放板（TransportBoard waitEntryOnly 路径）(60s)
    }
}