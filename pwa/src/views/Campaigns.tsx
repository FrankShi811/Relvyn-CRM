import { Check, ExternalLink, ListChecks, Plus, SkipForward } from "lucide-react";
import { useMemo, useState } from "react";
import { DEMO_SOURCE } from "../demo";
import { mailtoUrl, safeWhatsAppUrl, uid } from "../domain";
import { useStore } from "../store";
import type { Channel } from "../types";
import { Button, Card, EmptyState, Field, Modal, PageHeader } from "../components/ui";

export function Campaigns() {
  const { leads, outreach, saveOutreach } = useStore();
  const [creating, setCreating] = useState(false);
  const [notice, setNotice] = useState("");
  const pending = outreach.filter(x => x.status === "pending" || x.status === "opened");
  const done = outreach.filter(x => x.status === "confirmed-sent").length;
  const next = pending[0];
  const lead = leads.find(x => x.id === next?.leadId);
  const open = async () => {
    if (!next || !lead) return;
    if (lead.source === DEMO_SOURCE) {
      setNotice("示例任务仅用于界面预览，不会打开外部应用或联系真实客户。");
      return;
    }
    const url = next.channel === "whatsapp" ? safeWhatsAppUrl(lead.phone, next.body) : mailtoUrl(lead.email, next.subject || "", next.body);
    if (url) window.open(url, "_blank", "noopener,noreferrer");
    await saveOutreach({ ...next, status: "opened" });
  };
  return <>
    <PageHeader title="自动化触达" subtitle="纯 PWA 生成可审计任务队列，每一条都必须人工打开并确认发送。"
      actions={<Button onClick={() => setCreating(true)}><Plus size={16}/>创建任务</Button>}/>
    {notice && <div className="notice" role="status">{notice}<button aria-label="关闭提示" onClick={() => setNotice("")}>×</button></div>}
    <section className="metrics compact">
      <Card className="metric"><div className="metric-icon"><ListChecks/></div><span>全部任务</span><strong>{outreach.length}</strong><small>本地任务队列</small></Card>
      <Card className="metric"><span>等待人工执行</span><strong>{pending.length}</strong><small>不会后台自动发送</small></Card>
      <Card className="metric"><span>已确认发送</span><strong>{done}</strong><small>仅统计用户确认</small></Card>
    </section>
    <Card className="campaign-workbench">
      <div className="section-title"><div><h2>人工发送队列</h2><p>浏览器无法确认外部应用是否真正发送，因此需要二次确认。</p></div></div>
      {!next || !lead ? <EmptyState title="队列已清空" body="创建一个 WhatsApp 或邮件触达任务开始执行。"/> :
      <div className="queue-current">
        <div><span className="queue-count">待执行 {pending.length}</span><h3>{lead.nickname || lead.name}</h3><p>{next.body}</p>{next.subject && <small>主题：{next.subject}</small>}</div>
        <div className="queue-actions"><Button onClick={() => void open()}><ExternalLink size={16}/>打开外部应用</Button>
          <Button variant="secondary" disabled={next.status !== "opened"} onClick={() => void saveOutreach({ ...next, status: "confirmed-sent" })}><Check size={16}/>确认已发送</Button>
          <Button variant="ghost" onClick={() => void saveOutreach({ ...next, status: "skipped" })}><SkipForward size={16}/>跳过</Button></div>
      </div>}
      <div className="queue-list">{outreach.slice(0, 12).map(item => {
        const customer = leads.find(x => x.id === item.leadId);
        return <div key={item.id}><strong>{customer?.nickname || customer?.name || "未知客户"}</strong><span>{item.channel === "whatsapp" ? "WhatsApp" : "邮件"}</span><small>{item.status}</small></div>;
      })}</div>
    </Card>
    {creating && <CreateCampaign leads={leads} onClose={() => setCreating(false)} onCreate={async items => { for (const item of items) await saveOutreach(item); setCreating(false); }}/>}
  </>;
}

function CreateCampaign({ leads, onClose, onCreate }: { leads: ReturnType<typeof useStore>["leads"]; onClose: () => void; onCreate: (items: ReturnType<typeof useStore>["outreach"]) => Promise<void> }) {
  const [channel, setChannel] = useState<Channel>("whatsapp");
  const eligible = useMemo(() => leads.filter(x => channel === "whatsapp" ? x.phone : x.email), [leads, channel]);
  const [selected, setSelected] = useState<string[]>([]);
  const [subject, setSubject] = useState("");
  const [body, setBody] = useState("");
  const create = () => onCreate(selected.map(leadId => ({
    id: uid(), leadId, channel, subject: channel === "email" ? subject : undefined, body, status: "pending" as const, createdAt: new Date().toISOString()
  })));
  return <Modal title="创建人工触达任务" onClose={onClose} wide>
    <div className="form-grid">
      <Field label="渠道"><select name="campaign-channel" value={channel} onChange={e => { setChannel(e.target.value as Channel); setSelected([]); }}><option value="whatsapp">WhatsApp</option><option value="email">邮件</option></select></Field>
      {channel === "email" && <Field label="邮件主题"><input name="campaign-subject" autoComplete="off" value={subject} onChange={e => setSubject(e.target.value)}/></Field>}
      <Field label="消息正文"><textarea name="campaign-body" autoComplete="off" rows={5} value={body} onChange={e => setBody(e.target.value)} placeholder="输入人工确认过的话术…"/></Field>
    </div>
    <div className="audience-list"><div><strong>选择客户</strong><span>{selected.length} / {eligible.length}</span></div>{eligible.map(lead => <label key={lead.id}><input type="checkbox" name="campaign-audience" value={lead.id} checked={selected.includes(lead.id)} onChange={e => setSelected(ids => e.target.checked ? [...ids, lead.id] : ids.filter(x => x !== lead.id))}/><span>{lead.nickname || lead.name}</span><small>{channel === "whatsapp" ? lead.phone : lead.email}</small></label>)}</div>
    <div className="modal-actions"><Button variant="secondary" onClick={onClose}>取消</Button><Button disabled={!selected.length || !body.trim()} onClick={() => void create()}>创建 {selected.length} 条任务</Button></div>
  </Modal>;
}
