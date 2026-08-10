import { AlertTriangle, BrainCircuit, CheckCircle2, Sparkles } from "lucide-react";
import { useState } from "react";
import { analyzeLead, sessionKey } from "../ai";
import { buildBrain, dataCoverage, gradeFor, purchaseDetails } from "../domain";
import { useStore } from "../store";
import { Button, Card, EmptyState, GradeBadge, PageHeader } from "../components/ui";

export function Intelligence() {
  const { leads, touches, knowledge, aiSettings, saveLead } = useStore();
  const [selectedId, setSelectedId] = useState(leads[0]?.id || "");
  const [running, setRunning] = useState(false);
  const [error, setError] = useState("");
  const lead = leads.find(x => x.id === selectedId) || leads[0];
  const purchase = lead ? purchaseDetails(lead) : null;
  const pipeline = [
    { label: "Hot leads", detail: "A / B 级且仍在推进", items: leads.filter(item => (item.grade === "A" || item.grade === "B") && !["成交", "复购", "暂停"].includes(item.stage)) },
    { label: "Negotiating", detail: "报价或谈判阶段", items: leads.filter(item => ["报价中", "谈判中"].includes(item.stage)) },
    { label: "Follow-up required", detail: "等待人工跟进", items: leads.filter(item => item.tags.some(tag => /follow-up|待回复|跟进/i.test(tag))) },
    { label: "Won / Lost", detail: "成交、复购或暂停", items: leads.filter(item => ["成交", "复购", "暂停"].includes(item.stage) || item.tags.some(tag => /won|lost/i.test(tag))) }
  ];
  const analyze = async () => {
    if (!lead) return;
    setRunning(true); setError("");
    try {
      const result = await analyzeLead(aiSettings, sessionKey.get(), lead, touches, knowledge);
      await saveLead({ ...lead, score: Math.round(result.score), grade: gradeFor(result.score), aiSummary: result.summary, aiRisks: result.risks, aiNextAction: result.nextAction, updatedAt: new Date().toISOString() });
    } catch (e) { setError(e instanceof Error ? e.message : "AI 分析失败"); }
    finally { setRunning(false); }
  };
  return <>
    <PageHeader title="商机智能" subtitle="从 Opportunity Pipeline 进入客户事实、风险和下一步判断。"
      actions={<Button disabled={!lead || running} onClick={() => void analyze()}><Sparkles size={16}/>{running ? "正在分析…" : "运行 AI 分析"}</Button>}/>
    {!lead ? <EmptyState title="暂无客户可分析" body="请先进入客户列表导入或新建客户。"/> :
    <>
    <Card className="pipeline-overview">
      <div className="section-title"><div><h2>Opportunity Pipeline</h2><p>按当前阶段与人工标签汇总，点击分组可查看代表客户</p></div></div>
      <div className="pipeline-grid">{pipeline.map(group => <button type="button" key={group.label} disabled={!group.items.length} onClick={() => setSelectedId(group.items[0]?.id || "")}>
        <span>{group.label}</span><strong>{group.items.length}</strong><small>{group.items.length ? group.items.slice(0, 2).map(item => item.nickname || item.name).join("、") : group.detail}</small>
      </button>)}</div>
    </Card>
    <section className="intelligence-layout">
      <Card className="lead-rail"><h2>客户队列</h2>{leads.map(item => <button key={item.id} className={item.id === lead.id ? "selected" : ""} onClick={() => setSelectedId(item.id)}>
        <span><strong>{item.nickname || item.name}</strong><small>{item.company || item.productInterest || "资料待补充"}</small></span><GradeBadge grade={item.grade} score={item.score}/>
      </button>)}</Card>
      <div className="analysis-main">
        {error && <div className="error-banner" role="alert"><AlertTriangle/>{error}</div>}
        <Card className="score-card">
          <div className={`score-ring grade-${lead.grade.toLowerCase()}`}><strong>{lead.score}</strong><span>{lead.grade} 级</span></div>
          <div><span className="section-kicker">LEAD INTELLIGENCE</span><h2>{lead.name}</h2><p>{lead.aiSummary || "尚未完成 AI 分析。当前 D / 0 安全基线不会用本地关键词伪造商机评分。"}</p></div>
        </Card>
        <div className="analysis-columns">
          <Card><h3>可核验资料</h3><div className="check-list">
            {[
              ["统一身份", lead.buyerId || lead.phone],
              ["公司与地区", [lead.company, lead.country].filter(Boolean).join(" · ")],
              ["产品兴趣", lead.productInterest],
              ["采购数量", purchase?.quantity],
              ["目标价格", purchase?.targetPrice],
              ["目的地", purchase?.destination],
              ["物流偏好", purchase?.logistics],
              ["沟通记录", `${touches.filter(x => x.leadId === lead.id).length} 条人工记录`]
            ].map(([label, value]) => <div key={label}><CheckCircle2/><span><strong>{label}</strong><small>{value || "待补充"}</small></span></div>)}
          </div><div className="coverage-line"><span>资料完整度</span><strong>{dataCoverage(lead, touches)}%</strong></div><progress value={dataCoverage(lead, touches)} max="100"/></Card>
          <Card><h3>风险与下一步</h3>{lead.aiRisks?.length ? <ul className="risk-list">{lead.aiRisks.map(x => <li key={x}>{x}</li>)}</ul> : <p className="muted-copy">尚无经过 AI 验证的风险结论。</p>}
            <div className="next-action"><span>建议下一步</span><strong>{lead.aiNextAction || buildBrain(lead, touches).nextAction}</strong></div></Card>
        </div>
      </div>
    </section></>}
  </>;
}
