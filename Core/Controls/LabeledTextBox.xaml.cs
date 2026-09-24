using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RBLAOI.Core.Controls
{
    public partial class LabeledTextBox : UserControl
    {
        public enum LabelMode
        {
            Top,   // 标签在文本框上方
            Left   // 标签在文本框左侧
        }

        public static readonly DependencyProperty LabelTextProperty =
            DependencyProperty.Register("LabelText", typeof(string), typeof(LabeledTextBox),
                new PropertyMetadata("Label"));

        public static readonly DependencyProperty TextProperty =
            DependencyProperty.Register("Text", typeof(string), typeof(LabeledTextBox),
                new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

        public static readonly DependencyProperty ModeProperty =
            DependencyProperty.Register("Mode", typeof(LabelMode), typeof(LabeledTextBox),
                new PropertyMetadata(LabelMode.Top, OnModeChanged));

        public static readonly DependencyProperty TextBoxWidthProperty =
            DependencyProperty.Register("TextBoxWidth", typeof(double), typeof(LabeledTextBox),
                new PropertyMetadata(double.NaN, OnTextBoxSizeChanged));

        public static readonly DependencyProperty TextBoxHeightProperty =
            DependencyProperty.Register("TextBoxHeight", typeof(double), typeof(LabeledTextBox),
                new PropertyMetadata(double.NaN, OnTextBoxSizeChanged));

        public static readonly DependencyProperty TextBoxPaddingProperty =
            DependencyProperty.Register("TextBoxPadding", typeof(Thickness), typeof(LabeledTextBox),
                new PropertyMetadata(new Thickness(6, 4, 6, 4), OnTextBoxPaddingChanged));

        // ==================== 新增依赖属性 ====================

        public static readonly DependencyProperty TextBoxBorderBrushProperty =
            DependencyProperty.Register("TextBoxBorderBrush", typeof(Brush), typeof(LabeledTextBox),
                new PropertyMetadata(null));

        public static readonly DependencyProperty TextBoxBorderThicknessProperty =
            DependencyProperty.Register("TextBoxBorderThickness", typeof(Thickness), typeof(LabeledTextBox),
                new PropertyMetadata(new Thickness(1)));

        public static readonly DependencyProperty LabelForegroundProperty =
            DependencyProperty.Register("LabelForeground", typeof(Brush), typeof(LabeledTextBox),
                new PropertyMetadata(null));

        public static readonly DependencyProperty TextBoxFontWeightProperty =
            DependencyProperty.Register("TextBoxFontWeight", typeof(FontWeight), typeof(LabeledTextBox),
                new PropertyMetadata(FontWeights.Normal));

        public string LabelText
        {
            get => (string)GetValue(LabelTextProperty);
            set => SetValue(LabelTextProperty, value);
        }

        public string Text
        {
            get => (string)GetValue(TextProperty);
            set => SetValue(TextProperty, value);
        }

        public LabelMode Mode
        {
            get => (LabelMode)GetValue(ModeProperty);
            set => SetValue(ModeProperty, value);
        }

        public double TextBoxWidth
        {
            get => (double)GetValue(TextBoxWidthProperty);
            set => SetValue(TextBoxWidthProperty, value);
        }

        public double TextBoxHeight
        {
            get => (double)GetValue(TextBoxHeightProperty);
            set => SetValue(TextBoxHeightProperty, value);
        }

        public Thickness TextBoxPadding
        {
            get => (Thickness)GetValue(TextBoxPaddingProperty);
            set => SetValue(TextBoxPaddingProperty, value);
        }

        public Brush TextBoxBorderBrush
        {
            get => (Brush)GetValue(TextBoxBorderBrushProperty);
            set => SetValue(TextBoxBorderBrushProperty, value);
        }

        public Thickness TextBoxBorderThickness
        {
            get => (Thickness)GetValue(TextBoxBorderThicknessProperty);
            set => SetValue(TextBoxBorderThicknessProperty, value);
        }

        public Brush LabelForeground
        {
            get => (Brush)GetValue(LabelForegroundProperty);
            set => SetValue(LabelForegroundProperty, value);
        }

        public FontWeight TextBoxFontWeight
        {
            get => (FontWeight)GetValue(TextBoxFontWeightProperty);
            set => SetValue(TextBoxFontWeightProperty, value);
        }

        public LabeledTextBox()
        {
            InitializeComponent();
            Loaded += ApplyDefaultResources;
        }

        private void ApplyDefaultResources(object sender, RoutedEventArgs e)
        {
            if (TextBoxBorderBrush == null)
                SetCurrentValue(TextBoxBorderBrushProperty, TryFindResource("ThemeSubBorder"));
            if (LabelForeground == null)
                SetCurrentValue(LabelForegroundProperty, TryFindResource("ThemeSubText"));
        }

        private static void OnModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = (LabeledTextBox)d;
            control.UpdateVisibility();
        }

        private void UpdateVisibility()
        {
            if (TopPanel == null || LeftPanel == null) return;

            if (Mode == LabelMode.Top)
            {
                TopPanel.Visibility = Visibility.Visible;
                LeftPanel.Visibility = Visibility.Collapsed;
            }
            else
            {
                TopPanel.Visibility = Visibility.Collapsed;
                LeftPanel.Visibility = Visibility.Visible;
            }
        }
        private static void OnTextBoxSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var ctrl = (LabeledTextBox)d;
            // 在 Loaded 后绑定到实际的 TextBox
            ctrl.Dispatcher.BeginInvoke(new Action(() =>
            {
                var tb = FindVisualChild<TextBox>(ctrl);
                if (tb != null)
                {
                    if (!double.IsNaN(ctrl.TextBoxWidth)) tb.Width = ctrl.TextBoxWidth;
                    if (!double.IsNaN(ctrl.TextBoxHeight)) tb.Height = ctrl.TextBoxHeight;
                }
            }));
        }

        private static void OnTextBoxPaddingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var ctrl = (LabeledTextBox)d;
            ctrl.Dispatcher.BeginInvoke(new Action(() =>
            {
                var tb = FindVisualChild<TextBox>(ctrl);
                if (tb != null) tb.Padding = ctrl.TextBoxPadding;
            }));
        }

        private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T t) return t;
                var result = FindVisualChild<T>(child);
                if (result != null) return result;
            }
            return null;
        }
    }
}