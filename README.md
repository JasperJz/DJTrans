# DJTrans — DJI 相机素材传输工具（Windows）

小体积、高性能、稳定的桌面客户端：插上 DJI Action/Pocket（或读卡器）即自动识别，缩略图快照浏览，快速多选/反选，带**断点续传、分块校验、增量去重**的导出引擎，以及上传、删除、安全弹出。

## 为什么做

DJI Action/Pocket 全系 USB 连电脑时是 **USB Mass Storage（U 盘盘符）**，官方没有 Windows 端导出工具（Mimo 仅手机）。手工拷贝没有增量、没有校验、断线重来、挑选素材只能开资源管理器一个个看。

## 功能

- **自动识别**：开机前/后插上设备都能识别（卷热插拔 + 轮询兜底），DJI 卷自动标记
- **快照网格**：照片/视频缩略图（Windows Shell 解码，DNG 用内嵌预览提取），磁盘+内存缓存，海量条目虚拟渲染
- **快速选择**：单击 / Ctrl / Shift / 框选 / Ctrl+A 全选 / **反选** / 类型筛选 / 排序 / 缩放；`.lrf` 代理与 `.srt` 字幕默认排除
- **导出引擎**（相机 → 电脑）：
  - 断点续传：8MB 分块 XxHash3 检查点 + journal 原子写；**杀进程/拔线后重启精确续传**
  - 校验：分块哈希全量比对，绝不交付损坏文件
  - 增量去重：目标已存在相同内容（大小+首尾块指纹）自动秒跳过
  - 冲突策略：智能跳过 / 询问 / 覆盖 / 跳过 / 保留两者（应用到全部）
  - 队列：双工作者并行（实测 65-70 MB/s）、逐项/总进度、速度、ETA、暂停/继续/取消/重试
  - 目标空间预检、目录组织（镜像相机结构 / 按拍摄日期 / 平铺）、保留拍摄时间
- **上传**（电脑 → 相机，写入 `DCIM\DJTRANS_IN`，同样校验）
- **删除**：强确认（可移动盘无回收站，永久删除）
- **安全弹出**：FSCTL 锁卷+卸载
- 单实例互斥（避免两个进程写坏同一续传现场）

## 安装与运行

- **推荐（自包含，无需任何运行时，49MB）**：`build-selfcontained/DJTrans.exe`
- **轻量（需 .NET 10 Desktop Runtime，0.34MB）**：`build-fd/DJTrans.exe`

从源码构建：

```
dotnet build DJTrans.slnx -c Release
dotnet test tests/DJTrans.Core.Tests
dotnet publish src/DJTrans -c Release -o build -p:PublishSingleFile=true -p:SelfContained=true -p:EnableCompressionInSingleFile=true
```

## 技术栈与结构

C# / .NET 10 / WinForms（PerMonitorV2 高 DPI），零第三方运行时依赖（仅 System.IO.Hashing）。

```
src/DJTrans/        UI（自绘虚拟缩略图网格 / 传输队列 / 对话框）
src/DJTrans.Core/   无 UI 可测库：扫描、卷监视、断点续传引擎、DNG 内嵌预览
tests/              xunit（50+ 用例：续传/冲突/校验/重建/扫描/名称解析）
docs/engineering/PLAN.md   需求、验收条目与证据
```

## 已知限制

- 不支持 MTP 设备（DJI Action/Pocket 不使用 MTP；手机等 MTP 设备不在支持范围）
- HEVC(H.265) 视频缩略图依赖系统编解码器，未安装时显示占位图标
- 可移动盘删除是永久的（Windows 对可移动卷不提供回收站）
- 传输中断点续传仅对"导出（下载）"方向有完整 journal 支持；上传中断会整体重传

## 验证状态

详见 `docs/engineering/PLAN.md` §9（验收状态追踪）。核心证据：真机 OsmoAction 上 51 文件导出 SHA256 100% 一致；2.4GB 文件杀进程后从 2256MB 精确续传；二次导出秒级增量跳过；50/50 单元测试通过。
