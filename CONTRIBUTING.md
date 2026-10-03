# 参与 DJTrans

欢迎提交问题和 Pull Request。UI 文案使用简体中文，技术栈为 C# / .NET 10 / WinForms，不引入大型运行时依赖。

## 本地验证

```powershell
dotnet build DJTrans.slnx -c Release
dotnet test tests/DJTrans.Core.Tests -c Release
dotnet run --project src/DJTrans -c Release
```

传输引擎、恢复、冲突或校验逻辑变更需增加能区分错误行为的回归测试。测试使用临时目录和合成数据。真机写入与删除只允许自行创建的 `__DJTRANS_TEST__*` 文件，不操作用户媒体。

工程计划与验收状态统一维护在 [PLAN.md](docs/engineering/PLAN.md)，验证结果写入第 9 节。提交说明应包含问题、最终行为、验证结果和未验证边界。

## 提交问题与截图

报告版本、Windows 版本、设备型号、文件系统、操作步骤、预期与实际结果。先移除日志、截图中的用户名、完整路径、媒体名称、缩略图和卷序列号。不要上传真实媒体、settings.json、传输 journal 或凭据；需要样本时优先使用合成文件。

安全问题请按 [SECURITY.md](SECURITY.md) 私下报告。
