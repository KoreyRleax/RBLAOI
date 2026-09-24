using RBLAOI.Core.Managers;
using RBLAOI.Core.Utility;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Ports;
using System.Linq;
using System.Runtime.Remoting.Channels;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace RBLAOI.Core.Motion
{
    /// <summary>
    /// 运动控制IO类 - 封装与下位机(单片机)的串口通信
    /// </summary>
    public class MotionIO : IDisposable
    {
        #region ==================== 单例模式 ====================

        private static MotionIO _instance;
        private static readonly object _lock = new object();

        public static MotionIO Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        _instance ??= new MotionIO();
                    }
                }
                return _instance;
            }
        }

        private MotionIO()
        {
            _serialPort = new SerialPort();
            _serialPort.DataReceived += OnSerialDataReceived;
            _serialPort.Encoding = Encoding.ASCII;
        }

        public static void Cleanup()
        {
            if (_instance != null)
            {
                _instance.Close();
                _instance._serialPort?.Dispose();
                _instance = null;
            }
        }

        #endregion

        #region ==================== 事件委托 ====================

        public event Action<string> DataReceived;
        public event Action<bool> ConnectionStateChanged;
        public event Action<string> ErrorOccurred;
        /// <summary>
        /// IO 状态更新事件（后台自动轮询），订阅者可在任意线程处理
        /// </summary>
        public event Action<IOState> IOStateUpdated;

        /// <summary>
        /// GET_STATE 综合状态快照更新事件（IO + 位置 + 轴忙闲一次性提交），订阅者可在任意线程处理
        /// </summary>
        public event Action<StateSnapshot> StateSnapshotUpdated;

        /// <summary>
        /// 最近一次 IO 状态缓存
        /// </summary>
        public IOState CurrentIOState { get; private set; }

        /// <summary>
        /// 最近一次 GET_STATE 综合状态快照缓存（IsFresh=false 表示已过期，值保留为最后已知状态）
        /// </summary>
        public StateSnapshot LastStateSnapshot { get; private set; }

        /// <summary>
        /// 当前 GET_STATE 实际刷新率（Hz）：最近 50 次成功快照的到达间隔滑动平均，0 表示尚无数据
        /// </summary>
        public double CurrentStateRateHz { get; private set; }

        #endregion

        #region ==================== 协议会话状态机 ====================

        /// <summary>
        /// F407 协议会话状态：连接必须 PING 握手成功才 Ready；Ready 前禁止普通查询/轮询
        /// </summary>
        public enum MotionSessionState
        {
            Closed,      // 未连接
            Handshaking, // PING 握手中
            Ready,       // 握手成功，可正常查询/运动
            Recovering,  // 收到 SESSION_REQUIRED，正在有界恢复握手
            Restarting,  // RESTART 重启中（停轮询→发RESTART→重握手）
            Failed       // 握手失败，保持未就绪
        }

        private MotionSessionState _sessionState = MotionSessionState.Closed;
        private readonly SemaphoreSlim _restartLock = new SemaphoreSlim(1, 1);
        private int _recovering;   // Interlocked 单实例恢复门（防止 SESSION_REQUIRED 风暴重复触发恢复任务）

        /// <summary>当前会话状态（UI 可订阅 SessionStateChanged 展示握手/恢复过程）</summary>
        public MotionSessionState SessionState => _sessionState;

        /// <summary>协议会话是否就绪（Ready）；未就绪时禁止普通查询与动作</summary>
        public bool IsSessionReady => _sessionState == MotionSessionState.Ready;

        /// <summary>会话代次：每次握手/重启递增，用于隔离新旧代回复（迟到旧回复不得完成新请求）</summary>
        public int SessionGeneration { get; private set; }

        public event Action<MotionSessionState> SessionStateChanged;

        private void SetSessionState(MotionSessionState state)
        {
            if (_sessionState == state) return;
            _sessionState = state;
            SessionStateChanged?.Invoke(state);
        }

        #endregion

        #region ==================== 私有字段 ====================

        private SerialPort _serialPort;
        private long _rxCount = 0;
        private string _currentCoordMode = "未知";
        private bool _isConnected = false;
        private readonly object _sendLock = new object();
        private readonly object _receiveLock = new object();
        private StringBuilder _receiveBuffer = new StringBuilder();
        private CancellationTokenSource _cts;
        private readonly SemaphoreSlim _moveLock = new SemaphoreSlim(1, 1);

        private readonly Channel<CommandItem> _commandChannel = Channel.CreateBounded<CommandItem>(new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.DropOldest });// 队列满时丢弃最旧的
        private Task _queueProcessor;
        private CancellationTokenSource _queueCts;

        private CancellationTokenSource _statePollCts;
        private bool _isStatePollPending;
        /// <summary>GET_STATE 轮询连续失败次数（仅用于限频告警日志）</summary>
        private int _statePollFailStreak;
        /// <summary>GET_STATE 统计：成功次数 / 失败次数 / 平均 RTT 累计</summary>
        private long _statePollOkCount, _statePollTimeoutCount;
        private double _statePollRttSumMs;

        /// <summary>刷新率计算：最近 N 次成功快照的到达时间队列（仅 StatePollLoop 单线程访问）</summary>
        private const int RefreshRateWindowSize = 50;
        private readonly Queue<DateTime> _snapshotArrivalTimes = new Queue<DateTime>();

        /// <summary>是否已提升系统定时器精度（timeBeginPeriod(1)，使 Task.Delay(1) 真正达到 1ms）</summary>
        private bool _timerPeriodSet;

        [System.Runtime.InteropServices.DllImport("winmm.dll")]
        private static extern uint timeBeginPeriod(uint uMilliseconds);

        [System.Runtime.InteropServices.DllImport("winmm.dll")]
        private static extern uint timeEndPeriod(uint uMilliseconds);

        /// <summary>
        /// GET_STATE 综合快照解析正则：IO 支持 1~16 位十六进制（可带 0x），
        /// POS/STATUS 按字段名查找，不依赖固定宽度和逗号下标
        /// </summary>
        private static readonly System.Text.RegularExpressions.Regex StateRegex =
            new System.Text.RegularExpressions.Regex(
                @"^STATE\s*:\s*IO\s*=\s*(?:0x)?(?<io>[0-9A-Fa-f]{1,16})\s*;\s*" +
                @"POS\s*:\s*(?<pos>[^;]+)\s*;\s*STATUS\s*:\s*(?<status>.+)$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                System.Text.RegularExpressions.RegexOptions.Compiled);

        private const int DEFAULT_BAUD_RATE = 460800;
        private const int DEFAULT_DATA_BITS = 8;
        private const StopBits DEFAULT_STOP_BITS = StopBits.One;

        #endregion

        #region ==================== 公共属性 ====================

        public string PortName { get; private set; }
        public int BaudRate { get; private set; }

        public bool IsConnected
        {
            get => _isConnected;
            private set
            {
                if (_isConnected != value)
                {
                    _isConnected = value;
                    ConnectionStateChanged?.Invoke(value);
                }
            }
        }

        public long RxCount => _rxCount;
        public string CurrentCoordMode => _currentCoordMode;

        #endregion

        #region ==================== 连接管理 ====================

        public bool Open(string portName, int baudRate = DEFAULT_BAUD_RATE,
            int dataBits = DEFAULT_DATA_BITS, StopBits stopBits = DEFAULT_STOP_BITS,
            Parity parity = Parity.None)
        {
            try
            {
                if (_serialPort.IsOpen) Close();

                _serialPort.PortName = portName;
                _serialPort.BaudRate = baudRate;
                _serialPort.DataBits = dataBits;
                _serialPort.StopBits = stopBits;
                _serialPort.Parity = parity;
                _serialPort.ReadTimeout = 1000;
                _serialPort.WriteTimeout = 1000;

                _serialPort.Open();

                PortName = portName;
                BaudRate = baudRate;
                IsConnected = true;

                _cts = new CancellationTokenSource();
                Task.Run(() => ParseDataTask(_cts.Token));
                StartQueueProcessor();

                // F407 协议会话握手：两次 PING 成功后才 Ready 并启动轮询；
                // 握手失败（设备未上电/未响应）→ 关闭串口并返回 false，不启动任何轮询
                // （tcs 由 ParseDataTask 线程完成，无同步上下文依赖，Open 内同步等待安全）
                bool handshaked = HandshakeAsync().GetAwaiter().GetResult();
                if (!handshaked)
                {
                    Log.Error($"串口已打开但协议握手失败: {portName}@{baudRate}");
                    ErrorOccurred?.Invoke($"下位机握手失败: {portName} 无响应");
                    Close();
                    return false;
                }

                // 必须显式进入 Ready：StatePollLoop 首轮检查 IsSessionReady，不置 Ready 轮询会立即退出
                SetSessionState(MotionSessionState.Ready);
                StartStatePolling();

                // 提升系统定时器分辨率到 1ms：轮询间隔设置为 1ms 时 Task.Delay 才能真正生效
                try
                {
                    if (timeBeginPeriod(1) == 0) _timerPeriodSet = true;
                }
                catch { }

                Log.Info($"串口连接成功(握手通过): {portName}@{baudRate}");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"打开串口失败: {ex.Message}");
                ErrorOccurred?.Invoke($"打开串口失败: {ex.Message}");
                return false;
            }
        }

        public void Close()
        {
            try
            {
                StopQueueProcessor();
                StopStatePolling();
                _cts?.Cancel();

                // 清空命令队列并确定性完成所有等待者（断线后旧命令作废，等待方收到 "ERROR" 而非悬挂）
                while (_commandChannel.Reader.TryRead(out var pending))
                {
                    pending.TaskSource.TrySetResult("ERROR");
                }

                // 清空接收缓冲（重连后防止旧会话残留的半条数据拼到新回复上污染解析）
                lock (_receiveLock)
                {
                    _receiveBuffer.Clear();
                }
                try
                {
                    if (_serialPort?.IsOpen == true)
                    {
                        _serialPort.DiscardInBuffer();
                        _serialPort.DiscardOutBuffer();
                    }
                }
                catch { }

                // 恢复系统定时器分辨率（与 Open 时的 timeBeginPeriod(1) 成对）
                try
                {
                    if (_timerPeriodSet)
                    {
                        timeEndPeriod(1);
                        _timerPeriodSet = false;
                    }
                }
                catch { }

                if (_serialPort?.IsOpen == true)
                    _serialPort.Close();

                IsConnected = false;
                CurrentIOState = null;
                LastStateSnapshot = null;
                CurrentStateRateHz = 0;
                _snapshotArrivalTimes.Clear();
                _rxCount = 0;
                _currentCoordMode = "未知";
                SetSessionState(MotionSessionState.Closed);

                Log.Info("串口已关闭");
            }
            catch (Exception ex)
            {
                Log.Error($"关闭串口失败: {ex.Message}");
                ErrorOccurred?.Invoke($"关闭串口失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 清空指令队列和串口接收缓冲区，切换方案时调用，防止残留数据干扰
        /// </summary>
        public void Flush()
        {
            // 1. 清空指令队列中所有待处理项，并确定性完成等待者（不静默丢弃）
            while (_commandChannel.Reader.TryRead(out var pending))
            {
                pending.TaskSource.TrySetResult("ERROR");
            }

            // 2. 清空接收缓冲区
            lock (_receiveLock)
            {
                _receiveBuffer.Clear();
            }

            // 3. 清空串口硬件缓冲区
            try
            {
                if (_serialPort?.IsOpen == true)
                {
                    _serialPort.DiscardInBuffer();
                    _serialPort.DiscardOutBuffer();
                }
            }
            catch { }

            Log.Info("MotionIO 状态已清空");
        }

        /// <summary>
        /// F407 协议会话握手：连续两次 PING 等待设备响应（本代），全部成功才算 Ready。
        /// Open / RESTART 恢复 / SESSION_REQUIRED 恢复统一调用，每次握手递增 SessionGeneration。
        /// PING 直写串口不走命令队列（恢复期间队列可能被旧代在途请求阻塞，握手不能被队列拖死）。
        /// </summary>
        private async Task<bool> HandshakeAsync()
        {
            SetSessionState(MotionSessionState.Handshaking);
            SessionGeneration++;   // 新代：旧代迟到回复全部作废

            // 清残留数据，防止旧会话回复误完成 PING（如设备重启前遗留的残帧）
            lock (_receiveLock) { _receiveBuffer.Clear(); }
            try { _serialPort?.DiscardInBuffer(); } catch { }

            for (int i = 0; i < 2; i++)
            {
                // ConfigureAwait(false)：Open() 在 UI 线程同步等待握手，若此处捕获 UI 上下文会形成死锁
                string r = await PingDirectAsync(2000).ConfigureAwait(false);
                if (string.IsNullOrEmpty(r) || r == "TIMEOUT" || r == "ERROR")
                {
                    Log.Warning($"协议握手失败(PING {i + 1}/2): {(string.IsNullOrEmpty(r) ? "无回复" : r)}");
                    return false;
                }
            }
            Log.Info($"协议会话握手成功 (generation={SessionGeneration})");
            return true;
        }

        /// <summary>
        /// 直写 PING 并等待设备回复（绕开命令队列，超时约定同 ExecuteCommandAsync）。
        /// 精确匹配 "PING:" 前缀（F407 回复格式 PING:&lt;us&gt;，如 PING:123us，us 为固件处理耗时）：
        /// 设备主动推送的数据（H_HOME_OK 等）不再被误当成握手成功。
        /// </summary>
        private async Task<string> PingDirectAsync(int timeoutMs)
        {
            int gen = SessionGeneration;   // 捕获本代：旧代迟到回复不完成本代 PING
            var tcs = new TaskCompletionSource<string>();
            var cts = new CancellationTokenSource(timeoutMs);
            void handler(string data)
            {
                if (gen != SessionGeneration) return;
                foreach (var segment in data.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (segment.StartsWith("PING:", StringComparison.OrdinalIgnoreCase))
                    {
                        tcs.TrySetResult(segment);
                        return;
                    }
                }
            }
            DataReceived += handler;
            try
            {
                SendCommand("PING");
                cts.Token.Register(() => tcs.TrySetResult("TIMEOUT"));
                return await tcs.Task.ConfigureAwait(false);
            }
            finally
            {
                DataReceived -= handler;
                cts.Dispose();
            }
        }

        /// <summary>
        /// 重启按钮统一入口：停轮询 → 发 RESTART → 等待设备重启 → 新一代握手 → 恢复轮询。
        /// 失败保持 Failed 并明确报错，不形成 SESSION_REQUIRED 风暴；单实例锁防重复点击。
        /// </summary>
        public async Task<bool> RestartAndRehandshakeAsync()
        {
            await _restartLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!IsConnected) return false;
                SetSessionState(MotionSessionState.Restarting);
                StopStatePolling();
                Flush();                   // 清队列+完成等待者+清接收缓冲
                SendCommand("RESTART");    // 直写不等回复（设备将重启）
                await Task.Delay(2000).ConfigureAwait(false);    // 等待设备重启
                bool ok = await HandshakeAsync().ConfigureAwait(false);
                if (ok)
                {
                    SetSessionState(MotionSessionState.Ready);
                    StartStatePolling();
                    Log.Info("RESTART 重启完成，会话已恢复");
                    return true;
                }
                SetSessionState(MotionSessionState.Failed);
                ErrorOccurred?.Invoke("主板已复位，但重新握手失败：设备无响应，请检查供电/串口");
                return false;
            }
            catch (Exception ex)
            {
                Log.Error($"RESTART 重握手异常: {ex.Message}");
                SetSessionState(MotionSessionState.Failed);
                return false;
            }
            finally
            {
                _restartLock.Release();
            }
        }

        /// <summary>
        /// SESSION_REQUIRED 有界恢复：停轮询 → 新一代握手 → 恢复。
        /// 由 Interlocked 门保证同一时刻只有一个恢复任务，不重复触发、不成风暴。
        /// </summary>
        private async Task RecoverSessionAsync()
        {
            try
            {
                SetSessionState(MotionSessionState.Recovering);
                StopStatePolling();
                Flush();
                bool ok = await HandshakeAsync().ConfigureAwait(false);
                if (ok)
                {
                    SetSessionState(MotionSessionState.Ready);
                    StartStatePolling();
                    Log.Info("SESSION_REQUIRED 恢复成功，会话已重建");
                }
                else
                {
                    SetSessionState(MotionSessionState.Failed);
                    ErrorOccurred?.Invoke("会话恢复失败：握手无响应，请检查设备");
                }
            }
            catch (Exception ex)
            {
                Log.Error($"会话恢复异常: {ex.Message}");
                SetSessionState(MotionSessionState.Failed);
            }
            finally
            {
                Interlocked.Exchange(ref _recovering, 0);
            }
        }

        public static List<string> GetAvailablePorts()
        {
            return SerialPort.GetPortNames().ToList();
        }

        #endregion

        #region ==================== 基础指令发送 ====================

        /// <summary>
        /// 发送指令（同步，不等待响应，不经过队列）
        /// </summary>
        public bool SendCommand(string cmd)
        {
            if (!IsConnected)
            {
                Log.Warning($"串口未连接，无法发送指令: {cmd}");
                return false;
            }
            if (string.IsNullOrWhiteSpace(cmd)) return false;

            // 会话未就绪（Handshaking/Recovering/Restarting/Failed）时只允许 PING/RESTART（握手/重启自身），
            // 普通命令一律拒绝——防止握手期间业务命令（如 SPEED JOG、IO 输出）插队破坏 PING 交互
            if (!IsSessionReady && !cmd.StartsWith("PING", StringComparison.OrdinalIgnoreCase) &&
                !cmd.StartsWith("RESTART", StringComparison.OrdinalIgnoreCase))
            {
                Log.Warning($"会话未就绪，拒绝发送指令: {cmd}");
                return false;
            }

            lock (_sendLock)
            {
                try
                {
                    string fullCmd = cmd.Trim() + "\r\n";
                    _serialPort.Write(fullCmd);
                    if (!fullCmd.Contains("GET_STATE"))
                        Log.Debug($"\r\n [Send] {fullCmd}");
                    _serialPort.BaseStream.Flush();
                    return true;
                }
                catch (Exception ex)
                {
                    Log.Error($"发送指令失败: {cmd}, 错误: {ex.Message}");
                    return false;
                }
            }
        }

        /// <summary>
        /// 发送指令（异步，不等待响应，不经过队列）
        /// </summary>
        public async Task<bool> SendCommandAsync(string cmd)
        {
            return await Task.Run(() => SendCommand(cmd));
        }

        /// <summary>
        /// 发送指令并等待任意响应（经过队列，串行执行）
        /// </summary>
        public async Task<string> SendCommandAndWaitAsync(string cmd, int timeoutMs = 3000)
        {
            return await EnqueueCommand(cmd, null, timeoutMs);
        }

        /// <summary>
        /// 发送指令并等待匹配的响应（经过队列，串行执行）
        /// </summary>
        public async Task<string> SendCommandAndWaitAsync(string cmd, string expectedResponse, int timeoutMs = 2000)
        {
            return await EnqueueCommand(cmd, expectedResponse, timeoutMs);
        }

        #endregion

        #region ==================== 指令队列（内部实现） ====================

        private class CommandItem
        {
            public string Command { get; set; }
            public string ExpectedResponse { get; set; }
            public int TimeoutMs { get; set; }
            public TaskCompletionSource<string> TaskSource { get; set; }
            /// <summary>true=必须以 expectedResponse 开头匹配完整行（防止长回复被拆成两半时误匹配前缀）</summary>
            public bool CompleteLineOnly { get; set; }
            /// <summary>入队时的会话代次：回复匹配时校验，防止旧代迟到回复完成新请求</summary>
            public int Generation { get; set; }
        }

        private void StartQueueProcessor()
        {
            _queueCts = new CancellationTokenSource();
            _queueProcessor = Task.Run(() => ProcessQueue(_queueCts.Token));
            Log.Info("指令队列处理器已启动");
        }

        private void StopQueueProcessor()
        {
            _queueCts?.Cancel();
            _queueProcessor = null;
        }

        private async Task<string> EnqueueCommand(string cmd, string expectedResponse, int timeoutMs,
            bool replaceDuplicate = false, bool completeLineOnly = false)
        {
            // 会话未就绪（恢复/重启/失败）时拒绝入队，明确失败而非堆积/悬挂；PING 豁免（握手本身走队列）
            if (!IsSessionReady && cmd != "PING")
                return "NOT_READY";

            var item = new CommandItem
            {
                Command = cmd,
                ExpectedResponse = expectedResponse,
                TimeoutMs = timeoutMs,
                TaskSource = new TaskCompletionSource<string>(),
                CompleteLineOnly = completeLineOnly,
                Generation = SessionGeneration
            };

            // ✅ Channel 是线程安全的，不需要锁
            await _commandChannel.Writer.WriteAsync(item);

            return await item.TaskSource.Task;
        }

        private async Task ProcessQueue(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                CommandItem item = null;
                try
                {
                    // ✅ 查看队列深度
                    if (_commandChannel.Reader.CanCount)
                    {
                        int count = _commandChannel.Reader.Count;
                        if (count > 10)
                            Log.Info($"指令队列: {count} 个待处理");
                    }

                    // ✅ 异步等待，不占用锁
                    item = await _commandChannel.Reader.ReadAsync(token);

                    string result = await ExecuteCommandAsync(item.Command, item.ExpectedResponse, item.TimeoutMs, item.CompleteLineOnly, item.Generation);
                    item.TaskSource.TrySetResult(result);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // 队列处理器不能因单条命令异常而死亡（否则所有走队列的命令包括 GET_STATE 将永久失效）
                    Log.Error($"指令队列处理异常: {ex.Message}");
                    item?.TaskSource.TrySetResult("ERROR");
                }
            }
        }

        private async Task<string> ExecuteCommandAsync(string cmd, string expectedResponse, int timeoutMs, bool completeLineOnly = false, int generation = -1)
        {
            var sw = Stopwatch.StartNew();
            var tcs = new TaskCompletionSource<string>();
            var cts = new CancellationTokenSource(timeoutMs);

            void handler(string data)
            {
                if (string.IsNullOrEmpty(expectedResponse))
                {
                    // 任意完整行=设备响应（PING 握手等）；仍校验代次，旧代迟到回复不完成本代请求
                    if (generation < 0 || generation == SessionGeneration)
                        tcs.TrySetResult(data);
                }
                else
                {
                    foreach (var segment in data.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        bool matched = completeLineOnly
                            ? segment.StartsWith(expectedResponse, StringComparison.OrdinalIgnoreCase)
                            : segment.Contains(expectedResponse);
                        // 代次校验：RESTART/恢复后旧代回复不得完成新请求
                        if (matched && (generation < 0 || generation == SessionGeneration))
                        {
                            tcs.TrySetResult(segment);
                            sw.Stop();
                            //Log.Debug($"指令耗时: {cmd} -> {sw.ElapsedMilliseconds}ms");
                            return;
                        }
                    }
                }
            }

            DataReceived += handler;
            try
            {
                SendCommand(cmd);
                cts.Token.Register(() => tcs.TrySetResult("TIMEOUT"));
                return await tcs.Task.ConfigureAwait(false);
            }
            finally
            {
                DataReceived -= handler;
                cts.Dispose();
            }
        }

        /// <summary>
        /// 发送指令并等待匹配的响应（直发，不经过指令队列）。
        /// 2026-08-25 排队延迟优化：CAMERA3D_ON/OFF 若走队列会陪绑在途 GET_STATE 的 RS-485 偶发
        /// 超时（IO_TIMEOUT_MS=100ms×重试1次）→ 排队 ~400ms 计入 D 段。直发后仅受串口写锁（ms 级）
        /// 与回复往返（实测中位 ~17ms）约束；GET_STATE 轮询照常，IO 监测不受影响。
        /// 回复匹配与队列命令并行安全：DataReceived 多播，各 handler 按 expectedResponse 独立匹配，
        /// 互不吞并；仅需避免 expectedResponse=null（PING 握手）同期待回（正常检测流程无此场景）。
        /// </summary>
        private async Task<string> SendCommandAndWaitDirectAsync(string cmd, string expectedResponse, int timeoutMs)
        {
            if (!IsSessionReady && cmd != "PING")
                return "NOT_READY";

            var tcs = new TaskCompletionSource<string>();
            var cts = new CancellationTokenSource(timeoutMs);
            int gen = SessionGeneration;   // 调用时快照代次，旧代迟到回复不完成本请求

            void handler(string data)
            {
                foreach (var segment in data.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (segment.StartsWith(expectedResponse, StringComparison.OrdinalIgnoreCase) &&
                        (gen < 0 || gen == SessionGeneration))
                    {
                        tcs.TrySetResult(segment);
                        return;
                    }
                }
            }

            DataReceived += handler;
            try
            {
                if (!await SendCommandAsync(cmd))   // 写失败立即返回，不等超时
                {
                    tcs.TrySetResult("SEND_FAIL");
                    return "SEND_FAIL";
                }
                cts.Token.Register(() => tcs.TrySetResult("TIMEOUT"));
                return await tcs.Task.ConfigureAwait(false);
            }
            finally
            {
                DataReceived -= handler;   // 同 ExecuteCommandAsync 模式，防订阅泄漏
                cts.Dispose();
            }
        }

        #endregion

        #region ==================== 轴回零状态跟踪 ====================

        private readonly bool[] _axisHomed = new bool[4];
        private const int HOMED_X = 0, HOMED_Y = 1, HOMED_Z = 2, HOMED_H = 3;

        /// <summary>XYZ 轴是否均已回零</summary>
        public bool IsXyzHomed => _axisHomed[HOMED_X] && _axisHomed[HOMED_Y] && _axisHomed[HOMED_Z];

        /// <summary>连接断开时重置回零状态</summary>
        public void ResetHomedStatus()
        {
            for (int i = 0; i < _axisHomed.Length; i++) _axisHomed[i] = false;
        }

        #endregion

        #region ==================== 运动控制指令 ====================
        public async Task<bool> MoveToAndWaitOK(double? x = null, double? y = null, double? z = null, double? h = null,
                                               int timeoutMs = 8000)
        {
            await _moveLock.WaitAsync();
            try
            {
                var currentPos = await GetCurrentPosition();

                StringBuilder cmd = new StringBuilder();
                var movingAxes = new List<string>();
                var targets = new Dictionary<string, long>(); // 各轴目标脉冲（快照轮询提前判完成用）

                if (x.HasValue)
                {
                    long target = MmToPulse(x.Value, "X");
                    cmd.Append($"X{target},");
                    targets["X"] = target;
                    if (NeedMove(target, currentPos.X)) movingAxes.Add("X");
                }
                if (y.HasValue)
                {
                    long target = MmToPulse(y.Value, "Y");
                    cmd.Append($"Y{target},");
                    targets["Y"] = target;
                    if (NeedMove(target, currentPos.Y)) movingAxes.Add("Y");
                }
                if (z.HasValue)
                {
                    long target = MmToPulse(z.Value, "Z");
                    cmd.Append($"Z{target},");
                    targets["Z"] = target;
                    if (NeedMove(target, currentPos.Z)) movingAxes.Add("Z");
                }
                if (h.HasValue)
                {
                    long target = MmToPulse(h.Value, "H");
                    cmd.Append($"H{target},");
                    targets["H"] = target;
                    if (NeedMove(target, currentPos.H)) movingAxes.Add("H");
                }

                string cmdStr = cmd.ToString().TrimEnd(',');
                if (string.IsNullOrEmpty(cmdStr)) return false;

                // 先订阅事件再发指令，防止下位机回复太快导致信号丢失
                // 传目标脉冲启用快照轮询：完成事件偶发丢失时快照确认到位即提前返回，不必白等满超时
                var waitTasks = movingAxes.Select(axis =>
                    WaitAxisComplete(axis, timeoutMs, targets.TryGetValue(axis, out var t) ? t : (long?)null)).ToList();

                if (!await SendCommandAsync(cmdStr)) return false;

                if (movingAxes.Count == 0)
                    return true;

                var results = await Task.WhenAll(waitTasks);
                return results.All(r => r);
            }
            catch (Exception ex)
            {
                Log.Error($"[MoveToAndWaitOK] 异常: {ex.Message}");
                return false;
            }
            finally
            {
                _moveLock.Release();
            }
        }

        public async Task<bool> MoveToAndWaitStop(double? x = null, double? y = null, double? z = null, double? h = null,
                                                  int timeoutMs = 10000)
        {
            await _moveLock.WaitAsync();
            try
            {
                var cmd = new StringBuilder();
                var axes = new List<string>();

                if (x.HasValue) { cmd.Append($"X{MmToPulse(x.Value, "X")},"); axes.Add("X"); }
                if (y.HasValue) { cmd.Append($"Y{MmToPulse(y.Value, "Y")},"); axes.Add("Y"); }
                if (z.HasValue) { cmd.Append($"Z{MmToPulse(z.Value, "Z")},"); axes.Add("Z"); }
                if (h.HasValue) { cmd.Append($"H{MmToPulse(h.Value, "H")},"); axes.Add("H"); }

                string cmdStr = cmd.ToString().TrimEnd(',');
                if (string.IsNullOrEmpty(cmdStr)) return false;

                if (!await SendCommandAsync(cmdStr)) return false;

                return await WaitAxesStop(axes, timeoutMs);
            }
            catch (Exception ex)
            {
                Log.Error($"[MoveToAndWaitStop] 异常: {ex.Message}");
                return false;
            }
            finally
            {
                _moveLock.Release();
            }
        }

        /// <summary>
        /// 3D 扫描 - 开启3D触发 → 发送SCAN3D等待轴完成 → 关闭3D触发（OK方式）
        /// </summary>
        public async Task<bool> Scan3DToAndWaitOK(double? x = null, double? y = null, int timeoutMs = 10000)
        {
            try
            {
                // (1) 开启3D相机触发（2026-08-25 直发：不经过指令队列，消除在途 GET_STATE 超时陪绑 ~400ms）
                string onRs = await SendCommandAndWaitDirectAsync("CAMERA3D_ON", "CAMERA3D_ON_OK", (int)SysParam.Instance.Data.Camera3DTriggerTimeout);
                if (!onRs.Contains("CAMERA3D_ON_OK"))
                {
                    Log.Error("[Scan3DToAndWaitOK] 开启3D触发失败");
                    return false;
                }

                // (2) 获取当前位置，用于判断是否需要移动
                var currentPos = await GetCurrentPosition();

                // (3) 发送 SCAN3D 指令
                var cmd = new StringBuilder("SCAN3D ");
                var movingAxes = new List<string>();
                long targetXPulse = 0, targetYPulse = 0;   // 快照兜底判据用（目标脉冲）

                if (x.HasValue)
                {
                    targetXPulse = MmToPulse(x.Value, "X");
                    cmd.Append($"X{targetXPulse},");
                    if (NeedMove(targetXPulse, currentPos.X)) movingAxes.Add("X");
                }
                if (y.HasValue)
                {
                    targetYPulse = MmToPulse(y.Value, "Y");
                    cmd.Append($"Y{targetYPulse},");
                    if (NeedMove(targetYPulse, currentPos.Y)) movingAxes.Add("Y");
                }

                string cmdStr = cmd.ToString().TrimEnd(',');
                if (cmdStr == "SCAN3D")
                {
                    await SendCommandAndWaitDirectAsync("CAMERA3D_OFF", "CAMERA3D_OFF_OK", (int)SysParam.Instance.Data.Camera3DTriggerTimeout);
                    return false;
                }

                Log.Info($"[Scan3DToAndWaitOK] 发送: {cmdStr}");
                if (!await SendCommandAsync(cmdStr))
                {
                    await SendCommandAndWaitDirectAsync("CAMERA3D_OFF", "CAMERA3D_OFF_OK", (int)SysParam.Instance.Data.Camera3DTriggerTimeout);
                    return false;
                }

                // (4) 等待轴完成（只等待需要移动的轴）
                // 传入目标脉冲启用快照轮询：X_COMPLETE 偶发丢失/延迟时，快照确认到位即提前返回（不必白等满超时）
                bool axisOk = true;
                if (movingAxes.Count > 0)
                {
                    var waitTasks = movingAxes.Select(axis => WaitAxisComplete(axis, timeoutMs,
                        axis == "X" ? targetXPulse : targetYPulse)).ToList();
                    var results = await Task.WhenAll(waitTasks);
                    axisOk = results.All(r => r);

                    // 超时兜底：X_COMPLETE 事件偶发丢失时等待会假超时（实测扫描已到位、VM 已推数据）。
                    // 用 GET_STATE 快照（19~21Hz 刷新）确认轴实际已到目标位且空闲 → 按成功处理，
                    // 避免白等超时 + 重复扫描；快照缺失/过期/未到位则保持失败（保守，重试只留给真失败）。
                    if (!axisOk)
                    {
                        var failAxes = movingAxes.Where((axis, idx) => !results[idx]).ToList();
                        long GetTarget(string axis) => axis == "X" ? targetXPulse : targetYPulse;
                        if (failAxes.All(axis => AxisArrivedBySnapshot(axis, GetTarget(axis))))
                        {
                            var snap = LastStateSnapshot;
                            Log.Warning($"[Scan3DToAndWaitOK] 轴等待超时但快照确认已到位(X={snap?.X},Y={snap?.Y}), 按成功处理");
                            axisOk = true;
                        }
                        else
                        {
                            var snap = LastStateSnapshot;
                            Log.Warning($"[Scan3DToAndWaitOK] 轴等待超时且快照未确认到位: " +
                                (snap == null ? "无快照" :
                                $"Fresh={snap.IsFresh} X={snap.X}(Busy={snap.XBusy}) Y={snap.Y}(Busy={snap.YBusy}) " +
                                $"目标 X={targetXPulse},Y={targetYPulse}"));
                        }
                    }
                }

                // (5) 关闭3D相机触发
                string offRs = await SendCommandAndWaitDirectAsync("CAMERA3D_OFF", "CAMERA3D_OFF_OK", (int)SysParam.Instance.Data.Camera3DTriggerTimeout);
                if (!offRs.Contains("CAMERA3D_OFF_OK"))
                    Log.Warning("[Scan3DToAndWaitOK] 关闭3D触发失败");

                return axisOk;
            }
            catch (Exception ex)
            {
                Log.Error($"[Scan3DToAndWaitOK] 异常: {ex.Message}");
                _ = SendCommandAndWaitDirectAsync("CAMERA3D_OFF", "CAMERA3D_OFF_OK", (int)SysParam.Instance.Data.Camera3DTriggerTimeout);
                return false;
            }
        }

        /// <summary>
        /// 3D 扫描 - 开启3D触发 → 发送SCAN3D轮询轴停止 → 关闭3D触发（Stop方式）
        /// </summary>
        public async Task<bool> Scan3DToAndWaitStop(double? x = null, double? y = null, int timeoutMs = 60000)
        {
            try
            {
                // (1) 开启3D相机触发（2026-08-25 直发：不经过指令队列，消除在途 GET_STATE 超时陪绑 ~400ms）
                string onRs = await SendCommandAndWaitDirectAsync("CAMERA3D_ON", "CAMERA3D_ON_OK", (int)SysParam.Instance.Data.Camera3DTriggerTimeout);
                if (!onRs.Contains("CAMERA3D_ON_OK"))
                {
                    Log.Error("[Scan3DToAndWaitStop] 开启3D触发失败");
                    return false;
                }

                // (2) 发送 SCAN3D 指令
                var cmd = new StringBuilder("SCAN3D ");
                var axes = new List<string>();

                if (x.HasValue) { cmd.Append($"X{MmToPulse(x.Value, "X")},"); axes.Add("X"); }
                if (y.HasValue) { cmd.Append($"Y{MmToPulse(y.Value, "Y")},"); axes.Add("Y"); }

                string cmdStr = cmd.ToString().TrimEnd(',');
                if (cmdStr == "SCAN3D") return false;

                Log.Info($"[Scan3DToAndWaitStop] 发送: {cmdStr}");
                if (!await SendCommandAsync(cmdStr))
                {
                    await SendCommandAndWaitDirectAsync("CAMERA3D_OFF", "CAMERA3D_OFF_OK", (int)SysParam.Instance.Data.Camera3DTriggerTimeout);
                    return false;
                }

                // (3) 轮询轴停止
                bool axisStopped = axes.Count == 0 || await WaitAxesStop(axes, timeoutMs);

                // (4) 关闭3D相机触发
                string offRs = await SendCommandAndWaitDirectAsync("CAMERA3D_OFF", "CAMERA3D_OFF_OK", (int)SysParam.Instance.Data.Camera3DTriggerTimeout);
                if (!offRs.Contains("CAMERA3D_OFF_OK"))
                    Log.Warning("[Scan3DToAndWaitStop] 关闭3D触发失败");

                return axisStopped;
            }
            catch (Exception ex)
            {
                Log.Error($"[Scan3DToAndWaitStop] 异常: {ex.Message}");
                _ = SendCommandAndWaitDirectAsync("CAMERA3D_OFF", "CAMERA3D_OFF_OK", (int)SysParam.Instance.Data.Camera3DTriggerTimeout);
                return false;
            }
        }

        public Task<PositionInfo> GetCurrentPosition(int timeoutMs = 100)
        {
            // GET_STATE 综合快照是唯一位置来源（不再直发 GET_POS）。
            // timeoutMs 仅为兼容旧签名保留，不再使用
            var pos = new PositionInfo();
            var snapshot = LastStateSnapshot;
            if (snapshot != null)
            {
                pos.X = snapshot.X;
                pos.Y = snapshot.Y;
                pos.Z = snapshot.Z;
                pos.H = snapshot.H;
            }
            return Task.FromResult(pos);
        }

        private bool NeedMove(long targetPulse, long currentPulse, long tolerance = 500)
        {
            return Math.Abs(targetPulse - currentPulse) > tolerance;
        }

        #endregion

        #region ==================== 轴状态等待与查询 ====================

        /// <summary>GET_STATE 快照兜底：轴已空闲且位置在目标容差(500脉冲,同NeedMove)内 → 视为已完成。
        /// 用于 X_COMPLETE 事件偶发丢失导致的轴等待假超时；快照缺失/过期/运动中/未到位 → false（保守）</summary>
        private bool AxisArrivedBySnapshot(string axis, long targetPulse)
        {
            var snap = LastStateSnapshot;
            if (snap == null || !snap.IsFresh) return false;
            switch (axis)
            {
                case "X": return !snap.XBusy && Math.Abs(snap.X - targetPulse) <= 500;
                case "Y": return !snap.YBusy && Math.Abs(snap.Y - targetPulse) <= 500;
                case "Z": return !snap.ZBusy && Math.Abs(snap.Z - targetPulse) <= 500;
                case "H": return !snap.HBusy && Math.Abs(snap.H - targetPulse) <= 500;
                default: return false;
            }
        }

        /// <summary>
        /// 等待轴完成信号（X_OK / X_COMPLETE / X_HOME_OK / X_HOME_DONE）。
        /// 可选 targetPulse：传入时等待期间每 200ms 轮询 GET_STATE 快照，一旦快照确认
        /// 轴已空闲且到位即提前返回——X_COMPLETE 事件偶发丢失/延迟时不再白等满超时
        /// （快照 19~21Hz 刷新，到位后最迟 ~50ms 即可在轮询中看到）。不传则行为与原先完全一致。
        /// </summary>
        public async Task<bool> WaitAxisComplete(string axis, int timeoutMs = 10000, long? targetPulse = null)
        {
            var tcs = new TaskCompletionSource<bool>();
            var cts = new CancellationTokenSource();
            bool isCompleted = false, isTimeout = false;
            var timeoutTask = Task.Delay(timeoutMs, cts.Token);

            Action<string> handler = data =>
            {
                if (isCompleted || isTimeout) return;
                foreach (var segment in data.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (segment.Contains($"{axis}_OK") ||
                segment.Contains($"{axis}_COMPLETE") ||
                segment.Contains($"{axis}_HOME_OK") ||
                segment.Contains($"{axis}_HOME_DONE"))
                    {
                        isCompleted = true;
                        tcs.TrySetResult(true);
                        return;
                    }
                }
            };

            // 快照轮询提前判完成（仅当给了目标脉冲时启用；Home/回零等未知目标点不启用）
            var snapshotTask = targetPulse.HasValue
                ? PollSnapshotArrivalAsync(axis, targetPulse.Value, cts.Token)
                : Task.FromResult(false);

            DataReceived += handler;
            try
            {
                var done = await Task.WhenAny(tcs.Task, snapshotTask, timeoutTask);
                if (done == timeoutTask)
                {
                    isTimeout = true;
                    return false;
                }
                if (done == snapshotTask && await snapshotTask)
                    return true;
                return await tcs.Task;
            }
            finally
            {
                isTimeout = true; // 阻止 handler 后续动作，并让轮询循环尽快退出
                cts.Cancel();
                DataReceived -= handler;
                cts.Dispose();
            }
        }

        /// <summary>
        /// 每 200ms 检查 GET_STATE 快照：轴空闲且位置在目标容差内 → 返回 true；被取消返回 false。
        /// 与 AxisArrivedBySnapshot 判据一致（快照缺失/过期/运动中/未到位 → 继续轮询），保守安全。
        /// </summary>
        private async Task<bool> PollSnapshotArrivalAsync(string axis, long targetPulse, CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    if (AxisArrivedBySnapshot(axis, targetPulse)) return true;
                    await Task.Delay(200, ct);
                }
            }
            catch (OperationCanceledException)
            {
                // 正常取消：事件已到 / 已超时，轮询无需再运行
            }
            return false;
        }

        public async Task<bool> WaitAxesStop(List<string> axes, int timeoutMs = 10000)
        {
            if (axes == null || axes.Count == 0) return true;

            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                bool allStopped = true;
                foreach (var axis in axes)
                {
                    if (await IsMoving(axis))
                    {
                        allStopped = false;
                        break;
                    }
                }
                if (allStopped) return true;
                await Task.Delay(100);
            }
            return false;
        }

        public Task<bool> IsMoving(string axis, int timeoutMs = 100)
        {
            // GET_STATE 快照是唯一忙闲来源（不再直发 GET_STATUS）；
            // 状态未知（无快照/快照过期）时保守判定为"运动中"，避免 WaitAxesStop 提前放行
            var snapshot = LastStateSnapshot;
            if (snapshot == null || !snapshot.IsFresh) return Task.FromResult(true);
            switch (axis)
            {
                case "X": return Task.FromResult(snapshot.XBusy);
                case "Y": return Task.FromResult(snapshot.YBusy);
                case "Z": return Task.FromResult(snapshot.ZBusy);
                case "H": return Task.FromResult(snapshot.HBusy);
                default: return Task.FromResult(false);
            }
        }

        /// <summary>
        /// 获取所有轴运动状态（GET_STATE 综合快照，不再直发 GET_STATUS）
        /// </summary>
        public Task<AxisStatus> GetAllAxisStatus(int timeoutMs = 100)
        {
            var status = new AxisStatus();
            var snapshot = LastStateSnapshot;
            if (snapshot == null)
            {
                // 无快照（连接瞬间/会话未就绪）：保守返回全 true（视为运动中），
                // 与 IsMoving 一致——防止上层把"未知"误判为"全轴已停止"
                status.XMoving = status.YMoving = status.ZMoving = status.HMoving = true;
                return Task.FromResult(status);
            }
            status.XMoving = snapshot.XBusy;
            status.YMoving = snapshot.YBusy;
            status.ZMoving = snapshot.ZBusy;
            status.HMoving = snapshot.HBusy;
            return Task.FromResult(status);
        }
        public class AxisStatus
        {
            public bool XMoving { get; set; }
            public bool YMoving { get; set; }
            public bool ZMoving { get; set; }
            public bool HMoving { get; set; }
        }
        #endregion

        #region ==================== IO状态查询 ====================

        public Task<IOState> GetAll_IOState(int timeoutMs = 100)
        {
            // GET_STATE 综合快照是唯一 IO 来源（不再直发 GET_IO）。
            // 未收到过快照时返回 null（与旧 GET_IO 查询失败语义一致，GetIOState 将返回 false）
            return Task.FromResult(CurrentIOState);
        }

        public async Task<bool> GetIOState(Func<IOState, bool> getSignal, int timeoutMs = 200)
        {
            var state = await GetAll_IOState(timeoutMs);
            return state != null && getSignal(state);
        }

        /// <summary>
        /// 获取指定 IO 输出信号的当前状态
        /// </summary>
        public async Task<bool> GetIOState(OutSignal signal, int timeoutMs = 200)
        {
            var state = await GetAll_IOState(timeoutMs);
            if (state == null) return false;
            return signal switch
            {
                OutSignal.Clamp1 => state.OutputClamp1,
                OutSignal.Clamp2 => state.OutputClamp2,
                OutSignal.Block => state.OutputBlock,
                OutSignal.Motor => state.OutputMotor,
                OutSignal.RedLight => state.OutputRedLight,
                OutSignal.GreenLight => state.OutputGreenLight,
                OutSignal.YellowLight => state.OutputYellowLight,
                OutSignal.Buzzer => state.OutputBuzzer,
                OutSignal.Camera3DTrigger => state.OutputCamera3DTrigger,
                _ => false,
            };
        }

        /// <summary>
        /// 获取指定 IO 输入信号的当前状态
        /// </summary>
        public async Task<bool> GetIOState(InputSignal signal, int timeoutMs = 200)
        {
            var state = await GetAll_IOState(timeoutMs);
            if (state == null) return false;
            return signal switch
            {
                InputSignal.WaitSensor => state.InputWait,
                InputSignal.WorkSensor => state.InputWork,
                InputSignal.LiftUpSensor => state.InputLiftUp,
                InputSignal.LiftDownSensor => state.InputLiftDown,
                InputSignal.OutputSensor => state.InputOutput,
                InputSignal.StartBtn => state.InputStart,
                InputSignal.PauseBtn => state.InputPause,
                InputSignal.AirPressure => state.InputAirPressure,
                InputSignal.Emergency => state.InputEmergency,
                InputSignal.RequestMaterial => state.InputRequestMaterial,
                InputSignal.SafetyLight => state.InputSafetyLight,
                InputSignal.DoorSwitch => state.InputDoorSwitch,
                _ => false,
            };
        }

        /// <summary>
        /// 设置指定 IO 输出信号为 ON 或 OFF
        /// </summary>
        public async Task<bool> SetIO(OutSignal signal, bool turnOn, int timeoutMs = 500)
        {
            string prefix = GetIOCommandPrefix(signal);
            string state = turnOn ? "ON" : "OFF";
            string cmd = $"{prefix}_{state}";
            string expected = $"{prefix}_{state}_OK";
            string rs = await SendCommandAndWaitAsync(cmd, expected, timeoutMs);
            return rs.Contains(expected);
        }

        /// <summary>
        /// IOSignal → 指令前缀映射
        /// </summary>
        private static string GetIOCommandPrefix(OutSignal signal) => signal switch
        {
            OutSignal.Clamp1 => "CLAMP1",
            OutSignal.Clamp2 => "CLAMP2",
            OutSignal.Block => "BLOCK",
            OutSignal.Motor => "MOTOR",
            OutSignal.RedLight => "REDLED",
            OutSignal.GreenLight => "GREENLED",
            OutSignal.YellowLight => "YELLOWLED",
            OutSignal.Buzzer => "BEEP",
            OutSignal.Camera3DTrigger => "CAMERA3D",
            _ => throw new ArgumentOutOfRangeException(nameof(signal)),
        };

        // ==================== 传送带 MOTOR 连续运转指令（2026-08-10 授权追加，不动现有逻辑） ====================
        // 指令常量与回复校验、[Send]/[Recv] 日志由本类统一承担（与 MOVE_*/CLAMP 系列同构），
        // 上层状态机（FlowStateMachine 子类）只调用这些公开 API，不重复写协议层。

        /// <summary>传送带正向连续运行：发 MOTOR FWD，等 MOTOR_FWD_OK</summary>
        public async Task<bool> StartMotorForwardAsync(int timeoutMs = 3000)
        {
            string rs = await SendCommandAndWaitAsync("MOTOR_FWD", "MOTOR_FWD_OK", timeoutMs);
            return rs != null && rs.Contains("MOTOR_FWD_OK");
        }

        /// <summary>传送带反向连续运行：发 MOTOR REV，等 MOTOR_REV_OK</summary>
        public async Task<bool> StartMotorReverseAsync(int timeoutMs = 3000)
        {
            string rs = await SendCommandAndWaitAsync("MOTOR_REV", "MOTOR_REV_OK", timeoutMs);
            return rs != null && rs.Contains("MOTOR_REV_OK");
        }

        /// <summary>停止传送带并取消正在运行的 MOVE_* 状态机：发 MOTOR_STOP，等 MOTOR_STOP_OK</summary>
        public async Task<bool> StopMotorAsync(int timeoutMs = 3000)
        {
            string rs = await SendCommandAndWaitAsync("MOTOR_STOP", "MOTOR_STOP_OK", timeoutMs);
            return rs != null && rs.Contains("MOTOR_STOP_OK");
        }

        /// <summary>
        /// 启动后台 GET_STATE 综合状态轮询（唯一状态源：一次回复同时更新 IO / 位置 / 忙闲）
        /// </summary>
        private void StartStatePolling()
        {
            _statePollCts = new CancellationTokenSource();
            _isStatePollPending = false;
            _statePollFailStreak = 0;
            _statePollOkCount = 0;
            _statePollTimeoutCount = 0;
            _statePollRttSumMs = 0;
            Task.Run(() => StatePollLoop(_statePollCts.Token));
        }

        private void StopStatePolling()
        {
            _statePollCts?.Cancel();
            _statePollCts?.Dispose();
            _statePollCts = null;
        }

        private async Task StatePollLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                // 会话未就绪（恢复/重启/失败）时轮询立即退出（保险门，正常情况下 StartStatePolling 仅在 Ready 后调用）
                if (!IsSessionReady) break;

                // 同一时刻最多一条 GET_STATE 在途；收到完整回复后再发下一条
                if (!_isStatePollPending)
                {
                    _isStatePollPending = true;
                    try
                    {
                        var req = await RequestGetState(500);
                        var snapshot = req.Snapshot;
                        if (snapshot != null && !token.IsCancellationRequested)
                        {
                            // 只有整条回复解析成功才提交这一批 IO、位置和忙闲状态（原子更新）
                            snapshot.ReceivedAtUtc = DateTime.UtcNow;
                            snapshot.IsFresh = true;
                            LastStateSnapshot = snapshot;
                            _statePollFailStreak = 0;
                            _statePollOkCount++;
                            _statePollRttSumMs += snapshot.RttMs;

                            CurrentIOState = IOState.ParseFromHex64(snapshot.IoHex);
                            StateSnapshotUpdated?.Invoke(snapshot);
                            IOStateUpdated?.Invoke(CurrentIOState);

                            // 刷新率 = 1000 / 最近 N 次成功快照的平均到达间隔(ms)，反映实际刷新频率
                            _snapshotArrivalTimes.Enqueue(snapshot.ReceivedAtUtc);
                            while (_snapshotArrivalTimes.Count > RefreshRateWindowSize)
                                _snapshotArrivalTimes.Dequeue();
                            if (_snapshotArrivalTimes.Count >= 2)
                            {
                                double spanMs = (_snapshotArrivalTimes.Last() - _snapshotArrivalTimes.First()).TotalMilliseconds;
                                double avgIntervalMs = spanMs / (_snapshotArrivalTimes.Count - 1);
                                CurrentStateRateHz = avgIntervalMs > 0 ? 1000.0 / avgIntervalMs : 0;
                            }

                            // 每 500 轮输出一次实际 RTT / 刷新率统计（刷新率与面板同源：滑动窗口实际到达率）
                            if (_statePollOkCount % 500 == 0)
                            {
                                double avgRtt = _statePollRttSumMs / _statePollOkCount;
                                Log.Info($"GET_STATE 统计: {_statePollOkCount} 成功 / {_statePollTimeoutCount} 超时, 平均RTT {avgRtt:F1}ms, 实际刷新率 {CurrentStateRateHz:F0}Hz");
                            }
                        }
                        else
                        {
                            // 超时/坏帧：保留上一份有效快照并标记 stale，不解释成"位置 0"或"设备空闲"
                            _statePollTimeoutCount++;
                            _statePollFailStreak++;
                            if (LastStateSnapshot != null)
                            {
                                LastStateSnapshot.IsFresh = false;
                                StateSnapshotUpdated?.Invoke(LastStateSnapshot);
                            }
                            // 连续失败时输出限频告警（3 次提示，此后每 300 轮约 10 秒一次），
                            // 附带上位机实际收到的原始回复，用于区分"设备无响应(超时)"和"回复格式错误(解析失败)"
                            if (_statePollFailStreak == 3 || _statePollFailStreak % 300 == 0)
                            {
                                string rawBrief = req.RawResponse?.Replace("\r", "").Replace("\n", " ").Trim();
                                if (rawBrief != null && rawBrief.Length > 120) rawBrief = rawBrief.Substring(0, 120);
                                string detail = string.IsNullOrEmpty(rawBrief) ? "(设备无回复/超时)" : $"最近回复: {rawBrief}";
                                Log.Warning($"GET_STATE 已连续 {_statePollFailStreak} 次无有效回复，状态标记为过期（stale）；{detail}");
                            }
                            // 自愈：持续失败（约每 60 轮 ≈ 2 秒）清空接收缓冲一次——
                            // 若解析持续失败是因为残留片段污染，清缓冲后可恢复；不影响命令队列
                            if (_statePollFailStreak % 60 == 0)
                            {
                                lock (_receiveLock)
                                {
                                    _receiveBuffer.Clear();
                                }
                                try { _serialPort?.DiscardInBuffer(); } catch { }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"GET_STATE 轮询异常: {ex.Message}");
                    }
                    finally
                    {
                        _isStatePollPending = false;
                    }
                }
                // 轮询周期 = 单次 RTT + 间隔。间隔直接采用系统参数（下限 1ms 仅防 0/负数），
                // 且 Open 时已 timeBeginPeriod(1) 提升系统定时器精度，Task.Delay(1) 实际 ≈1ms；
                // 实际刷新率 = 1000 / (RTT + interval)，受设备 RTT 上限约束
                int interval = (int)Math.Max(SysParam.Instance.Data.IoPollInterval, 1);
                await Task.Delay(interval, token);
            }
        }

        /// <summary>
        /// 发送一次 GET_STATE 并等待完整回复（经命令队列串行执行，不绕过队列直写串口）
        /// </summary>
        private async Task<GetStateResult> RequestGetState(int timeoutMs)
        {
            var result = new GetStateResult();
            var sw = Stopwatch.StartNew();
            string response = await EnqueueCommand("GET_STATE", "STATE:", timeoutMs, completeLineOnly: true);
            sw.Stop();
            result.RawResponse = response;
            if (response == "TIMEOUT" || string.IsNullOrEmpty(response)) return result;
            if (!TryParseGetState(response, out var snapshot)) return result;
            snapshot.RttMs = sw.ElapsedMilliseconds;
            result.Snapshot = snapshot;
            return result;
        }

        /// <summary>GET_STATE 请求结果：Snapshot 为 null 表示超时或解析失败（RawResponse 为原始回复，用于诊断）</summary>
        private class GetStateResult
        {
            public StateSnapshot Snapshot;
            public string RawResponse;
        }

        /// <summary>
        /// 解析 GET_STATE 完整回复：IO 按字段名取十六进制，POS/STATUS 按 X/Y/Z/H 槽位查找，
        /// IO + 4 个位置 + 4 个忙闲共 9 项全部成功才算解析成功（不允许部分提交）
        /// </summary>
        private static bool TryParseGetState(string response, out StateSnapshot snapshot)
        {
            snapshot = null;
            var m = StateRegex.Match(response ?? "");
            if (!m.Success) return false;

            // IO：1~16 位十六进制，可带 0x，不依赖固定宽度（F401/F103 直连打印 8 位，F407 网关打印 16 位）
            string ioHex = m.Groups["io"].Value.Trim();
            string cleanHex = ioHex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? ioHex.Substring(2) : ioHex;
            if (!ulong.TryParse(cleanHex, System.Globalization.NumberStyles.HexNumber, null, out ulong ioValue))
                return false;

            if (!TryParseAxisLong(m.Groups["pos"].Value, 'X', out long x)) return false;
            if (!TryParseAxisLong(m.Groups["pos"].Value, 'Y', out long y)) return false;
            if (!TryParseAxisLong(m.Groups["pos"].Value, 'Z', out long z)) return false;
            if (!TryParseAxisLong(m.Groups["pos"].Value, 'H', out long h)) return false;

            if (!TryParseAxisBool(m.Groups["status"].Value, 'X', out bool xBusy)) return false;
            if (!TryParseAxisBool(m.Groups["status"].Value, 'Y', out bool yBusy)) return false;
            if (!TryParseAxisBool(m.Groups["status"].Value, 'Z', out bool zBusy)) return false;
            if (!TryParseAxisBool(m.Groups["status"].Value, 'H', out bool hBusy)) return false;

            snapshot = new StateSnapshot
            {
                Io = ioValue,
                IoHex = ioHex,
                X = x, Y = y, Z = z, H = h,
                XBusy = xBusy, YBusy = yBusy, ZBusy = zBusy, HBusy = hBusy
            };
            return true;
        }

        /// <summary>按轴名（X/Y/Z/H 字段名）查找 POS 段中的有符号整数，不依赖"第几个值"的位置假设</summary>
        private static bool TryParseAxisLong(string text, char axis, out long value)
        {
            value = 0;
            foreach (var part in text.Split(','))
            {
                var t = part.Trim();
                if (t.Length >= 2 && t[0] == axis)
                    return long.TryParse(t.Substring(1), out value);
            }
            return false;
        }

        /// <summary>按轴名查找 STATUS 段：0 = IDLE，任意非 0 值 = 忙（设备实测忙时输出 X10000 等内部 mode 状态码，不一定是 1）</summary>
        private static bool TryParseAxisBool(string text, char axis, out bool value)
        {
            value = false;
            foreach (var part in text.Split(','))
            {
                var t = part.Trim();
                if (t.Length >= 2 && t[0] == axis)
                {
                    if (long.TryParse(t.Substring(1), out long v))
                    {
                        value = v != 0;
                        return true;
                    }
                    return false; // 非数字值，解析失败（保留旧快照，不猜测）
                }
            }
            return false;
        }

        #endregion

        #region ==================== 高级指令封装 ====================

        public async Task<CommandResult> HomeXY(int timeoutMs = 10000)
        {
            // 发送归零指令
            await SendCommandAsync("G28 XY");

            // 并行等待 X 和 Y 轴完成
            var xTask = WaitAxisComplete("X", timeoutMs);
            var yTask = WaitAxisComplete("Y", timeoutMs);

            await Task.WhenAll(xTask, yTask);

            if (xTask.Result && yTask.Result)
            {
                _axisHomed[HOMED_X] = _axisHomed[HOMED_Y] = true;
                return CommandResult.Ok("回零完成");
            }

            return CommandResult.Timeout("归零超时");
        }

        public async Task<CommandResult> HomeXYZ(int timeoutMs = 10000)
        {
            // 发送归零指令
            await SendCommandAsync("G28 XYZ");

            // 并行等待 X、Y、Z 轴完成
            var xTask = WaitAxisComplete("X", timeoutMs);
            var yTask = WaitAxisComplete("Y", timeoutMs);
            var zTask = WaitAxisComplete("Z", timeoutMs);

            await Task.WhenAll(xTask, yTask, zTask);

            if (xTask.Result && yTask.Result && zTask.Result)
            {
                _axisHomed[HOMED_X] = _axisHomed[HOMED_Y] = _axisHomed[HOMED_Z] = true;
                return CommandResult.Ok("XYZ回零完成");
            }

            return CommandResult.Timeout("XYZ归零超时");
        }

        public async Task<CommandResult> HomeXYZH(int timeoutMs = 10000)
        {
            // H回零前要顶板下降
            await SetIO(OutSignal.Clamp1, false);
            await SetIO(OutSignal.Clamp2, false);

            // 先挂各轴等待handler再发指令，避免下位机立即回复时handler未挂载导致完成信号丢失
            var xTask = WaitAxisComplete("X", timeoutMs);
            var yTask = WaitAxisComplete("Y", timeoutMs);
            var hTask = WaitAxisComplete("H", timeoutMs);
            var zTask = WaitAxisComplete("Z", timeoutMs);

            await SendCommandAsync("G28 XYZH");

            // 并行等待 X/Y/Z/H 轴完成
            await Task.WhenAll(xTask, yTask, hTask, zTask);

            if (xTask.Result && yTask.Result && hTask.Result && zTask.Result)
            {
                _axisHomed[HOMED_X] = _axisHomed[HOMED_Y] = _axisHomed[HOMED_Z] = _axisHomed[HOMED_H] = true;
                return CommandResult.Ok("回零完成");
            }

            return CommandResult.Timeout("归零超时");
        }

        /// <summary>
        /// 单轴回零通用方法：发送 G28 {axis} 并等待轴完成信号
        /// </summary>
        public async Task<bool> Home(string axis, int timeoutMs = 10000)
        {
            if (axis.Contains("H"))
            {
                await SetIO(OutSignal.Clamp1, false);
                await SetIO(OutSignal.Clamp2, false);
            }
            // 先挂等待handler再发指令，避免下位机立即回复时handler未挂载导致完成信号丢失
            var waitTask = WaitAxisComplete(axis, timeoutMs);
            await SendCommandAsync($"G28 {axis}");
            return await waitTask;
        }

        public async Task ResetIO()
        {
            await SetIO(OutSignal.Clamp1, false);
            await Task.Delay(50);
            await SetIO(OutSignal.Clamp2, false);
            await Task.Delay(50);
            await SetIO(OutSignal.Block, false);
            await Task.Delay(50);
        }

        public async Task<CommandResult> MoveTo(double? x = null, double? y = null, double? z = null, double? h = null,
                                                int timeoutMs = 10000)
        {
            return await MoveToAndWaitOK(x, y, z, h, timeoutMs)
                ? CommandResult.Ok("移动完成")
                : CommandResult.Timeout("移动超时");
        }



        #endregion

        #region ==================== 数据接收与解析 ====================

        private void OnSerialDataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            try
            {
                string data = _serialPort.ReadExisting();
                lock (_receiveLock)
                {
                    _rxCount += data.Length;
                    _receiveBuffer.Append(data);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"接收数据错误: {ex.Message}");
            }
        }

        private async Task ParseDataTask(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                string dataToProcess = null;
                lock (_receiveLock)
                {
                    if (_receiveBuffer.Length > 0)
                    {
                        string raw = _receiveBuffer.ToString();
                        // 只广播完整行：最后一个未以换行结尾的片段留在缓冲区等待下一批数据拼接，
                        // 防止 GET_STATE 等长回复被 ReadExisting 拆成两半后，后半段丢失导致解析失败
                        int lastNewline = raw.LastIndexOf('\n');
                        if (lastNewline >= 0)
                        {
                            dataToProcess = raw.Substring(0, lastNewline + 1);
                            _receiveBuffer.Clear();
                            _receiveBuffer.Append(raw.Substring(lastNewline + 1));
                        }
                        else if (raw.Length > 1024)
                        {
                            // 异常保护：长时间无换行且缓冲过大，强制取出防止无限膨胀
                            dataToProcess = raw;
                            _receiveBuffer.Clear();
                        }
                    }
                }
                if (!string.IsNullOrEmpty(dataToProcess))
                {
                    // 日志按行拆分：GET_STATE 轮询回复(STATE: 行)频繁且量大，隐藏；其他行(完成信号/错误等)正常打印。
                    // 避免 H_HOME_DONE 等关键回复与 STATE: 行拼接在同一批次时被整体过滤，导致"看起来没回复"。
                    foreach (var line in dataToProcess.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (!line.Contains("IO:") && !line.Contains("STATUS:") && !line.Contains("STATE:"))
                            Log.Debug($"\r\n [Recv] {line}");
                    }

                    // F407 会话保护：设备重启后会话回到 UNLOCKED，普通请求均返回 SESSION_REQUIRED。
                    // 仅在 Ready 且无恢复任务在跑时触发一次有界恢复（Interlocked 门防风暴），
                    // 恢复失败保持 Failed 且轮询已停，不会每轮重复触发新恢复任务
                    if (dataToProcess.Contains("SESSION_REQUIRED") &&
                        _sessionState == MotionSessionState.Ready &&
                        Interlocked.CompareExchange(ref _recovering, 1, 0) == 0)
                    {
                        Log.Warning("收到 SESSION_REQUIRED，触发有界会话恢复");
                        _ = Task.Run(() => RecoverSessionAsync());
                    }

                    DataReceived?.Invoke(dataToProcess);
                }

                // 5ms 广播周期：降低回复匹配的等待延迟（影响 GET_STATE 实测 RTT）
                await Task.Delay(5, token);
            }
        }

        #endregion

        #region ==================== 速度控制 ====================

        /// <summary>
        /// 设置 PTP 速度（系统参数全局速度），发送 SPEED X...,Y... 指令
        /// </summary>
        public async Task<bool> SetSpeedPtp(double mmPerSec)
        {
            int pulseSpeed = (int)MmToPulse(mmPerSec, "X");
            return await SendCommandAsync($"SPEED X{pulseSpeed},Y{pulseSpeed},Z{pulseSpeed},H{pulseSpeed}");
        }

        /// <summary>
        /// 设置 JOG 速度（IO 面板手动调试），发送 SPEED JOG X...,Y... 指令
        /// </summary>
        public async Task<bool> SetSpeedJog(double mmPerSec)
        {
            int pulseSpeed = (int)MmToPulse(mmPerSec, "X");
            return await SendCommandAsync($"SPEED JOG X{pulseSpeed},Y{pulseSpeed},Z{pulseSpeed},H{pulseSpeed}");
        }

        /// <summary>
        /// 设置 3D 扫描速度，发送 SPEED SCAN3D X... 指令
        /// </summary>
        public async Task<bool> SetScan3DSpeed(double mmPerSec)
        {
            int pulseSpeed = (int)MmToPulse(mmPerSec, "X");
            return await SendCommandAsync($"SPEED SCAN3D X{pulseSpeed},Y{pulseSpeed}");
        }


        /// <summary>
        /// 查询当前 PTP 速度，返回脉冲数
        /// </summary>
        public async Task<int> GetPtpSpeed()
        {
            string response = await SendCommandAndWaitAsync("GET_SPEED", "SPEED", 2000);
            if (string.IsNullOrEmpty(response) || response == "TIMEOUT")
                return -1;

            try
            {
                var match = System.Text.RegularExpressions.Regex.Match(response, @"PTP X(\d+)");
                if (match.Success && int.TryParse(match.Groups[1].Value, out int pulseSpeed))
                    return pulseSpeed;
            }
            catch { }

            return -1;
        }

        /// <summary>
        /// 查询当前 JOG 速度，返回脉冲数
        /// </summary>
        public async Task<int> GetJogSpeed()
        {
            string response = await SendCommandAndWaitAsync("GET_SPEED", "SPEED", 2000);
            if (string.IsNullOrEmpty(response) || response == "TIMEOUT")
                return -1;

            try
            {
                var match = System.Text.RegularExpressions.Regex.Match(response, @"JOG X(\d+)");
                if (match.Success && int.TryParse(match.Groups[1].Value, out int pulseSpeed))
                    return pulseSpeed;
            }
            catch { }

            return -1;
        }

        public async Task<int> GetScan3DSpeed()
        {
            string response = await SendCommandAndWaitAsync("GET_SPEED", "SPEED", 2000);
            if (string.IsNullOrEmpty(response) || response == "TIMEOUT")
                return -1;

            try
            {
                var match = System.Text.RegularExpressions.Regex.Match(response, @"SCAN3D X(\d+)");
                if (match.Success && int.TryParse(match.Groups[1].Value, out int pulseSpeed))
                    return pulseSpeed;
            }
            catch { }

            return -1;
        }
        #endregion

        #region ==================== 辅助方法 ====================

        private double GetPulsePerMm(string axis)
        {
            var data = SysParam.Instance.Data;
            switch (axis)
            {
                case "X": return data.PulseEquivalentX;
                case "Y": return data.PulseEquivalentY;
                case "Z": return data.PulseEquivalentZ;
                case "H": return data.PulseEquivalentH;
                default: return data.PulseEquivalentX;
            }
        }

        public long MmToPulse(double mm, string axis) => (long)Math.Round(mm * GetPulsePerMm(axis));
        public double PulseToMm(long pulses, string axis) => pulses / GetPulsePerMm(axis);

        #endregion

        #region ==================== IDisposable ====================

        private bool _disposed = false;

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    StopQueueProcessor();
                    StopStatePolling();
                    _cts?.Cancel();
                    _cts?.Dispose();
                    _queueCts?.Dispose();
                    _moveLock?.Dispose();

                    // 恢复系统定时器分辨率
                    try
                    {
                        if (_timerPeriodSet)
                        {
                            timeEndPeriod(1);
                            _timerPeriodSet = false;
                        }
                    }
                    catch { }

                    if (_serialPort != null)
                    {
                        _serialPort.DataReceived -= OnSerialDataReceived;
                        if (_serialPort.IsOpen) _serialPort.Close();
                        _serialPort.Dispose();
                    }
                }
                _disposed = true;
            }
        }

        ~MotionIO()
        {
            Dispose(false);
        }

        #endregion
    }

    #region ==================== 辅助类 ====================

    public class CommandResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public string RawResponse { get; set; }
        public bool IsTimeout { get; set; }

        public static CommandResult Ok(string message = null, string raw = null)
            => new CommandResult { Success = true, Message = message, RawResponse = raw };

        public static CommandResult Fail(string message = null, string raw = null)
            => new CommandResult { Success = false, Message = message, RawResponse = raw };

        public static CommandResult Timeout(string message = "操作超时")
            => new CommandResult { Success = false, Message = message, IsTimeout = true };

        public static implicit operator bool(CommandResult result) => result.Success;
        public static implicit operator CommandResult(bool success) => success ? Ok() : Fail();
    }

    public class PositionInfo
    {
        private static double GetPulsePerMm(string axis)
        {
            var data = SysParam.Instance.Data;
            switch (axis)
            {
                case "X": return data.PulseEquivalentX;
                case "Y": return data.PulseEquivalentY;
                case "Z": return data.PulseEquivalentZ;
                case "H": return data.PulseEquivalentH;
                default: return data.PulseEquivalentX;
            }
        }

        public long X { get; set; }
        public long Y { get; set; }
        public long Z { get; set; }
        public long H { get; set; }

        public double Xmm => X / GetPulsePerMm("X");
        public double Ymm => Y / GetPulsePerMm("Y");
        public double Zmm => Z / GetPulsePerMm("Z");
        public double Hmm => H / GetPulsePerMm("H");
    }

    /// <summary>
    /// GET_STATE 综合状态快照（一次回复同时携带 IO 位图、X/Y/Z/H 位置和忙闲状态）
    /// </summary>
    public sealed class StateSnapshot
    {
        /// <summary>IO 位图（无符号 64 位十六进制解析结果）</summary>
        public ulong Io { get; set; }

        /// <summary>IO 位图原始十六进制字符串（如 0x0000080701040D80，可直接传给 IOState.ParseFromHex64）</summary>
        public string IoHex { get; set; }

        /// <summary>X/Y/Z/H 位置脉冲值（不是毫米）</summary>
        public long X { get; set; }
        public long Y { get; set; }
        public long Z { get; set; }
        public long H { get; set; }

        /// <summary>轴忙闲：true=运动中（mode 非 IDLE），false=IDLE</summary>
        public bool XBusy { get; set; }
        public bool YBusy { get; set; }
        public bool ZBusy { get; set; }
        public bool HBusy { get; set; }

        /// <summary>快照接收时间（UTC）</summary>
        public DateTime ReceivedAtUtc { get; set; }

        /// <summary>true=本轮 GET_STATE 解析成功；false=超时/坏帧后保留的过期快照，值不可信</summary>
        public bool IsFresh { get; set; }

        /// <summary>本轮 GET_STATE 往返耗时（ms），用于统计刷新率</summary>
        public long RttMs { get; set; }
    }

    #endregion
}