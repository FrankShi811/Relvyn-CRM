using WAFlow.Core.Domain;

namespace WAFlow.Core.Services;

/// <summary>
/// Vendor-neutral bridge for the repository's workflow/recommendation surfaces.
/// The first release deliberately returns a recommendation/manual action by default;
/// it never treats readiness as permission to contact a third party.
/// </summary>
public sealed class McpWorkflowIntegrationService
{
    public ExternalAgentWorkflowDecision EvaluateSourcingReadiness(
        SourcingRequest request,
        ExternalAgentWorkflowNodeConfig node,
        McpGatewaySettings gatewaySettings)
    {
        var readiness = request.Readiness;
        var matched = readiness.CollectedCount >= gatewaySettings.SourcingReadinessThreshold
                      && readiness.ProductIdentifiable;
        var explicitAutomatic = matched
                                && gatewaySettings.AutomaticExecutionEnabled
                                && node.AutomaticExecutionExplicitlyEnabled
                                && !node.HumanApprovalRequired;
        return new ExternalAgentWorkflowDecision
        {
            TriggerMatched = matched,
            ShowAgentAction = matched,
            CreateRecommendation = matched,
            MayExecuteAutomatically = explicitAutomatic,
            Readiness = readiness,
            Reason = !readiness.ProductIdentifiable && readiness.CollectedCount >= gatewaySettings.SourcingReadinessThreshold
                ? $"{readiness.CollectedCount} elements collected, but product identity is still required."
                : matched
                    ? explicitAutomatic
                        ? "The user explicitly enabled automatic execution for this workflow node and its permissions still apply."
                        : "Sourcing readiness reached. Show an Agent action and require human selection/review."
                    : $"Readiness is {readiness.CollectedCount}/5; continue collecting information."
        };
    }

    public McpTaskMapping ToMapping(ExternalAgentWorkflowNodeConfig node) => new()
    {
        TaskType = node.TaskType,
        ServerId = node.ServerId,
        ToolName = node.ToolName,
        InputMapping = new Dictionary<string, string>(node.InputMapping, StringComparer.OrdinalIgnoreCase),
        Enabled = true
    };
}
