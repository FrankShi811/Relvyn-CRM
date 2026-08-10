import { describe, expect, it } from "vitest";
import { createDemoWorkspace, DEMO_SOURCE } from "./demo";
import { buildBrain, safeWhatsAppUrl } from "./domain";

describe("Relvyn demo workspace", () => {
  const demo = createDemoWorkspace(new Date("2026-08-10T08:00:00Z"));

  it("covers the requested showcase states with fictional local data", () => {
    expect(demo.leads).toHaveLength(8);
    expect(new Set(demo.leads.map(lead => lead.id)).size).toBe(8);
    expect(demo.knowledge.length).toBeGreaterThanOrEqual(2);
    expect(demo.outreach.length).toBeGreaterThanOrEqual(3);
    expect(demo.leads.every(lead => lead.source === DEMO_SOURCE)).toBe(true);
    expect(demo.leads.some(lead => ["报价中", "谈判中"].includes(lead.stage))).toBe(true);
    expect(demo.leads.some(lead => ["成交", "复购"].includes(lead.stage))).toBe(true);
    expect(demo.leads.some(lead => lead.tags.includes("Lost"))).toBe(true);
  });

  it("gives every buyer a conversation while preserving genuine buying gaps", () => {
    for (const lead of demo.leads) {
      expect(demo.touches.filter(touch => touch.leadId === lead.id).length).toBeGreaterThanOrEqual(2);
      expect(lead.aiSummary).toBeTruthy();
      expect(lead.customFields.采购数量).toBeTruthy();
      expect(lead.customFields.目的地).toBeTruthy();
      expect(buildBrain(lead, demo.touches).facts).toEqual(expect.arrayContaining([
        expect.stringContaining("关注产品："),
        expect.stringContaining("采购数量："),
        expect.stringContaining("目的地：")
      ]));
    }

    const samuel = demo.leads.find(lead => lead.id === "demo-lead-samuel")!;
    const samuelBrain = buildBrain(samuel, demo.touches);
    expect(samuelBrain.coverage).toBeLessThan(100);
    expect(samuelBrain.gaps).toEqual(expect.arrayContaining(["目标价格", "物流偏好"]));

    const elise = demo.leads.find(lead => lead.id === "demo-lead-elise")!;
    const eliseBrain = buildBrain(elise, demo.touches);
    expect(eliseBrain.coverage).toBeLessThan(100);
    expect(eliseBrain.gaps).toContain("目标价格");
  });

  it("uses stable IDs and non-routable WhatsApp demo contacts", () => {
    const second = createDemoWorkspace(new Date("2026-08-11T08:00:00Z"));
    expect(second.leads.map(lead => lead.id)).toEqual(demo.leads.map(lead => lead.id));
    expect(demo.leads.every(lead => safeWhatsAppUrl(lead.phone, "test") === "")).toBe(true);
  });
});
