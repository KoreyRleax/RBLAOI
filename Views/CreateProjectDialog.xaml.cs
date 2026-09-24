using System.Windows;

namespace RBLAOI.Views
{
    public partial class CreateProjectDialog : Window
    {
        public string ProjectName => txtProjectName.Text.Trim();
        public double BoardWidth => double.TryParse(txtBoardWidth.Text, out double w) ? w : 200;
        public double BoardHeight => double.TryParse(txtBoardHeight.Text, out double h) ? h : 200;

        public CreateProjectDialog()
        {
            InitializeComponent();
            txtProjectName.Focus();
            txtProjectName.SelectAll();
        }

        private void BtnConfirm_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(ProjectName))
            {
                MessageBox.Show("请输入程序名称", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            this.DialogResult = true;
            this.Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = false;
            this.Close();
        }
    }
}