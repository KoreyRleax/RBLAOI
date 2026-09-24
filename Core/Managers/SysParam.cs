using RBLAOI.Core.Utility;
using RBLAOI.Models;
using System;
using System.IO;
using System.Xml.Serialization;

namespace RBLAOI.Core.Managers
{
    /// <summary>
    /// 系统参数类（全局配置，单例）
    /// </summary>
    public class SysParam
    {
        private static SysParam _instance;
        private static readonly object _lock = new object();
        private static readonly string ConfigPath =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "SysParam.xml");

        public static SysParam Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                            _instance = new SysParam();
                    }
                }
                return _instance;
            }
        }

        /// <summary>
        /// 核心数据容器，所有系统参数均存储于此
        /// </summary>
        public SysParamData Data { get; private set; } = new SysParamData();

        private SysParam()
        {
            Load();
        }

        #region 序列化加载与保存

        public void Load()
        {
            try
            {
                string dir = Path.GetDirectoryName(ConfigPath);
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                if (!File.Exists(ConfigPath))
                {
                    Save(); // 生成默认配置文件
                    return;
                }

                var serializer = new XmlSerializer(typeof(SysParamData));
                using (var reader = new StreamReader(ConfigPath))
                {
                    Data = (SysParamData)serializer.Deserialize(reader) ?? new SysParamData();
                }
            }
            catch (Exception ex)
            {
                Log.Error($"加载系统参数失败: {ex.Message}");
                Data = new SysParamData();
            }
        }

        public void Save()
        {
            try
            {
                string dir = Path.GetDirectoryName(ConfigPath);
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var serializer = new XmlSerializer(typeof(SysParamData));
                using (var writer = new StreamWriter(ConfigPath))
                {
                    serializer.Serialize(writer, Data);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"保存系统参数失败: {ex.Message}");
            }
        }

        #endregion

        #region 常用属性（便捷访问，避免外部 code 修改过大）

        // 连接相关
        public string VisionIp
        {
            get => Data.VisionIp;
            set { Data.VisionIp = value; Save(); }
        }
        public int VisionPort
        {
            get => Data.VisionPort;
            set { Data.VisionPort = value; Save(); }
        }
        public string Vision3DIp
        {
            get => Data.Vision3DIp;
            set { Data.Vision3DIp = value; Save(); }
        }
        public int Vision3DPort
        {
            get => Data.Vision3DPort;
            set { Data.Vision3DPort = value; Save(); }
        }
        public string SelectedSerialPort { get => Data.SelectedSerialPort; set { Data.SelectedSerialPort = value; Save(); } }
        public int SelectedBaudRate { get => Data.SelectedBaudRate; set { Data.SelectedBaudRate = value; Save(); } }
        public string LightSourceSerialPort { get => Data.LightSourceSerialPort; set { Data.LightSourceSerialPort = value; Save(); } }
        public int LightSourceBaudRate { get => Data.LightSourceBaudRate; set { Data.LightSourceBaudRate = value; Save(); } }
        public int LightSourceChannelCount { get => Data.LightSourceChannelCount; set { Data.LightSourceChannelCount = value; Save(); } }
        public string LightTrainDir { get => Data.LightTrainDir; set { Data.LightTrainDir = value; Save(); } }

        // 定位参数（全局）
        public double RightBottomX
        {
            get => Data.RightBottomX;
            set { Data.RightBottomX = value; Save(); }
        }
        public double RightBottomY
        {
            get => Data.RightBottomY;
            set { Data.RightBottomY = value; Save(); }
        }

        // 图片显示相关（隐藏在后台，但可能外部还在用）
        public int ImageDisplayRows
        {
            get => Data.ImageDisplayRows;
            set { Data.ImageDisplayRows = value; Save(); }
        }
        public int ImageDisplayCols
        {
            get => Data.ImageDisplayCols;
            set { Data.ImageDisplayCols = value; Save(); }
        }

        // 主题
        public string CurrentTheme
        {
            get => Data.CurrentTheme;
            set { Data.CurrentTheme = value; Save(); }
        }

        // 拼图补偿
        public double StitchOffsetX
        {
            get => Data.StitchOffsetX;
            set { Data.StitchOffsetX = value; Save(); }
        }
        public double StitchOffsetY
        {
            get => Data.StitchOffsetY;
            set { Data.StitchOffsetY = value; Save(); }
        }

        // Halcon 结果图层显示（右键菜单持久化）
        public string OverlayFilter
        {
            get => Data.OverlayFilter;
            set { Data.OverlayFilter = value; Save(); }
        }
        public string OverlayDisplayMode
        {
            get => Data.OverlayDisplayMode;
            set { Data.OverlayDisplayMode = value; Save(); }
        }

        #endregion
    }
}