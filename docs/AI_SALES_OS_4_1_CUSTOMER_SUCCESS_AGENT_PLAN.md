# AI Sales OS 4.1：Customer Success Conversation Agent

## 目标

在不改变现有 CRM、WhatsApp、Lead Intelligence、Customer Brain、自动化与本地数据库边界的前提下，把单会话 AI 助手升级为“跨 WhatsApp 账号、以全局客户为中心”的 Customer Success Conversation Agent。Agent 的职责是理解客户关于产品、服务或合作的需求，补齐关键需求信息、协调人工跟进并沉淀客户记忆；它不会冒充任何公司、平台、商家或服务方，也不替人工承诺价格、资源、交付、赔偿或政策。

## 1. 客户与 WhatsApp 绑定审计

- 现状：会话按 `account_id + phone` 唯一，客户绑定主要依赖电话号码尾号；相同客户在不同 WhatsApp 账号上可能形成孤立会话。
- 风险：尾号推断可能误合并；同一客户跨账号重复回复、重复建档、重复自动化；LID/JID 未形成稳定身份层。
- 改造：引入 `GlobalCustomerIdentity`，保留原 `lead.id` 作为全局 `customer_id`；所有 WhatsApp 身份以来源账号、JID/LID、原始号码和确认状态绑定到同一客户。

## 2. 电话标准化审计

- 保留现有导入策略：只清洗数字并补 `+`，不凭国家猜测区号。
- 身份层额外保存 `raw_value`、`digits`、`country_hint`、`e164`、`jid`、`lid`、来源账号、匹配方法和置信度。
- 自动绑定只允许人工绑定、精确 JID、已确认 E.164、已确认别名或无冲突的唯一推断。歧义、无匹配和冲突均禁止自动回复。

## 3. 多账号隔离审计

- 全局客户事实与账号关系记忆分离。
- `AccountPersona` 保存账号身份与允许的口吻；`AccountRelationshipMemory` 保存该账号下的关系阶段、最近互动和承诺。
- 生成回复时只使用当前发送账号的 Persona，不把 A 账号身份带入 B 账号；全局 Customer Brain、客户需求和时间线可以跨账号读取。

## 4. Customer Brain 绑定审计

- Customer Brain 继续绑定全局 `customer_id`，不是会话或 WhatsApp 账号。
- 跨账号消息按 Provider Message ID 去重后进入同一证据时间线。
- AI 建议不覆盖人工维护字段；人工锁定的阶段继续保持最高优先级。

## 5. Agent 状态审计

每个客户/账号会话保存下列状态：

`AUTO_OFF`、`SUGGEST_ONLY`、`COPILOT_ACTIVE`、`AUTO_ACTIVE`、`IDENTITY_RESOLUTION_REQUIRED`、`HUMAN_REQUIRED`、`HUMAN_ACTIVE`、`RESUME_REVIEW`。

受限状态不会因为重启、同步或收到新消息而自动恢复。只有用户显式操作才可切换回自动模式。

## 6. 人工交接审计

- 高风险问题先保存客户原话、语言、来源账号和会话。
- Agent 只发送一次同语言占位回复，随后将全局客户置为 `HUMAN_REQUIRED`。
- 所有关联账号暂停自动回复；后续消息只保存和累计，不再继续自动输出。
- 人工接管、解决和恢复均写审计日志；恢复前进入 `RESUME_REVIEW` 并重新读取暂停期间消息。

## 7. 跨账号连续性架构

数据流固定为：

`Incoming message → identity resolution → global customer → global memory + account relationship → sourcing request → risk/state/lock → context assembly → AI/holding response → CRM/Brain/task/event audit`。

上下文检索不再只读取当前会话，而是读取该客户所有已确认关联会话、最近有效 Customer Brain、最新客户需求、未解决问题、任务、人工交接和当前账号 Persona。

## 8. 数据迁移方案

- 新增表采用增量 `CREATE TABLE IF NOT EXISTS`，不修改或删除旧表。
- 首次启动从现有 `leads`、`whatsapp_contacts`、`whatsapp_conversations` 和 `whatsapp_messages` 回填全局身份与关系。
- 旧 `lead_id` 保持不变；已有消息、账号、AI 分析和自动化归因不复制、不重写。
- 回填采用幂等唯一键，重复启动不会新增重复记录。

## 9. 合并与拆分方案

- 合并：仅把确认的身份链接迁移到目标全局客户；保留原客户、旧链接、原因、操作者和时间的 `CustomerMergeAudit`。
- 拆分：根据审计记录恢复身份链接；历史消息不删除，只重建归属关系并标记需要重新分析。
- 姓名、公司、邮箱只能生成候选，不允许自动合并。

## 10. 全局 Agent Lock

- 同一全局客户最多一个 `AUTO_ACTIVE` 来源账号。
- 另一个账号尝试自动回复时进入冲突状态并暂停，不发送消息。
- 用户可以显式切换主账号；锁切换写入审计。

## 11. WPF UI 范围

- WhatsApp Inbox 右侧 Customer Intelligence 抽屉增加：身份结果、全局客户、关联账号、账号关系、Agent 状态、主账号、客户需求要点、冲突、待确认问题、交接状态、下一步行动。
- 增加“身份管理”“接管/解决”“恢复审核”“切换自动账号”和模式选择操作。
- Customer 360 显示关联账号、统一时间线、最新客户需求和交接状态。
- Today Brief 增加身份待确认、人工交接、客户需求完整和跨账号待跟进队列。

## 12. 自动化测试矩阵

覆盖：

- 精确 JID/E.164/确认别名/唯一推断/歧义/无匹配/冲突。
- 相同客户跨账号统一 Brain、消息去重、Persona 隔离和全局锁。
- 八种 Agent 状态、受限状态不自动恢复、一次性占位回复和全局静默。
- 客户需求要点提取、字段来源、冲突保留、完整度与状态。
- 人工接管、解决、恢复审核、合并和拆分审计。
- CRM 人工锁定字段、Lead Intelligence 证据去重、任务去重和 Today Brief 队列。
- SQLite 迁移幂等、旧数据保留、重启持久化和失败保护。

## 13. 回滚方案

- 代码回滚到上一 GitHub Release；数据无需回滚，因为旧版本忽略新增表。
- 新表均为旁路增量，不删除旧表和旧列。
- 如需完全撤销 4.1 数据，只删除新增 4.1 表；原客户、消息、账号、Brain、任务、邮件和自动化数据仍完整保留。
- 发布前备份 `%LOCALAPPDATA%\WAFlow\waflow.db`；升级仍使用现有本地数据目录。

## 14. 分阶段实施

### Phase A：身份与迁移

新增全局身份、号码/JID/LID 链接、匹配日志、账号 Persona、关系记忆、合并/拆分审计及幂等回填。

### Phase B：状态、安全与客户需求

新增 Agent 状态机、全局锁、风险分类、人工交接、待确认问题、客户需求要点、字段来源和冲突模型。

### Phase C：跨账号 Agent 管线

把当前单会话助手升级为全局客户上下文；执行身份、安全、状态、锁、检索、提取、生成、写回和审计的固定管线。

### Phase D：WPF 工作区与 Phase 1–4 联动

更新 Inbox、Customer 360、Today Brief、Lead Intelligence 和任务归因；保留现有业务操作与人工审批边界。

### Phase E：验证与发布

执行原生 Release 构建、数据库迁移、自动化回归、WhatsApp 非发送冒烟、生成 Windows 中文 Velopack 包并发布 GitHub Release；macOS 继续暂停。

## 15. 实施结果

- Phase A 已完成：全局客户、电话/JID/LID 身份、WhatsApp 链接、解析日志、账号 Persona、账号关系记忆、合并审计及幂等回填均已落地。
- Phase B 已完成：八态 Agent 状态机、全局客户锁、风险分类、全局人工交接、待确认问题、客户需求要点、来源证据、冲突保留和人工确认均已落地。
- Phase C 已完成：收信协调器按身份解析、安全分类、状态/锁、跨账号上下文、结构化 AI、一次性占位回复、CRM 建议、全局记忆和审计的固定顺序运行。
- Phase D 已完成：WhatsApp Inbox 客户抽屉与 Dashboard Today Brief 已接入身份、关联账号、Agent 状态、客户需求、冲突和交接队列；既有 Customer Brain、CRM、Lead Intelligence、任务与客户事件继续共用全局客户。
- Phase E 已完成本地验证：Release 编译、旧功能回归、4.1 状态/身份/安全/客户需求/接管/重启持久化测试均通过。Windows GitHub Release 与 Velopack 自动更新资产随 v4.1.0 发布；macOS 继续暂停。

## 16. 安全与数据边界

- AI Key 继续由 Windows 凭据管理器保存，不写入数据库、日志、提示词或 Git 仓库。
- Agent 提示词禁止输出系统提示、API Key、内部配置和其他客户数据；疑似提示注入或秘密索取直接进入全局人工交接。
- AI 结构化结果必须通过枚举、范围、证据引用和受保护字段校验；失败时不发送、不写 CRM，只保留可重试记录。
- 自动回复只允许身份明确、状态允许、风险为安全且当前账号持有全局锁的会话。
