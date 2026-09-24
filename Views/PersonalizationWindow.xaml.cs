using RBLAOI.ViewModels;
using System.Windows;

namespace RBLAOI.Views
{
    public partial class PersonalizationWindow : Window
    {
        private PersonalizationViewModel _viewModel;

        public PersonalizationWindow()
        {
            InitializeComponent();
            _viewModel = new PersonalizationViewModel();
            DataContext = _viewModel;
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.Save();
            DialogResult = true;
            Close();
        }

        private void BtnExit_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel.HasChanges())
            {
                var result = MessageBox.Show("设置已更改，是否保存？", "提示",
                    MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result == MessageBoxResult.Yes)
                {
                    _viewModel.Save();
                    DialogResult = true;
                    Close();
                    return;
                }
                _viewModel.Revert();
            }
            DialogResult = false;
            Close();
        }

        protected override void OnClosed(System.EventArgs e)
        {
            _viewModel?.Dispose();
            base.OnClosed(e);
        }
    }
}
