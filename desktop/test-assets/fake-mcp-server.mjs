import readline from "node:readline";

const lines = readline.createInterface({ input: process.stdin, crlfDelay: Infinity });

function write(message) {
  process.stdout.write(`${JSON.stringify(message)}\n`);
}

function result(id, value) {
  write({ jsonrpc: "2.0", id, result: value });
}

function error(id, code, message) {
  write({ jsonrpc: "2.0", id, error: { code, message } });
}

const productSchema = {
  type: "object",
  properties: {
    taskType: { type: "string" },
    requirement: { type: "object" },
    requirementCompleteness: { type: "object" },
    additionalInstructions: { type: "string" },
    context: { type: "object" },
    attachments: { type: "array" },
    relvynTask: { type: "object" }
  },
  required: ["taskType", "requirement", "requirementCompleteness"]
};

lines.on("line", async (line) => {
  if (!line.trim()) return;
  let request;
  try {
    request = JSON.parse(line);
  } catch {
    return;
  }
  if (request.method === "notifications/initialized" || request.id === undefined) return;
  switch (request.method) {
    case "initialize":
      result(request.id, {
        protocolVersion: request.params?.protocolVersion ?? "2025-06-18",
        capabilities: { tools: {}, resources: {}, prompts: {} },
        serverInfo: { name: "relvyn-fake-mcp", version: "1.0.0" },
        instructions: "Test-only deterministic MCP server."
      });
      break;
    case "tools/list":
      result(request.id, {
        tools: [
          { name: "product_search_mock", description: "Best-effort product sourcing with partial requirements.", inputSchema: productSchema },
          { name: "needs_information_mock", description: "Returns a needs-information sourcing result.", inputSchema: productSchema },
          { name: "echo", description: "Echoes validated arguments.", inputSchema: { type: "object" } },
          { name: "slow_task", description: "Waits before returning.", inputSchema: { type: "object" } },
          { name: "fail_task", description: "Returns an MCP tool error.", inputSchema: { type: "object" } },
          { name: "send_whatsapp_message", description: "Unsafe customer-channel tool used to verify hard denial.", inputSchema: { type: "object" } }
        ]
      });
      break;
    case "resources/list":
      result(request.id, { resources: [{ uri: "relvyn://catalog/demo", name: "Demo catalog", description: "Fake resource", mimeType: "application/json" }] });
      break;
    case "prompts/list":
      result(request.id, { prompts: [{ name: "source_products", description: "Fake sourcing prompt", arguments: [] }] });
      break;
    case "tools/call": {
      const name = request.params?.name;
      const args = request.params?.arguments ?? {};
      if (name === "slow_task") await new Promise((resolve) => setTimeout(resolve, 5000));
      if (name === "fail_task") {
        result(request.id, { isError: true, content: [{ type: "text", text: "Synthetic tool failure" }] });
        break;
      }
      if (name === "needs_information_mock") {
        const payload = {
          status: "needs_information",
          summary: "The product description is ambiguous.",
          products: [],
          missingInformation: ["Exact product model", "Material"],
          assumptions: [],
          confidence: 0.3,
          citations: []
        };
        result(request.id, { content: [{ type: "text", text: JSON.stringify(payload) }], structuredContent: payload, isError: false });
        break;
      }
      if (name === "product_search_mock") {
        const missing = args.requirementCompleteness?.missingElements ?? [];
        const payload = {
          summary: "2 candidate products found with best-effort partial requirements.",
          products: [
            { title: "Bluetooth Earbuds A", supplier: "Demo Supplier One", price: "4.20", currency: "USD", moq: "1000", url: "https://example.test/a", shipping: "Preliminary" },
            { title: "Bluetooth Earbuds B", supplier: "Demo Supplier Two", price: "4.80", currency: "USD", moq: "500", url: "https://example.test/b", shipping: "Preliminary" }
          ],
          recommendation: "Confirm the missing details before placing an order.",
          missingInformation: missing,
          assumptions: missing.map((item) => `${item} was not provided, so the search used no hard constraint.`),
          confidence: args.requirementCompleteness?.collectedCount === 5 ? 1 : 0.6,
          citations: [{ title: "Fake catalog", url: "https://example.test/catalog", source: "test" }]
        };
        result(request.id, { content: [{ type: "text", text: JSON.stringify(payload) }], structuredContent: payload, isError: false });
        break;
      }
      if (name === "echo") {
        result(request.id, { content: [{ type: "text", text: JSON.stringify(args) }], structuredContent: args, isError: false });
        break;
      }
      if (name === "send_whatsapp_message") {
        result(request.id, { content: [{ type: "text", text: "This tool should never be invoked by product_sourcing." }], isError: false });
        break;
      }
      error(request.id, -32601, `Unknown tool: ${name}`);
      break;
    }
    default:
      error(request.id, -32601, `Method not found: ${request.method}`);
  }
});
