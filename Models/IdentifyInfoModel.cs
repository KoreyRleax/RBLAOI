   using RBLAOI.Core.Utility;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RBLAOI.Models
{
    public class IdentifyInfoModel: NotificationObject
    {
        public string _pointNumber;  //序号
        public string PointNumber
        {
            get { return _pointNumber; }
            set
            {
                _pointNumber = value;
                RaisePropertyChanged("PointNumber");
            }
        }
        public string _diffrenceXY;  //XY偏差
        public string DiffrenceXY
        {
            get { return _diffrenceXY; }
            set
            {
                _diffrenceXY = value;
                RaisePropertyChanged("DiffrenceXY");
            }
        }
        public string _diffrenceHigh;  //高度偏差
        public string DiffrenceHigh
        {
            get { return _diffrenceHigh; }
            set
            {
                _diffrenceHigh = value;
                RaisePropertyChanged("DiffrenceHigh");
            }
        }
        public string _result; //结果
        public string Result
        {
            get { return _result; }
            set
            {
                _result = value;
                RaisePropertyChanged("Result");
            }
        }
    }
}
