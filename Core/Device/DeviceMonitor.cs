using RBLAOI.Core.Managers;
using RBLAOI.Core.Motion;
using RBLAOI.Core.Utility;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;

namespace RBLAOI.Core.Device
{
    /// <summary>
    /// 设备状态枚举（仿插针机 MachineStatus，简化）：
    /// NotReady 未就绪（有轴未回零）→ Ready 就绪（回零完成、在待机位）→ Manual 手动（回零完成但不在待机位）
    /// → Running 自动（任一流程执行中）→ Pause 暂停（检测/全图采集暂停中）→ Alarm 报警（任一报警激活，最高优先级）
    /// → Demo 演示（演示模式运行中，预留定义，暂不联动设备状态机）
    /// </summary>
    public enum DeviceState
    {
        NotReady, // 未就绪：有轴未回零
        Ready,    // 就绪：回零完成、无流程、无报警、在待机位
        Manual,   // 手动：回零完成但不在待机位（手动移动过）
        Running,  // 自动：任一流程执行中
        Pause,    // 暂停：检测/全图采集暂停中
        Alarm,    // 报警：任一报警激活
        Demo,     // 演示：演示模式运行中（预留定义，本次演示模式不联动设备状态机）
    }

    /// <summary>
    /// 开关信号持续时间判定（仿 PLC 去抖，语义通俗）：
    /// 由喂入方每轮把当前电平喂进来（SetCurLevel 采样），内部跟踪 ON/OFF 各自已持续时长，
    /// 用 IsOnFor(durationMs) / IsOffFor(durationMs) 查询"电平已持续超过 n ms"——中途变化自动重置计时。
    /// </summary>
    public class OnOffSignal
    {
        private long _onStart = long.MinValue;  // 最近一次 ON 开始时刻（Stopwatch 时间戳），尚未 ON 过 = MinValue
        private long _offStart = long.MinValue; // 最近一次 OFF 开始时刻

        /// <summary>每轮喂入当前电平（由 DeviceMonitor.FeedSignals 在 IO 快照周期自动调用）</summary>
        public void SetCurLevel(bool level)
        {
            long now = Stopwatch.GetTimestamp();
            if (level)
            {
                if (_onStart == long.MinValue) _onStart = now; // ON 持续计时开始
                _offStart = long.MinValue;                      // OFF 计时清零
            }
            else
            {
                if (_offStart == long.MinValue) _offStart = now;
                _onStart = long.MinValue;
            }
        }

        /// <summary>ON 电平已持续超过 durationMs（未 ON 过或 ON 时长不足返回 false）</summary>
        public bool IsOnFor(int durationMs)
            => _onStart != long.MinValue && ElapsedMs(_onStart) > durationMs;

        /// <summary>OFF 电平已持续超过 durationMs</summary>
        public bool IsOffFor(int durationMs)
            => _offStart != long.MinValue && ElapsedMs(_offStart) > durationMs;

        private static double ElapsedMs(long startTick)
            => (Stopwatch.GetTimestamp() - startTick) * 1000.0 / Stopwatch.Frequency;
    }

    /// <summary>
    /// 设备业务监控（仿插针机 ioAndDataMonitor 设计，与采集同线程）：
    /// 订阅 MotionIO.IOStateUpdated（GET_STATE 轮询线程，~20ms 触发，频率由系统参数 IO 轮询间隔控制），
    /// 每个快照周期按序执行：① FeedSignals 喂入全部 IO 位去抖计时（信号数据维护）→ ② 各业务检查函数。
    /// 本类是设备信号的唯一入口（静态 OnOffSignal 字段，命名与 IOState 属性一致，直接 IsOnFor/IsOffFor 查询）
    /// 与业务监控（报警弹窗/蜂鸣器/置标志/状态栏文本）的统一宿主——单线程顺序执行，无线程/无锁。
    ///
    /// 报警生命周期（锁存 + 复位确认）：
    ///   Idle ──(任一轴报警信号 ON 持续 0.2s)──▶ 锁存：模态弹窗一次 + 蜂鸣器 ON + NeedHome 置位 + 状态栏提示
    ///   锁存中：点掉弹窗不解除（蜂鸣器继续响）；报警信号消失 → 自动解除；
    ///   复位按钮（复用系统"复位"）：清除锁存 + 蜂鸣器 OFF；若信号仍 ON → 进入"已复位待消除"，状态栏持续兜底提示。
    /// </summary>
    public class DeviceMonitor
    {
        private static readonly object _lock = new object();
        private static DeviceMonitor _instance;

        public static DeviceMonitor Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null) _instance = new DeviceMonitor();
                    }
                }
                return _instance;
            }
        }

        // ==================== 输入信号去抖 (位 0-15, 命名与 IOState 属性一致, 业务/流程层直接查询) ====================
        public static readonly OnOffSignal InputWait = new OnOffSignal();            // 位0:  X0 待料位感应（入口）
        public static readonly OnOffSignal InputWork = new OnOffSignal();            // 位1:  X1 工作位感应（板到位）
        public static readonly OnOffSignal InputLiftUp = new OnOffSignal();          // 位2:  X2 顶升上限位
        public static readonly OnOffSignal InputLiftDown = new OnOffSignal();        // 位3:  X3 顶升下限位
        public static readonly OnOffSignal InputOutput = new OnOffSignal();          // 位4:  X4 出料位感应（出口）
        public static readonly OnOffSignal InputStart = new OnOffSignal();           // 位5:  X5 启动
        public static readonly OnOffSignal InputPause = new OnOffSignal();           // 位6:  X6 暂停
        public static readonly OnOffSignal InputAirPressure = new OnOffSignal();     // 位7:  X7 整机气压检测
        public static readonly OnOffSignal InputEmergency = new OnOffSignal();       // 位8:  X8 急停
        public static readonly OnOffSignal InputRequestMaterial = new OnOffSignal(); // 位9:  X9 后机求料
        public static readonly OnOffSignal InputSafetyLight = new OnOffSignal();     // 位10: X10 安全光幕
        public static readonly OnOffSignal InputDoorSwitch = new OnOffSignal();      // 位11: X11 门禁开关
        public static readonly OnOffSignal InputReserve12 = new OnOffSignal();       // 位12: 预留
        public static readonly OnOffSignal InputReserve13 = new OnOffSignal();       // 位13: 预留
        public static readonly OnOffSignal InputReserve14 = new OnOffSignal();       // 位14: 预留
        public static readonly OnOffSignal InputReserve15 = new OnOffSignal();       // 位15: 预留

        // ==================== 输出信号去抖 (位 16-31) ====================
        public static readonly OnOffSignal OutputClamp1 = new OnOffSignal();          // 位16: Y0 顶升气缸（顶板1）
        public static readonly OnOffSignal OutputClamp2 = new OnOffSignal();          // 位17: Y1 顶升气缸（顶板2）
        public static readonly OnOffSignal OutputBlock = new OnOffSignal();           // 位18: Y3 出料位阻挡气缸
        public static readonly OnOffSignal OutputMotor = new OnOffSignal();           // 位19: Y4 流水线马达
        public static readonly OnOffSignal OutputRedLight = new OnOffSignal();        // 位20: Y5 红灯
        public static readonly OnOffSignal OutputGreenLight = new OnOffSignal();      // 位21: Y6 绿灯
        public static readonly OnOffSignal OutputYellowLight = new OnOffSignal();     // 位22: Y7 黄灯
        public static readonly OnOffSignal OutputBuzzer = new OnOffSignal();          // 位23: Y8 蜂鸣器
        public static readonly OnOffSignal OutputCamera3DTrigger = new OnOffSignal(); // 位24: Y9 3D相机触发
        public static readonly OnOffSignal OutputReserve25 = new OnOffSignal();       // 位25: 预留
        public static readonly OnOffSignal OutputReserve26 = new OnOffSignal();       // 位26: 预留
        public static readonly OnOffSignal OutputReserve27 = new OnOffSignal();       // 位27: 预留
        public static readonly OnOffSignal OutputReserve28 = new OnOffSignal();       // 位28: 预留
        public static readonly OnOffSignal OutputReserve29 = new OnOffSignal();       // 位29: 预留
        public static readonly OnOffSignal OutputReserve30 = new OnOffSignal();       // 位30: 预留
        public static readonly OnOffSignal OutputReserve31 = new OnOffSignal();       // 位31: 预留

        // ==================== 轴报警信号去抖 (位 32-63 电机板 IO，按需补充) ====================
        public static readonly OnOffSignal AlarmX = new OnOffSignal();    // 位35: X轴报警
        public static readonly OnOffSignal AlarmY = new OnOffSignal();    // 位43: Y轴报警
        public static readonly OnOffSignal AlarmZ = new OnOffSignal();    // 位51: Z轴报警
        public static readonly OnOffSignal AlarmH = new OnOffSignal();    // 位59: H轴报警

        // ==================== 报警状态（流程层查询，bool/string 原子读写） ====================

        /// <summary>当前是否有轴报警锁存（蜂鸣器跟随此状态）</summary>
        public bool AxisAlarm { get; private set; }

        /// <summary>状态栏提示文本（锁存中/已复位待消除），空=无报警；由 UI 定时刷新显示</summary>
        public string AlarmText { get; private set; } = "";

        /// <summary>报警激活跳变事件（AxisAlarm false→true 时触发一次；多报警源并存不重复触发；轮询线程回调，处理器须线程安全）</summary>
        public event Action AlarmTriggered;

        /// <summary>报警解除事件（AxisAlarm true→false 时触发一次：全部报警信号消失；"复位但信号仍在"期间不触发）</summary>
        public event Action AlarmCleared;

        /// <summary>轴报警后需要回零的标志（仿插针机 isNeedHome，按轴置位；回零完成后由流程层清除）</summary>
        public bool NeedHomeX { get; private set; }
        public bool NeedHomeY { get; private set; }
        public bool NeedHomeZ { get; private set; }
        public bool NeedHomeH { get; private set; }
        public bool NeedHomeAny => NeedHomeX || NeedHomeY || NeedHomeZ || NeedHomeH;

        /// <summary>回零标志是否由急停触发（急停按下置位，回零完成后清除）——流程入口提示文案区分"急停/轴报警"用</summary>
        public bool NeedHomeByEmergency { get; private set; }

        private const int AlarmDebounceMs = 200; // 报警去抖：信号 ON 持续 0.2s 才判定
        private bool _resetPending;              // 已复位但仍有报警激活（蜂鸣器停，状态栏继续提示）
        private bool _buzzerOn;                  // 蜂鸣器当前期望状态（仅状态变化时下发指令，避免刷串口）

        /// <summary>报警源定义：每个信号独立触发弹窗，状态栏按激活顺序用 > 追加显示</summary>
        private class AlarmItem
        {
            public OnOffSignal Signal;    // 去抖信号
            public string Text;           // 状态栏/标题文字（如"X轴驱动器报警"/"急停已按下"/"气压不足"）
            public string Detail;         // 弹窗内容（各自独立弹窗，不合并）
            public bool Active;           // 当前激活（信号去抖通过）
            public bool Reported;         // 已弹窗（激活期间只弹一次，信号消失复位）
            public Func<bool> Enabled;    // 报警检查开关（系统参数勾选才检查；null=始终检查）
        }

        private readonly List<AlarmItem> _alarms;

        // ==================== 设备状态机（仿插针机 machineStateUpdate） ====================

        /// <summary>当前设备状态（流程层查询；报警时禁止动作）</summary>
        public DeviceState State { get; private set; } = DeviceState.NotReady;

        /// <summary>设备状态中文文本（供状态栏显示）</summary>
        public string StateText => State switch
        {
            DeviceState.Alarm => "报警",
            DeviceState.Running => "自动",
            DeviceState.Pause => "暂停",
            DeviceState.NotReady => "未就绪",
            DeviceState.Manual => "手动",
            DeviceState.Demo => "演示",
            _ => "就绪",
        };

        /// <summary>流程状态来源（MainViewModel 注册）：是否有流程运行中 / 是否暂停 / 是否演示模式选中（供设备状态机使用；灯色已改为只随设备状态）</summary>
        private Func<bool> _flowRunning;
        private Func<bool> _flowPaused;
        private Func<bool> _flowDemo;

        /// <summary>红绿黄灯当前期望状态（仅状态变化时下发，避免刷串口）</summary>
        private bool _redOn, _greenOn, _yellowOn;

        /// <summary>注册流程状态来源（MainViewModel 构造时调用）</summary>
        public void RegisterFlowStateProviders(Func<bool> flowRunning, Func<bool> flowPaused, Func<bool> flowDemo)
        {
            _flowRunning = flowRunning;
            _flowPaused = flowPaused;
            _flowDemo = flowDemo;
        }

        private DeviceMonitor()
        {
            // 报警源注册：每个信号一个独立条目，状态栏追加显示 + 独立弹窗
            _alarms = new List<AlarmItem>
            {
                new AlarmItem { Signal = AlarmX, Text = "X轴驱动器报警", Detail = "X轴驱动器报警！\n\n请立即检查设备，处理完毕后点击「复位」清除报警。" },
                new AlarmItem { Signal = AlarmY, Text = "Y轴驱动器报警", Detail = "Y轴驱动器报警！\n\n请立即检查设备，处理完毕后点击「复位」清除报警。" },
                new AlarmItem { Signal = AlarmZ, Text = "Z轴驱动器报警", Detail = "Z轴驱动器报警！\n\n请立即检查设备，处理完毕后点击「复位」清除报警。" },
                new AlarmItem { Signal = AlarmH, Text = "H轴驱动器报警", Detail = "H轴驱动器报警！\n\n请立即检查设备，处理完毕后点击「复位」清除报警。" },
                new AlarmItem { Signal = InputEmergency, Text = "急停已按下", Detail = "急停已按下！\n\n请确认急停按钮已释放后点击「复位」。" },
                new AlarmItem { Signal = InputAirPressure, Text = "气压不足", Detail = "气压不足！\n\n请检查气源压力后点击「复位」。" },
                // 安全门/光幕：现场已接线；由系统参数开关控制是否检查（仿插针机 bChkSafeDoorAlarm/bChkLightAlarm）
                new AlarmItem {
                    Signal = InputDoorSwitch, Text = "安全门已开启", Detail = "安全门已开启！\n\n请关闭安全门后点击「复位」。",
                    Enabled = () => SysParam.Instance.Data.EnableSafetyDoorAlarm },
                new AlarmItem {
                    Signal = InputSafetyLight, Text = "安全光幕被遮挡", Detail = "安全光幕被遮挡！\n\n请清理光幕区域障碍后点击「复位」。",
                    Enabled = () => SysParam.Instance.Data.EnableLightCurtainAlarm },
            };
            MotionIO.Instance.IOStateUpdated += state => OnCycle(state); // 依赖 IO 轮询间隔，用户可在参数界面自定义
        }

        /// <summary>显式初始化：触发单例创建完成事件订阅（程序启动时调用一次）</summary>
        public static void EnsureInit()
        {
            _ = Instance;
        }

        /// <summary>
        /// 复位报警（复用系统"复位"按钮调用）：清除锁存 + 关闭蜂鸣器。
        /// 仅清除软件报警状态——若硬件报警信号仍为 ON，蜂鸣器不响，由状态栏持续提示，直到报警信号消失。
        /// </summary>
        public void ResetAlarm()
        {
            _resetPending = true; // 信号仍在时防蜂鸣器立即恢复；报警全部消失后自动复位
            if (_buzzerOn)
            {
                _buzzerOn = false;
                _ = MotionIO.Instance.SetIO(OutSignal.Buzzer, false);
            }
            Log.Info("[Alarm] 报警已复位（报警激活中则状态栏继续提示）", "Alarm");
        }

        /// <summary>回零完成后清除回零标志（流程层在 ServoHome 成功后调用）</summary>
        public void ClearNeedHomeFlags()
        {
            NeedHomeX = NeedHomeY = NeedHomeZ = NeedHomeH = false;
            NeedHomeByEmergency = false;
        }

        /// <summary>仅清除 H 轴回零标志（轨道归零成功后调用，不影响 XYZ 标志）</summary>
        public void ClearNeedHomeH()
        {
            NeedHomeH = false;
        }

        /// <summary>每个 GET_STATE 快照周期按序执行（仿 ioAndDataMonitor 内部调用顺序，跑在轮询线程）</summary>
        private void OnCycle(IOState state)
        {
            if (state == null) return;
            FeedSignals(state);     // ① 喂入全部 IO 位去抖计时（信号数据维护）
            CheckAlarms();          // ② 报警检查（轴报警/急停/气压，多报警源）
            UpdateMachineState();   // ③ 设备状态机更新（优先级：Alarm>NotReady>Pause>Running>Ready/Manual）
            // 未来扩展（保持调用顺序，扩展只加方法调用）：
            // CheckAppearance();  // 外观/到位检查
        }

        /// <summary>
        /// 设备状态机更新（仿插针机 machineStateUpdate，优先级从高到低）：
        /// Alarm（任一报警激活）→ NotReady（有轴未回零）→ Pause（检测/全图采集/连续暂停）→ Running（任一流程运行中）
        /// → Ready（在待机位）/ Manual（不在待机位）。
        /// 状态变化时：记日志。红绿黄灯每周期评估（灯色依赖流程业务执行阶段，不随 State 变化，见 UpdateLights）。
        /// </summary>
        private void UpdateMachineState()
        {
            DeviceState newState;
            if (AxisAlarm)
                newState = DeviceState.Alarm;
            else if (!MotionIO.Instance.IsXyzHomed || NeedHomeAny)
                newState = DeviceState.NotReady;
            else if (_flowDemo != null && _flowDemo())
                newState = DeviceState.Demo;   // 演示模式选中即"演示"（优先于暂停/运行显示）
            else if (_flowPaused != null && _flowPaused())
                newState = DeviceState.Pause;
            else if (_flowRunning != null && _flowRunning())
                newState = DeviceState.Running;
            else
                newState = IsAtStandby() ? DeviceState.Ready : DeviceState.Manual;

            if (newState != State)
            {
                State = newState;
                Log.Info($"[Device] 设备状态 → {newState}", "Device");
            }
            UpdateLights(newState);   // 每周期评估灯色（内部仅状态变化时下发指令）
        }

        /// <summary>
        /// 红绿黄灯控制（2026-08-14 用户规则：三色灯只随设备状态变化，去掉流程运行中设置的逻辑）：
        /// 报警(Alarm) → 红；未就绪/就绪/手动(NotReady/Ready/Manual) → 黄；运行/暂停/演示(Running/Pause/Demo) → 绿。
        /// 仅状态变化时下发指令。
        /// </summary>
        private void UpdateLights(DeviceState state)
        {
            bool red = state == DeviceState.Alarm;
            bool yellow = state == DeviceState.NotReady
                || state == DeviceState.Ready || state == DeviceState.Manual;
            bool green = !red && !yellow;
            if (red != _redOn) { _redOn = red; _ = MotionIO.Instance.SetIO(OutSignal.RedLight, red); }
            if (green != _greenOn) { _greenOn = green; _ = MotionIO.Instance.SetIO(OutSignal.GreenLight, green); }
            if (yellow != _yellowOn) { _yellowOn = yellow; _ = MotionIO.Instance.SetIO(OutSignal.YellowLight, yellow); }
        }

        /// <summary>是否在待机位（X/Y/Z 与系统参数 StandbyX/Y/Z 对比，±容差）</summary>
        private static bool IsAtStandby()
        {
            var data = SysParam.Instance.Data;
            var pos = MotionIO.Instance.GetCurrentPosition(100).Result; // 读 GET_STATE 快照（同步完成，无死锁）
            const double tol = 10.0; // 待机位容差 (mm)
            return Math.Abs(pos.Xmm - data.StandbyX) < tol
                && Math.Abs(pos.Ymm - data.StandbyY) < tol
                && Math.Abs(pos.Zmm - data.StandbyZ) < tol;
        }

        /// <summary>喂入全部 IO 位当前电平（仿插针机 sensorUpdate）</summary>
        private static void FeedSignals(IOState state)
        {
            InputWait.SetCurLevel(state.InputWait);
            InputWork.SetCurLevel(state.InputWork);
            InputLiftUp.SetCurLevel(state.InputLiftUp);
            InputLiftDown.SetCurLevel(state.InputLiftDown);
            InputOutput.SetCurLevel(state.InputOutput);
            InputStart.SetCurLevel(state.InputStart);
            InputPause.SetCurLevel(state.InputPause);
            InputAirPressure.SetCurLevel(state.InputAirPressure);
            InputEmergency.SetCurLevel(state.InputEmergency);
            InputRequestMaterial.SetCurLevel(state.InputRequestMaterial);
            InputSafetyLight.SetCurLevel(state.InputSafetyLight);
            InputDoorSwitch.SetCurLevel(state.InputDoorSwitch);
            InputReserve12.SetCurLevel(state.InputReserve12);
            InputReserve13.SetCurLevel(state.InputReserve13);
            InputReserve14.SetCurLevel(state.InputReserve14);
            InputReserve15.SetCurLevel(state.InputReserve15);

            OutputClamp1.SetCurLevel(state.OutputClamp1);
            OutputClamp2.SetCurLevel(state.OutputClamp2);
            OutputBlock.SetCurLevel(state.OutputBlock);
            OutputMotor.SetCurLevel(state.OutputMotor);
            OutputRedLight.SetCurLevel(state.OutputRedLight);
            OutputGreenLight.SetCurLevel(state.OutputGreenLight);
            OutputYellowLight.SetCurLevel(state.OutputYellowLight);
            OutputBuzzer.SetCurLevel(state.OutputBuzzer);
            OutputCamera3DTrigger.SetCurLevel(state.OutputCamera3DTrigger);
            OutputReserve25.SetCurLevel(state.OutputReserve25);
            OutputReserve26.SetCurLevel(state.OutputReserve26);
            OutputReserve27.SetCurLevel(state.OutputReserve27);
            OutputReserve28.SetCurLevel(state.OutputReserve28);
            OutputReserve29.SetCurLevel(state.OutputReserve29);
            OutputReserve30.SetCurLevel(state.OutputReserve30);
            OutputReserve31.SetCurLevel(state.OutputReserve31);

            AlarmX.SetCurLevel(state.AlarmX);
            AlarmY.SetCurLevel(state.AlarmY);
            AlarmZ.SetCurLevel(state.AlarmZ);
            AlarmH.SetCurLevel(state.AlarmH);
        }

        /// <summary>
        /// 报警检查（多报警源，仿插针机 chkAxisAlarm + PLC 报警锁存）：
        /// 每个报警源信号 ON 持续 0.2s → 激活：独立模态弹窗一次（无法预知何时报警，弹窗不合并）+ 状态栏追加显示；
        /// 点掉弹窗不清除（蜂鸣器继续响）；信号消失 → 自动解除；复位按钮 → 清锁存关蜂鸣器（信号仍在则状态栏兜底）。
        /// 弹窗调度到 UI 线程（BeginInvoke 非阻塞）、蜂鸣器仅状态变化时下发，均不阻塞轮询线程。
        /// </summary>
        private void CheckAlarms()
        {
            bool anyActive = false;
            foreach (var item in _alarms)
            {
                // 报警检查开关（如安全门/光幕按系统参数勾选才检查；未启用时复位状态，不参与激活）
                if (item.Enabled != null && !item.Enabled())
                {
                    item.Active = false;
                    item.Reported = false;
                    continue;
                }
                bool active = item.Signal.IsOnFor(AlarmDebounceMs);
                item.Active = active;
                if (active)
                {
                    anyActive = true;
                    if (!item.Reported)
                    {
                        item.Reported = true;
                        Log.Error($"[Alarm] {item.Text}", "Alarm");
                        // 报警联动回零标志：轴报警按轴置位；急停按下=伺服使能关闭、位置不可信，四轴全置位
                        if (item.Signal == AlarmX) NeedHomeX = true;
                        else if (item.Signal == AlarmY) NeedHomeY = true;
                        else if (item.Signal == AlarmZ) NeedHomeZ = true;
                        else if (item.Signal == AlarmH) NeedHomeH = true;
                        else if (item.Signal == InputEmergency) { NeedHomeX = NeedHomeY = NeedHomeZ = NeedHomeH = true; NeedHomeByEmergency = true; }
                        // 各自独立弹窗（模态，一次；BeginInvoke 调度 UI 线程不阻塞轮询）
                        string detail = item.Detail;
                        Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                            MessageBox.Show(detail, item.Text, MessageBoxButton.OK, MessageBoxImage.Error)));
                    }
                }
                else
                {
                    item.Reported = false; // 信号消失：允许下次再报
                }
            }

            // 任一报警激活即报警状态（供流程层禁止动作）；边沿跳变触发事件（供流程层终止运行中的流程）
            bool prevActive = AxisAlarm;
            AxisAlarm = anyActive;
            if (anyActive && !prevActive) AlarmTriggered?.Invoke();     // 激活跳变：一次（多报警源并存不重复触发）
            else if (!anyActive && prevActive) AlarmCleared?.Invoke();  // 全部消失：一次（复位但信号仍在期间不触发）

            // 蜂鸣器：任一报警激活 → ON；已复位（_resetPending）不响；全部消失 → OFF（仅状态变化时下发）
            bool buzzerWant = anyActive && !_resetPending;
            if (buzzerWant != _buzzerOn)
            {
                _buzzerOn = buzzerWant;
                _ = MotionIO.Instance.SetIO(OutSignal.Buzzer, buzzerWant);
            }

            if (!anyActive) _resetPending = false; // 报警全部消失：复位状态自动清除

            // 状态栏：激活的报警按注册顺序用 > 追加显示（如"X轴驱动器报警>急停已按下>气压不足"）
            AlarmText = anyActive
                ? string.Join(">", _alarms.Where(a => a.Active).Select(a => a.Text))
                : "";
        }
    }
}
