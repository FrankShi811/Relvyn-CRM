import { createContext, useContext, useEffect, useMemo, useState, type ReactNode } from "react";
import { storage } from "./db";
import { gradeFor, uid } from "./domain";
import type { AiSettings, KnowledgeDocument, Lead, OutreachItem, Touch } from "./types";

interface StoreValue {
  loading: boolean;
  leads: Lead[];
  touches: Touch[];
  knowledge: KnowledgeDocument[];
  outreach: OutreachItem[];
  aiSettings: AiSettings;
  refresh: () => Promise<void>;
  saveLead: (lead: Lead) => Promise<void>;
  importLeads: (leads: Lead[], removeIds?: string[]) => Promise<void>;
  removeLead: (id: string) => Promise<void>;
  saveTouch: (touch: Touch) => Promise<void>;
  saveKnowledge: (doc: KnowledgeDocument) => Promise<void>;
  removeKnowledge: (id: string) => Promise<void>;
  saveOutreach: (item: OutreachItem) => Promise<void>;
  saveAiSettings: (value: AiSettings) => Promise<void>;
  loadDemo: () => Promise<void>;
  clearAll: () => Promise<void>;
}

const StoreContext = createContext<StoreValue | null>(null);
const defaultSettings: AiSettings = { baseUrl: "https://api.deepseek.com/v1", model: "deepseek-chat", reasoning: "auto" };

export function StoreProvider({ children }: { children: ReactNode }) {
  const [loading, setLoading] = useState(true);
  const [leads, setLeads] = useState<Lead[]>([]);
  const [touches, setTouches] = useState<Touch[]>([]);
  const [knowledge, setKnowledge] = useState<KnowledgeDocument[]>([]);
  const [outreach, setOutreach] = useState<OutreachItem[]>([]);
  const [aiSettings, setAiSettings] = useState<AiSettings>(defaultSettings);

  const refresh = async () => {
    const [nextLeads, nextTouches, nextKnowledge, nextOutreach, nextSettings] = await Promise.all([
      storage.leads(), storage.touches(), storage.knowledge(), storage.outreach(), storage.settings()
    ]);
    setLeads(nextLeads.sort((a, b) => b.updatedAt.localeCompare(a.updatedAt)));
    setTouches(nextTouches.sort((a, b) => a.timestamp.localeCompare(b.timestamp)));
    setKnowledge(nextKnowledge.sort((a, b) => b.createdAt.localeCompare(a.createdAt)));
    setOutreach(nextOutreach.sort((a, b) => b.createdAt.localeCompare(a.createdAt)));
    setAiSettings(nextSettings || defaultSettings);
    setLoading(false);
  };

  useEffect(() => { void refresh(); }, []);

  const actions = useMemo(() => ({
    saveLead: async (lead: Lead) => { await storage.saveLead(lead); await refresh(); },
    importLeads: async (leads: Lead[], removeIds: string[] = []) => {
      await storage.importLeads(leads, removeIds);
      await refresh();
    },
    removeLead: async (id: string) => { await storage.deleteLead(id); await refresh(); },
    saveTouch: async (touch: Touch) => { await storage.saveTouch(touch); await refresh(); },
    saveKnowledge: async (doc: KnowledgeDocument) => { await storage.saveKnowledge(doc); await refresh(); },
    removeKnowledge: async (id: string) => { await storage.deleteKnowledge(id); await refresh(); },
    saveOutreach: async (item: OutreachItem) => { await storage.saveOutreach(item); await refresh(); },
    saveAiSettings: async (value: AiSettings) => { await storage.saveSettings(value); await refresh(); },
    clearAll: async () => { await storage.clear(); await refresh(); },
    loadDemo: async () => {
      const now = new Date();
      const leadA: Lead = {
        id: uid(), buyerId: "DH-10482", name: "Azita Rahimi", nickname: "Azita", phone: "+8613800013800",
        email: "azita@example.com", company: "SP Trading", country: "United States", productInterest: "Industrial needle machine",
        stage: "需求确认", grade: "B", score: 74, owner: "Frank", tags: ["重点跟进", "样品"],
        notes: "关注申请审核和产品参数，需要进一步确认数量与时间。", source: "PWA 示例数据",
        updatedAt: now.toISOString(), lastContactAt: now.toISOString(), customFields: { 来源渠道: "行业活动", 客户类型: "企业客户" },
        aiSummary: "客户已经表达具体产品兴趣并持续互动，具备进一步确认采购要素的价值。",
        aiNextAction: "用简短问题确认数量、目标价、目的地和期望交期。", aiRisks: ["尚未确认预算与最终采购时间"]
      };
      const leadB: Lead = {
        id: uid(), buyerId: "DH-10731", name: "Russell Brown", nickname: "Russell", phone: "+14155550186",
        email: "russell@example.com", company: "RB Optics", country: "Canada", productInterest: "Optical accessories",
        stage: "初步沟通", grade: gradeFor(58), score: 58, owner: "Frank", tags: ["待回复"], notes: "老客户重新激活。",
        source: "PWA 示例数据", updatedAt: new Date(now.getTime() - 3600000).toISOString(), customFields: { 平台: "WhatsApp" }
      };
      await storage.saveLead(leadA); await storage.saveLead(leadB);
      await storage.saveTouch({
        id: uid(), leadId: leadA.id, channel: "whatsapp", direction: "incoming",
        body: "Can you send the application review and machine specifications?", timestamp: now.toISOString(), status: "received"
      });
      await storage.saveTouch({
        id: uid(), leadId: leadB.id, channel: "email", direction: "incoming", subject: "Re: New catalog",
        body: "Please share the latest product catalog.", timestamp: new Date(now.getTime() - 7200000).toISOString(), status: "received"
      });
      await storage.saveKnowledge({
        id: uid(), name: "产品回复规范.md", category: "产品资料", enabled: true, createdAt: now.toISOString(),
        text: "提供报价前应确认产品型号、数量、目标价、目的地和期望交期。不得在未核实库存时承诺现货。"
      });
      await refresh();
    }
  }), []);

  return <StoreContext.Provider value={{ loading, leads, touches, knowledge, outreach, aiSettings, refresh, ...actions }}>{children}</StoreContext.Provider>;
}

export const useStore = () => {
  const value = useContext(StoreContext);
  if (!value) throw new Error("StoreProvider missing");
  return value;
};
