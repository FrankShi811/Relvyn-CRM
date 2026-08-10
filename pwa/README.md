# AI Sales OS PWA v5.5.3

AI Sales OS 的纯浏览器版本，面向 macOS、Windows、iPhone、Android 等现代浏览器。它与 Windows 原生正式版共用产品方向，但数据和运行环境完全独立。

本版本仅更新经过来源记录的新应用图标与离线缓存版本，不改变浏览器本地数据结构、AI 设置或 Windows 桌面发布链。

## 已实现

- Dashboard 今日行动简报
- Excel / CSV 动态字段导入和本地 CRM
- 客户列表每页 10 / 30 位切换与分页浏览
- Buyer ID 优先、电话号码兜底的统一客户身份
- Lead Intelligence 与 Customer Brain
- 商机、Inbox 和客户分析工作台的独立客户滚动列表
- 本地知识库检索
- AI 商机分析及 WhatsApp / 邮件草稿
- WhatsApp、邮件外部应用跳转与人工确认发送记录
- 人工触达任务队列
- 客户分析报告、打印 / PDF、JSON 导出
- 完整工作区备份与恢复
- 离线界面缓存、桌面安装、亮暗主题和手机响应式布局

## 纯 PWA 边界

浏览器无法可靠复刻 Windows 原生进程的后台常驻能力，因此本版本不会伪装以下功能：

- 不读取手机 WhatsApp 的实时收件箱
- 不保持所有 WhatsApp / IMAP 账号后台长连接
- 不执行无人值守群发
- 不在 GitHub Pages 后端保存 API Key
- 不与 Windows 版本地 SQLite 数据库自动同步

WhatsApp 和邮件发送采用安全的“生成草稿 → 打开外部应用 → 用户确认 → 写入本地轨迹”流程。

## 本地开发

```bash
npm install
npm test
npm run dev
```

生产构建：

```bash
npm run build
```

GitHub Pages 路径由 `vite.config.ts` 中的 `/AI-whatsapp-OS/` 设置。
