using System;
using Newtonsoft.Json;
using RBLAOI.Core.Utility;

namespace RBLAOI.Models
{
    /// <summary>
    /// 检测结果数据
    /// </summary>
    public class PinResult : NotificationObject
    {
        private int _pinID;
        private int _detectIndex;
        private int _pinIndex;
        private double _x;
        private double _y;
        public string _diffX;
        public string _diffY;
        public string _result;
        private double _h;
        private string _pinType = "A";

        /// <summary>焊盘X坐标(mm)，VM方案检测返回</summary>
        public double PadX { get; set; }

        /// <summary>焊盘Y坐标(mm)，VM方案检测返回</summary>
        public double PadY { get; set; }

        /// <summary>是否为空针（焊盘有但针脚缺失），VM方案检测返回</summary>
        public bool IsEmptyPin { get; set; }

        /// <summary>是否为歪针（Blob检测中无黑区针尖坐标）</summary>
        public bool IsCrookedPin { get; set; }

        /// <summary>网格索引（row*cols+col），用于定位到拼图中的 tile</summary>
        public int GridIndex { get; set; }

        /// <summary>PINID（全局第几个针，从0自增）</summary>
        public int PINID
        {
            get => _pinID;
            set => SetProperty(ref _pinID, value);
        }

        /// <summary>检测位号（第几个检测位）</summary>
        public int DetectIndex
        {
            get => _detectIndex;
            set => SetProperty(ref _detectIndex, value);
        }

        /// <summary>位号（当前检测位内的第几个针）</summary>
        public int PinIndex
        {
            get => _pinIndex;
            set => SetProperty(ref _pinIndex, value);
        }

        public double X
        {
            get => _x;
            set => SetProperty(ref _x, value);
        }

        public double Y
        {
            get => _y;
            set => SetProperty(ref _y, value);
        }
        public string DiffX
        {
            get => _diffX;
            set => SetProperty(ref _diffX, value);
        }
        public string DiffY
        {
            get => _diffY;
            set => SetProperty(ref _diffY, value);
        }
        public string Result
        {
            get => _result;
            set => SetProperty(ref _result, value);
        }

        /// <summary>实际高度测量值（mm），由3D相机检测得到</summary>
        public double H
        {
            get => _h;
            set
            {
                if (SetProperty(ref _h, value))
                    RaisePropertyChanged(nameof(DiffH));
            }
        }

        /// <summary>高度差（显示用），由 H 与当前方案理想高度计算而来，不序列化</summary>
        [JsonIgnore]
        public string DiffH => (H - _idealHeight).ToString("F3");

        private double _idealHeight;

        /// <summary>当前方案理想高度，用于计算显示用 DiffH</summary>
        public void SetIdealHeight(double idealHeight)
        {
            if (Math.Abs(_idealHeight - idealHeight) < 0.001) return;
            _idealHeight = idealHeight;
            RaisePropertyChanged(nameof(DiffH));
        }

        /// <summary>针型标记（A/B/C...），用于从 PinTypeParams 查找对应的检测参数</summary>
        public string PinType
        {
            get => _pinType;
            set => SetProperty(ref _pinType, value);
        }
    }
}
