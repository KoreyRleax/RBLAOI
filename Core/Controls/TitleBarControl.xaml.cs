using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace RBLAOI.Core.Controls
{
    public partial class TitleBarControl : UserControl
    {
        // 依赖属性：标题文字
        public static readonly DependencyProperty TitleProperty =
            DependencyProperty.Register("Title", typeof(string), typeof(TitleBarControl),
                new PropertyMetadata("AOI 视觉检测系统"));

        public string Title
        {
            get => (string)GetValue(TitleProperty);
            set => SetValue(TitleProperty, value);
        }

        // 依赖属性：图标（emoji 字符，IconSource 为空时显示）
        public static readonly DependencyProperty TitleIconProperty =
            DependencyProperty.Register("TitleIcon", typeof(string), typeof(TitleBarControl),
                new PropertyMetadata("🔍"));

        public string TitleIcon
        {
            get => (string)GetValue(TitleIconProperty);
            set => SetValue(TitleIconProperty, value);
        }

        // 依赖属性：图标图片（ImageSource，优先于 TitleIcon 显示）
        public static readonly DependencyProperty IconSourceProperty =
            DependencyProperty.Register("IconSource", typeof(ImageSource), typeof(TitleBarControl),
                new PropertyMetadata(null));

        public ImageSource IconSource
        {
            get => (ImageSource)GetValue(IconSourceProperty);
            set => SetValue(IconSourceProperty, value);
        }

        private Window _parentWindow;

        public TitleBarControl()
        {
            InitializeComponent();
            this.Loaded += TitleBarControl_Loaded;
        }

        private void TitleBarControl_Loaded(object sender, RoutedEventArgs e)
        {
            _parentWindow = Window.GetWindow(this);

            if (_parentWindow != null)
            {
                _parentWindow.StateChanged += ParentWindow_StateChanged;
                UpdateMaximizeButton();
            }
        }

        private void ParentWindow_StateChanged(object sender, System.EventArgs e)
        {
            UpdateMaximizeButton();
        }

        private void UpdateMaximizeButton()
        {
            if (_parentWindow != null && btnMaximize != null)
            {
                btnMaximize.Content = _parentWindow.WindowState == WindowState.Maximized
                    ? "\uE923"  // 还原图标
                    : "\uE922"; // 最大化图标
            }
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_parentWindow == null)
            {
                _parentWindow = Window.GetWindow(this);
            }

            if (e.ClickCount == 2)
            {
                MaximizeWindow_Click(sender, e);
            }
            else if (_parentWindow != null)
            {
                _parentWindow.DragMove();
            }
        }

        private void MinimizeWindow_Click(object sender, RoutedEventArgs e)
        {
            if (_parentWindow == null)
                _parentWindow = Window.GetWindow(this);

            _parentWindow.WindowState = WindowState.Minimized;
        }

        private void MaximizeWindow_Click(object sender, RoutedEventArgs e)
        {
            if (_parentWindow == null)
                _parentWindow = Window.GetWindow(this);

            _parentWindow.WindowState = _parentWindow.WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }

        private void CloseWindow_Click(object sender, RoutedEventArgs e)
        {
            if (_parentWindow == null)
                _parentWindow = Window.GetWindow(this);

            _parentWindow.Close();
        }
    }
}