import { BrainCircuit, Download, FileText, Printer, ShieldCheck, Sparkles, TriangleAlert } from "lucide-react";
import { useMemo, useState } from "react";
import { buildBrain } from "../domain";
import { useStore } from "../store";
import type { Lead } from "../types";
import { Button, Card, EmptyState, GradeBadge, PageHeader } from "../components/ui";

export function Analytics() {
  const { leads, touches } = useStore();
  const ordered = useMemo(() => [...leads].sort((a, b) => b.score - a.score), [leads]);
  const [leadId, setLeadId] = useState(ordered[0]?.id || "");
  const lead = ordered.find(item => item.id === leadId) || ordered[0];
  const brain = lead ? buildBrain(lead, touches) : null;
  const related = lead ? touches.filter(item => item.leadId === lead.id) : [];

  const exportReport = () => {
    if (!lead || !brain) return;
    const payload = { generatedAt: new Date().toISOString(), customer: lead, customerBrain: brain, timeline: related };
    const blob = new Blob([JSON.stringify(payload, null, 2)], { type: "application/json" });
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = `${lead.nickname || lead.name}-customer-brain.json`;
    a.click();
    URL.revokeObjectURL(url);
  };

  return <>
    <PageHeader title="客户智能分析" subtitle="把 CRM、人工触达记录和 AI 结论汇总成可追溯的 Customer Brain。"
      actions={<><Button variant="secondary" disabled={!lead} onClick={() => window.print()}><Printer size={16}/>打印 / PDF</Button><Button disabled={!lead} onClick={exportReport}><Download size={16}/>导出报告</Button></>}/>
    {!lead || !brain ? <EmptyState title="还没有可分析的客户" body="先导入客户或加载示例数据，即可生成客户大脑。"/> :
    <section className="analytics-layout">
      <Card className="analysis-people">
        <div className="section-title"><div><h2>选择客户</h2><p>按 AI 分数从高到低</p></div></div>
        {ordered.map(item => <button key={item.id} className={item.id === lead.id ? "selected" : ""} onClick={() => setLeadId(item.id)}>
          <span className="avatar">{(item.nickname || item.name).slice(0, 1).toUpperCase()}</span>
          <span><strong>{item.nickname || item.name}</strong><small>{item.buyerId || item.phone || "身份待补充"}</small></span>
          <GradeBadge grade={item.grade} score={item.score}/>
        </button>)}
      </Card>
      <div className="analysis-report">
        <section className="report-cover">
          <div><span className="eyebrow">AI SALES BRIEF</span><h2>{lead.nickname || lead.name}</h2><p>{lead.company || "公司待补充"} · {lead.stage} · {lead.country || "地区待补充"}</p></div>
          <div className="report-score"><strong>{brain.coverage}</strong><span>% 资料覆盖</span></div>
        </section>
        <section className="report-cards">
          <Card><div className="report-icon ai"><BrainCircuit/></div><span>AI 商机判断</span><strong><GradeBadge grade={lead.grade} score={lead.score}/></strong><p>{brain.summary}</p></Card>
          <Card><div className="report-icon safe"><ShieldCheck/></div><span>可核验事实</span><strong>{brain.facts.length} 项</strong><p>{brain.facts[0] || "尚无足够事实"}</p></Card>
          <Card><div className="report-icon warn"><TriangleAlert/></div><span>风险与缺口</span><strong>{brain.risks.length + brain.gaps.length} 项</strong><p>{brain.risks[0] || brain.gaps[0] || "暂未发现明显缺口"}</p></Card>
        </section>
        <Card className="next-step-card"><div><Sparkles/><span>建议下一步</span></div><h2>{brain.nextAction}</h2><p>建议由销售人员核对上下文后执行；PWA 不会自动代表你联系客户。</p></Card>
        <section className="report-columns">
          <Card><h2>已确认事实</h2>{brain.facts.length ? <ul>{brain.facts.map(item => <li key={item}>{item}</li>)}</ul> : <p className="muted">暂无可核验事实。</p>}</Card>
          <Card><h2>待补充信息</h2>{brain.gaps.length ? <ul>{brain.gaps.map(item => <li key={item}>{item}</li>)}</ul> : <p className="muted">核心资料已覆盖。</p>}</Card>
        </section>
        <Card className="timeline"><h2>客户触达轨迹</h2>{related.length ? related.map(item => <div key={item.id}><span className={`timeline-dot ${item.channel}`}/><time>{new Date(item.timestamp).toLocaleString("zh-CN")}</time><strong>{item.channel === "whatsapp" ? "WhatsApp" : "邮件"} · {item.direction === "incoming" ? "客户回复" : "人工确认发送"}</strong><p>{item.subject && `${item.subject} — `}{item.body}</p></div>) : <p className="muted">暂无人工确认的沟通记录。</p>}</Card>
      </div>
    </section>}
  </>;
}
