# GPT ↔ Codex 产品工作流

1.12.7 的工作流由小组件自己的 `WorkflowLoopRuntime` / `WorkflowLoopService` 执行。手动点击“开始循环”仅授权指定TaskId、项目和写文件范围内的任务；打开程序、恢复记录或显示连接就绪都不会启动任务。当前GPT回复正文引用不可读，真实两轮验收Blocked，不能据历史报告宣称现版本闭环可用。

## 连接与执行

- GPT：复用已安装的官方 Codex 桌面工具服务。需要运行中的桌面应用、可唯一确认的本机 pipe、明确的现有 GPT 会话与 Codex 控制会话。不会安装桥、复制密钥或修改全局 MCP 配置。
- Codex：启动属于本工具的独立官方 `app-server`，复用既有 D 盘独立登录。模型目录只证明枚举结果，不能证明推理或执行成功。
- 流程：GPT 建议 → 对应 Codex 原生 Turn → 固定 Release 编译与检查 → GPT 复核。只有匹配的本轮请求和终止事件才算结果。
- GPT 复核首个非空行必须精确为 `ACC_REVIEW: COMPLETE`、`CONTINUE` 或 `BLOCKED`。格式不明就暂停，普通文本中的示例不能启动下一轮。
- GPT建议首个非空行为`ACC_ADVICE: {单行JSON}`，字段恰为TaskId、RequestId、ProjectRoot、AuthorizationId、Instructions、WritePaths、RequiresConfirmation。身份逐字匹配，WritePaths必须是用户授权子集；需要用户确认即Blocked。建议不是新的授权。

原生Codex请求使用显式禁网的`readOnly`策略，不申请扩大权限。所有额外shell、文件和权限审批均拒绝。官方dynamic tools提供限定项目的文件列举、读取及按预期SHA写入，宿主同时检查冻结的全任务范围和本轮子范围、项目/Task/Session、路径、文本大小和幂等性。工具注册本身不是执行成功证据；必须确认匹配的原生Turn终止。

构建定义禁止模型写入，执行前冻结并在验证前核验；变化时不运行宿主命令。普通生产源码修改后的检查不会自动以完整宿主权限运行，当前会Blocked并报告缺少受限环境验收。已移除普通验证器对旧fixture的特许。本轮独立验收入口只允许两个固定纯字符串表达式与冻结驱动，未推广到任意源码。

## 范围与停止

默认最多3轮、30分钟，具体轮数、时间与阈值在任务首次执行时冻结；五小时额度不满足保护阈值时停止新请求，默认阈值20%。未知、无效或不可确认额度不能派发模型任务。

循环本身没有关闭程序或关机授权。本轮明确禁止关机、重启、睡眠、关闭用户程序、修改网络或代理；没有武装电源动作，也没有用真实电源动作做测试。Executor及原有额度耗尽门禁保持。

暂停、断连、超时或未确认的外部请求会保存恢复状态；重启不自动重发。恢复先核验已确认结果journal；未知投递保留pending并Blocked，确认按钮不能抹去未知请求或代替原生停止证据。跨实例运行锁失败不覆盖其他实例状态。停止是持久化状态，同任务不再启动；已完成TaskId有独立永久小标记，不依赖30条展示历史去重。

## 本地文件

- `<用户选择的D盘数据目录>\workflow\settings.json`：项目、会话、目标、轮数；持久授权始终为 false。
- `workflow\loop-state.json`：当前运行 ID、请求 ID、阶段、目标及最近报告；单份原子备份。它是当前状态，不是完整生命周期审计。
- `workflow\results\<TaskId>\<RequestId>.json`：先落盘的已确认结果journal；缺少或关联不符不会推定成功。
- `workflow\codex-dispatches\<RequestId>.json`：派发前CreateNew标记；未知投递不重发。
- `workflow\completed-tasks\<TaskId>.json`：永久完成标记，只存版本/任务ID/时间。迁移当前及可读历史，已被旧版本淘汰的任务ID无法凭空补造。
- `workflow\logs\latest-build.log` / `latest-checks.log`：固定本地验证日志，脱敏并有界。
- `<用户选择的D盘数据目录>\codex_resume_state.md` / `resume\latest`：开发恢复记录与最新完整恢复点。

只清理自己启动的 RPC 辅助进程和验证子进程。未修改 Codex 核心配置、AGENTS、其他 Skill 或代理/网络设置。不会自动推送 GitHub。

## 验收命令

`--workflow-check` 使用真实 WPF 控件和假控制器，不调用真实模型、网络动作、程序关闭或电源动作。

`--workflow-run --workflow-config <D盘JSON>` 是真实执行入口；配置必须明确 `LocalExecutionAuthorized=true`，不能用作 Dry Run。`--workflow-acknowledge` 仅用于明确核验旧请求后的恢复确认，不派发任务。

`--workflow-stability-connection-check`是本轮独立D盘只读连接/自有空闲重连探测，零模型任务。`--workflow-stability-live-check`会发送真实模型请求，只用于明确授权的一次性新隔离任务；已有目录的claim禁止重跑。最新真实结果及未验证项见[当前状态](AI-CONTROL-CENTER-STATUS.md)。

生产大项目、物理断网、系统重启、IDE 未保存内存及长期无人值守运行，需要另行验收；隔离小项目成功不能扩大到这些结论。
