using RBLAOI.Core.Utility;

namespace RBLAOI.Models
{
    /// <summary>
    /// 检测位数据
    /// </summary>
    public class DetectPosition : NotificationObject
    {
        private int _index;         // 检测位序号（从0自增）
        private int _grabIndex;     // 来源拍照位号
        private double _x;
        private double _y;
        private int _row;
        private int _col;
        private bool _isDetectPosition;
        private string _pinTypes = "";

        /// <summary>检测位序号（在检测位列表中从0自增）</summary>
        public int Index
        {
            get => _index;
            set => SetProperty(ref _index, value);
        }

        /// <summary>拍照位号（来源 GrabPosition.Index）</summary>
        public int GrabIndex
        {
            get => _grabIndex;
            set => SetProperty(ref _grabIndex, value);
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

        public int Row
        {
            get => _row;
            set => SetProperty(ref _row, value);
        }

        public int Col
        {
            get => _col;
            set => SetProperty(ref _col, value);
        }

        public bool IsDetectPosition
        {
            get => _isDetectPosition;
            set => SetProperty(ref _isDetectPosition, value);
        }

        /// <summary>标记的针型，如 "A"、"AB"、"ABC"</summary>
        public string PinTypes
        {
            get => _pinTypes;
            set => SetProperty(ref _pinTypes, value);
        }
    }
}
