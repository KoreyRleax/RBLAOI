using RBLAOI.Core.Utility;
using System;
using System.Diagnostics;
using System.IO.Ports;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RBLAOI.Core.LightSource
{
    /// <summary>单通道状态：亮度 0-255，On=true 亮 / false 灭</summary>
    public sealed class LightChannelState
    {
        public int Brightness { get; set; }
        public bool On { get; set; }

        public LightChannelState() { }

        public LightChannelState(int brightness, bool on)
        {
            Brightness = brightness;
            On = on;
        }
    }

    public static class LightSourceProtocol
    {
        public const int BaudRate = 19200;

        /// <summary>多通道完整帧：S + 每通道3位亮度+T/F + C#</summary>
        public static string BuildMultiChannel(LightChannelState[] channels)
        {
            if (channels == null || channels.Length < 1 || channels.Length > 8)
                throw new ArgumentOutOfRangeException(nameof(channels), "通道数必须为 1-8");

            var builder = new StringBuilder("S");
            foreach (var channel in channels)
            {
                if (channel.Brightness < 0 || channel.Brightness > 255)
                    throw new ArgumentOutOfRangeException(nameof(channels), "亮度必须为 0-255");
                builder.Append(channel.Brightness.ToString("000"));
                builder.Append(channel.On ? 'T' : 'F');
            }
            builder.Append('C');
            builder.Append('#');
            return builder.ToString();
        }

        /// <summary>单通道纯亮度：SA0125#（不能控制亮灭）</summary>
        public static string BuildSingleChannelBrightness(int channelIndex, int brightness)
        {
            if (channelIndex < 0 || channelIndex > 7)
                throw new ArgumentOutOfRangeException(nameof(channelIndex), "光源通道为 1-8");
            if (brightness < 0 || brightness > 255)
                throw new ArgumentOutOfRangeException(nameof(brightness), "亮度必须为 0-255");
            return $"S{(char)('A' + channelIndex)}{brightness.ToString("0000")}#";
        }

        /// <summary>单通道亮灭+亮度：用多通道帧只发一路（如 S128TC# / S000FC#）</summary>
        public static string BuildSingleChannelState(int channelIndex, LightChannelState state)
        {
            if (channelIndex < 0 || channelIndex > 7)
                throw new ArgumentOutOfRangeException(nameof(channelIndex), "光源通道为 1-8");
            if (state.Brightness < 0 || state.Brightness > 255)
                throw new ArgumentOutOfRangeException(nameof(state), "亮度必须为 0-255");
            return $"S{state.Brightness.ToString("000")}{(state.On ? 'T' : 'F')}C#";
        }

        /// <summary>回复校验：多通道返回 !，单通道返回 A-H</summary>
        public static bool IsReplyOk(string reply)
        {
            if (string.IsNullOrWhiteSpace(reply)) return false;
            reply = reply.Trim();
            if (reply == "!") return true;
            return reply.Length == 1 && reply[0] >= 'A' && reply[0] <= 'H';
        }
    }

    /// <summary>
    /// 独立光源串口服务：与 MotionIO / 330 串口完全隔离。
    /// 控制器回复为单字符，收到即完整；超时返回空串。
    /// </summary>
    public sealed class LightSourceController : IDisposable
    {
        public static LightSourceController Instance { get; } = new LightSourceController();

        private readonly SerialPort _port = new SerialPort();
        private readonly object _lock = new object();
        private LightChannelState[] _channels = new LightChannelState[4];
        private bool _disposed;

        public bool IsOpen => _port.IsOpen;
        public string PortName => _port.PortName;

        private LightSourceController()
        {
            _port.BaudRate = LightSourceProtocol.BaudRate;
            _port.DataBits = 8;
            _port.StopBits = StopBits.One;
            _port.Parity = Parity.None;
            _port.Handshake = Handshake.None;
            _port.ReadTimeout = 200;
            _port.WriteTimeout = 500;
            _port.Encoding = Encoding.ASCII;
            SetChannelCount(4);
        }

        /// <summary>设置控制器实际通道数（2/4/6/8），并初始化每路为 128 亮</summary>
        public void SetChannelCount(int count)
        {
            if (count < 1 || count > 8)
                throw new ArgumentOutOfRangeException(nameof(count), "光源通道数为 1-8");
            _channels = new LightChannelState[count];
            for (var i = 0; i < count; i++)
                _channels[i] = new LightChannelState(128, false);
        }

        public bool Open(string portName, int baudRate = LightSourceProtocol.BaudRate)
        {
            if (_port.IsOpen) Close();
            try
            {
                _port.PortName = portName;
                _port.BaudRate = baudRate;
                _port.Open();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public void Close()
        {
            if (_port.IsOpen)
                _port.Close();
        }

        /// <summary>发送 ASCII 指令并等待单字符回复；无回复返回空串（收发均记录日志）</summary>
        public string SendCommand(string command, int timeoutMs = 500)
        {
            lock (_lock)
            {
                if (!_port.IsOpen)
                    throw new InvalidOperationException("光源串口未连接");

                _port.DiscardInBuffer();
                _port.Write(command);
                Log.Info($"[SendLight] {command}", "LightSource");

                var stopwatch = Stopwatch.StartNew();
                var response = new StringBuilder();
                while (stopwatch.ElapsedMilliseconds < timeoutMs)
                {
                    var chunk = _port.ReadExisting();
                    if (!string.IsNullOrEmpty(chunk))
                    {
                        response.Append(chunk);
                        break; // ZVD 回复为单字符，收到即完整
                    }
                    Thread.Sleep(10);
                }
                var reply = response.ToString().Trim();
                Log.Info($"[RecvLight] {(string.IsNullOrEmpty(reply) ? "(无回复)" : reply)}", "LightSource");
                return reply;
            }
        }

        public Task<string> SendCommandAsync(string command, int timeoutMs = 500)
            => Task.Run(() => SendCommand(command, timeoutMs));

        /// <summary>
        /// 设置单通道（实测协议：单通道指令 S{ch}{4位亮度}#，回复对应通道字母 A-H）。
        /// 实测 SA0000# 有效（亮度 0 即灭），亮灭由亮度值控制，无 T/F 位。
        /// </summary>
        public bool SetChannel(int channelIndex, int brightness, bool on)
        {
            if (channelIndex < 0 || channelIndex >= _channels.Length)
                return false;
            _channels[channelIndex] = new LightChannelState(brightness, on);
            int value = on ? brightness : 0; // 灭 = 亮度 0
            var reply = SendCommand(LightSourceProtocol.BuildSingleChannelBrightness(channelIndex, value));
            return LightSourceProtocol.IsReplyOk(reply);
        }

        /// <summary>一次设置全部通道（逐通道单指令下发）</summary>
        public bool SetAll(int[] brightness, bool[] on)
        {
            if (brightness == null || on == null || brightness.Length != _channels.Length || on.Length != _channels.Length)
                return false;
            for (var i = 0; i < _channels.Length; i++)
                _channels[i] = new LightChannelState(brightness[i], on[i]);
            var allOk = true;
            for (var i = 0; i < _channels.Length; i++)
                allOk &= SetChannel(i, _channels[i].Brightness, _channels[i].On);
            return allOk;
        }

        /// <summary>一次设置全部通道（传入完整状态数组，逐通道单指令下发）</summary>
        public bool SetAll(LightChannelState[] states)
        {
            if (states == null || states.Length != _channels.Length)
                return false;
            for (var i = 0; i < _channels.Length; i++)
                _channels[i] = new LightChannelState(states[i].Brightness, states[i].On);
            var allOk = true;
            for (var i = 0; i < _channels.Length; i++)
                allOk &= SetChannel(i, _channels[i].Brightness, _channels[i].On);
            return allOk;
        }

        /// <summary>全灭（亮度 0 下发）</summary>
        public bool AllOff()
        {
            var allOk = true;
            for (var i = 0; i < _channels.Length; i++)
            {
                _channels[i].On = false;
                allOk &= SetChannel(i, 0, false);
            }
            return allOk;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Close();
            _port.Dispose();
        }
    }
}
