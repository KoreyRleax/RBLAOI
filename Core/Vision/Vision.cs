using HalconDotNet;
using MvCamCtrl.NET;
using MvCamCtrl.NET.CameraParams;
using MvCameraControl;
using RBLAOI.Core.Controls;
using RBLAOI.Core.Utility;
using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace RBLAOI.Core.Vision
{
    public class Vision
    {
        private HWindow _halconWindow;
        private CustomHSmartWindowControl  _halconControl;
        public Vision()
        {
        }
        /// <summary>
        /// Halcon窗口句柄
        /// </summary>
        public HWindow HalconWindow
        {
            get => _halconWindow;
            set
            {
                _halconWindow = value;
                if (_halconWindow != null)
                {
                    InitializeHalconWindow();
                }
            }
        }
        /// <summary>
        /// Halcon控件（用于布局操作）
        /// </summary>
        public CustomHSmartWindowControl  HalconControl
        {
            get => _halconControl;
            set => _halconControl = value;
        }
        /// <summary>
        /// 设置Halcon窗口（同时设置句柄和控件）
        /// </summary>
        public void SetHalconWindow(CustomHSmartWindowControl  control)
        {
            if (control == null) return;

            _halconControl = control;
            _halconWindow = control.HalconWindow;

            InitializeHalconWindow();
        }
        /// 强制刷新Halcon窗口显示（居中）
        /// </summary>
        public void RefreshDisplay()
        {
            if (_halconControl == null) return;

            // 强制更新布局
            _halconControl.InvalidateVisual();
            _halconControl.UpdateLayout();

            // 触发父容器重新布局
            var parent = System.Windows.Media.VisualTreeHelper.GetParent(_halconControl) as System.Windows.FrameworkElement;
            parent?.InvalidateMeasure();
            parent?.UpdateLayout();
        }
        /// <summary>
        /// 初始化Halcon窗口
        /// </summary>
        private void InitializeHalconWindow()
        {
            try
            {
                HOperatorSet.SetDraw(_halconWindow, "margin");
                HOperatorSet.SetSystem("tsp_width", 3000);
                HOperatorSet.SetSystem("tsp_height", 3000);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Halcon窗口初始化失败: {ex.Message}");
            }
        }
        /// <summary>
        /// 关闭Halcon窗口
        /// </summary>
        public void ClearHalconWindow()
        {
            try
            {
                if (_halconWindow != null && _halconWindow != null)
                {
                    HOperatorSet.ClearWindow(_halconWindow);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Halcon窗口关闭失败: {ex.Message}");
            }
        }
        /// <summary>
        /// 在Halcon窗口中显示图像
        /// </summary>
        public void DisplayImageByImage(HObject image)
        {
            if (image == null || !image.IsInitialized())
                return;

            try
            {

                // 显示图像
                HOperatorSet.ClearWindow(_halconWindow);
                HOperatorSet.DispObj(image, _halconWindow);

            }
            catch (Exception ex)
            {
                Log.Error($"显示图片失败: {ex.Message}");
            }
        }
        /// <summary>
        /// 将Halcon窗口图像居中显示（模拟右键双击触发基类居中）
        /// </summary>
        public void ResetViewByMouseClick()
        {
            if (_halconControl == null) return;

            try
            {
                // 获取控件中心点
                var centerX = _halconControl.ActualWidth / 2;
                var centerY = _halconControl.ActualHeight / 2;

                // ========== 第一次右键按下（ClickCount=1） ==========
                var args1 = new MouseButtonEventArgs(
                    Mouse.PrimaryDevice,
                    Environment.TickCount,
                    MouseButton.Right)
                {
                    RoutedEvent = UIElement.MouseRightButtonDownEvent,
                    Source = _halconControl
                };
                _halconControl.RaiseEvent(args1);

                // 通过反射设置 ClickCount = 2
                var clickCountProperty = typeof(MouseButtonEventArgs)
                    .GetProperty("ClickCount",
                        System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.Public);

                if (clickCountProperty != null && clickCountProperty.CanWrite)
                {
                    clickCountProperty.SetValue(args1, 2);
                }

                _halconControl.RaiseEvent(args1);

                // ========== 第一次右键释放 ==========
                var upArgs1 = new MouseButtonEventArgs(
                    Mouse.PrimaryDevice,
                    Environment.TickCount,
                    MouseButton.Right)
                {
                    RoutedEvent = UIElement.MouseRightButtonUpEvent,
                    Source = _halconControl
                };
                _halconControl.RaiseEvent(upArgs1);
            }
            catch (Exception ex)
            {
                Log.Error($"模拟右键双击失败: {ex.Message}");
            }
        }
    }
}