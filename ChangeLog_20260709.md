# 更新日志 2026-07-09

## 1. 全局去重阈值调整
- **背景：** PINID #9 和 #21 的PadY差0.102mm > 原阈值0.1mm，重复漏网
- **修改：** 0.1mm → 0.2mm，同时覆盖后续叠加层去重
- **文件：** `ViewModels/MainViewModel.cs`

## 2. FOV有效区过滤统一
- **背景：** X轴用`padMmX`(焊盘理想X)做判断，Y轴用`pinMmY`(实际检测Y)，两轴坐标源不一致
- **修改：** X/Y统一使用实际检测位置 `pinMmX/pinMmY`
- **文件：** `ViewModels/MainViewModel.cs`

## 3. 叠加层去重独立执行
- **背景：** 叠加层自身去重被包在 `if(dupCount>0)` 内，空针不在PinResults中，导致叠加层空针重复永远清理不掉
- **修改：** 将叠加层自身去重移出PinResults去重块，每次检测完成独立执行
- **文件：** `ViewModels/MainViewModel.cs`

## 4. 检测流程防重入
- **背景：** `isWorkFlowRun[Check]=true` 设在 `await CheckAndHomeAxesAsync()` 之后，异步期间用户再点开始检测可重入
- **修改：** 提前到第一个await之前设置标志位，`CheckAndHomeAxesAsync` 新增 `skipReentryCheck` 参数
- **文件：** `ViewModels/MainViewModel.cs`

## 5. 启动自动连接2D/3D网口
- **背景：** 之前需要用户手动点击连接按钮
- **修改：** 构造函数中添加 `ConnectVision()` 自动连接
- **文件：** `ViewModels/MainViewModel.cs`

## 6. 检测流程增加2D/3D连接检查
- **背景：** 检测前未检查网口连接状态
- **修改：** `StartCheckFlow` 开头添加 `CheckVision2DConnected()` 和 `CheckVision3DConnected()`
- **文件：** `ViewModels/MainViewModel.cs`

## 7. 移除VM方案加载/卸载UI代码
- **背景：** 3D和2D检测融合到一个VM工程后，不再需要加载/卸载VM方案的操作
- **修改：** 移除 EditProjectWindow.xaml.cs 中的 BtnLoadVmSolution_Click 和 BtnUnloadVmSolution_Click
- **文件：** `Views/EditProjectWindow.xaml.cs`
