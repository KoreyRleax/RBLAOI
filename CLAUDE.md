# AOI 视觉检测系统 (RBLAOI)

## 写在前面：请遵循以下规则
1.当上下文到30%请自己执行/compact指令压缩上下文       

2.请全程使用中文和我对话

3.只有当我确认本次修改没有问题，我通过了才能推送到远程

5. 每次推送要把记忆文件和 CLAUDE.md，同步到远程；

写代码时，如果不是主要业务逻辑，请不要把所有代码全部写在MainViewModel，类似"轮子"可以放在其他文件夹；

## 不允许改动的文件
MotionIO.cs —— 除"新增运动指令封装方法"（如 MOTOR FWD/REV/STOP 系列，2026-08-10 起授权）外，禁止修改现有逻辑；新增指令须在更新日志中注明


## 项目结构（已读文件迭代更新：✅=已读 ⬜=未读，读到一批补一批，避免反复探索耗token）

```
RBLAOI/
├── Core/
│   ├── LightSource/LightSourceController.cs  ✅ 光源串口单例服务（ZVD协议/SetAll/Open带波特率）
│   ├── Device/DeviceMonitor.cs                ✅ 设备状态机(六态+Demo)、IO去抖(OnOffSignal)、报警锁存、三色灯控制
│   ├── Managers/SysParam.cs                  ✅ 系统参数单例，便捷属性赋值即Save()
│   ├── Utility/Log.cs                        ✅ 异步写日志
│   ├── Motion/MotionIO.cs                    ✅ 关键区已读（指令发送/队列、GET_STATE轮询、IO查询、MOTOR指令）；除新增指令封装外禁止改
│   ├── Controls/  Managers其余  ⬜ 未读
│   ├── Vision/Vision2DClient.cs            ✅ TCP客户端(GRAB/DETECT指令+响应匹配)；响应匹配必须按行行首StartsWith，禁止Contains子串匹配(2026-08-13)
│   ├── Vision/Vision3DClient.cs            ✅ TCP客户端(3D指令+WaitForPrefixNonEmptyAsync)；匹配规则同2D
│   └── IAP/ 已删除                         🗑 固件升级功能 2026-08-19 移除（IAPWindow/IAPService/根目录残留一并清理）
├── Models/
│   ├── ProjectData.cs                        ✅ 方案级参数（含光源亮度/开关，串口字段已废弃）
│   └── SysParamData.cs                       ✅ 系统参数（光源串口/波特率/通道数、传送带流程超时、训练路径）
├── ViewModels/
│   ├── MainViewModel.cs                         ✅ 核心（主文件：字段/属性/事件/串口通信/连接检查/辅助方法）
│   ├── MainViewModel.CollectionFlow.cs          ✅ 全图采集三层状态机（拆分 2026-08-19）
│   ├── MainViewModel.DetectionFlow.cs           ✅ 检测流程状态机 + VM 3D解析 + 检测动作（拆分）
│   ├── MainViewModel.Project.cs                 ✅ 方案管理 + VM管理 + 检测位管理（拆分）
│   ├── MainViewModel.MenuCommands.cs            ✅ 主界面菜单 文件/编辑/帮助（拆分）
│   ├── MainViewModel.RunCommands.cs             ✅ 运行（连续模式 + 精度检测）（拆分）
│   ├── MainViewModel.TransportFlow.cs           ✅ 功能（传送带状态机 + 轨宽检查）（拆分）
│   ├── MainViewModel.VisionOverlay.cs           ✅ VM 检测结果叠加层绘制（拆分）
│   └── SysParamViewModel.cs                     ✅ [SysParam]反射自动同步 + 连接状态属性
├── VmScripts/
│   └── UserGlobalScript.cs                      ✅ VM 全局脚本（原根目录，2026-08-19 归类）
├── Views/
│   ├── LightControlWindow.xaml(.cs)          ✅ 光源调试（4通道滑块0-255+开关，实时下发）
│   ├── EditProjectWindow.xaml.cs             ✅ 方案编辑（光源参数透传/回写）
│   ├── SysParamWindow.xaml(.cs)              ✅ 系统参数（连接管理：下位机/2D/3D/光源）
│   ├── MainWindow.xaml.cs                    ✅ 部分（Closing强制退出/ContentRendered）
│   └── 其余窗口                               ⬜ 未读
├── App.xaml.cs                               ✅ 启动加载主题+日志、OnExit清理
├── RBLAOI.csproj                             ✅ 旧式csproj，显式<Compile>（新增文件须手动注册）
└── Styles/                                   ⬜ 未读
```

> 关键结论（已核实）：光源串口号/波特率/通道数=系统参数（SysParamData，所有方案共用，启动时ConnectLightSource统一连接）；亮度/开关=方案参数（ProjectData，EditProjectWindow→LightControlWindow链路）。

## 标准配色方案（DataGrid/表格相关，已固化到全局样式）

- 选中行：**浅蓝 #A9C6E8 背景 + 黑字 #1E1E1E**（全局 ThemedDataGridStyle 已生效，勿改回深蓝）
- 鼠标全选（编辑态选区）：**深蓝 #1863DA**（与浅蓝行背景区分）
- 移动/操作按钮 hover：**深蓝 #1863DA**，按下：**深蓝 #0F4C81**，禁用：35% 透明度
- 原则：浅蓝选中 + 深蓝交互，与黑/白文字均不冲突；新表格/按钮沿用此配色

## 注意事项（必看）
每次作重要更改时（比如结构性改动），需要输出一个更新日志（日志格式：时间，标题，背景，解决方案，总结）到项目根目录下

每次完成某个对话的所有任务后，请自行审查代码后直接进行编译，编译通过后才是真正的完成；

- **IO 状态由后台 GET_STATE 轮询持续维护**（连接成功即启动，~20ms 刷新 `CurrentIOState` 并触发 `IOStateUpdated` 事件）：读 IO 直接读 `CurrentIOState` / `GetIOState`（读内存快照，不发指令）；**状态机流程内等待 IO 信号（如传送带入口/出口传感器）每轮循环 `GetIOState` 判断即可**——循环驱动状态机的迭代本身就是等待，后台轮询已维护快照；不要订阅 `IOStateUpdated` 做事件等待包装，也不要写独立的轮询循环/轮询线程；

## 环境警告（2026-08-19 实测，务必遵守）
- **本机 git（PortableGit 2.55）写 `.git/refs/heads/` 有 bug**：commit/reset/checkout/rebase 等更新分支 ref 的操作会静默丢失 ref 文件（reflog 正常但 ref 文件消失 → "does not have any commits yet"）。**对策：每次 git 写 ref 后检查 `git rev-parse HEAD`，失败则手动写 `.git/refs/heads/fix/multi-pintype-blob-parser` = 目标 hash**（reflog 尾行可取）。
- **`git rm` 会连带删除同目录其他文件**（本机实测删 Views/IAPWindow 时 Views/ 整个目录消失）。**对策：删除文件用普通 `rm` + `git add -A`，禁用 `git rm`**。
- 上述异常已致 2026-08-19 一次仓库修复（全新 clone 重建 .git），详见更新日志。


## VM手册
C:\Program Files\VisionMaster4.4.40\Applications\Help 和 C:\Program Files\VisionMaster4.4.40\Development\V4.x\Documentations\CH 是 VM 的官方帮助文档；

## 记忆
项目记忆存储在 `.claude/memory/` 中（受git版本控制，同步到所有设备）。**每次会话开始先读 `MEMORY.md` 索引；索引摘要见下，具体内容按需读取对应文件全文**（文件含 frontmatter，读取时跳过 `---` 头）。

### 记忆索引（与 `.claude/memory/MEMORY.md` 同步维护）

**协议/算法参考**
- [VM 3D协议](.claude/memory/vm-3d-protocol.md) — `DETECT_3D_OK:x,y,z;...` 单位µm；坐标映射；Pass1加权匹配(w=2)算校准偏移 → Pass2偏移+2mm阈值精匹配；**3D扫描重试：Scan3DToAndWaitOK 总尝试2次(RetryAsync)，别删——SCAN3D偶发失败是现场实况**
- [VM 2D GRAB协议](.claude/memory/vm-2d-grab-protocol.md) — GRAB响应行=文件名本身(GRAB_OK_*.bmp)；响应匹配必须按行行首StartsWith，**禁止Contains子串匹配**；VM端偶发异常现象记录
- [空针检测方案](.claude/memory/empty-pin-detection-design.md) — Blob分析+最近邻匹配替代矩形检测（分列+搜索半径限制，匹配逻辑同3D Pass2）

**架构/决策（必读）**
- [状态机架构决策](.claude/memory/flow-state-machine-pattern.md) — 直接调用+局部函数+FlowLoopAsync；三层状态（设备/壳层/业务）；按钮语义固定；三色灯语义；**终态分支陷阱：用 endDone 标志，不能 `bizState == End` 做返回条件**
- [GET_STATE 综合轮询](.claude/memory/get-state-composite-polling.md) — 唯一综合轮询源；查询API读快照(IsFresh)；无快照 IsMoving 保守 true；不再发 GET_IO/GET_POS/GET_STATUS
- [MotionIO.cs 改动授权](.claude/memory/motionio-change-approval.md) — 2026-08-04 GET_STATE 改造获用户批准修改，**非永久放开**，后续改动仍须先确认

**项目改动记录**
- [会话检查点](.claude/memory/session-checkpoint-20260814.md) — 2026-08-14 会话检查点：5项代码改动清单/模拟器验证进度(连续模式待重验)/待办/关键结论(io_init_mask已回零位无效等)——压缩上下文后新会话从此恢复
- [自动化测试手册](.claude/memory/auto-test-playbook-20260814.md) — **模拟器自动化测试完整手册**：启动顺序(先模拟器后上位机)/弹窗处理(Win32 #32770+BM_CLICK循环)/场景配置表(mode+io_init_mask)/UIA按钮操作(btnRun菜单)/验证点清单/10条试错经验——照做无需试错
- [模拟器验证规则](.claude/memory/simulator-test-rules-20260814.md) — **测试检测前先跑全图采集**（替换图片致Origin数量不对）；全图采集=模式"全图采集"/检测=模式"自动"；弹窗用Win32(#32770+BM_CLICK)；io_init_mask配置初始IO；模拟器日志 f407_gui_simulator.log
- [检测流程重构](.claude/memory/detection-flow-refactor-20260716.md) — Z轴调焦、双GRAB、Blob双路径、全局去重（BoardFocus / FocusParams 对照表）
- [复位+轨宽+Z待机位](.claude/memory/reset-rail-standbyz-20260717.md) — 复位增强(300mm/s)、StandbyZ、轨宽保护、Pin目录清理
- [GRAB重试删除+针尖图层](.claude/memory/vm-grab-retry-and-pin-layer-20260720.md) — 删GRAB重试、WaitForVmSaveTimeout、RuntimeDisplayMode、针尖图层切换
- [第6检测位轴卡死](.claude/memory/detection-pos6-stall.md) — 确认为下位机固件问题，PC端代码无问题

# Git
- 远程仓库地址：[kxy/RBLAOI](https://gitee.com/koreayRlax/RBLAOI)
- **只使用功能分支 `fix/multi-pintype-blob-parser` 进行拉取和推送，不操作 master**（master 由他人维护，勿动）

# 方案
方案路径： C:\Users\Administrator\Desktop\RBLAOI\bin\x64\Debug\Board\标准版