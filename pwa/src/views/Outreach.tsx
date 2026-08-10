import { Bot, Check, ExternalLink, Mail, Plus, Sparkles } from "lucide-react";
import { useMemo, useState } from "react";
import { draftMessage, sessionKey } from "../ai";
import { DEMO_SOURCE } from "../demo";
import { buildBrain, mailtoUrl, safeWhatsAppUrl, uid } from "../domain";
import { useStore } from "../store";
import type { Channel, Lead } from "../types";
import { Button, Card, EmptyState, Field, PageHeader } from "../components/ui";

export function Outreach({ channel }: { channel: Channel }) {
  const { leads, touches, knowledge, aiSettings, saveTouch } = useStore();
  const eligible = useMemo(() => leads.filter(x => channel === "whatsapp" ? x.phone : x.email), [leads, channel]);
  const [leadId, setLeadId] = useState(eligible[0]?.id || "");
  const [intent, setIntent] = useState("");
  const [subject, setSubject] = useState("");
  const [body, setBody] = useState("");
  const [risk, setRisk] = useState("");
  const [running, setRunning] = useState(false);
  const [opened, setOpened] = useState(false);
  const lead = eligible.find(x => x.id === leadId) || eligible[0];
  const isDemo = lead?.source === DEMO_SOURCE;
  const history = touches.filter(x => x.leadId === lead?.id && x.channel === channel);
  const title = channel === "whatsapp" ? "WhatsApp Inbox" : "邮件 Inbox";

  const generate = async () => {
    if (!lead) return;
    setRunning(true); setRisk("");
    try {
      const result = await draftMessage(aiSettings, sessionKey.get(), lead, buildBrain(lead, touches), touches, knowledge, intent, channel === "whatsapp" ? "WhatsApp" : "Email");
      setBody(result.body); if (result.subject) setSubject(result.subject); setRisk(result.risk);
    } catch (e) { setRisk(e instanceof Error ? e.message : "生成失败"); }
    finally { setRunning(false); }
  };
  const openExternal = () => {
    if (!lead || !body.trim()) return;
    if (isDemo) {
      setRisk("示例客户仅用于界面预览，不会打开 WhatsApp 或邮件客户端。");
      return;
    }
    const url = channel === "whatsapp" ? safeWhatsAppUrl(lead.phone, body) : mailtoUrl(lead.email, subject, body);
    if (!url) return;
    window.open(url, "_blank", "noopener,noreferrer"); setOpened(true);
  };
  const confirmSent = async () => {
    if (!lead || !body.trim()) return;
    await saveTouch({ id: uid(), leadId: lead.id, channel, direction: "outgoing", subject, body, timestamp: new Date().toISOString(), status: "confirmed-sent" });
    setBody(""); setSubject(""); setIntent(""); setOpened(false);
  };
  return <>
    <PageHeader title={title} subtitle={channel === "whatsapp" ? "生成草稿并跳转 WhatsApp；纯 PWA 不读取或伪造实时消息。" : "生成草稿并调用 Mac/手机默认邮件客户端；纯 PWA 不保持 IMAP 后台连接。"}
      actions={<Button variant="secondary" disabled><Plus size={16}/>{channel === "whatsapp" ? "新建对话" : "新建邮件"}</Button>}/>
    {!eligible.length ? <EmptyState title={`没有可用的${channel === "whatsapp" ? "电话号码" : "邮箱"}`} body="请先在客户列表补充联系方式。"/> :
    <section className="inbox-layout">
      <Card className="conversation-rail"><h2>客户</h2>{eligible.map(item => <button key={item.id} className={item.id === lead?.id ? "selected" : ""} onClick={() => { setLeadId(item.id); setOpened(false); }}>
        <span className="avatar">{(item.nickname || item.name).slice(0, 1).toUpperCase()}</span><span><strong>{item.nickname || item.name}</strong><small>{channel === "whatsapp" ? item.phone : item.email}</small></span>
      </button>)}</Card>
      <Card className="conversation">
        <header><div><h2>{lead?.nickname || lead?.name}</h2><p>{lead?.company || "未填写公司"} · {isDemo ? "示例对话，不会对外发送" : "本地人工记录"}</p></div>{channel === "whatsapp" ? <Bot/> : <Mail/>}</header>
        <div className="messages">{!history.length ? <div className="message-empty">暂无人工记录。PWA 不会把手机端消息伪装成已同步。</div> :
          history.map(item => <div key={item.id} className={`bubble ${item.direction}`}><strong>{item.subject}</strong><p>{item.body}</p><time>{new Date(item.timestamp).toLocaleString("zh-CN")}</time></div>)}</div>
        <div className="composer">
          {channel === "email" && <input aria-label="邮件主题" name="message-subject" autoComplete="off" value={subject} onChange={e => setSubject(e.target.value)} placeholder="输入邮件主题…"/>}
          <textarea aria-label="消息正文" name="message-body" autoComplete="off" value={body} onChange={e => setBody(e.target.value)} placeholder="输入正文，或在右侧让 AI 生成可编辑草稿…"/>
          <div><Button variant="secondary" onClick={() => void generate()} disabled={running}><Sparkles size={16}/>{running ? "生成中…" : "AI 写作"}</Button><Button onClick={openExternal} disabled={!body.trim()}><ExternalLink size={16}/>打开{channel === "whatsapp" ? " WhatsApp" : "邮件客户端"}</Button></div>
          {opened && <button className="confirm-strip" onClick={() => void confirmSent()}><Check size={17}/>我已在外部应用中确认发送，记录到客户轨迹</button>}
        </div>
      </Card>
      <Card className="copilot">
        <div className="brain-heading"><span><Sparkles/></span><div><h2>AI Sales Copilot</h2><p>只生成草稿，不自动发送</p></div></div>
        <Field label="这次想表达什么？"><textarea name="message-intent" autoComplete="off" rows={5} value={intent} onChange={e => setIntent(e.target.value)} placeholder="例如：礼貌询问预计采购数量，并提供下一步产品资料…"/></Field>
        <Button onClick={() => void generate()} disabled={!intent.trim() || running}>{running ? "正在生成…" : "立即生成草稿"}</Button>
        {lead && <div className="mini-brain"><span>Customer Brain</span><strong>{buildBrain(lead, touches).coverage}% 资料覆盖</strong><p>{buildBrain(lead, touches).nextAction}</p></div>}
        {risk && <div className="risk-note" role="status">{risk}</div>}
      </Card>
    </section>}
  </>;
}
