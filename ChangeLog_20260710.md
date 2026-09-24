# 更新日志

## 2026-07-10 切换方案轨宽检查 & 方案参数界面调整

### 背景
1. 切换方案时需要检查实际轨道宽度（H轴位置）与方案板高是否一致，不一致应提示用户自动调整
2. 方案参数界面需要调整布局并新增插件特征参数

### 修改内容

#### Task1: 切换方案时检查轨宽一致性
- **新增** `CheckAndNotifyRailWidthAsync()` 方法：获取当前H轴位置，与 `BoardHeight + 50` 比较，超过1mm偏差则弹窗询问是否自动调整
- 在 `AutoLoadLastProject()`、`OpenProject()`、`ReloadProject()` 三个方案加载入口均添加检查调用

#### Task2: 方案参数界面调整
- **ProjectData.cs**: 新增 `PluginFeatureCount`（int，默认1）、`FocusParams`（double[]）
- **EditProjectWindow.xaml**: 
  - "板子参数" → "基础设定"
  - 去掉"子基板数"
  - 新增"插件特征个数"输入框 + "焦距设定"按钮
- **EditProjectWindow.xaml.cs**: 新增 `PluginFeatureCount`、`FocusParams` 属性，添加 `BtnFocusSetting_Click` 事件
- **新建 FocusSettingWindow**: 焦距设定弹窗，根据插件特征个数动态生成A/B/C三组（焦距、ROI宽、ROI高）输入框，确认后存入 double[]

### 注意事项
- 焦距设定表单使用当前窗口的"插件特征个数"值（即时性），而非已保存值
- 旧方案兼容：PluginFeatureCount=0时默认转为1
- 轨宽检查公式 `targetH = BoardHeight + 50` 与原有 ShowEditProjectDialog 一致
