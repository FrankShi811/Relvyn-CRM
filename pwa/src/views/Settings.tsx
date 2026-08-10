import { Bot, Download, ExternalLink, HardDrive, KeyRound, RotateCcw, ShieldAlert, Upload } from "lucide-react";
import { useRef, useState } from "react";
import { sessionKey } from "../ai";
import { storage } from "../db";
import { useStore } from "../store";
import { Button, Card, Field, PageHeader } from "../components/ui";

export function Settings() {
  const { aiSettings, saveAiSettings, refresh, loadDemo, clearAll } = useStore();
  const [settings, setSettings] = useState(aiSettings);
  const [key, setKey] = useState(sessionKey.get());
  const [notice, setNotice] = useState("");
  const restoreRef = useRef<HTMLInputElement>(null);

  const save = async () => {
    await saveAiSettings(settings);
    sessionKey.set(key);
    setNotice("设置已保存。API Key 只保留在本次浏览器标签页的会话存储中。");
  };
  const exportAll = async () => {
    const blob = new Blob([JSON.stringify(await storage.snapshot(), null, 2)], { type: "application/json" });
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = `ai-sales-os-pwa-backup-${new Date().toISOString().slice(0, 10)}.json`;
    a.click();
    URL.revokeObjectURL(url);
  };
  const restore = async (file?: File) => {
    if (!file) return;
    try {
      await storage.restore(JSON.parse(await file.text()));
      await refresh();
      setNotice("备份已恢复；原有记录未自动删除。");
    } catch (error) {
      setNotice(error instanceof Error ? error.message : "备份恢复失败。");
    }
  };

  return <>
    <PageHeader title="API 与数据设置" subtitle="配置浏览器可调用的 AI 服务，并管理当前设备上的本地数据。"
      actions={<Button onClick={() => void save()}>保存并应用</Button>}/>
    {notice && <div className="notice" role="status">{notice}<button aria-label="关闭提示" onClick={() => setNotice("")}>×</button></div>}
    <section className="settings-grid">
      <Card className="settings-card">
        <div className="settings-heading"><span className="settings-icon ai"><Bot/></span><div><h2>AI 服务</h2><p>支持 OpenAI 兼容接口；实际可用性取决于 Provider 是否允许浏览器跨域访问。</p></div></div>
        <div className="form-grid">
          <Field label="API Base URL"><input type="url" name="ai-base-url" autoComplete="off" spellCheck={false} value={settings.baseUrl} onChange={e => setSettings({ ...settings, baseUrl: e.target.value })} placeholder="例如：https://api.example.com/v1"/></Field>
          <Field label="模型"><input name="ai-model" autoComplete="off" spellCheck={false} value={settings.model} onChange={e => setSettings({ ...settings, model: e.target.value })} placeholder="例如：model-name"/></Field>
          <Field label="推理 / 思考深度"><select name="ai-reasoning" value={settings.reasoning} onChange={e => setSettings({ ...settings, reasoning: e.target.value })}>
            {["auto","none","low","medium","high","xhigh","ultra"].map(value => <option key={value} value={value}>{value === "auto" ? "按模型默认" : value}</option>)}
          </select></Field>
          <Field label="API Key（仅当前标签页）" hint="关闭本标签页后自动清除；不会写入 IndexedDB 或备份文件。"><input type="password" name="ai-api-key" autoComplete="off" spellCheck={false} value={key} onChange={e => setKey(e.target.value)} placeholder="输入 API Key…"/></Field>
        </div>
        <div className="warning-panel"><ShieldAlert/><div><strong>浏览器安全边界</strong><p>GitHub Pages 无后端代理，API Key 会从你的浏览器直接发送给所选 Provider。若接口不开放 CORS，请改用支持浏览器调用的 Provider，或继续使用 Windows 正式版。</p></div></div>
      </Card>
      <Card className="settings-card">
        <div className="settings-heading"><span className="settings-icon local"><HardDrive/></span><div><h2>本地数据工作区</h2><p>客户、触达、知识库和 AI 结果保存在当前浏览器的 IndexedDB。</p></div></div>
        <div className="data-actions">
          <Button variant="secondary" onClick={() => void exportAll()}><Download size={16}/>导出完整备份</Button>
          <Button variant="secondary" onClick={() => restoreRef.current?.click()}><Upload size={16}/>恢复备份</Button>
          <Button variant="secondary" onClick={() => void loadDemo()}><RotateCcw size={16}/>加载示例数据</Button>
          <input hidden ref={restoreRef} type="file" aria-label="恢复 PWA 备份" accept=".json" onChange={e => void restore(e.target.files?.[0])}/>
        </div>
        <div className="danger-zone"><div><strong>清空当前浏览器数据</strong><p>此操作无法从云端找回，请先导出完整备份。</p></div><Button variant="danger" onClick={() => { if (confirm("确定清空本浏览器中的全部 AI Sales OS PWA 数据？")) void clearAll(); }}>清空数据</Button></div>
      </Card>
      <Card className="settings-card install-guide">
        <div className="settings-heading"><span className="settings-icon key"><KeyRound/></span><div><h2>在 Mac 上安装</h2><p>安装后可从程序坞独立打开，并在离线时查看已缓存界面和本地数据。</p></div></div>
        <ol>
          <li><strong>Safari：</strong>打开本页面，选择“文件 → 添加到程序坞”。</li>
          <li><strong>Chrome / Edge：</strong>点击地址栏右侧安装图标，或使用页面顶部“安装 PWA”。</li>
          <li><strong>首次使用：</strong>导入客户文件或加载示例数据；需要 AI 时再填 Provider 与临时 API Key。</li>
        </ol>
        <a className="inline-link" href="https://support.apple.com/guide/safari/ibrw9e991864/mac" target="_blank" rel="noreferrer">查看 Apple 的 Web App 说明 <ExternalLink size={14}/></a>
      </Card>
      <Card className="capability-card">
        <h2>纯 PWA 能力边界</h2>
        <div><strong>可实现</strong><span>CRM、Excel/CSV 导入、Buyer ID 统一身份、商机分析、Customer Brain、知识库、AI 草稿、本地报告、离线安装。</span></div>
        <div><strong>需人工确认</strong><span>WhatsApp / 邮件发送通过外部应用打开，返回后由用户确认记录。</span></div>
        <div><strong>浏览器无法可靠实现</strong><span>所有账号后台常驻、读取手机 WhatsApp 实时消息、IMAP 长连接、无人值守自动群发、Windows 自动更新。</span></div>
      </Card>
    </section>
  </>;
}
