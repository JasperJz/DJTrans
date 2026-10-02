# DJTrans 项目约定

- 目标：Windows 桌面工具，DJI Action/Pocket（USB Mass Storage 盘符）与读卡器卷的媒体浏览/缩略图/多选导出（断点续传/校验/队列）/上传/删除。好用、体积小、稳定。
- 技术栈：C# / .NET 10 / WinForms；CsWin32 生成 Shell 互操作；System.IO.Hashing(XxHash3)。禁止引入大型运行时依赖。
- 结构：`src/DJTrans`(UI exe) / `src/DJTrans.Core`(无UI可测库) / `tests/DJTrans.Core.Tests`(xunit) / `docs/engineering/PLAN.md`(唯一计划与验收状态文件)。
- 命令：构建 `dotnet build DJTrans.slnx -c Release`；测试 `dotnet test tests/DJTrans.Core.Tests`；运行 `dotnet run --project src/DJTrans -c Release`。
- 真机验收：E:(OsmoAction)/F:(读卡器) 只读操作自由；写设备/删除仅限自建测试文件 `__DJTRANS_TEST__*`；导出目标用临时目录。不得删除用户媒体。
- UI 文案 zh-CN；代码注释仅在表达代码无法自明的约束时写。
- 共享模型（MediaItem/TransferJob 等）改动需主 Agent 统一；提交信息/状态更新到 PLAN.md 第 9 节。
