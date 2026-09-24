using RBLAOI.Core.Utility;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RBLAOI.Core.Vision
{
    /// <summary>
    /// TCP客户端 - 连接视觉软件，发送指令，接收结果
    /// </summary>
    public class Vision3DClient
    {
        private static Vision3DClient _instance;
        private static readonly object _lock = new object();

        private TcpClient _tcpClient;
        private NetworkStream _stream;
        private byte[] _buffer = new byte[4096];
        private bool _isConnected = false;
        private readonly object _sendLock = new object();

        // 事件
        public event Action<string> OnResultReceived;      // 收到视觉软件主动返回的结果
        public event Action<bool> OnConnectionChanged;     // 连接状态变化
        public event Action<string> OnConnectionError;               // 连接错误信息:连接失败的时候弹出失败原因
        public event Action<string> OnRuntimeError;               // 运行时错误


        public static Vision3DClient Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                        {
                            _instance = new Vision3DClient();
                        }
                    }
                }
                return _instance;
            }
        }

        public bool IsConnected => _isConnected;

        /// <summary>
        /// 连接视觉软件
        /// </summary>
        public async Task<bool> ConnectAsync(string ip, int port)
        {
            try
            {
                // 先清理旧连接，确保 TCP 资源完全释放
                if (_isConnected || _tcpClient != null)
                {
                    _stream?.Close();
                    _tcpClient?.Close();
                    _tcpClient = null;
                    _stream = null;
                    _isConnected = false;
                }

                _tcpClient = new TcpClient();
                await _tcpClient.ConnectAsync(ip, port);
                _stream = _tcpClient.GetStream();
                _isConnected = true;

                // 开始异步接收数据
                _ = Task.Run(ReceiveData);

                OnConnectionChanged?.Invoke(true);
                Log.Error($"3D相机连接成功: {ip}:{port}");
                return true;
            }
            catch (Exception ex)
            {
                _isConnected = false;
                OnConnectionError?.Invoke($"3D相机连接失败: {ex.Message}");
                Log.Error($"3D相机连接失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 接收数据线程
        /// </summary>
        private async Task ReceiveData()
        {
            while (_isConnected && _tcpClient.Connected)
            {
                try
                {
                    int received = await _stream.ReadAsync(_buffer, 0, _buffer.Length);
                    if (received > 0)
                    {
                        string result = Encoding.Default.GetString(_buffer, 0, received);
                        Log.Debug($"[Recv3d] {result}");
                        OnResultReceived?.Invoke(result);
                    }
                    else
                    {
                        // 连接已断开
                        Disconnect();
                        break;
                    }
                }
                catch
                {
                    Disconnect();
                    break;
                }
            }
        }

        /// <summary>
        /// 发送指令：直接发送，不等待结果
        /// </summary>
        public bool SendCommand(string command)
        {
            if (!_isConnected || _stream == null)
            {
                Log.Error("3D相机未连接");
                return false;
            }

            try
            {
                string line = command.TrimEnd('\r', '\n') + "\r\n";
                byte[] data = Encoding.Default.GetBytes(line);
                lock (_sendLock)
                {
                    _stream.Write(data, 0, data.Length);
                }
                Log.Debug($"[Send3d] {line.TrimEnd()}");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"3D相机发送失败: {ex.Message}");
                return false;
            }
        }
        /// <summary>
        /// 发送原始指令（异步，不等待响应）
        /// </summary>
        /// <param name="cmd">指令内容，不含回车换行</param>
        public async Task<bool> SendCommandAsync(string cmd)
        {
            return await Task.Run(() => SendCommand(cmd));
        }
        /// <summary>
        /// 发送指令并等待结果
        /// </summary>
        public async Task<string> SendCommandAndWaitAsync(string command, int timeoutMs = 3000)
        {
            string result = null;
            var tcs = new TaskCompletionSource<string>();

            void handler(string res)
            {
                result = res;
                tcs.TrySetResult(res);
            }

            OnResultReceived += handler;

            try
            {
                await SendCommandAsync(command).ConfigureAwait(false);  // 关键！

                // 等待结果或超时
                if (await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs)) == tcs.Task)
                {
                    return result;
                }
                else
                {
                    OnRuntimeError?.Invoke($"超时: {command}");
                    return "TIMEOUT";
                }
            }
            finally
            {
                OnResultReceived -= handler;
            }
        }


        /// <summary>
        /// 发送指令并等待期望的响应
        /// </summary>
        /// <param name="command">要发送的指令</param>
        /// <param name="expectedResponse">期望的响应内容</param>
        /// <param name="timeoutMs">超时时间（毫秒）</param>
        /// <returns>匹配期望的响应，超时返回 "TIMEOUT"</returns>
        public async Task<string> SendCommandAndWaitAsync(string command, string expectedResponse, int timeoutMs = 3000)
        {
            string result = null;
            var tcs = new TaskCompletionSource<string>();

            void handler(string res)
            {
                // 只有包含期望响应才触发
                if (res.Contains(expectedResponse))
                {
                    result = res;
                    tcs.TrySetResult(res);
                }
            }

            OnResultReceived += handler;

            try
            {
                await SendCommandAsync(command).ConfigureAwait(false);

                if (await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs)) == tcs.Task)
                {
                    return result;
                }
                else
                {
                    OnRuntimeError?.Invoke($"超时: {command}");
                    return "TIMEOUT";
                }
            }
            finally
            {
                OnResultReceived -= handler;
            }
        }

        /// <summary>判断 res 中是否存在以 expected 开头的行（TCP 收包可能一次合并多行，须按行判断）</summary>
        private static bool AnyLineStartsWith(string res, string expected)
        {
            foreach (var line in res.Split('\n'))
            {
                if (line.TrimEnd('\r').StartsWith(expected)) return true;
            }
            return false;
        }

        /// <summary>
        /// 等待指定前缀的主动推送数据（不发送指令，仅监听）
        /// 用于硬触发模式下 3D VM 自动推送结果
        /// </summary>
        /// <param name="prefix">期望的数据前缀，如 "DETECT3D_OK:"</param>
        /// <param name="timeoutMs">超时毫秒</param>
        /// <returns>匹配前缀的完整数据，超时返回 "TIMEOUT"</returns>
        public async Task<string> WaitForPrefixAsync(string prefix, int timeoutMs = 3000)
        {
            string result = null;
            var tcs = new TaskCompletionSource<string>();

            void handler(string res)
            {
                // 前缀匹配按行行首判断（避免数据内容含前缀子串被误判）
                if (AnyLineStartsWith(res, prefix))
                {
                    result = res;
                    tcs.TrySetResult(res);
                }
            }

            OnResultReceived += handler;

            try
            {
                if (await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs)) == tcs.Task)
                {
                    return result;
                }
                else
                {
                    OnRuntimeError?.Invoke($"等待前缀超时: {prefix}");
                    return "TIMEOUT";
                }
            }
            finally
            {
                OnResultReceived -= handler;
            }
        }

        /// <summary>
        /// 等待指定前缀的主动推送数据，跳过空数据（仅含前缀无实际内容）
        /// VM有时会先推空数据再推真实数据，此方法保持监听直到收到有效数据或超时
        /// </summary>
        /// <param name="prefix">期望的数据前缀，如 "DETECT3D_OK:"</param>
        /// <param name="timeoutMs">超时毫秒</param>
        /// <param name="token">取消令牌（复位/停止/报警时中断等待，取消抛 OperationCanceledException 由调用方状态机统一处理；默认不取消=原行为）</param>
        /// <returns>匹配前缀的完整数据（非空），超时返回 "TIMEOUT"</returns>
        public async Task<string> WaitForPrefixNonEmptyAsync(string prefix, int timeoutMs = 3000, CancellationToken token = default)
        {
            var tcs = new TaskCompletionSource<string>();

            void handler(string res)
            {
                // 前缀匹配按行行首判断（避免数据内容含前缀子串被误判）
                if (AnyLineStartsWith(res, prefix))
                {
                    // 提取前缀后的内容，如果非空才视为有效数据
                    int idx = res.IndexOf(prefix);
                    string dataPart = res.Substring(idx + prefix.Length).Trim();
                    if (!string.IsNullOrEmpty(dataPart) && dataPart.Any(c => c != '|'))
                    {
                        tcs.TrySetResult(res);
                    }
                }
            }

            OnResultReceived += handler;

            try
            {
                // 三路等待：响应 / 超时 / 取消（取消时 Task.Delay(Timeout.Infinite, token) 先完成 → 走取消分支抛 OCE）
                var waits = new List<Task> { tcs.Task, Task.Delay(timeoutMs) };
                if (token != default) waits.Add(Task.Delay(Timeout.InfiniteTimeSpan, token));
                if (await Task.WhenAny(waits) == tcs.Task)
                {
                    return await tcs.Task;
                }
                token.ThrowIfCancellationRequested();   // 取消：抛 OCE（调用方状态机统一 catch 转 Terminated，不按超时弹窗）
                OnRuntimeError?.Invoke($"等待非空前缀超时: {prefix}");
                return "TIMEOUT";
            }
            finally
            {
                OnResultReceived -= handler;
            }
        }

        /// <summary>
        /// 断开连接（发送TCP RST，确保服务器立即释放资源）
        /// </summary>
        public void Disconnect()
        {
            _isConnected = false;
            // LingerState=true,0 使TCP发送RST而非FIN，服务器立即知道连接断开
            if (_tcpClient != null)
            {
                try { _tcpClient.LingerState = new LingerOption(true, 0); } catch { }
                try { _tcpClient.Close(); } catch { }
                _tcpClient = null;
            }
            if (_stream != null)
            {
                try { _stream.Close(); } catch { }
                _stream = null;
            }
            OnConnectionChanged?.Invoke(false);
            Log.Info("3D相机已断开连接");
        }
    }
}
