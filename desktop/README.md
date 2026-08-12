# AI Sales OS Windows 原生桌面版

该目录是 WPF/.NET 8 原生实现，不加载 React，不包含 Electron、Tauri 或 WebView，也不会启动 Fastify、Vite 或 localhost HTTP 服务。

## 运行时结构

- `WAFlow.Desktop`：保留的内部项目名；构建脚本将内部产物覆盖为根目录唯一的 `AI Sales OS.exe`，提供全部 WPF 原生界面。
- `WAFlow.Core`：保留的内部核心模块名，包含 SQLite、Excel/CSV、AI 模型发现、WhatsApp 回复分析、多账号、持久群发任务调度、严格作用域 Knowledge Base / RAG，以及分阶段客户情报报告和 Word/PDF 导出。
- `WAFlow.WhatsApp.Bridge.exe`：内嵌 Node SEA Windows EXE，通过标准输入输出 JSON-RPC 与主程序通信，不开放本地 HTTP 端口。
- `WAFlow.SmokeTests`：离线核心测试；AI Provider 使用模拟响应，不访问真实账号或客户。
- 应用级消息守护会同时维持全部已登录 WhatsApp 会话和已配置 IMAP 邮箱；侧栏未读气泡从 SQLite 汇总所有账号，隐藏页面不会把后台新消息误标为已读。

内部命名与默认 `%LOCALAPPDATA%\WAFlow` 数据目录有意保留，以兼容既有数据库、Windows 凭据和 WhatsApp 加密会话。v5.11.0 起，用户可在设置中把完整工作区迁移到其他本机固定磁盘；位置索引保存在 `%LOCALAPPDATA%\AI Sales OS\data-workspace.json`，API 与邮箱凭据仍由 Windows 凭据管理器保护。产品界面、文件名、版本属性和应用图标均已统一为 AI Sales OS。

## 构建与测试

```powershell
cd "D:\whatsapp 自动化"
.\scripts\test-desktop.ps1
.\scripts\build-desktop.ps1
.\scripts\build-windows-installer.ps1 -SkipAppBuild
```

如构建机的 Node.js 或 .NET SDK 不在 PATH，可分别设置 `WAFLOW_NODE_PATH` 和 `WAFLOW_DOTNET_PATH`。发布结果为根目录固定文件 `AI Sales OS.exe`，是 `win-x64` 自包含单文件 EXE；每次构建只覆盖该文件。Windows 安装器固定覆盖 `dist\installers\AI Sales OS Setup.exe`，默认优先推荐可用的非系统盘。

## 安全与发送边界

- AI 仅调用用户选择的 OpenAI Chat Completions 兼容 HTTPS API，或原生 Anthropic Claude Messages API；设置页通过模型目录自动读取可用模型和接口声明的推理档位，支持全域默认或 Customer Operations / Insights 分板块覆盖。API 未声明的档位只使用模型默认值，失败时保留原始客户数据并标记可重试。
- CRM 使用统一客户身份键：Buyer ID 存在时优先作为跨导入、Inbox、分析、自动化和记忆的业务标识；Buyer ID 缺失时才以标准化电话号码匹配。Buyer ID 冲突会失败关闭，不会自动合并旧客户或覆盖其历史。
- Knowledge Base 原件与版本保存在本地；只有人工批准、未过期、无冲突且作用域匹配的知识块才能进入检索，提示注入和 AI 未发送草稿不能成为正式知识。
- 客户情报报告的每个 AI 阶段都保存结构化结果和来源快照；重新分析只创建新版本，不会覆盖 CRM、WhatsApp 或 Lead Intelligence 原始数据。
- AI API Key 和 WhatsApp 会话加密密钥保存在 Windows 凭据管理器。
- 本地工作区迁移采用复制、SHA-256、SQLite 完整性校验、重启切换与启动失败回滚；新位置成功启动前不会删除旧工作区。
- 群发任务必须人工批准，发送前再次检查账号连接、E.164 号码、退订状态、消息内容和每日上限；营销同意状态保留为提示但不再把新导入客户全部排除。发送间隔不作为规避平台风控的承诺。
- 任务批准时记录公网 IP；运行中每 10 秒及每次发送前复核，变化即停止全部账号的自动触达并记录各任务停止位置。
- “发送历史与质量”按任务展示成功、失败、跳过、取消、待发送、完成进度、成功率和停止原因。
- 首次配置默认使用自定义 OpenAI 兼容接口，也可选择内置 Provider 或原生 Anthropic Claude 接口，并从实时拉取的模型中选择工作模型；程序不会预选特定模型供应商。
- 全局新手入门和七个模块教程分别保存已读状态；每个模块首次进入自动展示，主窗口和 API 设置页右上角长期保留“本页使用手册”。
- 未完成 AI 分析的客户始终为 D 级、0 分。新 WhatsApp 客户回复会把原始消息与历史上下文交给所选 AI 模型串行分析；V2 六维评分、WhatsApp 行为修正、原因和证据全部通过校验后，才会写入商机智能与 Dashboard 等级分布。
- 启动前执行 SQLite 完整性检查并保留最近 10 个一致性备份；若检测到可恢复损坏，会先归档原件，再通过重建页面和索引恢复可读取数据。
- 已连接账号允许在 Inbox 人工选择联系人并真实创建 WhatsApp 群组；群组、状态和频道仍不会进入自动发送队列。
- 非官方个人账号协议存在限制或封号风险，程序不实现规避检测的随机化、指纹或代理功能。
