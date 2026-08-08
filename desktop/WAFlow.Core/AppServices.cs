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
    public WindowsCredentialStore Secrets { get; }
    public DeepSeekService DeepSeek { get; }
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
    public CustomerSuccessAgentService CustomerSuccessAgent { get; }
    public CustomerSuccessAgentCoordinator CustomerSuccessCoordinator { get; }
    public CustomerBrainService CustomerBrain { get; }
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
        Secrets = new WindowsCredentialStore();
        KnowledgeRetrieval = new KnowledgeRetrievalService(Repository);
        DeepSeek = new DeepSeekService(
            Repository,
            Secrets,
            knowledgeRetrieval: KnowledgeRetrieval,
            providerSecretResolver: providerId => new WindowsCredentialStore($"WAFlow/AiProvider/{providerId}"));
        KnowledgeBase = new KnowledgeBaseService(
            Repository,
            new CompositeKnowledgeDocumentParser(new AiProviderImageTextExtractor(DeepSeek)));
        Imports = new ImportService(Repository);
        OpportunitySupplements = new OpportunitySupplementImportService(Repository);
        WhatsApp = new WhatsAppConnectionManager(DataWorkspace.RootDirectory);
        WhatsAppNumberValidation = new WhatsAppNumberValidationService(Repository, WhatsApp);
        WhatsAppSync = new WhatsAppSyncService(Repository, WhatsApp);
        CustomerBrain = new CustomerBrainService(Repository, DeepSeek, KnowledgeRetrieval);
        Email = new EmailService(Repository);
        EmailAssistant = new EmailAssistantService(Repository, DeepSeek, KnowledgeRetrieval, CustomerBrain);
        MessagingSync = new MessagingSyncService(Repository, WhatsApp, Email);
        LeadAutomation = new LeadIntelligenceAutomationService(Repository, DeepSeek, WhatsAppSync);
        PublicIp = new PublicIpMonitor(Repository);
        Campaigns = new CampaignAutomationService(Repository, WhatsApp, PublicIp, Email);
        CustomerEnrichment = new CustomerEnrichmentService(
            Repository,
            DeepSeek,
            CustomerBrain,
            WhatsAppSync,
            LeadAutomation,
            Imports);
        CustomerAnalysis = new CustomerAnalysisService(Repository, DeepSeek, KnowledgeRetrieval, CustomerBrain);
        CustomerReportExports = new CustomerReportExportService(Repository);
        CustomerActions = new CustomerActionLifecycleService(Repository, CustomerBrain);
        SalesLearning = new PersonalSalesLearningService(Repository);
        ConversationAssistant = new ConversationAssistantService(Repository, DeepSeek, SalesLearning, KnowledgeRetrieval, CustomerBrain);
        WhatsAppTranslation = new WhatsAppTranslationService(Repository, DeepSeek);
        CustomerIdentity = new CustomerIdentityService(Repository);
        SourcingRequests = new SourcingRequestService(Repository);
        KnowledgeLearning = new KnowledgeLearningService(Repository, SalesLearning);
        CustomerSuccessAgent = new CustomerSuccessAgentService(
            Repository,
            DeepSeek,
            CustomerIdentity,
            SourcingRequests,
            KnowledgeRetrieval,
            CustomerBrain);
        CustomerSuccessCoordinator = new CustomerSuccessAgentCoordinator(Repository, WhatsAppSync, WhatsApp, CustomerSuccessAgent);
        TodayBrief = new TodayBriefService(Repository, SalesLearning, CustomerBrain);
        DashboardUnreadDigest = new DashboardUnreadDigestService(Repository, DeepSeek);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await Repository.InitializeAsync(cancellationToken);
        // Load before any bridge starts: the governor is constructed during the
        // bridge's initialize command, so settings that arrive later would leave
        // the first sends of the session running on bridge defaults.
        WhatsApp.OutboundSettings = (await Repository.GetAppSettingsAsync(cancellationToken)).Outbound
                                    ?? new OutboundGovernorSettings();
        await CustomerIdentity.RepairOwnedAccountBindingsAsync(cancellationToken);
    }
}
