import { describe, expect, it } from "vitest";
import { buildBrain, findIdentity, gradeFor, normalizePhone, retrieveKnowledge, safeWhatsAppUrl } from "./domain";
import type { KnowledgeDocument, Lead, Touch } from "./types";

const lead = (overrides: Partial<Lead> = {}): Lead => ({
  id: "lead-1",
  buyerId: "BUYER-001",
  name: "Frank Shi",
  nickname: "Frank",
  phone: "+86 130 7361 1720",
  email: "frank@example.com",
  company: "DH Business",
  country: "China",
  productInterest: "Needle machine",
  stage: "需求确认",
  grade: "B",
  score: 70,
  owner: "Owner",
  tags: [],
  notes: "Needs specs",
  source: "test",
  updatedAt: "2026-07-28T00:00:00.000Z",
  customFields: { 采购数量: "120 units", 目标价格: "USD 168", 目的地: "Los Angeles", 物流偏好: "DDP sea freight" },
  ...overrides
});

describe("customer identity", () => {
  it("uses Buyer ID before phone", () => {
    const leads = [lead(), lead({ id: "lead-2", buyerId: "BUYER-002", phone: "+1 415 555 0186" })];
    expect(findIdentity(leads, "buyer-002", "+86 130 7361 1720")?.id).toBe("lead-2");
  });

  it("falls back to normalized phone only when Buyer ID is absent", () => {
    expect(findIdentity([lead()], "", "0086-130-7361-1720")?.id).toBeUndefined();
    expect(normalizePhone("(415) 555-0186")).toBe("+4155550186");
    expect(findIdentity([lead({ buyerId: "", phone: "+4155550186" })], "", "(415) 555-0186")?.id).toBe("lead-1");
  });
});

describe("sales intelligence", () => {
  it("keeps the safe grade boundaries", () => {
    expect([39, 40, 60, 80].map(gradeFor)).toEqual(["D", "C", "B", "A"]);
  });

  it("builds an evidence-backed brain", () => {
    const touch: Touch = {
      id: "touch-1", leadId: "lead-1", channel: "whatsapp", direction: "incoming",
      body: "Please send specifications.", timestamp: "2026-07-28T01:00:00.000Z", status: "received"
    };
    const brain = buildBrain(lead(), [touch]);
    expect(brain.coverage).toBe(100);
    expect(brain.facts.join(" ")).toContain("Please send specifications.");
    expect(brain.nextAction).toContain("确认");
  });

  it("retrieves only enabled matching knowledge", () => {
    const docs: KnowledgeDocument[] = [
      { id: "1", name: "机器资料", category: "产品", text: "needle machine specifications", enabled: true, createdAt: "" },
      { id: "2", name: "隐藏", category: "产品", text: "needle machine", enabled: false, createdAt: "" }
    ];
    expect(retrieveKnowledge(docs, "machine specifications").map(x => x.doc.id)).toEqual(["1"]);
  });

  it("creates a safe WhatsApp handoff URL", () => {
    expect(safeWhatsAppUrl("+1 (415) 555-0186", "Hello & 你好")).toContain("https://wa.me/14155550186?text=");
  });
});
