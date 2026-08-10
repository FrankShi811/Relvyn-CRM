import { createContext, useContext, useEffect, useMemo, useState, type ReactNode } from "react";
import { storage } from "./db";
import { createDemoWorkspace } from "./demo";
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
const demoSeededKey = "relvyn-pwa-demo-seeded-v1";

const demoWasSeeded = () => {
  try { return localStorage.getItem(demoSeededKey) === "done"; }
  catch { return true; }
};

const markDemoSeeded = () => {
  try { localStorage.setItem(demoSeededKey, "done"); }
  catch { /* IndexedDB still remains available when localStorage is restricted. */ }
};

async function writeDemoWorkspace() {
  const demo = createDemoWorkspace();
  await Promise.all([
    ...demo.leads.map(item => storage.saveLead(item)),
    ...demo.touches.map(item => storage.saveTouch(item)),
    ...demo.knowledge.map(item => storage.saveKnowledge(item)),
    ...demo.outreach.map(item => storage.saveOutreach(item))
  ]);
}

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

  useEffect(() => {
    void (async () => {
      const currentLeads = await storage.leads();
      if (!currentLeads.length && !demoWasSeeded()) {
        await writeDemoWorkspace();
        markDemoSeeded();
      }
      await refresh();
    })();
  }, []);

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
      await writeDemoWorkspace();
      markDemoSeeded();
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
