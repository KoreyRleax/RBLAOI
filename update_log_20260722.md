# 更新日志

## 2026-07-22 任务3/5/6：菜单清理、图标、插件特征联动、表格样式、hover高亮

### 背景
根据需求清理菜单栏冗余按钮，补充缺失图标，优化 EditProjectWindow 中 DataGrid 的交互体验和样式，以及添加查看工具栏按钮 hover 效果。

### 修改内容

#### 任务3：菜单栏清理 + 图标
- **文件**：`Views/MainWindow.xaml.cs`
- 从 `AddFileSubMenu()` 删除：发送程序、关闭方案、退出程序
- 从 `AddFunctionSubMenu()` 删除：点检检测、Mark检测、结束批次、自动聚焦
- 在 `GetIconForButton()` 中添加5个按钮图标：
  - 删除方案 → 🗑
  - 精度报告 → 📊
  - 复位 → 🔁（非重复的复位图标）
  - 个性化设置 → 🎨
  - 固件升级 → 📦
- 清理已删除按钮的无用图标映射

#### 任务5：插件特征个数联动 + 表格样式
- **文件**：`Views/EditProjectWindow.xaml`、`Views/EditProjectWindow.xaml.cs`
- 监听 `PluginFeatureCount` 的 `PropertyChanged` 事件，实时增删 `PinTypeParams` 行数
- DataGrid 样式改用 `BasedOn="{StaticResource ThemedDataGridStyle}"`，与系统主题统一
- 调整列宽以适应主题字体大小

#### 任务6：查看按钮 hover 高亮 + 板面按钮去中文
- **文件**：`Views/MainWindow.xaml.cs`
- `UpdateViewToolbar()` 中为板面按钮和针型按钮添加 `MouseEnter`/`MouseLeave` 背景切换（60,60,60 → 100,100,100）
- 板面按钮 Content 从 `"🔲板面"` 改为 `"🔲"`，宽度从 60 改为 40

### 编译结果
- 0 错误，0 警告
- 编译通过

### 总结
清理了冗余菜单项，补充了缺失图标，优化了 EditProjectWindow 交互体验和 DataGrid 主题一致性，添加了查看工具栏按钮 hover 效果。
