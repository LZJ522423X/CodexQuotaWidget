# 外部 Agent 状态与耗尽事件接口（1.7.0）

所有通知存储在 <用户选择的D盘数据目录>\events。控制中心只写状态，不启动外部Agent、不执行电脑交互。任何通知都不是操作授权，消费者不能执行建议文本或在开机时重放危险动作。

## 通用状态通知

codex_state_changed.json 为最新状态。codex_working.json 与 codex_completed.json 分别保留最后一次该类型，可能属于旧绑定；消费者必须先读通用最新文件，再核对类型文件 EventId、BindingId、CreatedAt、State、SchemaVersion、SequenceNumber 全部相等，才能把类型文件当作当前状态。

CodexStateChangedEvent 的 SchemaVersion=2，保留schema1读取兼容，与下方耗尽事件是不同协议。保留字段 EventId、BindingId、CreatedAt、ProjectRoot、SessionId、TurnId、Source、TrustLevel、State、EvidenceAt、LastActivity、UnknownReason、ProjectAssociationVerified、Synthetic、PassiveNotification、GrantsActionAuthority、TypeFileSemantics；新增 SequenceNumber、EvidenceLevel、EvidenceKind、EvidencePosition，schema2必须显式包含这四项。序号由本机writer持久分配，不信任外部填写。

State 使用字符串 Working / Waiting / Completed / Failed / Exhausted / Unknown。TrustLevel 使用 None / ObservedSession / ProjectVerified。CreatedAt 是通知生成时间，EvidenceAt/LastActivity 才是实际状态证据时间，不可用轮询时间续鲜。Completed仅表示一个Turn结束。

明确状态须有绑定、Session、Turn和证据；Unknown说明不可确认，并非程序崩溃结论。仅错误且无失败终止事件时也是Unknown，不能把重试中的错误当作Failed。跨项目显式关联可以给 ObservedSession 的真实展示，但 ProjectAssociationVerified=false，不满足收尾授权。PassiveNotification必须true、GrantsActionAuthority必须false；拒绝生产Synthetic通知。

相同绑定/Session/Turn/状态/实际证据/来源/可信等级不会因轮询时间不同重发；新绑定产生新身份。写入中断时类型文件可能缺失或旧，必须以上述通用文件为准。每类固定当前文件及单份原子备份；不保证事件投递。主文件损坏、缺主留备份时不自动回退重放。消费者还应核对来源、绑定/项目、实际时间新鲜度及字段完整性。

同数据目录的SequenceNumber严格增加，包括重启和Binding切换；它仅表示本工具writer的输出提交顺序，不是Codex内部事件序号，也不能证明原生事件发生顺序。不同数据目录的序号不可比较，CreatedAt不决定排序。EvidencePosition是实际证据行末字节位置，只在同绑定/Session/来源范围比较，不能跨来源当全局游标。EvidenceKind是受控typed名，不包含任务正文。EvidenceLevel字符串为None / LogPresent / SessionMatched / ProjectMatched / LifecycleConfirmed；它不能单独授权操作。

history/codex_state_timeline.json 的 Entries 按SequenceNumber升序，最多256条、4MB及一份.bak，只是状态采样，可能错过中间变化，不是完整原生日志。历史不证明停止，不得重放动作。若历史写入中断，可靠最新记录仍是提交标记，后续相同观察可补齐，不重分配身份或序号。

task级 Exhausted 只写通用状态文件，不覆盖 codex_exhausted.json；后者继续要求真实额度与任务停止双重确认。

## 可选被动接收回执

外部Agent自行写固定 agent_ack.json，控制中心只读，不会创建它或启动Agent。ACK是未经认证的接收声明，不能证明实际消费、动作成功、任务停止，也不参与自动收尾或Executor授权。

ACK自身SchemaVersion=1。JSON字段名称区分大小写，所有以下字段必须存在：

```json
{
  "SchemaVersion": 1,
  "EventId": "<当前通用事件GUID>",
  "BindingId": "<当前绑定GUID>",
  "SessionId": "<当前Session>",
  "SequenceNumber": 1,
  "AgentName": "external-agent",
  "ReceivedAt": "<接收时的ISO8601时间>",
  "Status": "Received",
  "PassiveAcknowledgement": true,
  "GrantsActionAuthority": false
}
```

EventId/BindingId/SessionId/SequenceNumber必须精确匹配已提交的当前schema2事件；未绑定、旧schema1或未提交Seq0不能接收ACK。默认AgentName仅dsh或external-agent（区分大小写），不是认证机制。ReceivedAt必须在当前事件CreatedAt与读取时刻之间。16KB上限、重复/未知字段、链接、异常路径、损坏、锁竞争或时间异常均产生独立拒绝诊断，不修改内部状态。Status不能填Completed或ActionSucceeded。

该ACK不消费或改写原1.5 codex_exhausted.json；后续真实Agent接入需要单独协议验收。

## 额度耗尽通知（保留1.5协议）

文件：<用户选择的D盘数据目录>\events\codex_exhausted.json。SchemaVersion=1。仅真实额度耗尽与明确项目任务停止同时确认时原子写入；当前未出现真实事件时文件可不存在，不能据文件缺失推断任务状态。

字段：EventId、CreatedAt（UTC）、TriggerReason、RemainingPercent、CycleKey、ResetAt、QuotaSource、ProjectRoot、TaskStatus（State/Source/SessionId/TurnId/ObservedAt/EvidenceAt/TaskStoppedConfirmed/CoverageComplete）、TaskStoppedConfirmed、Synthetic、RecoveryFilePath、SuggestedNextActions。

枚举State的JSON值：0 Unknown，1 Running，2 WaitingForInput，3 Completed，4 AbnormalExit，5 QuotaExhausted，6 UnableToContinue。Completed说明该turn结束，不说明整个开发项目完成。

消费者应验证schema/来源、事件与周期去重、时间新鲜度、项目/路径及Synthetic=false。通过验证仅代表收到被动通知，不授权关闭程序、提交远程数据或电源操作；不要执行任何建议文本。必须读取恢复文件、验证恢复状态并遵守用户授权。拒绝或忽略过期事件，不能在开机时自动重放。

只保留最新一份通知；消费者若离线错过旧事件，本接口不保证历史投递。控制中心不会启动、登录或控制外部Agent，也不把通知当Executor请求。
