using System.ComponentModel;
using System.Windows;

namespace RBLAOI.Views
{
    public partial class ProgressDialog : Window
    {
        private bool _isCompleted;

        public ProgressDialog()
        {
            InitializeComponent();
        }

        public void UpdateProgress(int current, int total)
        {
            int pct = total > 0 ? (int)((double)current / total * 100) : 0;
            progressBar.Value = pct;
            txtProgress.Text = $"正在加载图片... {current}/{total}";
        }

        public void MarkCompleted()
        {
            _isCompleted = true;
            Close();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!_isCompleted)
                e.Cancel = true;
            base.OnClosing(e);
        }
    }
}
