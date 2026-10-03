# DJTrans — DJI 相机素材传输工具（Windows）

[English](#english) | 中文

小体积、高性能、稳定的 Windows 桌面客户端：插上 DJI Osmo Action / Pocket（或读卡器）即自动识别，缩略图快照浏览，快速多选/反选，带**断点续传、分块校验、增量去重**的导出引擎，以及上传、删除、安全弹出。

> DJI 官方没有 Windows 端的素材导出工具（Mimo 仅手机端）。手工拷贝没有增量、没有校验、断线重来、挑素材只能开资源管理器一个个看 —— DJTrans 就是为此而生。

## ✨ 功能

| | |
|---|---|
| 🔌 **自动识别** | 开机前/后插上设备都能识别（卷热插拔 + 轮询兜底），DJI 卷自动标记 |
| 🖼️ **快照网格** | 照片/视频缩略图（Windows Shell 解码，DNG 用内嵌预览提取），磁盘+内存缓存，海量条目虚拟渲染 |
| 🖱️ **快速选择** | 单击 / Ctrl / Shift / 框选 / Ctrl+A 全选 / **反选** / 类型筛选 / 排序 / 缩放；`.lrf` 代理与 `.srt` 字幕默认排除 |
| 👁️ **预览** | 双击/回车用系统默认应用打开（照片→看图工具，视频→默认播放器） |
| ⬇️ **导出引擎** | 见下 |
| ⬆️ **上传** | 电脑 → 相机（写入 `DCIM\DJTRANS_IN`，同样校验） |
| 🗑️ **删除** | 强确认（可移动盘无回收站，永久删除） |
| ⏏ **安全弹出** | FSCTL 锁卷 + 卸载 |

导出引擎（相机 → 电脑）：

- **断点续传**：8MB 分块 XxHash3 检查点 + 原子 journal；杀进程/拔线后重启**精确续传**（真机实测 2.4GB 文件从 2256MB 处续传，只补尾部）
- **校验**：分块哈希全量比对，绝不交付损坏文件（真机实测 51 文件 SHA256 100% 一致）
- **增量去重**：目标已存在相同内容（大小+首尾块指纹）自动秒跳过，二次导出零等待
- **冲突策略**：智能跳过 / 询问 / 覆盖 / 跳过 / 保留两者（可"应用到全部"）
- **队列**：并行传输（实测 65-70 MB/s）、逐项/总进度、速度、ETA、暂停/继续/取消/重试
- **细节**：目标空间预检、目录组织（镜像相机结构 / 按拍摄日期 / 平铺）、保留拍摄时间、单实例互斥

## 📥 下载

到 [**Releases**](../../releases) 页面下载：

| 包 | 大小 | 适用 |
|---|---|---|
| `DJTrans-vX.Y.Z-win-x64-self-contained.zip` | ~50MB | **推荐**：零依赖，解压即用 |
| `DJTrans-vX.Y.Z-win-x64-framework-dependent.zip` | ~1MB | 已装 .NET 10 Desktop Runtime 的机器 |

系统要求：Windows 10 1809+ / Windows 11，x64。

## 🔧 从源码构建

```bash
git clone <repo>
cd DJTrans
dotnet build DJTrans.slnx -c Release   # 构建
dotnet test tests/DJTrans.Core.Tests    # 55 个单元测试
dotnet run --project src/DJTrans -c Release   # 运行
```

需要 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)。发布单文件：

```bash
dotnet publish src/DJTrans -c Release -o out -p:PublishSingleFile=true -p:SelfContained=true -p:EnableCompressionInSingleFile=true
```

推 tag（`v*`）会触发 CI 自动测试、打包两个 zip 并创建 GitHub Release。

## 🏗️ 架构

C# / .NET 10 / WinForms（PerMonitorV2 高 DPI），零第三方运行时依赖（仅 System.IO.Hashing）。

```
src/DJTrans/        UI：自绘虚拟缩略图网格 / 传输队列 / 对话框
src/DJTrans.Core/   无 UI 可测库：媒体扫描、卷热插拔监视、断点续传引擎、DNG 内嵌预览提取
tests/              xunit：引擎/续传/冲突/校验/状态机/扫描/名称解析
docs/engineering/   工程日志：需求、验收条目与真机证据（PLAN.md）
```

## ⚠️ 已知限制

- 仅支持 USB Mass Storage 设备（**DJI Action/Pocket 全系即此模式**）；MTP 设备（手机等）暂不支持
- HEVC(H.265) 视频缩略图依赖系统编解码器，未安装时显示占位图标
- 可移动盘删除是永久的（Windows 对可移动卷不提供回收站）
- 上传方向中断会整体重传（journal 续传目前仅导出方向）

无遥测、无联网行为，数据只在你选择的目录之间流动。许可证：[MIT](LICENSE)。

---

## English

**DJTrans** is a small, fast, reliable Windows desktop app for offloading media from DJI Osmo Action / Pocket cameras (which mount as USB mass-storage drives). Auto-detects the device on plug-in, shows a thumbnail grid, fast multi-select / invert-select, and exports with **resumable transfers (8MB-block XxHash3 checkpoints), block-hash verification, and incremental skip of already-exported files**, plus upload, delete and safe-eject. Measured 65–70 MB/s over camera USB with two parallel workers; verified on real hardware (51 files SHA256-identical; a 2.4GB file resumed exactly at its kill-point offset after a hard process kill).

Download from [Releases](../../releases) — grab the self-contained zip (no runtime needed). Build from source with the .NET 10 SDK. MIT licensed.
