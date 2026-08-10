# AI Sales OS 3.0：个人 AI 销售员工升级方案

> 产品定位：Every salesperson has their own AI sales employee.
>
> 技术边界：保留 Native WPF、.NET 8、SQLite、WhatsApp、Email、AI Provider、自动化与 GitHub Release 更新架构。
>
> 产品边界：本版本面向个人销售工作区，不增加多席位、共享收件箱、权限、分配、主管看板或团队路由。

## 1. 当前产品审计

### 1.1 已具备能力

| 层级 | 当前能力 | 状态 |
|---|---|---|
| 客户交互 | WhatsApp Inbox、Email Inbox、CRM、媒体和历史同步 | 已可用 |
| 客户智能 | Lead Intelligence V2、Customer Analysis、Inbox AI 助手 | 已可用但结果分散 |
| 决策 | AI 分数、ABCD、六维证据、风险、下一步 | 已可用 |
| 执行 | WhatsApp/Email 模板、即时/定时任务、节奏、安全停止、人工确认 | 已可用 |
| 知识 | 分阶段报告、版本历史、Word/PDF | 已可用 |
| Customer Brain | 跨 CRM、会话、报告和触达的证据物化 | V1 已有 |

### 1.2 核心缺口

1. Customer Brain 当前主要汇总已有分析，尚无自己的分阶段 AI 运行和中间结果。
2. Lead Intelligence 缺少独立的商机转化概率和机会决策轨迹。
3. 生命周期未覆盖“确认需求、报价、复购”等个人销售常用节点。
4. `NextFollowUpAt` 只是客户字段，不是可完成、可延期、可追溯的任务。
5. 行为时间线已有渠道事件，但缺少统一的客户事件日志和状态变化记录。
6. 推荐、行动和结果已具备底层表，但 UI 尚未形成 Customer 360 闭环。

## 2. 竞品差距与取舍

官方产品资料显示：

- HubSpot Breeze 使用 CRM、会话和业务上下文生成研究、下一步和客户健康建议。
- Salesforce Einstein 对线索和机会给出优先级及影响因素。
- Attio Workflows 强调信号触发、结构化步骤、人工批准、暂停和重跑。
- Respond.io AI Assist 在会话上下文中起草回复，并允许人工编辑后发送。
- WATI 将资格判断、会话和 CRM 更新连接为自动化链路。

AI Sales OS 需要吸收“统一上下文、可解释决策、可追溯执行”的做法，但不复制团队坐席、路由和权限体系。差异化是：

- 个人工作区和本地优先。
- 个人 WhatsApp/Email 与动态 Excel 字段直接形成客户记忆。
- AI 不覆盖原始资料，建议经人工批准后才变成动作。
- 对每一次结论保留来源、证据、模型、版本和结果。

参考：

- https://www.hubspot.com/products/artificial-intelligence?product=crm
- https://help.salesforce.com/s/articleView?id=ai.einstein_sales_scoring_parent.htm&language=en_US&type=5
- https://attio.com/platform/workflows
- https://respond.io/help/quick-start/glossary-of-terms
- https://www.wati.io/products/astra/blog/automate-business-conversations-with-whatsapp-ai-agent/

## 3. 3.0 北极星体验

用户打开软件后，AI 应持续回答五个问题：

1. 今天最应该跟进谁？
2. 为什么是这个客户？
3. 目前成交机会有多大，结论有多可信？
4. 下一步具体做什么、何时做？
5. 执行后发生了什么，AI 建议是否有效？

## 4. 产品与技术架构

```text
Customer Interaction Layer
CRM / WhatsApp / Email / Campaign / Manual Notes
                         ↓
Customer Intelligence Layer
Facts / Evidence / Behavior Timeline / Customer Brain
                         ↓
Decision Layer
Lead Score / Purchase Probability / Risks / Next Best Action
                         ↓
Execution Layer
AI Copilot / Follow-up Tasks / Approval / Automation
                         ↓
Knowledge Layer
Reports / Recommendation History / Action Outcome / Feedback
```

### 4.1 AI 分阶段管线

```text
Data Collection
  → Customer Understanding
  → Opportunity Evaluation
  → Sales Recommendation
  → Customer Brain Materialization
```

每一步保存：

- 输入快照和来源哈希。
- Provider、模型和运行状态。
- 结构化 JSON 中间结果。
- 错误、可重试状态和时间。
- 事实、推断、建议、缺口的证据边界。

### 4.2 失败策略

- AI 未配置或失败：保留 CRM、历史消息、Lead 分数和上一版 Brain。
- 不以本地关键词或规则替代 AI 商机转化概率。
- 失败运行可重试，不产生伪造的成功结果。
- 新资料到达后，旧 Brain 标记为需要重新分析，但历史版本不删除。

## 5. 数据模型与迁移

### 5.1 新增实体

#### Customer Brain Run

保存一次分阶段 AI 运行的输入、理解、机会评估、建议和错误。

#### Follow-up Task

保存客户、建议来源、标题、原因、优先级、截止时间、状态和结果。

#### Customer Event Log

保存客户创建、资料修改、阶段变化、AI 分析、建议、行动和结果事件。

### 5.2 扩展现有实体

- `CustomerIntelligenceProfile`：商机转化概率、建议阶段、运行状态、最近 Brain Run。
- `Lead`：AI 商机转化概率（仅结构化 AI 成功后写入）。
- `LeadStage`：新增 `RequirementConfirmed`、`Quotation`、`RepeatPurchase`。

### 5.3 SQLite 迁移

- 只使用 `CREATE TABLE/INDEX IF NOT EXISTS`。
- 新表通过 `customer_id` 外键关联 `leads(id)`，删除客户时级联清理。
- 不修改现有消息、账号、Campaign、报告和客户主键。
- JSON 模型增加字段时由默认值兼容旧记录。

## 6. 页面升级

### Dashboard

- 待跟进数量读取独立任务，不再只依赖客户字段。
- 后续增加“今日行动”列表，按到期时间、AI 优先级和商机转化概率排序。

### Lead Intelligence

- 保留 AI Score、等级、六维证据。
- 增加商机转化概率、Brain 新鲜度和建议生命周期。
- 继续明确区分 AI 分数与商机转化概率。

### Customer 360

在现有客户资料窗口中增加：

- Customer Brain 摘要、数据覆盖、置信度、商机转化概率。
- 事实 / AI 判断 / 建议 / 信息缺口。
- 推荐历史、跟进任务、行为与客户事件时间线。
- 手动运行或重试 Customer Brain。

### WhatsApp / Email Inbox

- 继续使用同一 Customer Brain。
- AI 回复、CRM 字段建议和阶段建议必须由用户确认。
- 新消息写入时间线，新资料使 Brain 进入待重新分析状态。

## 7. 实施分期

### Phase 1 — Foundation

- 新数据契约、SQLite 表、生命周期、分阶段 Brain Run。
- 跟进任务、客户事件日志。
- 旧库无损升级和幂等测试。
- 状态：已由 v3.0.0 完成。

### Phase 2 — Customer 360

- 客户资料窗口接入 Brain、任务、推荐和时间线。
- Lead Intelligence 增加商机转化概率和 Brain 状态。
- Dashboard 改用独立待办。
- 状态：已由 v3.0.0 完成。

### Phase 3 — Personal AI Employee

- Today Brief。
- Inbox 中基于 Brain 的回复和提问策略。
- 推荐接受、延期、完成、失败和结果反馈。
- 状态：已由 v3.1.0 完成。

### Phase 4 — Learning

- 比较建议采纳后的回复率、推进率和成交结果。
- 个人策略复盘与高效话术复用。
- 不引入团队管理功能。
- 状态：已由 v3.2.0 完成。
- 真实结果口径：
  - 回复只读取行动执行后实际收到的 WhatsApp / Email 客户来信。
  - 推进只读取执行后真实发生的 CRM 阶段变化。
  - 成交只读取 `customer` / `repeat_purchase` 等实际生命周期结果。
  - 失败发送不进入样本；尚未产生结果的行动保持待观察，30 天后标记为已过期。
  - 系统观察反馈与人工反馈独立保存，不用关键词、演示数据或本地评分规则代替真实结果。
- 个人经验沉淀：
  - Dashboard 与 Customer 360 展示样本量、回复率、推进率、成交率和策略复盘。
  - 高效话术按真实结果排序后提供给 AI 助手作为表达策略参考。
  - AI 助手不得复用其他客户的事实、价格、承诺或专属信息。

## 8. 发布闸门

1. 编译和 SmokeTests 全部通过。
2. 旧数据库升级、新数据库初始化、删除级联和幂等通过。
3. AI 成功、无 Key、无效 JSON、超时和重试通过。
4. 客户、聊天、账号、Campaign 和报告数量保持一致。
5. 使用隔离数据库运行 UI 冒烟，不覆盖本机正式数据。
6. 非 UI 大改完成后直接发布 GitHub Windows Release；不由开发流程覆盖、安装或重启本机正式程序。
7. macOS Release 保持暂停，直至 Apple Developer ID、签名和公证条件恢复。
