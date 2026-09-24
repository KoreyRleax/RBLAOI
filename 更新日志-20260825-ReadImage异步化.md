# 更新日志 2026-08-25：优化 11 —— ReadImage 19MB×2 异步化

> 分支：`fix/multi-pintype-blob-parser`（e13e0f6 之上新增改动，未提交）
> 改动文件：`Core/Managers/FullImageManager.cs`（单文件，调用点签名不变）
> 状态：**代码完成，未编译**（本环境无 VS2019，待用户 `_build.bat` + 实机验证）

## 一、背景

- 优化 5（8/24）已把"全量重拼 TileImages + DispObj 显示"移入 `RebuildStitchedAsync` 异步；
- **但 `ReadImage`（19MB 原图解码）+ `ScaleAndCrop`（缩放）缓存更新仍同步**，是 A 段（移动+拍照）最后一块同步阻塞，实测每拍 200-400ms。
- 检测本身用 GRAB 原始文件（`DETECT2D_BLOB` 直读），拼接缓存只服务 UI 显示 → 缓存更新可安全后移。

## 二、改动内容

### 1. ReplaceTile（L146）→ fire-and-forget 异步
- 同步的 `ReadImage + ScaleAndCrop` 移入 `Task.Run`（`ReplaceTileCacheAsync`，L177）；
- 调用点（RunCommands.cs L1116/L1244/L1300、MainViewModel.cs L775/L820、DetectionFlow.cs L321）**签名零改动**。

### 2. 版本号防乱序（连续模式核心）
- 新增 `_tileVersions`（Dictionary<int,long>），每 GridIndex 一个版本；
- 每次 ReplaceTile 先 `ver = ++_tileVersions[idx]`，后台任务完成后校验 `cur == ver` 才写缓存；
- **连续模式下同一位的多次替换，只有最新一次结果落缓存并触发重拼显示，过期结果 Dispose 丢弃**——杜绝"后发先至"显示旧图。

### 3. 线程安全（_cacheLock）
- `_cachedScaledImages` 的写（Dispose+赋值）与读（BuildStitched 的 `ConcatObj`）全部收敛 `_cacheLock` 互斥；
- `Clear()` 加锁并清空 `_tileVersions` → 新一轮开始时在途任务自动判过期，不会写空列表；
- 写缓存前加 `idx < Count` 边界检查，防 Clear 后越界；
- `StitchAndDisplay` 初始化加载的 Add 同步加锁（防御性）。

### 4. 顺带修复：原图泄漏
- `ScaleAndCrop` 不接管原图（RGB 时 `work=gray≠image`，原图无人 Dispose）——**原同步代码每拍泄漏 19MB×2**；
- 异步路径成功分支补 `image.Dispose()`，消除内存累积。

### 5. 行为差异说明
- 读图失败：原同步版生成灰空图占位；改后**保留旧缓存图**（显示不闪空，更优）；
- 显示刷新频率不变（每拍板面+针型各触发一次重拼）。

## 三、预期收益

- A 段省 **200-400ms/拍**（19MB BMP 解码+缩放 ×2 移出关键路径）；
- 单拍 ~4.6s → 预期 ~4.3s，40 位排 ~190s → ~180s（估算，待实机）。

## 四、验证要点（实机）

1. A 段中位下降量（对照 16mm/s 轮基线 A=1828ms）；
2. 连续模式 2+ 轮：拼图显示正确、无旧图闪回、无崩溃（重点验证版本号/锁）；
3. 单拍目检：板面/针型图层切换正常（ShowBoardView/ShowPinTypeView）；
4. 内存：长时间运行无增长（原图泄漏已修复，可对比任务管理器）。
