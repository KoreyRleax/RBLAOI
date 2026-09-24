using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace RBLAOI.Core.Utility
{
    /// <summary>
    /// 通知对象基类 - 实现属性更改通知
    /// </summary>
    public class NotificationObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void RaisePropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            RaisePropertyChanged(propertyName);
            return true;
        }
    }
}