using RBLAOI.Core.Managers;
using RBLAOI.ViewModels;
using System;
using System.Windows;

namespace RBLAOI.Views
{
    public partial class CalibrationWindow : Window
    {
        private SysParamViewModel _viewModel;
        private SysParam _sysParam;

        // 计算后的结果
        private double _calibratedOffsetX;
        private double _calibratedOffsetY;

        public CalibrationWindow(SysParamViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel;
            _sysParam = SysParam.Instance;

            txtPixelEqX.Text = _viewModel.CameraPixelEquivalent.ToString("F4");
            txtPixelEqY.Text = _viewModel.CameraPixelEquivalent.ToString("F4");
        }

        /// <summary>
        /// Step 1: 置零并保存
        /// </summary>
        private void BtnZero_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.StitchOffsetX = 0;
            _viewModel.StitchOffsetY = 0;
            _viewModel.Save();

            MessageBox.Show(
                "拼图X/Y补偿已置零并保存。\n\n请关闭此窗口，在主界面执行一次全图采集，\n然后观察拼接图的拼缝处特征错位像素数。",
                "置零完成",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        /// <summary>
        /// Step 2: 计算 X 补偿
        /// </summary>
        private void BtnCalcX_Click(object sender, RoutedEventArgs e)
        {
            if (!double.TryParse(txtRowDiff.Text, out double rowDiff) || rowDiff <= 0)
            {
                MessageBox.Show("请输入有效的行差（正整数）", "输入错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!double.TryParse(txtPxX.Text, out double pxOffset) || pxOffset == 0)
            {
                MessageBox.Show("请先测量并输入像素偏移量", "输入错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            double pixelEq = _viewModel.CameraPixelEquivalent;
            _calibratedOffsetX = pxOffset * pixelEq / rowDiff;

            // 取绝对值，因为方向由公式自动处理
            txtResultX.Text = $"拼图X补偿 = {_calibratedOffsetX:F4} mm";
        }

        /// <summary>
        /// Step 3: 计算 Y 补偿
        /// </summary>
        private void BtnCalcY_Click(object sender, RoutedEventArgs e)
        {
            if (!double.TryParse(txtColDiff.Text, out double colDiff) || colDiff <= 0)
            {
                MessageBox.Show("请输入有效的列差（正整数）", "输入错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!double.TryParse(txtPxY.Text, out double pxOffset) || pxOffset == 0)
            {
                MessageBox.Show("请先测量并输入像素偏移量", "输入错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            double pixelEq = _viewModel.CameraPixelEquivalent;
            _calibratedOffsetY = pxOffset * pixelEq / colDiff;

            txtResultY.Text = $"拼图Y补偿 = {_calibratedOffsetY:F4} mm";
        }

        /// <summary>
        /// 应用全部计算结果到系统参数
        /// </summary>
        private void BtnApply_Click(object sender, RoutedEventArgs e)
        {
            bool hasX = !string.IsNullOrEmpty(txtResultX.Text);
            bool hasY = !string.IsNullOrEmpty(txtResultY.Text);

            if (!hasX && !hasY)
            {
                MessageBox.Show("请先计算至少一个方向的补偿值", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (hasX)
                _viewModel.StitchOffsetX = _calibratedOffsetX;
            if (hasY)
                _viewModel.StitchOffsetY = _calibratedOffsetY;

            _viewModel.Save();

            string msg = "已应用";
            if (hasX) msg += $" X补偿={_calibratedOffsetX:F4}mm";
            if (hasX && hasY) msg += "，";
            if (hasY) msg += $" Y补偿={_calibratedOffsetY:F4}mm";

            MessageBox.Show(msg, "应用完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
