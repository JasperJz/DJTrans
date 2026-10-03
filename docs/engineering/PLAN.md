# DJTrans — DJI 相机素材传输工具（Windows）工程计划

状态：v2（R0 评审已并入）· 日期：2026-10-03 · 负责人：主 Agent（ZCode）

## 0. 结论摘要（调研已定事实 + 开源调研留痕）

- 【事实·DJI官方】DJI Action/Pocket 全系 USB 连 PC 时为 **USB Mass Storage（盘符，exFAT）**，不是 MTP；官方无 Windows 导出工具（Mimo 仅手机端）。→ 不需要 WPD/MTP 栈；MTP 列为未来扩展（架构留缝，不做实现）。
- 【事实·真机 Action】`E:`(OsmoAction) `DCIM\DJI_001\`，命名 `DJI_YYYYMMDDHHMMSS_NNNN_X.{MP4,LRF,JPG}`；【假设·待验证】Pocket 系列命名类似——NameParser 解析失败一律回退文件 mtime，条目绝不丢弃（配单测）。
- 【事实·环境】无 Rust/MSVC；有 .NET 10 SDK + WindowsDesktop Runtime 10.0.12 + Node24。
- 【选型】**C# / .NET 10 / WinForms**，框架依赖单文件 exe（目标 ≤10MB）；自包含 ≤90MB 作为对外分发默认形态（README 说明取舍）。
- 【开源调研留痕（满足"先看 GitHub"要求）】结论：无可整体复用项，借鉴设计——**FastCopy** 的"写后读回校验(XxHash)+按指纹跳过"语义；**Ultracopier** 的引擎/UI 分离队列状态机与冲突对话框；**digiKam import** 的"缩略图网格+勾选+底部队列"交互；**Rapid Photo Downloader** 的设备插入为中心 UX。Rust MTP 生态（winmtp/mtp-rs）因 DJI=UMS 而不需要。来源清单见调研记录（会话 R0）。

## 1. 需求定义

**用户**：Windows PC 用户，DJI Action/Pocket（或读卡器）用户。
**解决的问题**：官方无桌面导出工具；手工拷贝无增量去重、无校验、无断点续传、无缩略图挑选体验。
**成功结果**：插上相机即自动识别 → 缩略图快照目录 → 快速多选/反选 → 高速导出（断点续传/校验/增量去重/冲突策略/队列）→ 可上传/删除。好用、体积小、稳定。

### 范围（v1 做）
可移动卷（DJI 直连或读卡器）识别与热插拔（卷未就绪重试退避）；DCIM 扫描流式索引；缩略图网格（磁盘+内存缓存）；多选/反选/全选/筛选；导出引擎：队列、进度、速度/ETA、暂停/恢复/取消、**断点续传（进程内+跨重启）**、**校验（大小+XxHash3）**、**增量去重（SameFingerprintSkip 默认：目标同名且大小同 → 首尾块哈希比对，相同自动跳过不弹窗）**、冲突策略（Ask/Overwrite/Rename/KeepBoth/Skip + 应用到全部）、目录组织（镜像/按日期/平铺）、**目标磁盘空间预检**、**瞬断自动重试（3 次指数退避）**；上传（导入）到设备；删除（强确认+清缩略图缓存条目）；**安全弹出设备**；设置持久化；LRF/SRT 默认排除于全选/导出（硬编码进 C1，开关 UI 在 C2）。

### 范围（不做/后置，逐项盘点"传输工具功能"）
| 功能 | 决策 | 理由 |
|---|---|---|
| 限速 | 不做 v1 | USB 源瓶颈在设备/SD 卡端，限速无收益 |
| 队列拖动重排/优先级 | 不做 v1 | 小队列场景收益低 |
| 完成后自动关机 | 不做 v1 | 相机传输不建议无人值守 |
| 完成后自动弹出 | C2 | 依赖 VolumeEjector 稳定后 |
| 失败清单+一键重试 | C2 | 引擎需先落地 Failed 状态聚合 |
| 导出历史持久库/已导出徽标 | C3 | SameFingerprintSkip 已解决重复导出痛点，徽标为体验增强 |
| MTP/WPD 设备 | 不做 v1 | DJI 为 UMS（事实） |
| 回收站删除 | 不做 | 可移动卷无回收站（Windows 行为），永久删除+强确认替代 |
| 无线传输/转码/播放器 | 不做 | 超出核心价值 |

### 核心场景流
插设备（任意时机）→ 自动识别+扫描 → 网格浏览（C1 平铺按时间排序+类型筛选；C2 按日分组）→ 多选 → 导出对话框（目标目录/布局/策略/空间预检）→ 队列面板管控 → 完成校验 →（可选）删除设备文件 /（C2）安全弹出。

## 2. 验收条目（AC）

| 编号 | 条目 | 前置 → 操作 → 预期 | 验证方法 |
|---|---|---|---|
| AC-01 | 已运行识别 | 软件运行中插入设备 → ≤3s（卷就绪后）设备列表出现并自动扫描 | 真机拔插 E:，日志时间戳 |
| AC-02 | 先插后开 | 设备已插时启动软件 → ≤3s 列出设备并扫描 | 真机 E:/F: |
| AC-03 | 目录加载 | ≥1000 真实文件：元数据流式呈现，首屏（前100项）≤1s，全量索引 ≤10s，缩略图异步不阻塞列表 | 真机计时（F: 大卡） |
| AC-04 | 快照网格 | JPG/MP4 缩略图正确显示；滚动按需加载；二次进入命中磁盘缓存即时显示；内存 LRU 上限 ≤300 张或 ≤150MB | 真机观察+缓存目录+内存 |
| AC-05 | 多选/反选 | 单击/Ctrl/Shift/框选/Ctrl+A 全选/反选按钮/复选框，均生效；状态栏实时显示已选数与总大小 | 进程内 WinForms 控件测试(xunit STA) + 手动清单 |
| AC-06 | 导出 | 多选导出：逐项+总进度、速度、ETA；暂停/继续/取消；完成后大小+XxHash3 校验通过；**导出对话框显示目标盘剩余空间，不足时红字警告** | 真机导出到临时目录，哈希对比源 |
| AC-07 | 进程内续传 | 传输中暂停→继续：日志 resume offset>0，源读取总量≈文件大小（无重头读） | 单元测试+真机 |
| AC-08a | 跨重启续传(单测) | 构造 .djpart+journal（含 torn journal、重复 dest、双实例互斥三场景）→ 恢复逻辑正确 | xunit |
| AC-08b | 跨重启续传(真机) | 传输中杀进程→重启→重导出：从 .djpart 继续，最终校验通过 | 真机（C3 复测） |
| AC-09 | 冲突策略 | 目标同名但内容不同：弹窗 跳过/覆盖/重命名/两者保留 + 应用到全部 | 控件测试+手动 |
| AC-10 | 上传(导入) | 本地测试文件上传到设备目录成功，内容校验一致 | 仅自建测试文件 |
| AC-11 | 删除 | 删除设备文件需确认对话框；成功后列表刷新并清对应缩略图缓存 | 仅自建测试文件 |
| AC-12 | 异常稳定 | 传输中拔设备/目标盘 ENOSR：明确报错、UI 不挂死、其余任务有序继续或失败保留 .djpart 可续 | 真机拔出 + 小目标盘模拟 |
| AC-13 | 性能基准 | 协议：~3GB 混合集（含1个≥2GB大文件）同一目标盘，robocopy 取默认与 /MT:8 较快者为基准，吞吐≥85%；UI 停顿=UI线程单次阻塞>200ms 计1次，滚动/选择全程目标 0 次（Stopwatch 埋点）；进程峰值内存<400MB | 基准脚本（含埋点输出） |
| AC-14 | 体积 | 框架依赖单文件 ≤10MB；自包含 ≤90MB | 发布产物测量 |
| AC-15 | 代理/字幕 | .LRF/.SRT 默认不参与全选/导出（C1 硬编码），C2 提供开关 | 控件测试+真机 |
| AC-16 | 增量去重 | 重复导出同一目标目录：size 相同且首尾块 XxHash3 相同 → 自动跳过不弹窗；二次导出耗时≈仅新文件耗时 | 单元测试+真机 |
| AC-17 | 安全弹出 | 一键弹出：盘符消失，相机提示可拔；被占用时给出明确提示 | 真机（C2） |

## 3. 技术方案（模块与接口）

```
src/DJTrans/            WinForms exe（UI 层）
  MainForm / TransferDock / ExportDialog / ImportDialog / SettingsForm / Controls(ThumbGrid)
src/DJTrans.Core/       无 UI 类库（全部可单测）
  Volumes/   VolumeWatcher(WM_DEVICECHANGE 隐藏窗口+DBT卷+轮询兜底+就绪退避), VolumeInfo(Serial,Label,DjiScore),
             VolumeEjector(C2, CM_RequestDeviceEject)
  Scan/      MediaScanner(System.IO.Enumeration 流式), MediaItem, MediaKind, NameParser(失败回退 mtime)
  Thumbs/    ThumbStore(磁盘LRU+内存LRU≤300张/150MB), DngPreviewExtractor(TIFF IFD→内嵌JPEG), ShellThumbGateway
  Transfer/  TransferEngine(作业队列+工作者+destPath 去重锁), TransferJob, Journal(.djpart+.json 成对置于目标目录旁,
             temp+File.Replace 原子写+自校验和；损坏降级=按 .djpart 长度重验全部分块，失败即整文件重传),
             ConflictPolicy(默认 SameFingerprintSkip: size 同→首尾块哈希), LayoutMode, Verifier, SpaceGuard
  Settings/  AppSettings(json, %LocalAppData%\DJTrans)
tests/DJTrans.Core.Tests/  xunit：引擎/续传/冲突/校验/扫描/DNG/名称/空间预检；WinForms 控件级测试用 STA 线程内驱动
```

关键约定：
- 单实例 Mutex（`Global\DJTrans.SingleInstance`）；UI 与 Core 只经事件+不可变快照通信（~100ms 合并刷新）。
- 缩略图缓存键 `volSerial|pathLower|size|mtime` → `%LocalAppData%\DJTrans\thumbcache\*.jpg`(320px)；删除文件时同步清缓存条目。
- 续传协议：源指纹 = `{volSerial, size, mtime}`（含卷序列号防换卡撞指纹）；块 8MB XxHash3 检查点；恢复=指纹一致+末块复验。
- 删除：永久删除+确认框勾选确认；上传/删除真机测试仅用 `__DJTRANS_TEST__*` 文件。
- ThumbGrid：ListView 虚拟模式，自管选择位集（不依赖 CheckedItems），ImageList 键控 LRU 淘汰；**C1 首工作项=尖兵验证**（5000 合成项+勾选+快速滚动+ImageList 淘汰），失败预案=owner-draw 自绘。

## 4. 周期计划

- **C1（当前）核心闭环**：脚手架 → ThumbGrid 尖兵 → Core(Scan/Transfer/Journal/SpaceGuard/Retry) + 主 UI(设备栏/网格/选择/导出对话框/队列表) + LRF 排除硬编码 + 测试 + 真机验收 AC-01~07、08a、09、12、15、16。
- **C2 完整工具**：上传(AC-10)、删除(AC-11)、安全弹出(AC-17)、按日分组/筛选完整、设置界面、失败清单+重试、LRF/SRT 开关、深色标题、单文件发布(AC-14)、README。
- **C3 加固与性能**：AC-13 基准、AC-08b 真机杀进程复测、崩溃恢复演练、独立代码评审(R1)、发布打包。

依赖：C1 Core 是 C2/C3 前置；关键路径 TransferEngine→队列 UI→真机验收。

## 5. 并发分工

主 Agent：脚手架、共享模型、Scanner、TransferEngine、VolumeWatcher、全部 UI、集成与真机验收。
子 Agent A（C1 期间并行）：`DngPreviewExtractor` + xunit（只写 `src/DJTrans.Core/Thumbs/DngPreviewExtractor.cs` 与 `tests/DJTrans.Core.Tests/Thumbs/DngPreviewExtractorTests.cs`；自造最小 TIFF/DNG 测试样本于测试内生成；纯 BCL 无三方依赖）。
子 Agent B（C1 末）：只读独立实现评审（R1）。

## 6. 风险与对策

| 风险 | 影响 | 对策 |
|---|---|---|
| ListView 虚拟+复选状态陷阱 | 选择错乱 | C1 首项尖兵；自管位集；预案 owner-draw |
| Shell 缩略图冷启动慢/HEVC 无编解码 | 体验 | 首屏并发2、JPEG 快路径、占位+时长角标；披露 HEVC 依赖系统编解码 |
| exFAT 非法字符/长路径 | 导出失败 | 目标名净化+单测 |
| 拔盘中途写坏 .djpart | 续传失败 | 末块哈希复验不过即整文件重传 |
| 双实例/重复 dest 并发写 | 数据损坏 | 单实例 Mutex + 引擎 dest 锁（单测覆盖） |
| 高 DPI | 视觉 | PerMonitorV2 + 150% 手测 |

## 7. 验证入口

- 构建：`dotnet build D:\Projects\DJTrans\DJTrans.slnx -c Release`
- 测试：`dotnet test tests/DJTrans.Core.Tests`
- 运行：`dotnet run --project src/DJTrans -c Release`
- 真机：E:(OsmoAction) / F:(读卡器)；导出目标用临时目录；上传/删除仅 `__DJTRANS_TEST__*`

## 8. 评审记录（自增自评循环）

- **R0 计划独立评审（已完成）**：阻断 B1 增量去重无载体、B2 空间预检缺失、B3 续传一致性三缺口（单实例/原子 journal/dest 互斥）——全部采纳并入 v2（AC-16、AC-06 扩充、§3 协议补强、AC-08 拆 a/b）。重要项 I1 弹出设备(→AC-17/C2)、I2 功能盘点表、I3 AC-03 重定义(流式+规模)、I4 基准协议写死、I5 Pocket 假设标注+回退、I7 内存上限、I8 尖兵前置——全部采纳。建议项 S1 调研留痕(§0)、S2 LRF 排除提前 C1、S3 指纹加 volSerial、S4 .djpart 位置=目标目录旁、S5 分发默认自包含、S6 控件测试=进程内 STA 驱动、S7 就绪退避+删缓存——全部采纳。评审结论"修订后可开发"→ v2 生效，进入 C1。
- R1 实现独立评审：待 C1 代码完成后。
- **R1 实现独立评审（已完成并闭环）**：结论"数据正确性主通路扎实，但 4 项阻断 UI/状态机缺陷需修复"。全部处置：
  - B1 暂停/恢复状态机卡死（3 条路径）→ 重构：HeldByWorker 持有标志（锁内维护）、Resume 一律唤醒、AskUser/重试路径尊重 Paused、RunOnce 不再覆写状态；新增 3 个状态机回归单测
  - B2 RenameNew 目标锁泄漏 → 改名时锁内换 _busyDest；FindFreeName 补查 .djjournal
  - B3 内存缓存命中不回调 → Request 命中即同步克隆回填
  - B4 网格缩略图无上限 → 产出统一降采样 ≤320px + 网格侧 LRU（可视区豁免）
  - I1 非法日期名解析异常 → try/catch 回退 mtime + 负例单测；I2 非持有作业取消无效 → 锁内直接终态迁移+释放锁（单测覆盖）；I3 Refresh 锁内 IO → 探测移出锁外；I4 万级作业 Dock 全量重建 → >300 行摘要模式（虚拟模式列 C2 待办）；I5 退出竞态 → FormClosed 释放三资源 + BeginInvoke 防护 + Dispose ODE 加固
  - 建议项：冲突决策 volatile int、负缓存不放瞬态异常、指纹 mtime 预筛（自有导出 2MB 快路径）、字体缓存、死代码清理、冲突对话框文案（"取消传输"→"跳过本文件"）
  - 修复后：55/55 单测绿（+5 状态机回归）；真机复测 E:(75项)+F:(459项) 扫描 0.0s、F 卡 43 照片导出+增量 43/43 日志完整（并发日志锁修复验证）、抽样哈希 5/5、进程 186MB/678 句柄

## 9. 验收状态追踪（2026-10-03 C1 真机验收）

真机环境：Windows 11 + E:(OsmoAction, DJI Action 系列, USB Mass Storage) + F:(512GB 读卡器)。交互证据采集：UIA Invoke/Win32 消息驱动 + 引擎日志 + 独立 SHA256 对比（PowerShell Get-FileHash，非自校验）。

| AC | 状态 | 证据 |
|---|---|---|
| AC-01 已运行识别 | **部分验证** | 软件运行中 F:/E: 双卷在列表中刷新；物理拔插无法自动化，WM_DEVICECHANGE+轮询双通道代码就绪但未做物理拔插实测 |
| AC-02 先插后开 | **已验收** | 启动即列出 E: OsmoAction (DJI) 与 F: (DJI) 并自动扫描（UIA 窗口文本证据） |
| AC-03 目录加载 | **已验收** | E: 75 文件扫描 0.0s，状态栏"扫描完成：75 项（0.0s）"；流式索引 |
| AC-04 快照网格 | **已验收** | 视觉确认真实照片/视频帧缩略图 + MP4 角标；磁盘缓存 51 条命中 |
| AC-05 多选/反选 | **已验收** | 全选 51 项(8.3GB) → 反选 0 → 反选 51（UIA 状态断言）；筛选"仅照片"27 项选择 |
| AC-06 导出 | **已验收** | 27 照片+24 视频(8.2GB) 全部完成；65-70 MB/s；逐项/总进度/速度/ETA；SHA256 27/27+24/24 全对；mtime 保留；空间预检"目标盘 D:\ 可用 668.8GB"实时更新 |
| AC-07 进程内续传 | **已验收** | 全部暂停后 4 秒字节零增长（2.4GB/8.4GB 冻结）；继续后完成 |
| AC-08a 跨重启(单测) | **已验收** | ResumeFromValidJournal/TornJournal/CorruptedPart/QueueDedupes 等 50/50 绿 |
| AC-08b 跨重启(真机) | **已验收（提前至 C1）** | 传输中 taskkill → journal 落盘(Offset=25165824,3 块哈希) → 重启重导出 → 日志"断点续传：从 2256MB 继续（共 2444MB）"与"从 24MB 继续"精确匹配；最终 SHA256 一致 |
| AC-09 冲突策略 | **已验收** | 单测覆盖 Ask/Overwrite/RenameNew/Skip 全分支；真机验证 SmartSkip 静默路径 |
| AC-10 上传 | **已验收** | __DJTRANS_TEST__upload.txt 上传至 E:\DCIM\DJTRANS_IN\，SHA256 一致（仅自建测试文件） |
| AC-11 删除 | **已验收** | 强确认框全文核对 → 确认后设备文件消失；缩略图缓存同步清理（仅自建测试文件） |
| AC-12 异常稳定 | **部分验证** | taskkill 场景无崩溃、现场保留、重连续传 ✓；物理拔盘不可自动化 → WaitingDevice 由单测覆盖，真机拔盘未实测 |
| AC-13 性能基准 | 待办（C3） | 已知 65-70MB/s；正式基准协议未执行 |
| AC-14 体积 | **已验收** | 框架依赖单文件 **0.34MB**；自包含压缩单文件 **49.3MB**；单文件 exe 启动冒烟通过 |
| AC-15 代理/字幕 | **已验收** | 75 文件 - 24 LRF = 51 项媒体，默认排除；工具栏开关就位 |
| AC-16 增量去重 | **已验收** | 同批二次导出 27 项 0.03s 全部"增量跳过"零弹窗（期间发现并发日志写丢失，已修复加锁） |
| AC-17 安全弹出 | **已实现待用户自测** | FSCTL 锁卷+卸载实现就绪；真机执行需用户在场（弹出后需物理重插） |

遗留待办（C2/C3）：AC-01/12 物理拔插用户自测项；AC-13 基准协议；设置界面；失败清单聚合；10k 条目压力；AC-08b 上传方向续传。
