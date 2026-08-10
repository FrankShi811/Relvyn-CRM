import type { BrainProfile, Grade, KnowledgeDocument, Lead, Touch } from "./types";

export const uid = () => crypto.randomUUID();
export const normalizePhone = (value: string) => {
  const digits = value.replace(/\D/g, "");
  return digits.length >= 7 && !/^0+$/.test(digits) ? `+${digits}` : "";
};
export const normalizeBuyer = (value: string) => value.trim().toLowerCase();

export function findIdentity(leads: Lead[], buyerId: string, phone: string) {
  const buyer = normalizeBuyer(buyerId);
  if (buyer) return leads.find(x => normalizeBuyer(x.buyerId) === buyer);
  const normalizedPhone = normalizePhone(phone);
  return normalizedPhone ? leads.find(x => normalizePhone(x.phone) === normalizedPhone) : undefined;
}

export function gradeFor(score: number): Grade {
  if (score >= 80) return "A";
  if (score >= 60) return "B";
  if (score >= 40) return "C";
  return "D";
}

const customValue = (lead: Lead, ...keys: string[]) => {
  for (const key of keys) {
    const value = lead.customFields[key]?.trim();
    if (value) return value;
  }
  return "";
};

export function purchaseDetails(lead: Lead) {
  return {
    quantity: customValue(lead, "采购数量", "quantity", "Quantity", "order quantity"),
    targetPrice: customValue(lead, "目标价格", "目标价", "target price", "Target Price", "budget"),
    destination: customValue(lead, "目的地", "destination", "Destination", "delivery destination"),
    logistics: customValue(lead, "物流偏好", "logistics preference", "Logistics Preference", "shipping term")
  };
}

export function dataCoverage(lead: Lead, touches: Touch[]) {
  const purchase = purchaseDetails(lead);
  const checks = [
    lead.buyerId || lead.phone,
    lead.name,
    lead.company,
    lead.productInterest,
    lead.email || lead.phone,
    lead.notes,
    touches.some(x => x.leadId === lead.id),
    purchase.quantity,
    purchase.targetPrice,
    purchase.destination,
    purchase.logistics
  ];
  return Math.round(checks.filter(Boolean).length / checks.length * 100);
}

export function buildBrain(lead: Lead, touches: Touch[]): BrainProfile {
  const related = touches.filter(x => x.leadId === lead.id).sort((a, b) => a.timestamp.localeCompare(b.timestamp));
  const latestIncoming = [...related].reverse().find(x => x.direction === "incoming");
  const coverage = dataCoverage(lead, touches);
  const purchase = purchaseDetails(lead);
  const facts = [
    lead.productInterest && `关注产品：${lead.productInterest}`,
    purchase.quantity && `采购数量：${purchase.quantity}`,
    purchase.targetPrice && `目标价格：${purchase.targetPrice}`,
    purchase.destination && `目的地：${purchase.destination}`,
    purchase.logistics && `物流偏好：${purchase.logistics}`,
    lead.company && `公司：${lead.company}`,
    lead.country && `地区：${lead.country}`,
    latestIncoming && `最近客户原话：${latestIncoming.body}`
  ].filter(Boolean) as string[];
  const gaps = [
    !lead.productInterest && "产品需求",
    !lead.company && "公司信息",
    !lead.email && "邮箱",
    !lead.phone && "电话号码",
    !latestIncoming && "客户真实回复",
    !purchase.quantity && "采购数量",
    !purchase.targetPrice && "目标价格",
    !purchase.destination && "目的地",
    !purchase.logistics && "物流偏好"
  ].filter(Boolean) as string[];
  return {
    coverage,
    summary: lead.aiSummary || (facts.length ? `${lead.name || "该客户"}已建立 ${facts.length} 项可核验资料，当前处于“${lead.stage}”阶段。` : "当前资料不足，建议先补充客户背景和真实沟通。"),
    risks: lead.aiRisks?.length ? lead.aiRisks : gaps.length > 2 ? ["客户资料覆盖不足，重要判断需要人工核对"] : [],
    nextAction: lead.aiNextAction || (latestIncoming ? "根据客户最近回复确认具体需求、数量、预算和时间。" : "先发起一次简短、低压力的人工联系并记录真实反馈。"),
    facts,
    gaps
  };
}

export function retrieveKnowledge(docs: KnowledgeDocument[], query: string, limit = 5) {
  const terms = [...new Set(query.toLowerCase().split(/[\s,，。；;:：!?！？/\\]+/).filter(x => x.length > 1))];
  return docs.filter(x => x.enabled).map(doc => {
    const haystack = `${doc.name} ${doc.category} ${doc.text}`.toLowerCase();
    const score = terms.reduce((total, term) => total + (haystack.includes(term) ? 1 : 0), 0);
    return { doc, score };
  }).filter(x => x.score > 0).sort((a, b) => b.score - a.score).slice(0, limit);
}

export function safeWhatsAppUrl(phone: string, body: string) {
  const digits = normalizePhone(phone).replace("+", "");
  return digits ? `https://wa.me/${digits}?text=${encodeURIComponent(body)}` : "";
}

export function mailtoUrl(email: string, subject: string, body: string) {
  return email ? `mailto:${encodeURIComponent(email)}?subject=${encodeURIComponent(subject)}&body=${encodeURIComponent(body)}` : "";
}
