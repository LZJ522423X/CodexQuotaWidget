# CodexQuotaWidget / AI Control Center

Windows 11 x64 的 Codex 额度与任务小组件。当前正式版本 **1.13.9**。

[下载 Windows x64 程序](https://github.com/LZJ522423X/CodexQuotaWidget/releases/download/v1.13.9/CodexQuotaWidget-1.13.9-win-x64.zip) · [全部版本](https://github.com/LZJ522423X/CodexQuotaWidget/releases) · [更新记录](CHANGELOG.md)

## 下载安装

1. 下载 Release 中的 Windows x64 ZIP；可使用同一 Release 的 SHA256SUMS.txt 验证文件。
2. 解压到固定、可写的本地文件夹，保留 executor、startup-watcher 子目录。
3. 运行 CodexQuotaWidget.exe，通过设置中的官方登录入口为小组件建立独立账户授权。
4. 从托盘打开主界面，切换额度胶囊、查看额度或管理本地任务。

发布包自带 .NET 10 运行时，无须安装 SDK。读取额度需要已安装的官方 OpenAI 签名 Codex CLI/Desktop 及可用账户；离线或证据不足显示未知/过期，不假装连接成功。
本项目不是 OpenAI 官方产品；发布程序未进行商业代码签名，Windows 可能显示来源提示。

## 当前具备的功能

- 五小时、每周额度、重置倒计时与本地时间；服务端提供时显示额外额度窗口及重置机会。
- 重置机会仅在用户明确确认后调用官方接口，查看额度、刷新和检查更新不会消耗重置机会或发送模型任务。
- 灵动岛式额度胶囊、主题及动画；上/左/右贴边收起，屏幕中间保持展示，游戏状态下保守隐藏。
- 托盘、单实例、窗口位置和主题保存。
- 独立的开机启动、跟随可信 Codex Desktop 启动、跟随其完整退出三个开关。Watcher 无 UI、不调用模型，支持重复启动周期。
- 本地任务队列、状态与恢复提示；本地任务状态不冒充真实 AI 状态。
- 高级页面可绑定项目与真实 Codex Session；状态证据不足保持 Unknown。
- 同一个“检查更新”入口检查本体 Release 和已有可靠来源登记的 Skills；清楚区分有更新、最新、无法检查、来源不明。“更新 Skill”仅执行用户选中的已确认更新。
- 任务范围、项目/请求/会话身份核验、持久化检查点及防重复机制；异常时保留进度并安全阻断。

## 重要限制与安全边界

**GPT ↔ Codex 无人值守自动循环尚未完成真实端到端验收，不是本版可承诺的生产能力。** Legacy Desktop 可能只返回引用，SIWC 正文读取仍有未解决协议问题；连接成功和 HTTP 200 不代表取得完整正文。
SIWC 账户/模型验收属于单独编译的实验模式，正式包默认关闭，不自动登录、发送模型请求或切换付费 API。
高级工作流、Session 持久化及安全收尾保留现有 D 盘安全限制；默认 `%LOCALAPPDATA%\CodexQuotaWidget` 可使用基础界面、额度和本地队列。需要高级功能时，可显式指定安全的 D 盘数据目录：

```powershell
.\CodexQuotaWidget.exe --data-dir "D:\AIControlCenterData"
```

此示例目录不是对某台电脑源码位置的依赖。项目必须由用户明确选择，不会回退到开发者项目或测试项目。
正常启动不会恢复或派发历史任务，不会自动关机、关闭用户程序或改代理。
安全收尾与 Executor 是显式启用的高级功能，仍需真实额度耗尽和同项目任务停止等独立证据，Unknown 不能授权执行。
本次发布未执行真实电源/关闭程序测试，也未重新调用模型验收。

Skills 更新依赖可选的本地更新适配器和经过核验的来源登记，不会在首次运行时自动安装它们。未配置时显示无法检查；系统/插件管理、自建及来源不明 Skill 不会被自动覆盖。适配器源码见 [tools/SkillUpdater](tools/SkillUpdater/README.md)。没有其他工具的通用自动更新适配。
本体更新只提供可信 Release 下载入口，不自动覆盖正在运行的程序。

## 数据与隐私

默认数据目录为 `%LOCALAPPDATA%\CodexQuotaWidget`，独立认证目录为其 codex-home 子目录。可通过 `--data-dir` 或 `CODEX_QUOTA_WIDGET_DATA` 指定自己的目录。
不复制 Codex Desktop 或 ChatGPT Desktop 的登录令牌，不将凭据写入源码、普通诊断或 Release。用户数据、旧任务与历史请求不随发布包分发。
无需管理员权限；启动项只管理本产品当前用户级快捷方式。不要从临时解压目录开启自启动，迁移到另一个文件夹前请先在旧版本关闭本产品启动项，再在新位置重新启用。
诊断 `--check` / `--once` 只读取官方账户/额度；`--startup-smoke-check --data-dir <独立目录>` 为离线启动冒烟测试，不访问账户、不注册启动项、不启动工作流。

## 从源码构建

需要 Windows 11 x64 与 .NET SDK 10.0.401。代码不包含用户的运行配置、登录状态或 Skills 来源登记。

```powershell
dotnet build .\src\CodexQuotaWidget\CodexQuotaWidget.csproj -c Release
dotnet run --project .\tests\CodexQuotaWidget.Checks\CodexQuotaWidget.Checks.csproj -c Release
powershell -NoProfile -File .\scripts\publish.ps1
```

发布脚本只构建和打包，不创建 GitHub Release、不上传、不调用模型。
高级协议见 [工作流](docs/WORKFLOW-LOOP.md) 和 [事件接口](docs/EXTERNAL-AGENT-EVENTS.md)。

## 许可证

MIT，见 [LICENSE](LICENSE)；第三方运行时和界面动效参考见 [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt)。
