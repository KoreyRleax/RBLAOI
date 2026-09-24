using RBLAOI.Core.Managers;
using System.Windows;

namespace RBLAOI.Views
{
    public partial class AdjustBoardHeightWindow : Window
    {
        public double BoardWidth { get; private set; }
        public double BoardHeight { get; private set; }

        public AdjustBoardHeightWindow(double boardLength, double boardWidth)
        {
            InitializeComponent();
            BoardWidth = boardWidth;
            BoardHeight = boardLength;

            txtBoardLength.Text = BoardWidth.ToString("0.##");
            txtBoardWidth.Text = BoardHeight.ToString("0.##");
        }

        private void BtnConfirm_Click(object sender, RoutedEventArgs e)
        {
            if (!double.TryParse(txtBoardLength.Text, out double width) || width <= 0)
            {
                MessageBox.Show("请输入有效的板宽", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!double.TryParse(txtBoardWidth.Text, out double height) || height <= 0)
            {
                MessageBox.Show("请输入有效的板高", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            BoardWidth = width;
            BoardHeight = height;

            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
