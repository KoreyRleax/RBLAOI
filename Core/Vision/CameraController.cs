using MvCameraControl;
using RBLAOI.Core.Utility;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RBLAOI.Core.Vision
{
    /// <summary>
    /// 海康 MVS 相机控制器 — 基于 MvCameraControl.Net SDK (v4.x .NET Standard) 的相机管理服务
    /// 提供：枚举设备、打开/关闭相机、开始/停止采集、清除缓存、复位相机等功能
    ///
    /// SDK API 参考：
    ///   DeviceEnumerator.EnumDevices() → List&lt;IDeviceInfo&gt;
    ///   DeviceFactory.CreateDevice(info) → IDevice
    ///   IDevice.Open() / .Close()
    ///   IDevice.StreamGrabber.StartGrabbing() / .StopGrabbing() / .ClearImageBuffer()
    ///   IDevice.Parameters.SetEnumValue() / .SetFloatValue()
    /// </summary>
    public class CameraController : IDisposable
    {
        #region ==================== 单例模式 ====================

        private static CameraController _instance;
        private static readonly object _lock = new object();

        public static CameraController Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        _instance ??= new CameraController();
                    }
                }
                return _instance;
            }
        }

        public static void Cleanup()
        {
            if (_instance != null)
            {
                _instance.Dispose();
                _instance = null;
            }
        }

        #endregion

        #region ==================== 设备信息模型 ====================

        public class CameraDeviceInfo
        {
            public string UserDefinedName { get; set; }
            public string SerialNumber { get; set; }
            public string ModelName { get; set; }
            public uint DeviceIndex { get; set; }
            /// <summary>底层 IDeviceInfo 引用（用于 CreateDevice）</summary>
            internal IDeviceInfo RawInfo { get; set; }

            public override string ToString() => $"[{DeviceIndex}] {UserDefinedName ?? ModelName ?? "未知"} (SN: {SerialNumber})";
        }

        #endregion

        #region ==================== 自动初始化 ====================

        /// <summary>
        /// 确保相机已初始化并打开
        /// </summary>
        public bool EnsureCameraOpened(string preferSN = null)
        {
            if (_isOpen) return true;

            Log.Info("自动初始化 3D 相机...");

            try
            {
                var devices = EnumerateCameras();
                if (devices.Count == 0)
                {
                    Log.Warning("未找到任何 MVS 相机设备");
                    return false;
                }

                Log.Info($"找到 {devices.Count} 个相机设备");

                // 优先按 SN 匹配
                if (!string.IsNullOrEmpty(preferSN))
                {
                    var match = devices.FirstOrDefault(d => d.SerialNumber == preferSN);
                    if (match != null)
                    {
                        Log.Info($"按序列号匹配到相机: {match}");
                        return OpenDevice(match);
                    }
                }

                // 逐个尝试打开，跳过已被占用的设备（如2D相机被VM占用时，自动尝试3D相机）
                // 系统中可能有多个MVS相机（2D+3D），第一个可能被VM占用，需要尝试下一个
                foreach (var device in devices)
                {
                    Log.Info($"尝试打开相机: {device}");
                    if (OpenDevice(device))
                        return true;
                    Log.Warning($"相机 {device.SerialNumber} 打开失败（可能已被占用），尝试下一个...");
                }
                Log.Error("所有相机均无法打开");
                return false;
            }
            catch (Exception ex)
            {
                Log.Error($"自动初始化相机失败: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region ==================== 枚举设备 ====================

        /// <summary>
        /// 枚举所有连接的 MVS 相机
        /// </summary>
        public List<CameraDeviceInfo> EnumerateCameras()
        {
            var result = new List<CameraDeviceInfo>();

            try
            {
                var devList = new List<IDeviceInfo>();
                // 枚举 GigE + USB 设备
                int ret = DeviceEnumerator.EnumDevices(DeviceTLayerType.MvGigEDevice | DeviceTLayerType.MvUsbDevice, out devList);
                if (ret != 0)
                {
                    Log.Warning($"枚举相机失败，错误码: 0x{ret:X8}");
                    return result;
                }

                for (uint i = 0; i < devList.Count; i++)
                {
                    var info = devList[(int)i];
                    result.Add(new CameraDeviceInfo
                    {
                        DeviceIndex = i,
                        UserDefinedName = info.UserDefinedName,
                        SerialNumber = info.SerialNumber,
                        ModelName = info.ModelName,
                        RawInfo = info
                    });
                }

                Log.Info($"枚举到 {result.Count} 个 MVS 相机设备");
            }
            catch (Exception ex)
            {
                Log.Error($"枚举相机异常: {ex.Message}");
            }

            return result;
        }

        #endregion

        #region ==================== 设备控制 ====================

        private IDevice _device;
        private bool _isOpen = false;
        private bool _isGrabbing = false;
        private string _currentDeviceSN;

        public bool IsOpen => _isOpen;
        public bool IsGrabbing => _isGrabbing;

        /// <summary>
        /// 打开指定相机设备
        /// </summary>
        public bool OpenDevice(CameraDeviceInfo deviceInfo)
        {
            if (deviceInfo?.RawInfo == null)
            {
                Log.Error("打开相机失败: 设备信息为空");
                return false;
            }

            try
            {
                CloseCamera();

                _device = DeviceFactory.CreateDevice(deviceInfo.RawInfo);
                if (_device == null)
                {
                    Log.Error("创建设备实例失败");
                    return false;
                }

                int ret = _device.Open();
                if (ret != 0)
                {
                    Log.Error($"打开设备失败，错误码: 0x{ret:X8}");
                    _device = null;
                    return false;
                }

                _isOpen = true;
                _currentDeviceSN = deviceInfo.SerialNumber;
                Log.Info($"相机打开成功: {deviceInfo}");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"打开相机异常: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 按序列号打开相机
        /// </summary>
        public bool OpenCameraBySN(string serialNumber)
        {
            var devices = EnumerateCameras();
            var match = devices.FirstOrDefault(d => d.SerialNumber == serialNumber);
            return match != null && OpenDevice(match);
        }

        /// <summary>
        /// 关闭当前相机
        /// </summary>
        public void CloseCamera()
        {
            try
            {
                if (_isGrabbing)
                    StopGrabbing();

                if (_isOpen && _device != null)
                {
                    _device.Close();
                    _isOpen = false;
                    _currentDeviceSN = null;
                    Log.Info("相机已关闭");
                }
            }
            catch (Exception ex)
            {
                Log.Error($"关闭相机异常: {ex.Message}");
            }
        }

        #endregion

        #region ==================== 采集控制 ====================

        /// <summary>
        /// 开始采集
        /// </summary>
        public bool StartGrabbing()
        {
            if (_device == null || !_isOpen)
            {
                Log.Error("开始采集失败: 相机未打开");
                return false;
            }

            try
            {
                int ret = _device.StreamGrabber.StartGrabbing();
                if (ret != 0)
                {
                    Log.Error($"开始采集失败，错误码: 0x{ret:X8}");
                    return false;
                }
                _isGrabbing = true;
                Log.Info("相机开始采集");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"开始采集异常: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 停止采集
        /// </summary>
        public bool StopGrabbing()
        {
            if (_device == null || !_isGrabbing) return false;

            try
            {
                int ret = _device.StreamGrabber.StopGrabbing();
                if (ret != 0)
                    Log.Warning($"停止采集返回错误码: 0x{ret:X8}");

                _isGrabbing = false;
                Log.Info("相机已停止采集");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"停止采集异常: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region ==================== 缓存清除与相机复位 ====================

        /// <summary>
        /// 清除相机缓存 — 停止采集 → 清空内部缓冲区 → 重新开始采集
        /// 使用 StreamGrabber.ClearImageBuffer() 清空相机内部的 FIFO
        /// </summary>
        /// <param name="delayMs">停止到重新开始之间的等待(ms)，默认 100ms</param>
        public bool ClearCameraCache(int delayMs = 100)
        {
            Log.Info("清除相机缓存...");

            if (_device == null || !_isOpen)
            {
                Log.Warning("清除缓存失败: 相机未打开");
                return false;
            }

            try
            {
                // 第1步：停止采集
                if (_isGrabbing)
                {
                    _device.StreamGrabber.StopGrabbing();
                    _isGrabbing = false;
                }

                // 第2步：清空图像缓冲区（核心API — ClearImageBuffer）
                _device.StreamGrabber.ClearImageBuffer();

                // 第3步：等待相机状态稳定
                if (delayMs > 0)
                    Thread.Sleep(delayMs);

                // 第4步：重新开始采集
                int ret = _device.StreamGrabber.StartGrabbing();
                if (ret != 0)
                {
                    Log.Error($"缓存清除后重新开始采集失败，错误码: 0x{ret:X8}");
                    return false;
                }
                _isGrabbing = true;

                Log.Info("相机缓存清除完成 ✔");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"清除相机缓存异常: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 完全复位相机 — 关闭并重新打开（更彻底，但更慢）
        /// </summary>
        public async Task<bool> ResetCameraAsync(string serialNumber = null, int delayMs = 500)
        {
            Log.Info("复位相机...");

            string sn = serialNumber ?? _currentDeviceSN;
            if (string.IsNullOrEmpty(sn))
            {
                Log.Error("复位相机失败: 未指定相机");
                return false;
            }

            CloseCamera();

            if (delayMs > 0)
                await Task.Delay(delayMs);

            return OpenCameraBySN(sn);
        }

        #endregion

        #region ==================== 参数设置 ====================

        /// <summary>
        /// 设置触发模式
        /// </summary>
        public bool SetTriggerMode(uint mode)
        {
            if (_device == null || !_isOpen) return false;
            try
            {
                return _device.Parameters.SetEnumValue("TriggerMode", mode) == 0;
            }
            catch (Exception ex)
            {
                Log.Error($"设置触发模式失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 设置曝光时间(μs)
        /// </summary>
        public bool SetExposureTime(float exposureUs)
        {
            if (_device == null || !_isOpen) return false;
            try
            {
                return _device.Parameters.SetFloatValue("ExposureTime", exposureUs) == 0;
            }
            catch (Exception ex)
            {
                Log.Error($"设置曝光时间失败: {ex.Message}");
                return false;
            }
        }

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
                    CloseCamera();
                }
                _disposed = true;
            }
        }

        ~CameraController()
        {
            Dispose(false);
        }

        #endregion
    }
}
