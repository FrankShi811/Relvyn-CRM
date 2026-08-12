using WAFlow.Core.Domain;
using WAFlow.Core.Imports;
using WAFlow.Core.Infrastructure;
using WAFlow.Core.Services;

namespace WAFlow.Core;

public sealed class AppServices
{
    public DataWorkspaceManager DataWorkspaceManager { get; }
    public DataWorkspaceLocation DataWorkspace { get; }
    public LocalRepository Repository { get; }
    public LeadScoringService Scoring { get; }
    public ImportService Imports { get; }
    public OpportunitySupplementImportService OpportunitySupplements { get; }
    public WindowsCredentialStore ActiveAiCredential { get; }
    public AiProviderService AiProvider { get; }
    public WhatsAppConnectionManager WhatsApp { get; }
    public WhatsAppNumberValidationService WhatsAppNumberValidation { get; }
    public WhatsAppSyncService WhatsAppSync { get; }
    public EmailService Email { get; }
    public EmailAssistantService EmailAssistant { get; }
    public MessagingSyncService MessagingSync { get; }
    public LeadIntelligenceAutomationService LeadAutomation { get; }
    public PublicIpMonitor PublicIp { get; }
    public CampaignAutomationService Campaigns { get; }
    public CustomerAnalysisService CustomerAnalysis { get; }
    public CustomerReportExportService CustomerReportExports { get; }
    public ConversationAssistantService ConversationAssistant { get; }
    public WhatsAppTranslationService WhatsAppTranslation { get; }
    public CustomerIdentityService CustomerIdentity { get; }
    public SourcingRequestService SourcingRequests { get; }
    public McpAgentGatewayService McpAgents { get; }
    public McpWorkflowIntegrationService McpWorkflow { get; }
    public CustomerSuccessAgentService CustomerSuccessAgent { get; }
    public CustomerSuccessAgentCoordinator CustomerSuccessCoordinator { get; }
    public CustomerBrainService CustomerBrain { get; }
    public CustomerCommitmentService CustomerCommitments { get; }
    public CustomerEnrichmentService CustomerEnrichment { get; }
    public CustomerActionLifecycleService CustomerActions { get; }
    public PersonalSalesLearningService SalesLearning { get; }
    public TodayBriefService TodayBrief { get; }
    public DashboardUnreadDigestService DashboardUnreadDigest { get; }
    public KnowledgeBaseService KnowledgeBase { get; }
    public KnowledgeRetrievalService KnowledgeRetrieval { get; }
    public KnowledgeLearningService KnowledgeLearning { get; }

    public AppServices(
        LocalRepository? repository = null,
        DataWorkspaceManager? dataWorkspaceManager = null)
    {
        DataWorkspaceManager = dataWorkspaceManager ?? new DataWorkspaceManager();
        DataWorkspace = repository is null
            ? DataWorkspaceManager.Resolve()
            : DataWorkspaceManager.FromDatabasePath(repository.DatabasePath);
        Repository = repository ?? new LocalRepository(DataWorkspace.DatabasePath);
        Scoring = new LeadScoringService();
        ActiveAiCredential = new WindowsCredentialStore(WindowsCredentialStore.ActiveAiProviderTarget);
        KnowledgeRetrieval = new KnowledgeRetrievalService(Repository);
        AiProvider = new AiProviderService(
            Repository,
            ActiveAiCredential,
            knowledgeRetrieval: KnowledgeRetrieval,
            providerSecretResolver: providerId => new WindowsCredentialStore($"WAFlow/AiProvider/{providerId}"));
        KnowledgeBase = new KnowledgeBaseService(
            Repository,
            new CompositeKnowledgeDocumentParser(new AiProviderImageTextExtractor(AiProvider)));
        Imports = new ImportService(Repository);
        OpportunitySupplements = new OpportunitySupplementImportService(Repository);
        WhatsApp = new WhatsAppConnectionManager(DataWorkspace.RootDirectory);
        WhatsAppNumberValidation = new WhatsAppNumberValidationService(Repository, WhatsApp);
        WhatsAppSync = new WhatsAppSyncService(Repository, WhatsApp);
        CustomerCommitments = new CustomerCommitmentService(Repository);
        CustomerBrain = new CustomerBrainService(Repository, AiProvider, KnowledgeRetrieval, CustomerCommitments);
        Email = new EmailService(Repository);
        EmailAssistant = new EmailAssistantService(Repository, AiProvider, KnowledgeRetrieval, CustomerBrain);
        MessagingSync = new MessagingSyncService(Repository, WhatsApp, Email);
        LeadAutomation = new LeadIntelligenceAutomationService(Repository, AiProvider, WhatsAppSync);
        PublicIp = new PublicIpMonitor(Repository);
        Campaigns = new CampaignAutomationService(Repository, WhatsApp, PublicIp, Email);
        CustomerEnrichment = new CustomerEnrichmentService(
            Repository,
            AiProvider,
            CustomerBrain,
            WhatsAppSync,
            LeadAutomation,
            Imports);
        CustomerAnalysis = new CustomerAnalysisService(Repository, AiProvider, KnowledgeRetrieval, CustomerBrain);
        CustomerReportExports = new CustomerReportExportService(Repository);
        CustomerActions = new CustomerActionLifecycleService(Repository, CustomerBrain);
        SalesLearning = new PersonalSalesLearningService(Repository);
        ConversationAssistant = new ConversationAssistantService(Repository, AiProvider, SalesLearning, KnowledgeRetrieval, CustomerBrain);
        WhatsAppTranslation = new WhatsAppTranslationService(Repository, AiProvider);
        CustomerIdentity = new CustomerIdentityService(Repository);
        SourcingRequests = new SourcingRequestService(Repository);
        McpAgents = new McpAgentGatewayService(
            Repository,
            SourcingRequests,
            target => new WindowsCredentialStore(target));
        McpWorkflow = new McpWorkflowIntegrationService();
        KnowledgeLearning = new KnowledgeLearningService(Repository, SalesLearning);
        CustomerSuccessAgent = new CustomerSuccessAgentService(
            Repository,
            AiProvider,
            CustomerIdentity,
            SourcingRequests,
            KnowledgeRetrieval,
            CustomerBrain,
            WhatsApp,
            WhatsAppSync);
        CustomerSuccessCoordinator = new CustomerSuccessAgentCoordinator(Repository, WhatsAppSync, WhatsApp, CustomerSuccessAgent);
        TodayBrief = new TodayBriefService(Repository, SalesLearning, CustomerBrain);
        DashboardUnreadDigest = new DashboardUnreadDigestService(Repository, AiProvider);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await Repository.InitializeAsync(cancellationToken);
        await MigrateLegacyAiCredentialAsync(cancellationToken);
        // Load before any bridge starts: the governor is constructed during the
        // bridge's initialize command, so settings that arrive later would leave
        // the first sends of the session running on bridge defaults.
        WhatsApp.OutboundSettings = (await Repository.GetAppSettingsAsync(cancellationToken)).Outbound
                                    ?? new OutboundGovernorSettings();
        // Runtime ownership is process-local. Persisted drafts, locks and active
        // hosting states from the previous process must never resume or send on
        // startup; the user explicitly re-arms after reviewing the latest chat.
        await CustomerSuccessAgent.RecoverAfterRestartAsync(cancellationToken);
        await McpAgents.InitializeAsync(cancellationToken);
        await CustomerIdentity.RepairOwnedAccountBindingsAsync(cancellationToken);
    }

    private async Task MigrateLegacyAiCredentialAsync(CancellationToken cancellationToken)
    {
        var legacyStore = new WindowsCredentialStore(WindowsCredentialStore.LegacyAiProviderTarget);
        string? legacyKey;
        try { legacyKey = legacyStore.Read(); }
        catch { return; }
        if (string.IsNullOrWhiteSpace(legacyKey)) return;

        var settings = await Repository.GetAppSettingsAsync(cancellationToken);
        var providerId = AiProviderCatalog.Resolve(settings.ActiveProviderId).Id;
        var providerStore = new WindowsCredentialStore($"WAFlow/AiProvider/{providerId}");
        try
        {
            if (string.IsNullOrWhiteSpace(providerStore.Read())) providerStore.Save(legacyKey);
            if (string.IsNullOrWhiteSpace(ActiveAiCredential.Read())) ActiveAiCredential.Save(legacyKey);
        }
        catch
        {
            // A credential-store failure must not block local startup. Settings will show the provider as unavailable.
        }
    }
}
