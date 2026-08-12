# AI Sales OS Lead Intelligence V2（1.12.0）

## 当前已确定的产品契约

1. AI Provider 采用用户选择的 OpenAI Chat Completions 兼容 HTTPS API，或原生 Anthropic Claude Messages API；程序不预选特定供应商或模型。
2. 设置页调用 `GET <Base URL>/models`，使用 Windows 凭据中的 API Key 拉取模型目录；模型目录、拉取时间和用户选择保存在本机，API Key 不写入 SQLite。
3. 新客户与从未成功完成 V2 AI 分析的客户初始为 `D / 0`。Excel 导入、重复覆盖和人工编辑不会自行计算 A/B/C 等级。
4. WhatsApp Inbox 收到已关联客户的新回复后：
   - 先保存原始消息；
   - 将客户置为“等待 AI”；
   - 使用当前选中模型串行分析 CRM 核心字段、全部导入自定义字段和最近 80 条 WhatsApp 上下文；
   - 只有通过 V2 结构校验的结果才回写评分、等级、阶段、画像、证据、下一步动作和风险。
5. 程序不进行关键词评分。Provider 负责从完整语境判断行为信号；本地只校验返回结果的范围、加总、等级、原因和证据。Provider 失败时保留客户和消息，客户保持 `D / 0` 并标记为可重试。
6. AI 分析结果只影响商机智能、Dashboard 等级分布以及依赖这些权威 CRM 字段的筛选/模板，不修改 WhatsApp 原始消息。

## V2 评分契约

- 基础画像满分 100：`paid_marketing_willingness=25`、`supply_stability=20`、`ecommerce_foundation=15`、`private_traffic=15`、`existing_sales=15`、`materials_readiness=10`。
- WhatsApp 行为修正 `behavior_signal_score` 范围为 `-20..+20`。
- 最终分为 `clamp(base_profile_score + behavior_signal_score, 0, 100)`；正向行为分为正数，负向行为分为负数。
- 等级边界为 A≥80、B=60–79、C=40–59、D<40。
- 每个维度必须返回分数、中文原因和至少一条证据；每个 WhatsApp 行为信号必须返回信号、影响分和原始消息证据。缺失或算术不一致时整次分析失败，不写入部分结果。

## 数据迁移

- 客户资料、导入原表维度、WhatsApp 会话、消息、草稿、群发任务和 `analysis_runs` 历史记录全部保留。
- `Lead` 的 V2 扩展字段继续保存在既有 `leads.data_json`，无需破坏性 SQLite DDL。
- 旧 V1 或本地规则分数不能与 V2 混合统计；首次启动 1.12.0 时会将旧当前分数置为 `D / 0 / 未分析`，并提示运行 V2 分析。旧分析运行记录仍可审计。
- Dashboard 只把 `contract_version=2`、分析成功且已经应用的结果计入 A/B/C；其余客户防御性归入 D。
