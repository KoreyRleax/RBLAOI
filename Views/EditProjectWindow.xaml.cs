using RBLAOI.Core.Managers;
using RBLAOI.Core.Motion;
using RBLAOI.Models;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace RBLAOI.Views
{
    public partial class EditProjectWindow : Window, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        private void RaisePropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public double BoardWidth { get; set; }
        public double BoardHeight { get; set; }
        public double FovWidth { get; set; }
        public double FovHeight { get; set; }
        public double Overlap { get; set; }
        public int SubBoardCount { get; set; }
        public int PluginFeatureCount
        {
            get => _pluginFeatureCount;
            set { _pluginFeatureCount = value; RaisePropertyChanged(); }
        }
        private int _pluginFeatureCount = 1;

        public ObservableCollection<PinTypeParameter> PinTypeParams { get; set; }
            = new ObservableCollection<PinTypeParameter>();

        public double BoardFocusValue { get; set; }
        private double _boardFocus;

        public double CameraExposureValue { get; set; }

        // ========== 光源控制（方案级：亮度与开关；串口号/波特率/通道数为系统参数，所有方案共用） ==========
        public double LightChannel1 { get; set; }
        public double LightChannel2 { get; set; }
        public double LightChannel3 { get; set; }
        public double LightChannel4 { get; set; }
        public bool LightOn1 { get; set; } = true;
        public bool LightOn2 { get; set; } = true;
        public bool LightOn3 { get; set; } = true;
        public bool LightOn4 { get; set; } = true;

        public string Remark { get; set; }

        public EditProjectWindow(double boardWidth, double boardHeight, double fovWidth, double fovHeight,
            double overlap, int subBoardCount, int pluginFeatureCount,
            PinTypeParameter[] pinTypeParams, string remark = "",
            double boardFocus = 0, double cameraExposureTime = 0,
            double lightChannel1 = 128, double lightChannel2 = 128,
            double lightChannel3 = 128, double lightChannel4 = 128,
            bool lightOn1 = true, bool lightOn2 = true, bool lightOn3 = true, bool lightOn4 = true)
        {
            InitializeComponent();
            BoardWidth = boardWidth;
            BoardHeight = boardHeight;
            FovWidth = fovWidth;
            FovHeight = fovHeight;
            Overlap = overlap;
            SubBoardCount = subBoardCount;
            PluginFeatureCount = pluginFeatureCount;
            Remark = remark;
            _boardFocus = boardFocus;
            BoardFocusValue = boardFocus;
            CameraExposureValue = cameraExposureTime;
            LightChannel1 = lightChannel1;
            LightChannel2 = lightChannel2;
            LightChannel3 = lightChannel3;
            LightChannel4 = lightChannel4;
            LightOn1 = lightOn1;
            LightOn2 = lightOn2;
            LightOn3 = lightOn3;
            LightOn4 = lightOn4;

            // 初始化 PinTypeParams（按标记字母升序排列，保证界面行序稳定）
            PinTypeParams.Clear();
            if (pinTypeParams != null && pinTypeParams.Length > 0)
            {
                foreach (var p in pinTypeParams.OrderBy(x => x.Label, StringComparer.OrdinalIgnoreCase))
                    PinTypeParams.Add(p);
            }
            else
            {
                // 默认生成
                string[] labels = { "A", "B", "C", "D", "E", "F", "G", "H", "I", "J" };
                for (int i = 0; i < pluginFeatureCount && i < labels.Length; i++)
                {
                    PinTypeParams.Add(new PinTypeParameter { Label = labels[i] });
                }
            }

            DataContext = this;

            // 监听 PluginFeatureCount 变化，实时同步 DataGrid 行数
            PropertyChanged += OnEditProjectPropertyChanged;
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        /// <summary>
        /// 打开光源控制窗口（非模态）：4 通道亮度/开关/曝光变化时实时同步回本窗口（EditProjectWindow），
        /// 关闭后点"确定"统一保存方案。
        /// </summary>
        private void BtnLightControl_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new LightControlWindow(
                this,
                CameraExposureValue,
                LightChannel1, LightChannel2, LightChannel3, LightChannel4,
                LightOn1, LightOn2, LightOn3, LightOn4)
            {
                Owner = this
            };
            dlg.Show();
        }

        /// <summary>
        /// "移动到"按钮：Z 轴移动到该针尖的焦距。
        /// 动作完成/超时前禁用按钮，防止重复点击。
        /// </summary>
        private async void BtnMoveToFocus_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button btn && btn.DataContext is PinTypeParameter param)) return;
            if (!MotionIO.Instance.IsConnected)
            {
                MessageBox.Show("下位机未连接，无法移动Z轴。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            btn.IsEnabled = false; // 防重复点击：等待移动完成/超时
            try
            {
                bool ok = await MotionIO.Instance.MoveToAndWaitOK(z: param.Focus,
                    timeoutMs: (int)SysParam.Instance.Data.MoveZTimeout);
                MessageBox.Show(ok
                        ? $"已移动到针尖 {param.Label} 焦距 ({param.Focus:F2}mm)"
                        : $"移动到针尖 {param.Label} 焦距失败，请检查下位机",
                    "移动到", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"移动失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                btn.IsEnabled = true;
            }
        }

        /// <summary>板面焦距"移动到"按钮：Z 轴移动到板面焦距（防重复点击）</summary>
        private async void BtnMoveBoardFocus_Click(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            if (!MotionIO.Instance.IsConnected)
            {
                MessageBox.Show("下位机未连接，无法移动Z轴。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (btn != null) btn.IsEnabled = false;
            try
            {
                bool ok = await MotionIO.Instance.MoveToAndWaitOK(z: BoardFocusValue,
                    timeoutMs: (int)SysParam.Instance.Data.MoveZTimeout);
                MessageBox.Show(ok
                        ? $"已移动到板面焦距 ({BoardFocusValue:F2}mm)"
                        : "移动到板面焦距失败，请检查下位机",
                    "移动到", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"移动失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (btn != null) btn.IsEnabled = true;
            }
        }

        /// <summary>
        /// 监听属性变更，当 PluginFeatureCount 变化时同步 DataGrid 行数
        /// </summary>
        private void OnEditProjectPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PluginFeatureCount))
            {
                SyncPinTypeParams();
            }
        }

        /// <summary>
        /// 根据 PluginFeatureCount 增删 PinTypeParams 的行
        /// </summary>
        private void SyncPinTypeParams()
        {
            string[] labels = { "A", "B", "C", "D", "E", "F", "G", "H", "I", "J" };
            // 新增行（用默认值）
            while (PinTypeParams.Count < PluginFeatureCount && PinTypeParams.Count < labels.Length)
            {
                PinTypeParams.Add(new PinTypeParameter { Label = labels[PinTypeParams.Count] });
            }
            // 删除末尾行
            while (PinTypeParams.Count > PluginFeatureCount)
            {
                PinTypeParams.RemoveAt(PinTypeParams.Count - 1);
            }
        }
    }
}
