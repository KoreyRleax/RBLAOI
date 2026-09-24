using System;
using System.Xml.Serialization;
using RBLAOI.Core.Utility;

namespace RBLAOI.Models
{
    /// <summary>
    /// 拍照位信息（持久化到方案的 GrabPositions.json）
    /// </summary>
    public class GrabPosition : NotificationObject
    {
        private int _index;
        private int _row;
        private int _col;
        private double _x;
        private double _y;
        private bool _isDetectPosition = true;
        private string _pinTypes = "";

        public int Index
        {
            get => _index;
            set => SetProperty(ref _index, value);
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

        public bool IsDetectPosition
        {
            get => _isDetectPosition;
            set
            {
                if (SetProperty(ref _isDetectPosition, value))
                {
                    DetectPositionChanged?.Invoke(this);
                }
            }
        }

        /// <summary>标记的针型，如 "A"、"AB"、"ABC"</summary>
        public string PinTypes
        {
            get => _pinTypes;
            set => SetProperty(ref _pinTypes, value);
        }

        /// <summary>
        /// 检测位勾选状态变化事件（在 ViewModel 中订阅，用于刷新检测位列表）
        /// </summary>
        public event Action<GrabPosition> DetectPositionChanged;
    }
}
