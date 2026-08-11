# Relvyn MCP Agent Gateway

## Overview

Relvyn's MCP Agent Gateway is a vendor-neutral boundary between local customer work and external MCP-compatible tools. Relvyn owns customer context, readiness, approval, task state, audit, and result consumption. The external MCP Server owns the specialized operation.

The first task template is `product_sourcing`. There is no WorkBuddy-specific branch, tool name, payload, or SDK in Core. A discovered tool becomes usable through its Server ID, tool name, optional deterministic mapping, and permissions.

This implementation is Windows-desktop-first. It does not change or publish the PWA or macOS app.

## Architecture

```text
WhatsApp Inbox / Customer Brain / Opportunity recommendation
                         |
                 SourcingReadinessPolicy
                         |
              manual Find Products action
                         |
         Choose Server + Tool -> exact review
                         |
                 McpAgentGatewayService
       +-----------------+------------------+
       |                 |                  |
 registry/cache   permission/security   durable task queue
       |                 |                  |
       +--------- McpConnectionManager -----+
                         |
       stdio | Streamable HTTP | legacy SSE
                         |
                external MCP Server
                         |
           untrusted normalized result
                         |
         Customer timeline + CSM review UI
```

The official `ModelContextProtocol.Core` 1.4.1 client is isolated inside `McpConnectionManager`. Business modules call the Gateway and never call the MCP SDK directly.

Persistence is idempotently added to the existing SQLite workspace:

- `mcp_servers`
- `mcp_tools_cache`
- `mcp_capabilities_cache`
- `mcp_tasks`
- `mcp_task_events`
- `mcp_permissions`
- `mcp_permission_events`
- `mcp_mappings`

Disabling MCP leaves every existing Relvyn feature available.

## Sourcing readiness is not completeness

The five fields remain:

1. Product
2. Quantity
3. Target price
4. Destination
5. Logistics preference

The deterministic rule is:

| Collected | Product identifiable | Readiness | Agent action |
|---:|---|---|---|
| 0–2/5 | either | `insufficient` | disabled |
| 3–4/5 | no | `insufficient` | disabled with product-specific reason |
| 3–4/5 | yes | `agent_available` | enabled; human selection and review required |
| 5/5 | yes | `high_confidence` | enabled; same human-reviewed path |

Product identity may come from a clear name or description, model, SKU, URL, image reference, quoted historical product, or a confirmed Customer Brain requirement. No fixed three-field combination is hard-coded.

Completeness answers “how many elements are known.” Readiness answers “can a meaningful best-effort task start.” Confidence is deterministic: 3/5 = 0.60, 4/5 = 0.80, 5/5 = 1.00.

The workflow readiness trigger creates a recommendation or shows an action. It does not call MCP. `AutomaticExecutionEnabled` and the workflow node's explicit automatic flag both default to false; the Windows product-sourcing flow always requests named human approval.

## Partial task contract

`ProductSourcingTaskPayload` accepts null/missing fields and always includes an explicit completeness envelope:

```json
{
  "taskType": "product_sourcing",
  "requirement": {
    "product": "Bluetooth earbuds",
    "quantity": "5000",
    "targetPrice": null,
    "destination": "Los Angeles",
    "logisticsPreference": null,
    "requirementVersion": 1
  },
  "requirementCompleteness": {
    "collectedCount": 3,
    "totalCount": 5,
    "collectedElements": ["product", "quantity", "destination"],
    "missingElements": ["targetPrice", "logisticsPreference"],
    "productIdentifiable": true
  }
}
```

Missing fields are intentional, not a serialization error. Agents should perform best-effort search and return candidates, price ranges, MOQ, supplier or shipping possibilities where meaningful.

Normalized sourcing results can contain products, recommendation, `missingInformation`, `assumptions`, confidence, and citations. `needs_information` is a first-class task state. Missing information can be copied into the WhatsApp composer through **Ask Customer**, but Relvyn never sends it automatically.

Each requirement has a version. A task stores `requirementVersionUsed`; its result also stores collected and missing elements at execution time. New customer information enables **Refine Search**, creating a linked child task only when the version actually advanced.

## Adding an MCP Server

Open **Settings → MCP 与外部智能体 → 管理连接与任务 → 添加**.

Configure:

- Name and optional description
- Transport
- Endpoint, or stdio executable and one argument per line
- Authentication
- Timeout and auto-connect
- Context permissions (`Allow`, `Ask`, `Deny`)

Save, then select **测试连接**. The test performs initialize, protocol negotiation, and discovery of tools, resources, and prompts. Unsupported optional capabilities degrade independently rather than crashing Relvyn.

Remote HTTP endpoints must use HTTPS. Plain HTTP is accepted only for loopback development. stdio commands are launched without a shell, arguments stay separate, and the child receives a minimal environment instead of inherited process secrets.

## Supported transports and authentication

| Capability | Support |
|---|---|
| Streamable HTTP | yes; recommended remote transport |
| stdio | yes; managed process lifetime and clean disposal |
| SSE | yes; legacy compatibility |
| No auth | yes |
| Bearer token | yes |
| API key header | yes |
| OAuth | pre-issued access token in v1; interactive refresh-provider interface remains an extension point |

Credentials are stored per Server under `WAFlow/MCP/{serverId}` in Windows Credential Manager. They are not written to SQLite, connector exports, task payloads, or logs.

## Tool discovery, mapping, and Tool Explorer

Tools are keyed by `serverId::toolName`. Discovery caches schemas while retaining local enablement, risk, and approval settings. The UI supports `READ_ONLY`, `WRITE_LOCAL`, `EXTERNAL_ACTION`, and `HIGH_RISK`, plus `AlwaysAllow`, `AskEveryTime`, and `Deny`.

Tool Explorer renders basic top-level JSON Schema properties into a form, allows raw JSON review, requires an explicit confirmation checkbox, validates the input against the advertised schema, and shows bounded/redacted raw output and execution time.

`McpTaskMapping` provides deterministic mappings such as:

```text
query   = {{requirement.product}}
qty     = {{requirement.quantity}}
country = {{requirement.destination}}
```

When no mapping is configured, the stable Relvyn task payload is sent. Mapping and connector exports never include credentials.

## Agent task lifecycle

```text
AwaitingApproval -> Queued -> Running -> Completed
                              |        -> NeedsInformation
                              |        -> Waiting (retryable connection loss)
                              |        -> TimedOut / Failed / Cancelled
restart while active ----------------> Interrupted
```

Tasks, events, approval identity, target, requirement version, bounded results, and error codes persist locally. Global and per-Server concurrency are bounded. Retry applies only to retryable transport failures and uses exponential delay with jitter. Idempotency keys prevent an unchanged requirement/target/override/attachment set from launching twice.

A restart never silently replays an in-flight external action: queued/running/waiting records become `Interrupted` for review. An approved `Waiting` task can be retried after the same-process connection returns.

## Inbox workflow

The WhatsApp sidebar shows a five-segment requirement summary and one of:

- `Need more information`
- `Ready for Agent`
- `Complete`

At 3/5 with an identifiable product, **Find Products** opens one review surface:

1. Choose any connected Server / Tool.
2. Review or correct the five fields as a one-task **Task Override**.
3. Optionally add instructions and explicitly selected attachments.
4. Review the exact target, customer/context selection, missing fields, version, and attachment metadata.
5. Confirm and send.

Task Override does not update Customer Brain. Formal customer facts still use the existing Customer Brain update path.

The result card identifies the requirement version and missing-at-search-time fields. `Ask Customer` only fills the composer. `Refine Search` appears only when a later requirement version contains genuinely new information.

## Workflow integration

`McpWorkflowIntegrationService` is the generic External Agent node boundary. It accepts an `ExternalAgentWorkflowNodeConfig` containing Server, Tool, input mapping, allowed context, timeout, approval, and explicit automation state.

For the first release, `Sourcing Readiness Reached` defaults to:

```text
collectedCount >= 3 AND productIdentifiable
-> Create Recommendation / Show Agent Action
-> human chooses Agent
-> human reviews exact task
-> invoke
```

It is not a WorkBuddy node. Replacing or deleting any Server does not change Core.

## Security model

- Minimum necessary context; Server-specific `Allow` / `Ask` / `Deny` checks are enforced at invocation.
- Product Sourcing hard-denies direct WhatsApp, email, SMS, and customer-message tools.
- Human approval is stored with the exact task and target.
- Credentials are isolated in Windows Credential Manager and redacted from messages and logs.
- Remote HTTP requires TLS; stdio bypasses the shell and does not inherit the full environment.
- Tool input is bounded, parsed as an object, and validated against required/basic JSON Schema types.
- Attachments require explicit selection, per-file/count limits, hash metadata, and in-memory content transfer; the local path is not sent.
- Output is size-bounded, local paths are removed, and every result is labeled untrusted external data.
- MCP text never becomes a system instruction and cannot initiate another tool or customer message.
- Customer timeline receives concise business events, while technical task events remain in the Gateway audit store.
- Connector import/export includes configuration and mappings only—never tokens, passwords, secret references, or refresh tokens.

## Developer API

Use `AppServices.McpAgents` rather than the MCP SDK:

- `GetServersAsync`, `SaveServerAsync`, `DeleteServerAsync`, `DisconnectAsync`
- `TestConnectionAsync`, `RefreshToolsAsync`, `GetToolsAsync`
- `UpdateToolPolicyAsync`, `TestToolAsync`
- `BuildProductSourcingTaskAsync`, `SubmitApprovedAsync`
- `RefineProductSourcingAsync`, `CancelAsync`, `RetryWaitingTasksAsync`
- `GetTasksAsync`
- `ExportConnectorAsync`, `ImportConnectorAsync`

Use `AppServices.McpWorkflow` for deterministic workflow readiness and mapping.

## Troubleshooting

- **No Agent appears:** enable the Gateway, test a Server, enable at least one Tool, and ensure its approval policy is not Deny.
- **3/5 but button disabled:** the product is not identifiable. Add a name, model, SKU, link, image, or clear product description.
- **Context permission denied:** edit the Server's Allowed Data policy or remove that context from the review.
- **Authentication failed:** edit the Server and save a fresh credential; leaving the field empty retains the current one.
- **Waiting:** reconnect the Server. Only already-approved reversible tasks can continue in the same process.
- **Interrupted:** Relvyn restarted while the task was active. Review and intentionally create/refine a task rather than relying on silent replay.
- **Tool removed:** refresh discovery and choose a currently published Tool.

## Known extension points

- Interactive OAuth authorization/refresh UI (v1 accepts an already-issued access token).
- MCP server-native long-running task continuation through `ExternalTaskId`.
- More task templates and task-specific normalizers.
- Explicit workflow editor UI for advanced automatic execution; Core defaults remain human-reviewed.
- Streaming large attachment transport to replace bounded in-memory base64 for files near the configured limit.
