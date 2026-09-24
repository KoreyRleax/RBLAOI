using HalconDotNet;
using MvCamCtrl.NET.CameraParams;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace RBLAOI.Core.Controls
{
    public class CustomHSmartWindowControl : HSmartWindowControlWPF
    {
        public CustomHSmartWindowControl()
        {
        }

        // ==================== 事件 ====================
        public event Action SelectInspectionModeRequested;
        public event Action<int> DetectPositionToggled;
        public event Action ExitInspectionModeRequested;
        public event Action ToggleOverlayRequested;

        // ==================== 标记模式事件 ====================
        /// <summary>特征个数（决定子菜单选项数量）</summary>
        public int PluginFeatureCount { get; set; } = 1;

        /// <summary>标记模式选中事件（参数为标记索引 0=A,1=B,...）</summary>
        public event Action<int> MarkerModeSelected;

        /// <summary>清空所有标记请求</summary>
        public event Action ClearAllMarkersRequested;
        /// <summary>移动到鼠标所在的拍照位（参数为图像坐标 row, col）</summary>
        public event Action<double, double> MoveToDetectPositionRequested;

        /// <summary>扫描此处（参数为图像行、列坐标）</summary>
        public event Action<double, double> ScanPositionRequested;

        // ==================== 叠加层过滤事件 ====================
        /// <summary>结果过滤：全部</summary>
        public event Action OverlayFilterAll;
        /// <summary>结果过滤：仅OK</summary>
        public event Action OverlayFilterOK;
        /// <summary>结果过滤：仅NG</summary>
        public event Action OverlayFilterNG;
        /// <summary>显示模式：全部信息</summary>
        public event Action OverlayModeAllInfo;
        /// <summary>显示模式：仅编号</summary>
        public event Action OverlayModeNumberOnly;
        /// <summary>切换针尖/板面图层显示</summary>
        public event Action TogglePinLayerRequested;

        /// <summary>当前是否为针尖图层模式（影响右键菜单文字）</summary>
        public bool IsPinMode { get; set; }

        // ==================== 当前过滤状态（供菜单 Radio 状态） ====================
        public string CurrentOverlayFilter { get; set; } = "All";
        public string CurrentOverlayDisplayMode { get; set; } = "AllInfo";

        // ==================== 缩放/平移通知 ====================
        /// <summary>窗口图像视口发生变化时触发（缩放、平移、居中），供外部重绘叠加层</summary>
        public event EventHandler ImagePartChanged;

        private void NotifyImagePartChanged()
        {
            ImagePartChanged?.Invoke(this, EventArgs.Empty);
        }

        // ==================== 选择模式状态 ====================
        public bool IsInSelectionMode { get; set; }
        public int GridCols { get; set; }
        public int GridRows { get; set; }
        public double FovWidthPixels { get; set; }
        public double FovHeightPixels { get; set; }
        public bool IsOverlayVisible { get; set; } = true;

        // ==================== 平移拖拽 ====================
        private bool _isPanning = false;
        private Point _panStartPoint;
        private double _panStartRow1, _panStartCol1, _panStartRow2, _panStartCol2;
        private bool _rightButtonWasDragged;
        private Point _rightButtonDownPoint;
        private const double DragThreshold = 5;
        private bool _isSuppressContextMenu = false;
        // 右键菜单位置的图像坐标（菜单弹出时捕获）
        private double _menuImageRow, _menuImageCol;

        // ==================== 拖拽整帧节流（保留字段供右键按下使用） ====================
        private DateTime _lastDragUpdate = DateTime.MinValue;

        /// <summary>获取当前拼接图（拷贝版，由 MainViewModel 设置）</summary>
        public Func<HObject> GetStitchedImage { get; set; }

        /// <summary>获取当前拼接图（原始引用，免拷贝，由 MainViewModel 设置）</summary>
        public Func<HObject> GetRawStitchedImage { get; set; }

        /// <summary>绘制叠加层委托（由 MainViewModel 设置）</summary>
        public Action<HWindow> DrawOverlays { get; set; }

        /// <summary>清空窗口 → 显示拼接图（免拷贝）</summary>
        public void RedrawStitchedImage()
        {
            var hw = HalconWindow;
            if (hw == null) return;
            try
            {
                // 优先使用原始引用（免拷贝），回退到拷贝版
                var img = GetRawStitchedImage?.Invoke();
                bool isRaw = (img != null && img.IsInitialized());
                if (!isRaw)
                {
                    img = GetStitchedImage?.Invoke();
                    if (img == null || !img.IsInitialized()) return;
                }

                HOperatorSet.SetWindowParam(hw, "flush", "false");
                HOperatorSet.ClearWindow(hw);
                HOperatorSet.DispObj(img, hw);
                if (!isRaw) img.Dispose(); // 只有拷贝版才需要释放
                HOperatorSet.SetWindowParam(hw, "flush", "true");
            }
            catch { }
        }

        // ==================== 滚轮缩放 ====================

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            if (IsInSelectionMode)
            {
                // 选择模式下允许缩放，不要设置 e.Handled = true，否则基类的缩放命令无法执行
                base.OnMouseWheel(e);
                NotifyImagePartChanged();
                return;
            }
            base.OnMouseWheel(e);
            NotifyImagePartChanged();
        }

        // ==================== 左键事件 ====================

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            if (IsInSelectionMode && this.HalconWindow != null)
            {
                HandlePositionClick(e);
                e.Handled = true;
                return;
            }
            e.Handled = true;
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            e.Handled = true;
        }

        protected override void OnMouseDoubleClick(MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                e.Handled = true;
                return;
            }
            base.OnMouseDoubleClick(e);
        }

        // ==================== 鼠标移动 ====================

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_isPanning && this.HalconWindow != null)
            {
                if (!_rightButtonWasDragged)
                {
                    Point current = e.GetPosition(this);
                    if (Math.Abs(current.X - _rightButtonDownPoint.X) > DragThreshold ||
                        Math.Abs(current.Y - _rightButtonDownPoint.Y) > DragThreshold)
                    {
                        _rightButtonWasDragged = true;
                    }
                }

                Point currentPoint = e.GetPosition(this);
                double deltaX = currentPoint.X - _panStartPoint.X;
                double deltaY = currentPoint.Y - _panStartPoint.Y;

                HOperatorSet.GetWindowExtents(this.HalconWindow, out HTuple winRow, out HTuple winCol,
                                              out HTuple width, out HTuple height);

                double imgWidth = _panStartCol2 - _panStartCol1;
                double imgHeight = _panStartRow2 - _panStartRow1;
                double scaleX = imgWidth / width.D;
                double scaleY = imgHeight / height.D;

                double offsetX = deltaX * scaleX;
                double offsetY = deltaY * scaleY;

                double newRow1 = _panStartRow1 - offsetY;
                double newCol1 = _panStartCol1 - offsetX;
                double newRow2 = _panStartRow2 - offsetY;
                double newCol2 = _panStartCol2 - offsetX;

                try
                {
                    HOperatorSet.SetPart(this.HalconWindow, newRow1, newCol1, newRow2, newCol2);
                }
                catch (HalconDotNet.HOperatorException)
                {
                    // 图片极度缩小后平移可能导致坐标越界，忽略此错误
                }

                // 让基类同步内部状态（避免基类渲染周期用失效状态覆盖视口导致底图丢失）
                base.OnMouseMove(e);

                // 每帧完整重绘：底图（免拷贝）+ 叠加层
                // 不清窗 → 不闪；不节流 → 不黑
                RedrawStitchedImage();
                DrawOverlays?.Invoke(this.HalconWindow);

                e.Handled = true;
                return;
            }

            base.OnMouseMove(e);
        }

        // ==================== 右键按下 ====================

        protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
        {
            // FitToWindow 模拟期间需要基类双击居中功能
            if (_isSuppressContextMenu)
            {
                base.OnMouseRightButtonDown(e);
                e.Handled = true;
                return;
            }

            // 正常右键拖拽：记录位置，调 base 保持基类状态同步（拖拽中 base.OnMouseMove 保持内部一致性）
            if (this.HalconWindow == null) return;

            _rightButtonWasDragged = false;
            _rightButtonDownPoint = e.GetPosition(this);

            base.OnMouseRightButtonDown(e);

            _isPanning = true;
            _panStartPoint = _rightButtonDownPoint;
            _lastDragUpdate = DateTime.MinValue; // 确保拖拽第一帧立即执行完整重绘

            try
            {
                HOperatorSet.GetPart(this.HalconWindow, out HTuple r1, out HTuple c1, out HTuple r2, out HTuple c2);
                _panStartRow1 = r1.D;
                _panStartCol1 = c1.D;
                _panStartRow2 = r2.D;
                _panStartCol2 = c2.D;
            }
            catch
            {
                _panStartRow1 = _panStartCol1 = 0;
                _panStartRow2 = _panStartCol2 = 100;
            }

            this.CaptureMouse();
            e.Handled = true;
        }

        // ==================== 右键释放 ====================

        protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
        {
            if (_isSuppressContextMenu)
            {
                base.OnMouseRightButtonUp(e);
                // FitToWindow 模拟触发，无需弹出菜单
                _isPanning = false;
                this.ReleaseMouseCapture();
                e.Handled = true;
                return;
            }

            if (!_rightButtonWasDragged)
            {
                // 右键点击（无拖拽）→ 显示上下文菜单
                _isPanning = false;
                this.ReleaseMouseCapture();
                base.OnMouseRightButtonUp(e);
                ShowRightClickContextMenu();
                e.Handled = true;
                return;
            }

            // 右键拖拽结束：通知基类结束，触发叠加层重绘
            _isPanning = false;
            this.ReleaseMouseCapture();
            base.OnMouseRightButtonUp(e);
            NotifyImagePartChanged();
            e.Handled = true;
        }

        // ==================== 鼠标中键（居中显示） ====================

        protected override void OnMouseDown(MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Middle && this.HalconWindow != null)
            {
                FitToWindow();
                NotifyImagePartChanged();
                e.Handled = true;
                return;
            }
            base.OnMouseDown(e);
        }

        /// <summary>
        /// 将图像居中显示（模拟右键双击的基类行为）
        /// </summary>
        public void FitToWindow()
        {
            try
            {
                _isSuppressContextMenu = true;

                // 通过反射设置 ClickCount = 2，模拟双击事件触发基类居中
                var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
                {
                    RoutedEvent = UIElement.MouseRightButtonDownEvent,
                    Source = this
                };

                // 第一次右键按下（ClickCount=1）
                this.RaiseEvent(args);

                var clickCountProperty = typeof(MouseButtonEventArgs)
                    .GetProperty("ClickCount",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
                clickCountProperty?.SetValue(args, 2);

                // 第二次右键按下（触发基类居中）
                this.RaiseEvent(args);

                var upArgs = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
                {
                    RoutedEvent = UIElement.MouseRightButtonUpEvent,
                    Source = this
                };
                this.RaiseEvent(upArgs);
            }
            catch
            {
                // 忽略异常
            }
            finally
            {
                _isSuppressContextMenu = false;
            }
        }

        // ==================== 上下文菜单 ====================

        private void ShowRightClickContextMenu()
        {
            // 捕获菜单位置的图像坐标（此时鼠标还未移动）
            try
            {
                if (HalconWindow != null)
                {
                    HOperatorSet.GetPart(HalconWindow, out HTuple r1, out HTuple c1,
                                         out HTuple r2, out HTuple c2);
                    Point p = Mouse.GetPosition(this);
                    double cw = ActualWidth, ch = ActualHeight;
                    if (cw > 0 && ch > 0)
                    {
                        _menuImageCol = c1.D + (p.X / cw) * (c2.D - c1.D);
                        _menuImageRow = r1.D + (p.Y / ch) * (r2.D - r1.D);
                    }
                }
            }
            catch { }

            var menu = new ContextMenu();

            if (IsInSelectionMode)
            {
                var exitItem = new MenuItem { Header = "退出选择" };
                exitItem.Click += (s, args) =>
                {
                    IsInSelectionMode = false;
                    ExitInspectionModeRequested?.Invoke();
                };
                menu.Items.Add(exitItem);

                // 清空所有标记
                var clearAllItem = new MenuItem { Header = "清空所有标记" };
                clearAllItem.Click += (s, args) => { ClearAllMarkersRequested?.Invoke(); };
                menu.Items.Add(clearAllItem);
            }
            else
            {
                // "从界面上选择检测位" → 改为子菜单，包含 A/B/C... 标记选项
                var selectItem = new MenuItem { Header = "从界面上选择检测位" };
                for (int i = 0; i < PluginFeatureCount; i++)
                {
                    char marker = (char)('A' + i);
                    var subItem = new MenuItem { Header = $"标记 {marker}" };
                    int capturedIdx = i;
                    subItem.Click += (s, args) => { MarkerModeSelected?.Invoke(capturedIdx); };
                    selectItem.Items.Add(subItem);
                }
                menu.Items.Add(selectItem);
            }

            menu.Items.Add(new Separator());

            // 结果图层（子菜单容器，不含勾选）
            var overlayRoot = new MenuItem { Header = "结果图层" };

            // 可见性开关
            var visToggle = new MenuItem { Header = "显示结果图层", IsCheckable = true, IsChecked = IsOverlayVisible };
            visToggle.Click += (s, args) => ToggleOverlayRequested?.Invoke();
            overlayRoot.Items.Add(visToggle);
            overlayRoot.Items.Add(new Separator());

            // 子选项：结果过滤（全部/OK/NG）
            var filterAll = new MenuItem { Header = "全部", IsCheckable = true, IsChecked = (CurrentOverlayFilter == "All") };
            filterAll.Click += (s, args) => OverlayFilterAll?.Invoke();
            overlayRoot.Items.Add(filterAll);

            var filterOK = new MenuItem { Header = "OK", IsCheckable = true, IsChecked = (CurrentOverlayFilter == "OK") };
            filterOK.Click += (s, args) => OverlayFilterOK?.Invoke();
            overlayRoot.Items.Add(filterOK);

            var filterNG = new MenuItem { Header = "NG", IsCheckable = true, IsChecked = (CurrentOverlayFilter == "NG") };
            filterNG.Click += (s, args) => OverlayFilterNG?.Invoke();
            overlayRoot.Items.Add(filterNG);
            overlayRoot.Items.Add(new Separator());

            // 子选项：显示模式（所有信息/仅编号）
            var modeAllInfo = new MenuItem { Header = "所有", IsCheckable = true, IsChecked = (CurrentOverlayDisplayMode == "AllInfo") };
            modeAllInfo.Click += (s, args) => OverlayModeAllInfo?.Invoke();
            overlayRoot.Items.Add(modeAllInfo);

            var modeNumber = new MenuItem { Header = "仅编号", IsCheckable = true, IsChecked = (CurrentOverlayDisplayMode == "NumberOnly") };
            modeNumber.Click += (s, args) => OverlayModeNumberOnly?.Invoke();
            overlayRoot.Items.Add(modeNumber);

            menu.Items.Add(overlayRoot);

            menu.Items.Add(new Separator());
            var moveItem = new MenuItem { Header = "移动到此处" };
            moveItem.Click += (s, args) => OnMoveToDetectPosition();
            menu.Items.Add(moveItem);

            var scanItem = new MenuItem { Header = "扫描此处" };
            scanItem.Click += (s, args) => OnScanPosition();
            menu.Items.Add(scanItem);

            menu.PlacementTarget = this;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            menu.IsOpen = true;
        }

        // ==================== 点击位置转换为拍照位索引 ====================

        private void HandlePositionClick(MouseButtonEventArgs e)
        {
            try
            {
                Point mousePos = e.GetPosition(this);
                double controlWidth = this.ActualWidth;
                double controlHeight = this.ActualHeight;

                if (controlWidth <= 0 || controlHeight <= 0) return;

                HOperatorSet.GetPart(this.HalconWindow, out HTuple r1, out HTuple c1,
                                     out HTuple r2, out HTuple c2);

                double imgX = c1.D + (mousePos.X / controlWidth) * (c2.D - c1.D);
                double imgY = r1.D + (mousePos.Y / controlHeight) * (r2.D - r1.D);

                int col = (int)(imgX / FovWidthPixels);
                int row = (int)(imgY / FovHeightPixels);

                if (row >= 0 && row < GridRows && col >= 0 && col < GridCols)
                {
                    int index = row * GridCols + col;
                    DetectPositionToggled?.Invoke(index);
                }
            }
            catch
            {
                // 忽略坐标转换异常
            }
        }

        private void OnMoveToDetectPosition()
        {
            // 使用菜单弹出时捕获的坐标，避免菜单弹出后鼠标位置偏移
            MoveToDetectPositionRequested?.Invoke(_menuImageRow, _menuImageCol);
        }

        private void OnScanPosition()
        {
            ScanPositionRequested?.Invoke(_menuImageRow, _menuImageCol);
        }
    }
}
