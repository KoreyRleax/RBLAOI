using RBLAOI.Core.Managers;
using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace RBLAOI.Views
{
    public partial class ProgramHistoryWindow : Window
    {
        public string SelectedProjectPath { get; private set; }

        public ProgramHistoryWindow()
        {
            InitializeComponent();
            LoadProjects();
        }

        private void LoadProjects()
        {
            var infos = ProjectManager.Instance.GetAllProjectInfos();

            lvProjects.ItemsSource = infos.Select(i => new ProjectListItem
            {
                ProjectName = i.ProjectName,
                Remark = i.Remark,
                Size = GetDirectorySize(i.ProjectPath),
                ModifyTime = i.ModifyTime.ToString("yyyy-MM-dd HH:mm:ss"),
                ProjectPath = i.ProjectPath
            }).ToList();
        }

        private string GetDirectorySize(string dirPath)
        {
            if (!System.IO.Directory.Exists(dirPath)) return "0 B";
            try
            {
                long total = System.IO.Directory.GetFiles(dirPath, "*", System.IO.SearchOption.AllDirectories)
                    .Sum(f => new System.IO.FileInfo(f).Length);
                return FormatSize(total);
            }
            catch
            {
                return "未知";
            }
        }

        private string FormatSize(long bytes)
        {
            if (bytes >= 1073741824)  // 1 GB
                return $"{bytes / 1073741824.0:F2} GB";
            if (bytes >= 1048576)     // 1 MB
                return $"{bytes / 1048576.0:F2} MB";
            if (bytes >= 1024)        // 1 KB
                return $"{bytes / 1024.0:F1} KB";
            return $"{bytes} B";
        }

        private void LvProjects_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var selected = lvProjects.SelectedItem as ProjectListItem;
            if (selected != null)
            {
                SelectedProjectPath = selected.ProjectPath;
                DialogResult = true;
                Close();
            }
        }

        private void DeleteMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var selected = lvProjects.SelectedItem as ProjectListItem;
            if (selected == null) return;

            var result = MessageBox.Show($"确定要删除方案 \"{selected.ProjectName}\" 吗？\n此操作不可恢复！", "确认删除",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;

            if (ProjectManager.Instance.DeleteProject(selected.ProjectName))
            {
                LoadProjects(); // 刷新列表
            }
            else
            {
                MessageBox.Show("删除方案失败", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OpenInExplorerMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var selected = lvProjects.SelectedItem as ProjectListItem;
            if (selected == null || string.IsNullOrEmpty(selected.ProjectPath)) return;

            if (Directory.Exists(selected.ProjectPath))
            {
                System.Diagnostics.Process.Start("explorer.exe", selected.ProjectPath);
            }
            else
            {
                MessageBox.Show($"路径不存在: {selected.ProjectPath}", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void RenameMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var selected = lvProjects.SelectedItem as ProjectListItem;
            if (selected == null) return;

            var dlg = new InputDialog
            {
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Title = "重命名方案",
                InputText = selected.ProjectName
            };

            if (dlg.ShowDialog() == true)
            {
                string newName = dlg.InputText.Trim();
                if (string.IsNullOrEmpty(newName) || newName == selected.ProjectName) return;

                if (ProjectManager.Instance.RenameProject(selected.ProjectName, newName))
                {
                    LoadProjects(); // 刷新列表
                }
                else
                {
                    MessageBox.Show("重命名失败，请检查名称是否已存在", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
    }

    public class ProjectListItem
    {
        public string ProjectName { get; set; }
        public string Remark { get; set; }
        public string Size { get; set; }
        public string ModifyTime { get; set; }
        public string ProjectPath { get; set; }
    }

    public partial class InputDialog : Window
    {
        public string InputText { get; set; }

        public InputDialog()
        {
            Width = 400;
            Height = 180;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            Background = System.Windows.Media.Brushes.Transparent;

            var border = new Border
            {
                Background = TryFindResource("ThemeBg") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.White,
                BorderBrush = TryFindResource("ThemeBorder") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Gray,
                BorderThickness = new Thickness(1)
            };

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var titleBar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Background = TryFindResource("ThemePanelBg") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.LightGray,
            };
            var titleText = new TextBlock
            {
                Text = "重命名",
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = TryFindResource("ThemeText") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Black
            };
            titleBar.Children.Add(titleText);

            Grid.SetRow(titleBar, 0);
            grid.Children.Add(titleBar);

            var contentPanel = new StackPanel { Margin = new Thickness(20), VerticalAlignment = VerticalAlignment.Center };
            var label = new TextBlock
            {
                Text = "请输入新的方案名称:",
                FontSize = 13,
                Margin = new Thickness(0, 0, 0, 8),
                Foreground = TryFindResource("ThemeText") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Black
            };

            var textBox = new TextBox
            {
                FontSize = 14,
                Height = 30,
                Margin = new Thickness(0, 0, 0, 12)
            };
            textBox.TextChanged += (s, e) => InputText = textBox.Text;

            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var okBtn = new Button { Content = "确定", Width = 80, Height = 30, Margin = new Thickness(0, 0, 8, 0), FontSize = 13 };
            var cancelBtn = new Button { Content = "取消", Width = 80, Height = 30, FontSize = 13 };

            okBtn.Click += (s, e) => { DialogResult = true; Close(); };
            cancelBtn.Click += (s, e) => { DialogResult = false; Close(); };

            btnPanel.Children.Add(okBtn);
            btnPanel.Children.Add(cancelBtn);

            contentPanel.Children.Add(label);
            contentPanel.Children.Add(textBox);
            contentPanel.Children.Add(btnPanel);

            Grid.SetRow(contentPanel, 1);
            grid.Children.Add(contentPanel);

            border.Child = grid;
            Content = border;

            Loaded += (s, e) =>
            {
                textBox.Text = InputText ?? "";
                textBox.Focus();
                Dispatcher.BeginInvoke(new Action(() => textBox.SelectAll()));
            };
        }
    }
}
