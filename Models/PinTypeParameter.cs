using RBLAOI.Core.Utility;

namespace RBLAOI.Models
{
    /// <summary>
    /// 单种针型的检测参数
    /// </summary>
    public class PinTypeParameter : NotificationObject
    {
        private string _label = "A";           // 标记字母 A, B, C...
        private double _focus;                  // 焦距 (mm)
        private double _xyTolerance;            // XY允许偏差 (mm)
        private double _idealOffsetX;           // 理想针尖X（相对焊盘中心）(mm)
        private double _idealOffsetY;           // 理想针尖Y（相对焊盘中心）(mm)
        private double _heightTolerance;        // 高度允许偏差 (mm)
        private double _idealHeight;            // 理想高度 (mm)

        public string Label { get => _label; set => SetProperty(ref _label, value); }
        public double Focus { get => _focus; set => SetProperty(ref _focus, value); }
        public double XyTolerance { get => _xyTolerance; set => SetProperty(ref _xyTolerance, value); }
        public double IdealOffsetX { get => _idealOffsetX; set => SetProperty(ref _idealOffsetX, value); }
        public double IdealOffsetY { get => _idealOffsetY; set => SetProperty(ref _idealOffsetY, value); }
        public double HeightTolerance { get => _heightTolerance; set => SetProperty(ref _heightTolerance, value); }
        public double IdealHeight { get => _idealHeight; set => SetProperty(ref _idealHeight, value); }
    }
}
