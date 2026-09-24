using System;
using System.Xml.Serialization;

namespace RBLAOI.Models
{
    [Serializable]
    public class ProjectData
    {
        // ==================== 基本信息 ====================
        public string ProjectName { get; set; } = "新方案";
        public string ProjectPath { get; set; } = "";
        public DateTime CreateTime { get; set; } = DateTime.Now;
        public DateTime ModifyTime { get; set; } = DateTime.Now;
        public string Remark { get; set; } = "";

        // ==================== 基础设定 ====================
        public double BoardWidth { get; set; } = 200;
        public double BoardHeight { get; set; } = 200;
        public double FovWidth { get; set; } = 50;
        public double FovHeight { get; set; } = 50;
        public double Overlap { get; set; } = 0.1;   // 默认重叠率10%
        public int SubBoardCount { get; set; } = 0;
        public int PluginFeatureCount { get; set; } = 1;  // 插件特征个数，默认1
        public PinTypeParameter[] PinTypeParams { get; set; } = new PinTypeParameter[0];
        public double BoardFocus { get; set; } = 0;  // 板面焦距(mm)，焊盘拍照前Z轴定位用
        public double CameraExposureTime { get; set; } = 10000;  // 相机曝光时间(μs)，方案级，默认与系统参数一致；<=0 时回退系统参数

        // ==================== 光源控制（方案级：通道数与亮度开关；串口号为系统参数，所有方案共用） ====================
        [Obsolete("光源串口号已迁移至系统参数 SysParamData.LightSourceSerialPort，此字段仅保留兼容旧方案XML")]
        public string LightPort { get; set; } = "COM1";
        [Obsolete("光源通道数已迁移至系统参数 SysParamData.LightSourceChannelCount，此字段仅保留兼容旧方案XML")]
        public int LightChannelCount { get; set; } = 4;
        public double LightChannel1 { get; set; } = 128; // 通道1亮度 (0-255)
        public double LightChannel2 { get; set; } = 128; // 通道2亮度 (0-255)
        public double LightChannel3 { get; set; } = 128; // 通道3亮度 (0-255)
        public double LightChannel4 { get; set; } = 128; // 通道4亮度 (0-255)
        public bool LightOn1 { get; set; } = true;       // 通道1开关 (true亮/false灭)
        public bool LightOn2 { get; set; } = true;       // 通道2开关 (true亮/false灭)
        public bool LightOn3 { get; set; } = true;       // 通道3开关 (true亮/false灭)
        public bool LightOn4 { get; set; } = true;       // 通道4开关 (true亮/false灭)

        public string CurrentSide { get; set; } = "正面";
        public string CurrentTrack { get; set; } = "轨道1";
        public string MainBarcode { get; set; } = "0";
        public string SubBarcode { get; set; } = "0";
        public string CurrentBatch { get; set; } = "0";
        public int BatchCount { get; set; } = 0;

        // ==================== 程序统计信息 ====================
        public double PassRate { get; set; } = 0;
        public double InspectTime { get; set; } = 0;
        public double BoardTime { get; set; } = 0;
        public double FalseRate { get; set; } = 0;
        public double DefectRate { get; set; } = 0;
        public double BatchPassRate { get; set; } = 0;
        public int GoodCount { get; set; } = 0;
        public int NgCount { get; set; } = 0;

        // ==================== 检测参数（方案级，已迁移至 PinTypeParams 每针型独立） ====================

        // ==================== 计算属性 ====================
        public double YieldRate => (GoodCount + NgCount) > 0
            ? (double)GoodCount / (GoodCount + NgCount) * 100 : 0;
    }
}