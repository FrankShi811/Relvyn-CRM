# AI Sales OS 3.0 产品与架构升级方案

> 定位：面向全球销售团队的 AI Sales Intelligence Operating System
> 方案状态：2026-07-23，Customer Brain V1 已完成并进入 v2.2.0 发布验证
> 实施边界：保留现有 WPF/.NET 8、SQLite、WhatsApp、Email、AI Provider、自动化与更新机制；正式 UI 继续使用 v2.1.0 已发布基线，不覆盖用户电脑上的正式程序。

## 1. 当前产品审计

### 1.1 已确认的现有能力（事实）

| 层级 | 当前实现 | 成熟度 | 主要缺口 |
|---|---|---:|---|
| 客户交互层 | WhatsApp Inbox、Email Inbox、CRM、附件/媒体、会话同步 | 已可用 | 社交渠道仍是未来扩展 |
| 客户智能层 | Lead Intelligence V2、客户分析报告、对话 AI 助手 | 已可用但分散 | 缺少跨模块、可持续更新的统一 Customer Brain |
| 决策层 | 0–100 AI 分数、ABCD、六维证据、风险和下一步 | 已可用 | 建议没有独立生命周期，也没有执行结果回写 |
| 执行层 | WhatsApp/Email 群发、模板、定时、安全阀、人工确认 | 已可用 | 仍以任务表单为主，缺少可观察的智能工作流 |
| 知识层 | 分阶段客户报告、版本历史、Word/PDF 导出 | 已可用 | 缺少团队学习、建议效果与策略复盘数据 |

现有代码已经包含：

- `AiProviderService`：根据用户配置路由 OpenAI Chat Completions 兼容 Provider 或原生 Anthropic Claude 协议，负责结构化 Lead V2 分析与失败保护。
- `CustomerAnalysisService`：数据整理 → 事实提取 → 商业分析 → 销售策略 → 报告生成的多阶段流程。
- `ConversationAssistantService`：建议回复、需求摘要、购买信号、风险和需人工批准的 CRM 字段建议。
- `CampaignAutomationService`：WhatsApp/Email 任务、节奏、安全停止和结果统计。
- `LocalRepository`：客户、聊天、邮件、AI 分析、报告版本、自动化和审计的本地持久化。

### 1.2 根本问题（诊断）

当前系统拥有多个“智能结果”，但它们主要挂在各自模块：

1. 商机智能知道“值不值得跟”。
2. 客户分析知道“为什么以及如何推进”。
3. Inbox 助手知道“现在如何回复”。
4. Campaign 知道“发送了什么、是否成功”。

系统尚未把这些结果合并成一个持续演进的客户认知。因此同一客户的画像、风险、下一步和证据容易在页面间重复计算或短暂存在，无法形成：

`客户事实 → AI 判断 → 建议 → 人工执行 → 客户反馈 → 学习修正`

### 1.3 产品机会

AI Sales OS 3.0 不应继续堆叠页面，而应建立一个 Customer Brain 作为所有模块共享的决策底座。页面只是针对不同工作情境呈现同一份客户认知：

- Dashboard：今天最值得做什么。
- Lead Intelligence：为什么优先这个客户。
- CRM：哪些客户最有价值、资料是否健康。
- Inbox：正在聊天的客户是谁、下一句怎么说。
- Automation：哪些客户应进入哪条触达路径。
- Analysis：面向管理层的完整判断与证据。

## 2. 竞品与最佳实践

以下结论基于官方产品资料，不把营销口号当作已验证的产品效果。

| 类别 | 官方实践 | 可借鉴能力 | AI Sales OS 的差异化 |
|---|---|---|---|
| Salesforce | Pipeline Management 从邮件、通话和记录中提炼建议；Prospecting Agent 研究账户、排序并解释“为什么现在” | 统一信号、优先级、解释层、行动建议 | 在个人 WhatsApp/本地原生场景提供同等级客户认知 |
| HubSpot | Prospecting Workspace 以意向信号聚焦高潜客户，并回答何时联系、联系谁、说什么 | “今日工作台”而非传统报表 | 将 WhatsApp 行为与本地 CRM 直接闭环 |
| Attio | AI Workflow 处理线索增强、路由、简报、信号触发和交接 | 事件驱动、结构化属性、工作流可组合 | 保留动态表格字段，同时建立可审计客户大脑 |
| Apollo | 信号销售、AI 自定义字段、自动资格判断与序列触发 | 信号 → 评分 → 个性化 → 执行 | 不依赖外部销售数据库，也能从自有会话学习 |
| WATI / Respond.io | 多渠道会话、AI 路由/资格判断、CRM 上下文进入每次对话 | Inbox 内完成上下文与执行 | 从“客服会话”升级为“销售决策舱” |
| Linear / Notion / Raycast / Codex | 快速命令、上下文 AI、后台任务、明确状态与高密度交互 | AI 是工作流的一部分，不是独立聊天框 | 形成销售专用 Copilot、任务编排与决策解释 |

官方参考：

- Salesforce Pipeline Management: https://help.salesforce.com/s/articleView?id=sales.pipeline_mgmt_parent.htm&language=en_US&type=5
- Salesforce Prospecting Agent: https://www.salesforce.com/sales/prospecting/agent/
- Salesforce AI Pipeline Visibility: https://help.salesforce.com/s/articleView?id=sales.sales_pipe_visibility_ai_solutions.htm&language=en_US&type=5
- HubSpot Sales Hub: https://www.hubspot.com/products/sales?lang=en
- Attio AI Workflows: https://attio.com/blog/essential-ai-workflows-to-boost-your-gtm-strategy
- Apollo Signal-Based Selling: https://www.apollo.io/insights/signal-based-selling
- Respond.io Omnichannel AI CRM: https://respond.io/omnichannel-ai-crm-conversation-platform
- WATI: https://www.wati.io/
- Notion AI: https://www.notion.com/product/ai
- Linear: https://linear.app/
- Raycast: https://www.raycast.com/
- OpenAI Codex App: https://openai.com/index/introducing-the-codex-app/

## 3. AI Sales OS 3.0 产品路线

### 3.1 北极星体验

用户打开软件后的第一感受不是“我有多少客户”，而是：

1. 今天最应该联系哪五个人。
2. 为什么是他们。
3. 每个人应该采取什么动作。
4. AI 的判断基于哪些证据，置信度是多少。
5. 执行后是否有效，系统从结果学到了什么。

### 3.2 五层产品架构

```text
Customer Interaction Layer
WhatsApp / Email / Social / CRM Data
                 ↓
Customer Intelligence Layer
Customer Brain / Evidence / Behavior Timeline
                 ↓
Decision Layer
Lead Intelligence / Opportunity Prediction / Sales Strategy
                 ↓
Execution Layer
AI Copilot / Approval / Automation / Follow-up
                 ↓
Knowledge Layer
Reports / Recommendation Outcomes / Team Learning
```

### 3.3 核心产品对象

**Customer Brain** 是每位客户的统一智能档案，必须同时保存：

- 已验证事实：来源、时间、原文或字段。
- AI 推断：判断、置信度、所用证据。
- 销售建议：动作、理由、时限、成功标准。
- 数据缺口：尚未确认的问题。
- 风险：成交、使用、流失、合规。
- 行为时间线：消息、回复、触达、分析与阶段变化。
- 建议结果：采纳、忽略、完成、成功、失败及原因。

### 3.4 3.0 成功指标

| 目标 | 产品指标 |
|---|---|
| 提高决策速度 | 从打开客户到明确下一步的中位时间 |
| 提高建议可信度 | 有证据的 AI 判断占比、人工采纳率 |
| 提高执行闭环 | 建议转化为销售动作的比例 |
| 提高销售效果 | 建议采纳组的回复率、推进率、成交率 |
| 降低错误自动化 | 无效号码、退订、IP 安全停止与失败真实归因 |
| 构建团队学习 | 有结果反馈的建议占比、重复有效策略复用率 |

## 4. 技术架构升级

### 4.1 设计原则

1. **添加，不替换**：不修改现有客户主键、聊天、Provider 或 Campaign 契约。
2. **事实与判断分离**：原始 CRM/消息不可被 AI 覆盖；AI 产物独立版本化。
3. **人工可控**：AI 字段建议、发送、阶段变更继续要求人工批准。
4. **失败安全**：AI 失败保留当前等级和资料；进入可重试状态。
5. **本地优先**：所有 Customer Brain 数据仍保存在本地 SQLite。
6. **可演进**：未来 Provider、社交渠道或团队云同步通过接口接入。

### 4.2 新服务边界

```text
CustomerDataCollector
  ├─ CRM adapter
  ├─ WhatsApp adapter
  ├─ Email adapter
  ├─ Campaign adapter
  └─ Analysis history adapter
              ↓
CustomerBrainService
  ├─ materialize profile
  ├─ normalize evidence
  ├─ detect data coverage
  └─ version only on semantic change
              ↓
DecisionOrchestrator (Phase 2)
  ├─ Lead Intelligence
  ├─ Opportunity prediction
  ├─ Next-best-action
  └─ Strategy recommendation
              ↓
ActionAndFeedbackService (Phase 2)
  ├─ approval
  ├─ action logging
  ├─ outcome capture
  └─ learning feedback
```

第一阶段的 `CustomerBrainService` 只整合现有可信输出，不重新硬编码评分、不创造事实、不直接修改 CRM。

### 4.3 AI 结构化管线

```text
Data Collection
  → Fact Extraction
  → Customer Understanding
  → Business Analysis
  → Opportunity Evaluation
  → Sales Recommendation
  → Report Generation
```

每一步必须保存：

- 输入数据版本和摘要。
- 结构化 JSON。
- Provider、模型、时间和状态。
- 错误与重试原因。
- 证据清单和置信度。

## 5. 数据模型与迁移

### 5.1 新增表

#### `customer_intelligence_profiles`

每位客户一份当前有效的物化 Customer Brain；`customer_id` 唯一，`data_json` 保存可演进结构，`version` 只在语义变化时递增。

#### `ai_recommendation_history`

保存每次建议、理由、证据、状态、采纳与结果，不覆盖历史。

#### `customer_behavior_timeline`

把 WhatsApp、Email、Campaign、CRM 和 AI 事件映射为统一时间线；使用来源 ID 做幂等约束。

#### `sales_action_logs`

记录建议产生的人工/自动销售动作、负责人、截止时间和执行状态。

#### `ai_learning_feedback`

记录建议是否有用、动作结果和反馈来源，为后续模型选择和策略优化提供数据。

### 5.2 迁移策略

- 使用 `CREATE TABLE IF NOT EXISTS` 和新增索引，零破坏升级。
- 不改 `leads`、消息、Campaign 和报告表的现有字段。
- 新表全部外键关联 `leads(id)`，删除客户时级联清理智能衍生数据。
- 首次打开某客户时按需物化，不做阻塞式全库迁移。
- 数据库升级前继续使用现有启动保护与完整性检查。
- 失败时新表可为空，现有业务仍可工作。

## 6. 页面级重新设计

### Dashboard — AI Sales Command Center

- 顶部：Today Brief，展示今日目标、风险和 AI 状态。
- 主区：按“为什么现在”排序的优先客户队列。
- 侧区：待批准动作、超时跟进、自动化风险。
- 图表：ABCD、阶段漏斗、回复/推进趋势、AI 建议采纳效果。
- 所有指标可下钻到具体客户和证据。

### Lead Intelligence 2.0 — Opportunity Decision Surface

- 列表不只显示分数，增加置信度、最近信号、数据覆盖和下一动作。
- 右侧 Customer Brain 抽屉展示：
  - animated score ring
  - confidence meter
  - positive / risk signals
  - evidence graph
  - opportunity timeline
  - next-best-action
- 区分“事实”“AI 判断”“销售建议”。

### Customer Universe

- 默认仍保留高密度表格，补充可切换的卡片/地图视图。
- AI 优先级、客户健康、资料完整度成为首要筛选维度。
- 动态 Excel 字段继续保留，不强迫统一固定维度。
- 选中客户可打开 Brain Drawer，不离开列表。

### WhatsApp / Email — AI Sales Workspace

- 左：Conversation Universe，按新消息、价值、风险和跟进优先级排序。
- 中：自然会话、媒体、引用、状态和动态内容。
- 右：可折叠 AI Customer Brain。
- Composer 内 AI Copilot 提供建议回复、谈判策略、需确认字段和发送前风险。
- AI 永不自动覆盖客户资料；用户批准后才写入。

### Automation — AI Workflow Engine

- 使用可视节点表达：Audience → Analysis → Message → Approval → Schedule → Send → Outcome。
- 每个节点有状态、失败原因、重试和审计。
- 工作流运行时显示当前客户、下一步和安全阀。
- WhatsApp 与 Email 使用同一编排模型，渠道结果独立统计。

### Customer Analysis — Consulting Intelligence Report

- 顶部管理层摘要与客户分值。
- Customer story timeline、商业模式图、痛点层级、机会地图、风险雷达。
- 每条结论可展开证据。
- 右侧行动建议按 24 小时/7 天/30 天呈现。
- 屏幕预览与 Word/PDF 使用同一报告视觉语义。

### AI Control Center

- Provider/模型、连接健康、请求成功率、结构化输出成功率。
- 模型作用域：Lead、Copilot、Report、Workflow。
- 隐私、数据保留和本地存储说明。
- 每个模块可查看最近 AI 运行和失败原因。

## 7. 优先级

### S — 决定产品定位

1. Customer Brain 持久化与统一证据模型。
2. Lead Intelligence 2.0 使用 Brain 上下文。
3. Analysis 使用 Brain 快照并回写管理层结论。
4. Copilot 读取 Brain，并将人工批准结果写入动作与反馈。

### A — 决定日常效率

1. Dashboard Today Brief 与优先队列。
2. Workflow Engine 视觉编排和结果回路。
3. 仅在独立设计验收后继续扩展现有 v2.1.0 设计系统、抽屉、状态和动效。

### B — 决定规模化

1. 团队协作、负责人和审批。
2. 跨用户策略学习和高级 Analytics。
3. Social 渠道适配。

## 8. 实施阶段与验收

### Phase 0 — 审计与隔离验证（已完成）

- 已完成现有模块、Provider、数据库和 UI 审计。
- 在独立分支、独立 EXE、独立数据库中运行。
- 正式程序与 GitHub 不受影响。

### Phase 1 — Customer Brain Foundation（当前执行）

- 新增五个实体及 SQLite 表。
- 从现有 Lead、报告、消息和 Campaign 物化统一 Customer Brain。
- 不重新评分，不覆盖 CRM，不重复写建议。
- 单元/回归验证幂等、版本、删除级联和安全迁移。

验收：

- 任意客户可得到一份统一 Brain。
- 事实、推断、建议明确区分并有来源。
- 无新报告时可使用现有可用资料；信息不足显示缺口而不是失败。
- 相同输入重复刷新不产生新版本。

### Phase 2 — Decision & Action Loop

- Lead、Report、Copilot 统一读取 Brain。
- 建议支持接受、忽略、执行、完成和失败。
- 执行结果进入 Sales Action Log 与 Learning Feedback。

### Phase 3 — Command Center & Workflow Engine

- Dashboard Today Brief。
- 可视工作流与跨渠道触达。
- 建议效果、回复率、推进率的归因分析。

### Phase 4 — Team Learning

- 团队策略模板、审批、复盘。
- 在证据充分时比较模型和策略表现。
- 默认仍保持本地优先；云协作作为独立架构决策。

### 发布闸门

1. 编译和 SmokeTests 全部通过。
2. 新旧数据库升级与回滚保护通过。
3. 客户、消息、账号、Campaign 数量回归一致。
4. 运行隔离构建、全量回归和中文安装包冒烟。
5. 用户批准后发布 GitHub Release；开发流程不覆盖本地正式安装。
