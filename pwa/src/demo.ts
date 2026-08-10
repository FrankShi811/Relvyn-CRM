import type { KnowledgeDocument, Lead, OutreachItem, Touch } from "./types";

export const DEMO_SOURCE = "Relvyn PWA 示例数据";

export interface DemoWorkspace {
  leads: Lead[];
  touches: Touch[];
  knowledge: KnowledgeDocument[];
  outreach: OutreachItem[];
}

export function createDemoWorkspace(now = new Date()): DemoWorkspace {
  const at = (minutesAgo: number) => new Date(now.getTime() - minutesAgo * 60_000).toISOString();
  const demoPhone = "+000 000 000 000 (demo)";
  const leads: Lead[] = [
    {
      id: "demo-lead-azita",
      buyerId: "DEMO-10482",
      name: "Azita Rahimi",
      nickname: "Azita",
      phone: demoPhone,
      email: "azita@sp-trading.example",
      company: "SP Trading",
      country: "United States",
      productInterest: "Industrial sewing machines",
      stage: "谈判中",
      grade: "A",
      score: 91,
      owner: "Frank",
      tags: ["Hot lead", "谈判中"],
      notes: "Distributor evaluating a first mixed-model order for two regional warehouses.",
      source: DEMO_SOURCE,
      updatedAt: at(18),
      lastContactAt: at(18),
      customFields: {
        采购意向: "Hot lead",
        采购数量: "120 units",
        目标价格: "USD 168 per unit",
        目的地: "Los Angeles, United States",
        物流偏好: "DDP sea freight"
      },
      aiSummary: "The buyer has confirmed the model mix, target volume, and landed-price ceiling. The opportunity is ready for a final freight-backed quotation.",
      aiNextAction: "Send the revised DDP quotation and ask for confirmation of the 40/40/40 model split.",
      aiRisks: ["The target price depends on the final freight quote."]
    },
    {
      id: "demo-lead-mateo",
      buyerId: "DEMO-10517",
      name: "Mateo Silva",
      nickname: "Mateo",
      phone: demoPhone,
      email: "mateo@casa-norte.example",
      company: "Casa Norte Retail",
      country: "Brazil",
      productInterest: "Solar garden lights",
      stage: "报价中",
      grade: "B",
      score: 78,
      owner: "Lina",
      tags: ["Hot lead", "报价待确认"],
      notes: "Retail buyer planning a seasonal launch across 18 stores.",
      source: DEMO_SOURCE,
      updatedAt: at(95),
      lastContactAt: at(95),
      customFields: {
        采购意向: "Hot lead",
        采购数量: "2,400 sets",
        目标价格: "USD 6.80 per set",
        目的地: "Santos, Brazil",
        物流偏好: "FOB with buyer-appointed forwarder"
      },
      aiSummary: "The launch window and volume are credible, but packaging cost must be separated from the product quote before approval.",
      aiNextAction: "Return two quote options with standard and Portuguese retail packaging.",
      aiRisks: ["Seasonal delivery window leaves limited production buffer."]
    },
    {
      id: "demo-lead-nora",
      buyerId: "DEMO-10603",
      name: "Nora Al-Hassan",
      nickname: "Nora",
      phone: demoPhone,
      email: "nora@gulf-retail.example",
      company: "Gulf Retail Partners",
      country: "United Arab Emirates",
      productInterest: "Smart home switch panels",
      stage: "需求确认",
      grade: "A",
      score: 86,
      owner: "Frank",
      tags: ["Hot lead", "样品"],
      notes: "Project buyer comparing finishes for a serviced-apartment rollout.",
      source: DEMO_SOURCE,
      updatedAt: at(210),
      lastContactAt: at(210),
      customFields: {
        采购意向: "Hot lead",
        采购数量: "680 panels",
        目标价格: "USD 24 to 27 per panel",
        目的地: "Jebel Ali, United Arab Emirates",
        物流偏好: "Air samples, then CIF sea freight"
      },
      aiSummary: "The buyer has supplied a room schedule and finish preference. Sample approval is the remaining gate before a formal project quote.",
      aiNextAction: "Confirm the sample address and send the champagne-gold and matte-black finish pack.",
      aiRisks: ["Final quantity may change after the contractor freezes the room schedule."]
    },
    {
      id: "demo-lead-elise",
      buyerId: "DEMO-10644",
      name: "Elise Martin",
      nickname: "Elise",
      phone: demoPhone,
      email: "elise@atelier-maison.example",
      company: "Atelier Maison",
      country: "France",
      productInterest: "Hotel linen sets",
      stage: "初步沟通",
      grade: "B",
      score: 64,
      owner: "Maya",
      tags: ["Follow-up required", "规格待确认"],
      notes: "Procurement team requested certification and fabric-weight options.",
      source: DEMO_SOURCE,
      updatedAt: at(1_320),
      lastContactAt: at(1_320),
      customFields: {
        采购意向: "Follow-up required",
        采购数量: "350 room sets",
        目标价格: "",
        目的地: "Marseille, France",
        物流偏好: "CIF Marseille"
      },
      aiSummary: "Interest is specific, but the buyer has not selected fabric weight or confirmed the opening date. The opportunity needs a low-pressure specification follow-up.",
      aiNextAction: "Send the certification pack and ask which GSM option should be quoted.",
      aiRisks: ["Budget and delivery date are not yet confirmed."]
    },
    {
      id: "demo-lead-samuel",
      buyerId: "DEMO-10711",
      name: "Samuel Okafor",
      nickname: "Samuel",
      phone: demoPhone,
      email: "samuel@meridian-supplies.example",
      company: "Meridian Supplies",
      country: "Nigeria",
      productInterest: "Rechargeable work lights",
      stage: "新客户",
      grade: "C",
      score: 46,
      owner: "Lina",
      tags: ["Follow-up required", "新询盘"],
      notes: "New inbound inquiry. Buyer asked for a catalog and distributor terms.",
      source: DEMO_SOURCE,
      updatedAt: at(2_880),
      lastContactAt: at(2_880),
      customFields: {
        采购意向: "Follow-up required",
        采购数量: "Initial estimate 500 units",
        目标价格: "",
        目的地: "Lagos, Nigeria",
        物流偏好: ""
      },
      aiSummary: "The inquiry matches the product line, but purchasing authority, target price, and import route still need verification.",
      aiNextAction: "Share the compact catalog and ask which lumen range and target price apply.",
      aiRisks: ["Decision-maker role has not been verified.", "No target price is available."]
    },
    {
      id: "demo-lead-yuki",
      buyerId: "DEMO-10209",
      name: "Yuki Tanaka",
      nickname: "Yuki",
      phone: demoPhone,
      email: "yuki@aozora-commerce.example",
      company: "Aozora Commerce",
      country: "Japan",
      productInterest: "Compostable food trays",
      stage: "成交",
      grade: "A",
      score: 96,
      owner: "Maya",
      tags: ["Won", "首单"],
      notes: "Purchase order confirmed after material and print-sample approval.",
      source: DEMO_SOURCE,
      updatedAt: at(4_320),
      lastContactAt: at(4_320),
      customFields: {
        采购意向: "Won",
        采购数量: "50,000 trays",
        目标价格: "JPY 18.4 per tray",
        目的地: "Yokohama, Japan",
        物流偏好: "CIF Yokohama"
      },
      aiSummary: "The first order is confirmed. The account should move from acquisition to delivery assurance and repeat-order planning.",
      aiNextAction: "Send the production milestone schedule and set a reminder for the reorder forecast.",
      aiRisks: ["Printed-color approval must remain attached to the production order."]
    },
    {
      id: "demo-lead-lucas",
      buyerId: "DEMO-09874",
      name: "Lukas Schneider",
      nickname: "Lukas",
      phone: demoPhone,
      email: "lukas@rhein-werkzeug.example",
      company: "Rhein Werkzeughandel",
      country: "Germany",
      productInterest: "Compact air compressors",
      stage: "复购",
      grade: "A",
      score: 89,
      owner: "Frank",
      tags: ["Won", "复购"],
      notes: "Existing distributor planning a larger repeat order after low return rates.",
      source: DEMO_SOURCE,
      updatedAt: at(5_760),
      lastContactAt: at(5_760),
      customFields: {
        采购意向: "Won",
        采购数量: "320 units",
        目标价格: "EUR 74 landed",
        目的地: "Hamburg, Germany",
        物流偏好: "DDP road and sea combination"
      },
      aiSummary: "The account has a validated sales history and a concrete repeat-order forecast. Margin should be checked before confirming the larger volume tier.",
      aiNextAction: "Confirm the repeat-order rebate and reserve the requested production week.",
      aiRisks: ["The requested landed price leaves a narrow freight buffer."]
    },
    {
      id: "demo-lead-sofia",
      buyerId: "DEMO-10352",
      name: "Sofia Petrova",
      nickname: "Sofia",
      phone: demoPhone,
      email: "sofia@baltic-market.example",
      company: "Baltic Market Group",
      country: "Poland",
      productInterest: "Portable power stations",
      stage: "暂停",
      grade: "D",
      score: 28,
      owner: "Lina",
      tags: ["Lost", "预算冻结"],
      notes: "Opportunity paused after the buyer removed the category from the current budget.",
      source: DEMO_SOURCE,
      updatedAt: at(10_080),
      lastContactAt: at(10_080),
      customFields: {
        采购意向: "Lost",
        采购数量: "200 units",
        目标价格: "EUR 210 per unit",
        目的地: "Gdansk, Poland",
        物流偏好: "DAP Gdansk"
      },
      aiSummary: "The buyer confirmed the opportunity is not funded this quarter. Keep the relationship warm without treating it as active pipeline.",
      aiNextAction: "Schedule a light re-engagement for the next budgeting cycle.",
      aiRisks: ["No approved budget in the current quarter."]
    }
  ];

  const touches: Touch[] = [
    { id: "demo-touch-azita-1", leadId: "demo-lead-azita", channel: "whatsapp", direction: "outgoing", body: "I have prepared the updated model mix and am checking the DDP freight before I send the final quote.", timestamp: at(62), status: "confirmed-sent" },
    { id: "demo-touch-azita-2", leadId: "demo-lead-azita", channel: "whatsapp", direction: "incoming", body: "120 units works. Please keep the landed price near USD 168 and ship to our Los Angeles warehouse.", timestamp: at(18), status: "received" },
    { id: "demo-touch-mateo-1", leadId: "demo-lead-mateo", channel: "whatsapp", direction: "outgoing", body: "I can separate the product and Portuguese packaging costs so your team can compare both options.", timestamp: at(180), status: "confirmed-sent" },
    { id: "demo-touch-mateo-2", leadId: "demo-lead-mateo", channel: "whatsapp", direction: "incoming", body: "Please quote 2,400 sets for Santos. We need delivery before the September store campaign.", timestamp: at(95), status: "received" },
    { id: "demo-touch-nora-1", leadId: "demo-lead-nora", channel: "whatsapp", direction: "outgoing", body: "I will arrange both finish samples by air and include the compliance sheet.", timestamp: at(270), status: "confirmed-sent" },
    { id: "demo-touch-nora-2", leadId: "demo-lead-nora", channel: "whatsapp", direction: "incoming", body: "We prefer champagne gold and matte black. The current schedule is 680 panels for Jebel Ali.", timestamp: at(210), status: "received" },
    { id: "demo-touch-elise-1", leadId: "demo-lead-elise", channel: "email", direction: "outgoing", subject: "Linen certification and GSM options", body: "Attached is the certification overview. I can quote the 300 and 350 GSM options separately.", timestamp: at(1_440), status: "confirmed-sent" },
    { id: "demo-touch-elise-2", leadId: "demo-lead-elise", channel: "whatsapp", direction: "incoming", body: "Could you send the certification pack first? We are considering about 350 room sets for Marseille.", timestamp: at(1_320), status: "received" },
    { id: "demo-touch-samuel-1", leadId: "demo-lead-samuel", channel: "whatsapp", direction: "outgoing", body: "I can share the distributor catalog. Which lumen range is closest to your market?", timestamp: at(3_000), status: "confirmed-sent" },
    { id: "demo-touch-samuel-2", leadId: "demo-lead-samuel", channel: "whatsapp", direction: "incoming", body: "We may start with 500 rechargeable work lights for Lagos. Please send your distributor terms.", timestamp: at(2_880), status: "received" },
    { id: "demo-touch-yuki-1", leadId: "demo-lead-yuki", channel: "email", direction: "outgoing", subject: "Production milestones for confirmed order", body: "The approved print reference is attached to the order, and I will send weekly production updates.", timestamp: at(4_500), status: "confirmed-sent" },
    { id: "demo-touch-yuki-2", leadId: "demo-lead-yuki", channel: "whatsapp", direction: "incoming", body: "The purchase order for 50,000 trays is approved. Please keep CIF Yokohama as agreed.", timestamp: at(4_320), status: "received" },
    { id: "demo-touch-lucas-1", leadId: "demo-lead-lucas", channel: "whatsapp", direction: "outgoing", body: "I am checking the 320-unit volume tier and the requested production week now.", timestamp: at(5_940), status: "confirmed-sent" },
    { id: "demo-touch-lucas-2", leadId: "demo-lead-lucas", channel: "whatsapp", direction: "incoming", body: "Returns were low, so we want to reorder 320 compressors. Our target is EUR 74 landed in Hamburg.", timestamp: at(5_760), status: "received" },
    { id: "demo-touch-sofia-1", leadId: "demo-lead-sofia", channel: "whatsapp", direction: "outgoing", body: "Understood. I will pause the quote and check back when the next category budget opens.", timestamp: at(10_200), status: "confirmed-sent" },
    { id: "demo-touch-sofia-2", leadId: "demo-lead-sofia", channel: "whatsapp", direction: "incoming", body: "The project is frozen for this quarter, so please pause the 200-unit offer for now.", timestamp: at(10_080), status: "received" }
  ];

  const knowledge: KnowledgeDocument[] = [
    {
      id: "demo-knowledge-qualification",
      name: "Opportunity qualification checklist.md",
      category: "Sales playbook",
      enabled: true,
      createdAt: at(12_000),
      text: "Before confirming a quote, verify product specification, quantity, target price, destination, logistics preference, decision-maker role, and purchase timing. Separate confirmed facts from assumptions."
    },
    {
      id: "demo-knowledge-logistics",
      name: "Logistics response guide.md",
      category: "Operations",
      enabled: true,
      createdAt: at(11_400),
      text: "Do not promise a landed price until the freight basis and destination are confirmed. Record whether the buyer requests EXW, FOB, CIF, DAP, or DDP and name the destination port or warehouse."
    },
    {
      id: "demo-knowledge-follow-up",
      name: "Human follow-up principles.md",
      category: "Sales playbook",
      enabled: true,
      createdAt: at(10_800),
      text: "Use one clear question per follow-up. Reference the buyer's last confirmed detail, avoid invented urgency, and require human review before opening WhatsApp or email."
    }
  ];

  const outreach: OutreachItem[] = [
    { id: "demo-outreach-azita", leadId: "demo-lead-azita", channel: "whatsapp", body: "I have the revised DDP calculation ready. Can you confirm the 40/40/40 model split for the 120-unit order?", status: "pending", createdAt: at(12) },
    { id: "demo-outreach-nora", leadId: "demo-lead-nora", channel: "email", subject: "Finish samples and compliance pack", body: "I have prepared the champagne-gold and matte-black sample set together with the compliance sheet. Please confirm the delivery address for the air shipment.", status: "opened", createdAt: at(80) },
    { id: "demo-outreach-elise", leadId: "demo-lead-elise", channel: "email", subject: "Linen GSM options", body: "Here are the 300 and 350 GSM certification packs. Which option should I use for the 350-room quotation?", status: "pending", createdAt: at(240) },
    { id: "demo-outreach-yuki", leadId: "demo-lead-yuki", channel: "email", subject: "Confirmed production milestones", body: "Your order is confirmed. The approved print reference is attached, and the first production update is scheduled for Friday.", status: "confirmed-sent", createdAt: at(4_260) }
  ];

  return { leads, touches, knowledge, outreach };
}
