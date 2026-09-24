using RBLAOI.Core;
using RBLAOI.Core.Device;
using RBLAOI.Core.Managers;
using RBLAOI.Core.Utility;
using RBLAOI.Core.Vision;
using RBLAOI.Models;
using RBLAOI.ViewModels;
using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace RBLAOI.Views
{
    public partial class MainWindow : Window
    {
        // 记录当前激活的菜单按钮
        private Button _currentActiveButton;
        private Dictionary<string, Action> _actionMap;
        private MainViewModel _viewModel;

        // 标记工具栏
        private List<Button> _markerButtons = new List<Button>();
        private Button _btnDelete;
        private Button _btnClearAll;

        // 底部状态栏文字颜色：报警→标准红 #FF0000，未就绪→橙 #EF6C00，其余(待机/运行等)→绿 #558B2F
        //（复用于两个状态 TextBlock，避免每 500ms 新建画笔）
        private static readonly SolidColorBrush _statusAlarmBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x00, 0x00));
        private static readonly SolidColorBrush _statusNotReadyBrush = new SolidColorBrush(Color.FromRgb(0xEF, 0x6C, 0x00));
        private static readonly SolidColorBrush _statusNormalBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x8B, 0x2F));

        public MainWindow()
        {
            // 启动时杀死其他RBLAOI进程，避免2D/3D网口被旧进程占用
            try
            {
                int currentId = Process.GetCurrentProcess().Id;
                foreach (var proc in Process.GetProcessesByName("RBLAOI").Where(p => p.Id != currentId))
                {
                    Log.Warning($"发现旧进程 PID={proc.Id}，正在终止...");
                    proc.Kill();
                    proc.WaitForExit(3000);
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"清理旧进程异常: {ex.Message}");
            }

            InitializeComponent();
            // 初始化 ViewModel
            _viewModel = new MainViewModel();

            // 当前UI 订阅 ViewModel 的状态改变事件，可用于在状态栏显示操作反馈
            _viewModel.StatusChanged += OnStatusChanged;
            _viewModel.OnThemeChanged += RefreshSubMenu;
            _viewModel.EditModeChanged += RefreshEditSubMenu;
            _viewModel.ActionBusyStateChanged += OnActionBusyStateChanged;
            _viewModel.PauseStateChanged += OnPauseStateChanged;
            _viewModel.DemoModeStateChanged += OnDemoModeStateChanged;

            // 标记工具栏状态改变事件
            _viewModel.MarkerToolbarStateChanged += OnMarkerToolbarStateChanged;

            // 工作模式切换事件：刷新运行栏「单板模式/连续模式」按钮高亮
            _viewModel.ModeChanged += UpdateModeButtonHighlight;

            // 3. 设置数据上下文
            DataContext = _viewModel;

            // 4. 初始化功能映射
            InitializeActionMap();

            // 5. 设置文件菜单为激活状态并加载子按钮（替代XAML中的硬编码按钮）
            SwitchSubMenu("file");
            SetActiveMenu(btnFile);

            // 底部状态栏——定时刷新三段：状态(StateText) | 运行信息(StatusText) | 报警信息(AlarmText)
            // 状态文字动态变色：报警→红，未就绪→黄，其余(待机/运行等)→绿
            var statusTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            statusTimer.Tick += (s, e) =>
            {
                var monitor = DeviceMonitor.Instance;
                txtDeviceState.Text = monitor.StateText;
                txtInfo.Text = _viewModel.StatusText;
                string alarm = monitor.AlarmText;
                txtAlarm.Text = alarm;

                // 状态/运行信息文字变色：报警→红，未就绪→黄，其余(待机/运行等)→绿；报警段固定红
                SolidColorBrush brush;
                if (!string.IsNullOrEmpty(alarm))
                    brush = _statusAlarmBrush;
                else if (monitor.State == DeviceState.NotReady)
                    brush = _statusNotReadyBrush;
                else
                    brush = _statusNormalBrush;
                txtDeviceState.Foreground = brush;
                txtInfo.Foreground = brush;
                txtAlarm.Foreground = _statusAlarmBrush;
            };
            statusTimer.Start();
            // ✅ 窗口加载完成后刷新图标
            this.Loaded += (s, e) =>
            {
                Dispatcher.Invoke(new Action(() =>
                {
                    RefreshAllButtonIcons();
                }), System.Windows.Threading.DispatcherPriority.Background);
                // 窗口加载完成后自动连接2D/3D网口（共享 Task，StartPostInitTasks 中统一检查）
                _ = _viewModel.ConnectVision();
                // 窗口加载完成后执行初始化任务链（图片检查 → 轨宽 → 回零弹窗）
                _viewModel.StartPostInitTasks();
                // 初始化标记工具栏
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    UpdateMarkerToolbar();
                }), System.Windows.Threading.DispatcherPriority.Background);
                // 初始化查看模式工具栏
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    UpdateViewToolbar();
                }), System.Windows.Threading.DispatcherPriority.Background);
            };

        }
        /// <summary>
        /// 窗口关闭前清理资源并强制退出
        /// </summary>
        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_viewModel == null) return;

            // 询问是否保存方案
            e.Cancel = true; // 先取消关闭，避免窗口销毁过程中 MessageBox 阻塞
            var result = MessageBox.Show("是否保存当前方案？", "退出确认",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (result == MessageBoxResult.Cancel)
                return; // 取消 → 不退出
            if (result == MessageBoxResult.Yes)
                _viewModel.SaveProject();
            // No → 不保存直接退出

            // 取消所有正在运行的操作，断开连接，清理资源
            _viewModel.Cleanup();

            // OS 级强制终止，不经过 CLR（Halcon 原生窗口销毁后 CLR 已不安全）
            Process.GetCurrentProcess().Kill();
        }
        /// <summary>
        /// 窗口内容渲染完成后初始化
        /// </summary>
        private void Window_ContentRendered(object sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _viewModel.SetHalconWindow(MainHWindow);// 将Halcon窗口句柄传递给ViewModel，Vison内部会进行初始化
                                                        // _viewModel.RefreshDisplayAfterWindowReady();// 窗口就绪后重新显示拼接图（修复启动不显示问题）

            }), System.Windows.Threading.DispatcherPriority.Background);
        }
        private void OnStatusChanged(string message)
        {
            // 右侧固定按钮区已替代原机器状态文字，运行信息改到底部状态栏显示（500ms 定时器以 StatusText 为准）
            txtInfo.Text = message;
        }

        /// <summary>
        /// 刷新运行栏「单板模式/连续模式」按钮高亮（互斥：当前模式高亮，另一个恢复普通色）
        /// </summary>
        private void UpdateModeButtonHighlight()
        {
            var activeBrush = new SolidColorBrush(Color.FromRgb(80, 150, 255));
            var normalBrush = (Brush)FindResource("ThemeButton");
            foreach (var child in SubMenuPanel.Children)
            {
                if (child is Button btn && btn.Tag != null)
                {
                    string tag = btn.Tag.ToString();
                    if (tag == "single_mode" || tag == "continuous_mode" || tag == "demo_mode")
                    {
                        // 单板=非连续模式（演示时单板仍激活）；连续=连续模式；演示=演示模式选中
                        bool isCurrent = tag == "single_mode" ? !_viewModel.IsContinuousMode
                                        : tag == "continuous_mode" ? _viewModel.IsContinuousMode
                                        : _viewModel.IsDemoMode;
                        btn.Background = isCurrent ? activeBrush : normalBrush;
                    }
                }
            }
        }

        /// <summary>
        /// 根据 ViewModel 的动作忙状态启用/禁用对应按钮
        /// </summary>
        private void OnActionBusyStateChanged(string tag, bool isBusy)
        {
            if (Application.Current.Dispatcher.CheckAccess())
            {
                SetActionButtonState(tag, !isBusy);
            }
            else
            {
                Application.Current.Dispatcher.Invoke(() => SetActionButtonState(tag, !isBusy));
            }
        }

        private void OnPauseStateChanged()
        {
            if (Application.Current.Dispatcher.CheckAccess())
                UpdatePauseButtonText();
            else
                Application.Current.Dispatcher.Invoke(() => UpdatePauseButtonText());
        }

        private void OnDemoModeStateChanged()
        {
            if (Application.Current.Dispatcher.CheckAccess())
                UpdateDemoModeButtonText();
            else
                Application.Current.Dispatcher.Invoke(() => UpdateDemoModeButtonText());
        }

        private void UpdateDemoModeButtonText()
        {
            foreach (var child in SubMenuPanel.Children)
            {
                if (child is Button btn && btn.Tag?.ToString() == "demo_mode")
                {
                    btn.Content = _viewModel.DemoModeText;
                    // 刷新图标：直接刷新所有按钮图标
                    Dispatcher.BeginInvoke(new Action(RefreshAllButtonIcons),
                        System.Windows.Threading.DispatcherPriority.Background);
                    Dispatcher.BeginInvoke(new Action(UpdateModeButtonHighlight),
                        System.Windows.Threading.DispatcherPriority.Background);   // 选中/取消时同步高亮
                    return;
                }
            }
        }

        private void UpdatePauseButtonText()
        {
            foreach (var child in SubMenuPanel.Children)
            {
                if (child is Button btn && btn.Tag?.ToString() == "pause_check")
                {
                    btn.Content = _viewModel.PauseCheckText;
                    return;
                }
            }
        }

        private void SetActionButtonState(string tag, bool enabled)
        {
            foreach (var child in SubMenuPanel.Children)
            {
                if (child is Button btn && btn.Tag?.ToString() == tag)
                {
                    btn.IsEnabled = enabled;
                    return;
                }
            }
        }

        /// <summary>
        /// 初始化功能映射
        /// </summary>
        private void InitializeActionMap()
        {
            // 使用 Lambda 表达式，而不是直接引用方法
            _actionMap = new Dictionary<string, Action>
            {
                // 文件菜单
                ["program_history"] = () => _viewModel.ShowProgramHistory(),
                ["new_project"] = () => _viewModel.CreateNewProject(),
                ["open_project"] = () => _viewModel.OpenProject(),
                ["save"] = () => _viewModel.SaveProject(),
                ["save_as"] = () => _viewModel.SaveAsProject(),
                ["history_result"] = () => _viewModel.ShowHistoryResult(),
                ["precision_report"] = () => _viewModel.ShowPrecisionReportList(),
                ["send_program"] = () => _viewModel.SendProgram(),
                ["close_project"] = () => _viewModel.CloseProject(),
                ["delete_project"] = () => _viewModel.DeleteCurrentProject(),
                ["exit_app"] = () => this.Close(),
                ["edit_project"] = () => _viewModel.ShowEditProjectDialog(),
                ["toggle_edit_project"] = () => _viewModel.ShowEditProjectDialog(),

                // 编辑菜单
                ["toggle_edit"] = () => _viewModel.ToggleEdit(),
                ["camera_view"] = () => _viewModel.ShowCameraView(),
                ["lock_cad"] = () => _viewModel.LockCAD(),
                ["add_pin"] = () => _viewModel.AddPin(),
                ["delete_pin"] = () => _viewModel.DeletePin(),
                ["match_pin"] = () => _viewModel.MatchPin(),
                ["calibrate_mark"] = () => _viewModel.CalibrateMark(),

                // 功能菜单
                ["servo_home"] = () => _viewModel.ServoHome(),
                ["orbit_home"] = () => _viewModel.OrbitHome(),
                ["adjust_width"] = () => _viewModel.AdjustBoardHeight(),
                ["transport_board"] = () => _ = _viewModel.TransportBoard(),
                ["send_exit"] = () => _ = _viewModel.SendToExit(),
                ["send_entrance"] = () => _viewModel.SendToEntrance(),
                ["grab_full"] = () => _viewModel.GrabFullImage(),
                ["grab_fov"] = () => _viewModel.GrabFOV(),
                ["auto_check"] = () => _viewModel.StartAutoCheck(),
                ["manual_check"] = () => _viewModel.StartManualCheck(),
                ["spot_check"] = () => _viewModel.StartSpotCheck(),
                ["mark_check"] = () => _viewModel.StartMarkCheck(),
                ["end_batch"] = () => _viewModel.EndBatch(),
                ["auto_focus"] = () => _viewModel.AutoFocus(),
                ["stop"] = () => _viewModel.StopAction(),

                // 运行菜单
                ["start_check"] = () => _viewModel.StartCheck(),
                ["pause_check"] = () => _viewModel.PauseCheck(),
                ["stop_check"] = () => _viewModel.StopCheck(),
                ["continuous_run"] = () => _viewModel.ContinuousRun(),
                ["step_run"] = () => _viewModel.StepRun(),
                ["demo_mode"] = () => _viewModel.SetDemoMode(true),
                ["precision_check"] = () => _viewModel.StartPrecisionCheck(),
                ["reset_machine"] = () => _ = _viewModel.ResetMachine(),

                // 固定按钮区
                ["start_resume"] = () => _viewModel.StartOrResumeCheck(),
                ["stop_work"] = () =>
                {
                    // 纯暂停语义（恢复只能点「开始」）：全图采集→暂停采集；连续模式→暂停当前动作；单板检测→暂停检测
                    if (_viewModel.IsFullImageGrabRunning) _viewModel.PauseFullImageGrab();
                    else if (_viewModel.IsContinuousFlowRunning) _viewModel.PauseContinuousFlow();
                    else _viewModel.PauseCheckOnly();
                },
                ["home_all"] = () => _viewModel.HomeAllAxes(),
                ["single_mode"] = () => _viewModel.SetWorkMode(false),
                ["continuous_mode"] = () => _viewModel.SetWorkMode(true),

                // 设置菜单
                ["system_param"] = () => _viewModel.ShowSystemParam(),
                ["camera_param"] = () => _viewModel.ShowCameraParam(),
                ["motion_param"] = () => _viewModel.ShowMotionParam(),
                ["io_config"] = () => _viewModel.ShowIOConfig(),
                ["unit_test1"] = () => _viewModel.RunUnitTest1(),
                ["unit_test2"] = () => _viewModel.RunUnitTest2(),
                ["unit_test3"] = () => _viewModel.RunUnitTest3(),
                ["theme_setting"] = () => _viewModel.ThemeSetting(),
                ["personalization"] = () => _viewModel.ShowPersonalization(),
                // 帮助菜单
                ["user_manual"] = () => _viewModel.ShowUserManual(),
                ["about"] = () => _viewModel.ShowAbout()
            };
        }
        /// <summary>
        /// 主菜单点击事件
        /// </summary>
        private void MainMenu_Click(object sender, RoutedEventArgs e)
        {
            Button clickedButton = sender as Button;
            if (clickedButton == null) return;

            // 如果点击的是已经激活的菜单，不做任何操作
            if (_currentActiveButton == clickedButton) return;

            // 更新激活状态
            SetActiveMenu(clickedButton);

            // 根据点击的菜单切换子菜单按钮
            string menuTag = clickedButton.Tag.ToString();
            SwitchSubMenu(menuTag);
        }

        /// <summary>
        /// 设置激活的菜单按钮样式
        /// </summary>
        private void SetActiveMenu(Button activeButton)
        {
            // 恢复上一个按钮的样式
            if (_currentActiveButton != null)
            {
                _currentActiveButton.Style = (Style)FindResource("NavMenuButtonStyle");
            }

            // 设置新按钮的激活样式
            activeButton.Style = (Style)FindResource("NavMenuButtonActiveStyle");
            _currentActiveButton = activeButton;
        }

        /// <summary>
        /// 切换子菜单按钮（根据主菜单）
        /// </summary>
        private void SwitchSubMenu(string menuTag)
        {
            // 清空现有的子菜单按钮
            SubMenuPanel.Children.Clear();

            // 根据菜单标签添加对应的子按钮
            switch (menuTag)
            {
                case "file":
                    AddFileSubMenu();
                    break;
                case "edit":
                    Addtoggle_editSubMenu();
                    break;
                case "function":
                    AddFunctionSubMenu();
                    break;
                case "run":
                    AddRunSubMenu();
                    break;
                case "setting":
                    AddSettingSubMenu();
                    break;
                case "help":
                    AddHelpSubMenu();
                    break;
            }
            // ✅ 添加完成后，延迟刷新所有按钮图标
            Dispatcher.BeginInvoke(new Action(() =>
            {
                RefreshAllButtonIcons();
            }), System.Windows.Threading.DispatcherPriority.Background);

            // 子菜单切换后布局可能变化，延迟重绘 Halcon 底图防止丢失
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (MainHWindow != null && MainHWindow.IsVisible)
                {
                    // 避免新方案无图时显示旧方案缓存底图
                    if (_viewModel.HasStitchedImage)
                    {
                        MainHWindow.RedrawStitchedImage();
                        MainHWindow.DrawOverlays?.Invoke(MainHWindow.HalconWindow);
                    }
                }
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        /// <summary>
        /// 刷新子菜单（重新生成当前选中的菜单）
        /// </summary>
        private void RefreshSubMenu()
        {
            // 刷新当前激活菜单的样式
            //RefreshActiveMenuStyle();
            // ✅ 刷新主菜单样式
            RefreshMainMenuStyles();

            // 刷新当前激活菜单的样式（确保激活状态正确）
            if (_currentActiveButton != null)
            {
                _currentActiveButton.Style = (Style)FindResource("NavMenuButtonActiveStyle");
            }

            // 获取当前激活的菜单标签
            string currentTag = _currentActiveButton?.Tag?.ToString();
            if (!string.IsNullOrEmpty(currentTag))
            {
                // 重新生成子菜单
                SwitchSubMenu(currentTag);
            }
        }

        /// <summary>
        /// 刷新编辑菜单的子按钮
        /// </summary>
        private void RefreshEditSubMenu()
        {
            // 如果当前激活的不是编辑菜单，不需要刷新
            if (_currentActiveButton != btnEdit) return;

            // 重新生成编辑菜单
            SwitchSubMenu("edit");
        }

        /// <summary>
        /// 刷新主菜单所有按钮的样式
        /// </summary>
        private void RefreshMainMenuStyles()
        {
            // 获取所有主菜单按钮
            var buttons = new[] { btnFile, btnEdit, btnFunction, btnRun, btnSetting, btnHelp };

            foreach (var btn in buttons)
            {
                if (btn == _currentActiveButton)
                {
                    btn.Style = (Style)FindResource("NavMenuButtonActiveStyle");
                }
                else
                {
                    btn.Style = (Style)FindResource("NavMenuButtonStyle");
                }
            }
        }

        #region 子菜单定义


        /// <summary>
        /// 文件菜单的子按钮（不需要在Content中加图标）
        /// </summary>
        private void AddFileSubMenu()
        {
            AddSubButton("方案历史", "program_history");
            AddSubButton("新建方案", "new_project");
            AddSubButton("打开方案", "open_project");
            AddSubButton("修改方案", "toggle_edit_project");
            AddSubButton("保存", "save");
            AddSubButton("另存为", "save_as");
            AddSeparator();
            AddSubButton("历史结果", "history_result");
            AddSeparator();
            AddSubButton("删除方案", "delete_project");
        }

        /// <summary>
        /// 编辑菜单的子按钮
        /// </summary>
        private void Addtoggle_editSubMenu()
        {
            string toggle_editButtonText = _viewModel.IsEditMode ? "退出编辑" : "编辑";
            AddSubButton(toggle_editButtonText, "toggle_edit");  // 注意 tag 改为 toggle_toggle_edit
            AddSubButton("相机视图", "camera_view");
            AddSubButton("锁定CAD", "lock_cad");
            AddSubButton("增加PIN", "add_pin");
            AddSubButton("删除PIN", "delete_pin");
            AddSubButton("匹配PIN", "match_pin");
            AddSubButton("校正MARK", "calibrate_mark");
        }

        /// <summary>
        /// 功能菜单的子按钮
        /// </summary>
        private void AddFunctionSubMenu()
        {
            AddSubButton("相机归零", "servo_home");
            AddSubButton("轨道归零", "orbit_home");
            AddSubButton("调整轨宽", "adjust_width");
            AddSubButton("运送基板", "transport_board");
            AddSubButton("送到出口", "send_exit");
            AddSubButton("送到入口", "send_entrance");
        }

        /// <summary>
        /// 运行菜单的子按钮
        /// </summary>
        private void AddRunSubMenu()
        {
            // 工作模式切换(高亮):单板=默认,连续=入板→检测→出板循环,演示=单板下循环检测（选中高亮，停止由停止/复位按钮承担）
            AddSubButton("单板模式", "single_mode");
            AddSubButton("连续模式", "continuous_mode");
            AddSubButton("演示模式", "demo_mode");
            AddSeparator();
            AddSubButton(" 采集全图", "grab_full");
            AddSubButton(" 采集FOV", "grab_fov");
            AddSubButton("开始检测", "start_check");
            AddSubButton(" 暂停检测", "pause_check");
            AddSeparator();
            AddSubButton("精度检测", "precision_check");
            AddSeparator();
            AddSubButton("精度报告", "precision_report");

            // 模式按钮初始高亮（默认单板模式）
            Dispatcher.BeginInvoke(new Action(UpdateModeButtonHighlight), System.Windows.Threading.DispatcherPriority.Background);
        }

        /// <summary>
        /// 设置菜单的子按钮
        /// </summary>
        private void AddSettingSubMenu()
        {
            AddSubButton("系统参数", "system_param");
            AddSubButton("相机参数", "camera_param");
            AddSubButton("运动参数", "motion_param");
            AddSubButton("IO配置", "io_config");
            AddSubButton("主题切换", "theme_setting");
            AddSubButton("个性化设置", "personalization");
        }

        /// <summary>
        /// 帮助菜单的子按钮
        /// </summary>
        private void AddHelpSubMenu()
        {
            AddSubButton("使用说明", "user_manual");
            AddSubButton("关于", "about");
            AddSeparator();
            AddSubButton("单元测试1", "unit_test1");
            AddSubButton("单元测试2", "unit_test2");
            AddSubButton("单元测试3", "unit_test3");
        }

        #endregion

        #region 辅助方法
        /// <summary>
        /// 添加分隔线（垂直分隔符）
        /// </summary>
        private void AddSeparator()
        {
            Rectangle separator = new Rectangle
            {
                Width = 1,
                Height = 70,  // ✅ 改为 70
                Fill = (System.Windows.Media.Brush)FindResource("ThemeBorder"),
                Margin = new Thickness(4, 4, 4, 4),
                VerticalAlignment = VerticalAlignment.Center  // ✅ 添加垂直居中
            };
            SubMenuPanel.Children.Add(separator);
        }

        /// <summary>
        /// 子菜单按钮点击事件
        /// </summary>
        private void SubMenuButton_Click(object sender, RoutedEventArgs e)
        {
            Button btn = sender as Button;
            string tag = btn.Tag?.ToString();
            string text = btn.Content?.ToString();

            if (string.IsNullOrEmpty(tag)) return;

            // 查找并执行对应的功能
            if (_actionMap != null && _actionMap.TryGetValue(tag, out var action))
            {
                action?.Invoke();
            }
            else
            {
                MessageBox.Show($"功能开发中: {text}", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }

        }

        #endregion

        /// <summary>
        /// 添加子菜单按钮（正方形，图标在上文字在下）
        /// </summary>
        private void AddSubButton(string text, string tag, string icon = "🔧")
        {
            Button btn = new Button
            {
                Content = text,
                Tag = tag,
                Style = (Style)FindResource("ActionButtonStyle"),
                Margin = new Thickness(4, 4, 4, 4)
            };

            // 动态设置图标（可后续替换为图片）
            // 这里通过修改模板中的TextBlock来实现
            btn.Loaded += (s, e) =>
            {
                // 查找模板中的图标TextBlock并设置图标
                var iconText = FindVisualChild<TextBlock>(btn, "IconText");
                if (iconText != null)
                {
                    // 根据文字内容设置不同图标
                    string iconChar = GetIconForButton(text);
                    iconText.Text = iconChar;
                }
            };

            btn.Click += SubMenuButton_Click;
            SubMenuPanel.Children.Add(btn);
        }
        /// <summary>
        /// 刷新所有子菜单按钮的图标
        /// </summary>
        private void RefreshAllButtonIcons()
        {
            foreach (var child in SubMenuPanel.Children)
            {
                if (child is Button btn)
                {
                    btn.ApplyTemplate();
                    var iconText = btn.Template.FindName("IconText", btn) as TextBlock;
                    if (iconText != null)
                    {
                        string text = btn.Content.ToString();
                        string iconChar = GetIconForButton(text);
                        iconText.Text = iconChar;
                    }
                }
            }
        }
        /// <summary>
        /// 根据按钮文字获取对应的图标
        /// </summary>
        private string GetIconForButton(string text)
        {
            if (text.Contains("方案历史")) return "🕒";
            if (text.Contains("新建方案")) return "📄";
            if (text.Contains("打开方案")) return "📂";
            if (text.Contains("修改方案")) return "📝";
            if (text.Contains("历史结果")) return "🆗";
            if (text.Contains("保存")) return "💾";
            if (text.Contains("另存为")) return "🗂️";
            if (text.Contains("退出编辑")) return "🚪";
            if (text.Contains("相机视图")) return "📷";
            if (text.Contains("锁定CAD")) return "🔒";
            if (text.Contains("增加PIN")) return "➕";
            if (text.Contains("删除PIN")) return "➖";
            if (text.Contains("匹配PIN")) return "🔄";
            if (text.Contains("校正MARK")) return "🎯";
            if (text.Contains("相机归零")) return "📷";
            if (text.Contains("轨道归零")) return "🚂";
            if (text.Contains("伺服归零")) return "🔄";
            if (text.Contains("调整轨宽")) return "📏";
            if (text.Contains("运送基板")) return "🚚";
            if (text.Contains("送到出口")) return "📤";
            if (text.Contains("送到入口")) return "📥";
            if (text.Contains("采集全图")) return "📸";
            if (text.Contains("采集FOV")) return "🔍";
            if (text.Contains("自动检测")) return "🤖";
            if (text.Contains("手动检测")) return "✋";
            if (text.Contains("停止")) return "⏹️";
            if (text.Contains("开始检测")) return "▶️";
            if (text.Contains("暂停检测")) return "⏯️";
            if (text.Contains("继续检测")) return "▶️";
            if (text.Contains("停止检测")) return "⏹️";
            if (text.Contains("连续运行")) return "🔄";
            if (text.Contains("单步运行")) return "⏭️";
            if (text.Contains("演示模式")) return "🎪";
            if (text.Contains("停止演示")) return "⏹️";
            if (text.Contains("精度检测")) return "📏";
            if (text.Contains("系统参数")) return "⚙️";
            if (text.Contains("相机参数")) return "📷";
            if (text.Contains("运动参数")) return "🎮";
            if (text.Contains("采集设置")) return "⚙️";
            if (text.Contains("IO配置")) return "🔌";
            if (text.Contains("使用说明")) return "📖";
            if (text.Contains("关于")) return "ℹ️";
            if (text.Contains("单元测试")) return "❓";
            if (text.Contains("主题切换")) return "🌈";
            if (text.Contains("删除方案")) return "🗑";
            if (text.Contains("精度报告")) return "📊";
            if (text.Contains("复位")) return "🔁";
            if (text.Contains("个性化设置")) return "🎨";

            return "🔧"; // 默认图标
        }

        /// <summary>
        /// 辅助方法：查找VisualTree中的子元素
        /// </summary>
        private T FindVisualChild<T>(DependencyObject parent, string name) where T : FrameworkElement
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T t && t.Name == name)
                    return t;

                var result = FindVisualChild<T>(child, name);
                if (result != null)
                    return result;
            }
            return null;
        }
        private void TextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox textBox && textBox.DataContext is MainViewModel vm)
            {
                // 失去焦点时格式化显示
                if (double.TryParse(textBox.Text, out double value))
                {
                    textBox.Text = value.ToString("0.##");
                    // 更新绑定源
                    textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                }
            }
        }
        private void dgPinResults_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            // 右键不改变 SelectedItem，所以从鼠标位置找到被右击的行
            var pin = FindPinResultAtMousePosition();
            if (pin != null)
            {
                var menu = new ContextMenu();
                var item = new MenuItem { Header = "定位到该PIN" };
                item.Click += (s, args) =>
                {
                    _viewModel.NavigateToDetectIndex(pin.GridIndex);
                };
                menu.Items.Add(item);

                dgPinResults.ContextMenu = menu;
            }
            else
            {
                dgPinResults.ContextMenu = null;
            }
        }

        private PinResult FindPinResultAtMousePosition()
        {
            var pos = Mouse.GetPosition(dgPinResults);
            var hit = dgPinResults.InputHitTest(pos) as DependencyObject;
            while (hit != null && !(hit is DataGridRow))
                hit = VisualTreeHelper.GetParent(hit);
            return (hit as DataGridRow)?.DataContext as PinResult;
        }

        protected override void OnClosed(EventArgs e)
        {
            // 取消事件订阅
            _viewModel.ActionBusyStateChanged -= OnActionBusyStateChanged;
            _viewModel.PauseStateChanged -= OnPauseStateChanged;
            _viewModel.DemoModeStateChanged -= OnDemoModeStateChanged;
            _viewModel.MarkerToolbarStateChanged -= OnMarkerToolbarStateChanged;

            // 清理资源（Window_Closing 中已调用了 Environment.Exit，此处作为后备清理）
            try { _viewModel.Cleanup(); } catch { }

            base.OnClosed(e);

            // 后备强制退出（如果 Window_Closing 中的 Environment.Exit 未生效）
            Environment.Exit(0);
        }

        /// <summary>
        /// 根据 PluginFeatureCount 动态生成标记工具栏按钮
        /// </summary>
        private void UpdateMarkerToolbar()
        {
            // 清空现有按钮
            MarkerToolbar.Children.Clear();
            _markerButtons.Clear();

            int count = _viewModel.PluginFeatureCount;

            // 生成 A, B, C... 按钮
            for (int i = 0; i < count; i++)
            {
                char marker = (char)('A' + i);
                var btn = new Button
                {
                    Content = marker.ToString(),
                    Tag = i,
                    ToolTip = $"标记针型 {marker}",
                    Width = 28,
                    Height = 26,
                    Margin = new Thickness(1),
                    FontWeight = FontWeights.Bold,
                    FontSize = 14,
                    Foreground = Brushes.White,
                    Cursor = Cursors.Hand,
                    Background = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
                };
                int capturedIdx = i;
                btn.Click += (s, e) => _viewModel.SetMarkerMode(capturedIdx);
                MarkerToolbar.Children.Add(btn);
                _markerButtons.Add(btn);
            }

            // 分隔符
            MarkerToolbar.Children.Add(new Separator
            {
                Width = 1,
                Height = 20,
                Background = new SolidColorBrush(Color.FromRgb(80, 80, 80)),
                Margin = new Thickness(4, 0, 4, 0)
            });

            // 刷新方案按钮（删除按钮左边）：Segoe MDL2 Assets 线条图标，无边框、大小可控
            var btnRefresh = new Button
            {
                Content = "", // Segoe MDL2 Assets Refresh
                ToolTip = "刷新当前方案",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                Width = 28,
                Height = 26,
                Margin = new Thickness(1),
                FontSize = 15,
                Cursor = Cursors.Hand,
                Background = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
                Foreground = Brushes.White,
            };
            btnRefresh.Click += (s, e) => _viewModel.ReloadProjectCommand.Execute(null);
            MarkerToolbar.Children.Add(btnRefresh);

            // 删除按钮：Segoe MDL2 Assets 垃圾桶图标，与刷新按钮同风格
            _btnDelete = new Button
            {
                Content = "", // Segoe MDL2 Assets Delete
                ToolTip = "删除标记（点击检测位清除其标记）",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                Width = 28,
                Height = 26,
                Margin = new Thickness(1),
                FontSize = 15,
                Cursor = Cursors.Hand,
                Background = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
                Foreground = Brushes.White,
            };
            _btnDelete.Click += (s, e) => _viewModel.SetDeleteMode();
            MarkerToolbar.Children.Add(_btnDelete);

            // 清空全部按钮：Segoe MDL2 Assets 清除(×)图标
            _btnClearAll = new Button
            {
                Content = "", // Segoe MDL2 Assets Clear
                ToolTip = "清空所有标记",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                Width = 28,
                Height = 26,
                Margin = new Thickness(1),
                FontSize = 15,
                Cursor = Cursors.Hand,
                Background = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
                Foreground = Brushes.White,
            };
            _btnClearAll.Click += (s, e) => _viewModel.ClearAllMarkers();
            MarkerToolbar.Children.Add(_btnClearAll);
        }

        /// <summary>
        /// 根据 PluginFeatureCount 动态生成查看模式工具栏按钮
        /// </summary>
        private void UpdateViewToolbar()
        {
            ViewToolbar.Children.Clear();

            int count = _viewModel.PluginFeatureCount;

            // 板面图按钮（始终存在）：放大镜(MDL2) + PCB 板 Path 图标（板框+走线+引脚）
            var boardNormalBg = new SolidColorBrush(Color.FromRgb(60, 60, 60));
            var boardHoverBg = new SolidColorBrush(Color.FromRgb(100, 100, 100));
            var boardContent = new StackPanel { Orientation = Orientation.Horizontal };
            boardContent.Children.Add(new TextBlock
            {
                Text = "", // Segoe MDL2 Assets Search（放大镜）
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 13,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
            });
            boardContent.Children.Add(new Path
            {
                Stroke = Brushes.White,
                StrokeThickness = 1.3,
                Stretch = Stretch.Uniform,
                Width = 16,
                Height = 16,
                Margin = new Thickness(2, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Data = Geometry.Parse(
                    "M2,3 L14,3 L14,13 L2,13 Z " +            // 板子外框
                    "M5,6 L8,6 L8,10 L11,10 " +              // 走线
                    "M3,3 L3,1 M6,13 L6,15 M13,6 L15,6"),    // 引脚短线
            });
            var btnBoard = new Button
            {
                Content = boardContent,
                ToolTip = "查看板面拼图",
                Width = 40,
                Height = 26,
                Margin = new Thickness(1),
                Cursor = Cursors.Hand,
                Background = boardNormalBg,
                Foreground = Brushes.White,
            };
            btnBoard.MouseEnter += (s, e) => btnBoard.Background = boardHoverBg;
            btnBoard.MouseLeave += (s, e) => btnBoard.Background = boardNormalBg;
            btnBoard.Click += (s, e) => _viewModel.ShowBoardView();
            ViewToolbar.Children.Add(btnBoard);

            // 各针型查看按钮
            for (int i = 0; i < count; i++)
            {
                char marker = (char)('A' + i);
                var normalBg = new SolidColorBrush(Color.FromRgb(60, 60, 60));
                var hoverBg = new SolidColorBrush(Color.FromRgb(100, 100, 100));
                // 放大镜(MDL2) + 针型字母(默认字体粗体)：查看语义清晰，字母可区分
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(new TextBlock
                {
                    Text = "", // Segoe MDL2 Assets Search（放大镜）
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = 13,
                    Foreground = Brushes.White,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                sp.Children.Add(new TextBlock
                {
                    Text = marker.ToString(),
                    FontSize = 14,
                    FontWeight = FontWeights.Bold,
                    Foreground = Brushes.White,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(2, 0, 0, 0),
                });
                var btn = new Button
                {
                    Content = sp,
                    ToolTip = $"查看 {marker} 针尖图",
                    Width = 40,
                    Height = 26,
                    Margin = new Thickness(1),
                    Cursor = Cursors.Hand,
                    Background = normalBg,
                };
                btn.MouseEnter += (s, e) => btn.Background = hoverBg;
                btn.MouseLeave += (s, e) => btn.Background = normalBg;
                int capturedIdx = i;
                btn.Click += (s, e) => _viewModel.ShowPinTypeView(capturedIdx);
                ViewToolbar.Children.Add(btn);
            }
        }

        private void OnMarkerToolbarStateChanged()
        {
            // 每次状态改变时重建工具栏（确保 PluginFeatureCount 变化后按钮数量正确）
            UpdateMarkerToolbar();
            // 同步更新查看模式工具栏
            UpdateViewToolbar();

            int activeIdx = _viewModel.ActiveMarkerIndex;
            foreach (var btn in _markerButtons)
            {
                int idx = (int)btn.Tag;
                btn.Background = (idx == activeIdx)
                    ? new SolidColorBrush(Color.FromRgb(80, 150, 255))  // 激活色
                    : new SolidColorBrush(Color.FromRgb(60, 60, 60));   // 正常色
            }
            // 删除按钮样式
            if (_btnDelete != null)
            {
                _btnDelete.Background = (activeIdx == -2)
                    ? new SolidColorBrush(Color.FromRgb(200, 80, 80))
                    : new SolidColorBrush(Color.FromRgb(60, 60, 60));
            }
        }
    }
}