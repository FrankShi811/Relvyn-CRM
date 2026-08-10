[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$localDotnet = Join-Path $root 'work\dotnet8\dotnet.exe'
$dotnet = $env:WAFLOW_DOTNET_PATH
if (-not $dotnet -or -not (Test-Path -LiteralPath $dotnet)) {
  $dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { (Get-Command dotnet -ErrorAction Stop).Source }
}
$node = $env:WAFLOW_NODE_PATH
if (-not $node -or -not (Test-Path -LiteralPath $node)) {
  $node = (Get-Command node -ErrorAction Stop).Source
}
$work = Join-Path $root 'work'
$env:DOTNET_CLI_HOME = $work
$env:NUGET_PACKAGES = Join-Path $work 'nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

& (Join-Path $root 'scripts\test-compliance.ps1')

$desktopProject = Join-Path $root 'desktop\WAFlow.Desktop\WAFlow.Desktop.csproj'
$coreProject = Join-Path $root 'desktop\WAFlow.Core\WAFlow.Core.csproj'
$macProject = Join-Path $root 'desktop\WAFlow.Mac\WAFlow.Mac.csproj'
$appXaml = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\App.xaml')
$appStartupSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\App.xaml.cs')
$desktopShortcutSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\DesktopShortcutService.cs')
$uiScaleSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\UiScaleManager.cs')
$uiScaleHostSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\Controls\UiScaleHost.cs')
$windowsInstallerTestSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'scripts\test-windows-installer.ps1')
$themeSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\ThemeManager.cs')
$mainWindowXaml = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\MainWindow.xaml')
$mainWindowSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\MainWindow.xaml.cs')
$motionAssistSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\MotionAssist.cs')
$dashboardXaml = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\Pages\DashboardView.xaml')
$dashboardSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\Pages\DashboardView.xaml.cs')
$dashboardUnreadDigestSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Services\DashboardUnreadDigestService.cs')
$customersXaml = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\Pages\CustomersView.xaml')
$customersSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\Pages\CustomersView.xaml.cs')
$customerEnrichmentXaml = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\Pages\CustomerEnrichmentView.xaml')
$customerEnrichmentSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\Pages\CustomerEnrichmentView.xaml.cs')
$customerEnrichmentServiceSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Services\CustomerEnrichmentService.cs')
$customerEnrichmentAnalyzerSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Services\CustomerEnrichmentAnalyzer.cs')
$customerSearchProviderSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Services\CustomerSearchProviders.cs')
$customerEnrichmentRepositorySource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Infrastructure\LocalRepository.CustomerEnrichment.cs')
$appServicesSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\AppServices.cs')
$campaignsXaml = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\Pages\CampaignsView.xaml')
$campaignsSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\Pages\CampaignsView.xaml.cs')
$todayBriefSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Services\TodayBriefService.cs')
$customerBrainModelsSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Domain\CustomerBrainModels.cs')
$leadIntelligenceXaml = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\Pages\LeadIntelligenceView.xaml')
$leadIntelligenceSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\Pages\LeadIntelligenceView.xaml.cs')
$leadAutomationSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Services\LeadIntelligenceAutomationService.cs')
$opportunityImportModelsSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Domain\OpportunitySupplementModels.cs')
$opportunityImportServiceSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Imports\OpportunitySupplementImportService.cs')
$opportunityImportXaml = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\Windows\OpportunitySupplementImportWindow.xaml')
$opportunityImportSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\Windows\OpportunitySupplementImportWindow.xaml.cs')
$whatsAppInboxXaml = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\Pages\WhatsAppInboxView.xaml')
$whatsAppTranslationSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Services\WhatsAppTranslationService.cs')
$whatsAppTranslationModelsSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Domain\WhatsAppTranslationModels.cs')
$emailInboxXaml = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\Pages\EmailInboxView.xaml')
$bridgeSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'bridge\src\index.mjs')
$bridgeConnectionBootstrapSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'bridge\src\connection-bootstrap.mjs')
$bridgeMessageSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'bridge\src\message-content.mjs')
$bridgeOutboundRoutingSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'bridge\src\outbound-routing.mjs')
$bridgeConversationRoutingSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'bridge\src\conversation-routing.mjs')
$bridgeNetworkRoutingSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'bridge\src\network-routing.mjs')
$bridgeOfflineCatchupSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'bridge\src\offline-catchup.mjs')
$whatsAppInboxSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\Pages\WhatsAppInboxView.xaml.cs')
$emailInboxSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\Pages\EmailInboxView.xaml.cs')
$batchCollectionSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\Collections\BatchObservableCollection.cs')
$whatsAppSyncSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Services\WhatsAppSyncService.cs')
$whatsAppNamingSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Services\WhatsAppConversationNaming.cs')
$whatsAppManagerSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Services\WhatsAppConnectionManager.cs')
$whatsAppNumberValidationSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Services\WhatsAppNumberValidationService.cs')
$campaignAutomationSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Services\CampaignAutomationService.cs')
$customerSuccessSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Services\CustomerSuccessAgentCoordinator.cs')
$emailServiceSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Services\EmailService.cs')
$networkProxyResolverSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Services\NetworkProxyResolver.cs')
$windowsCredentialStoreSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Infrastructure\WindowsCredentialStore.cs')
$emailAssistantSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Services\EmailAssistantService.cs')
$messagingSyncSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Services\MessagingSyncService.cs')
$emailAccountXaml = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\Windows\EmailAccountWindow.xaml')
$emailAccountSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\Windows\EmailAccountWindow.xaml.cs')
$knowledgeBaseXaml = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\Pages\KnowledgeBaseView.xaml')
$knowledgeBaseSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\Pages\KnowledgeBaseView.xaml.cs')
$knowledgeServiceSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Services\KnowledgeBaseService.cs')
$knowledgeModelsSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Domain\KnowledgeModels.cs')
$localRepositorySource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Infrastructure\LocalRepository.cs')
$guideCatalogSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\GuideCatalog.cs')
$releaseCatalogSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\ReleaseCatalog.cs')
$updateStateSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\Updates\ApplicationUpdateState.cs')
$updateServiceSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\Updates\VelopackUpdateService.cs')
$updateCacheRetentionSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Infrastructure\UpdateCacheRetention.cs')
$updateCacheMaintenanceSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\Updates\LocalUpdateCacheMaintenance.cs')
$trimUpdateCacheSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'scripts\trim-local-update-cache.ps1')
$settingsXaml = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\Windows\SettingsWindow.xaml')
$settingsSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\Windows\SettingsWindow.xaml.cs')
$dataWorkspaceSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Infrastructure\DataWorkspaceManager.cs')
$whatsAppBridgeClientSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Services\WhatsAppBridgeClient.cs')
$bridgeBootstrapSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'bridge\scripts\sea-bootstrap.cjs')
$modulePreferencePersistenceSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Services\AiModulePreferencePersistence.cs')
$domainModelsSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Domain\Models.cs')
$businessRoleProfileSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Domain\BusinessRoleProfile.cs')
$businessRoleContextPolicySource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Services\BusinessRoleContextPolicy.cs')
$deepSeekSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Services\DeepSeekService.cs')
$aiProviderCatalogSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Services\AiProviderCatalog.cs')
$aiModelCapabilityResolverSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Services\AiModelCapabilityResolver.cs')
$conversationAssistantSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Services\ConversationAssistantService.cs')
$customerAnalysisSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Services\CustomerAnalysisService.cs')
$customerBrainSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Services\CustomerBrainService.cs')
$customerExternalFactPolicySource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Services\CustomerExternalFactPolicy.cs')
$customerReportExportSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Services\CustomerReportExportService.cs')
$customerSuccessAgentSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Services\CustomerSuccessAgentService.cs')
$buyerIdentitySource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Services\BuyerIdentity.cs')
$customerIdentitySource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Services\CustomerIdentityService.cs')
$importModelsSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Imports\ImportModels.cs')
$importServiceSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Imports\ImportService.cs')
$customerDimensionSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Imports\CustomerDimensionCatalog.cs')
$customerEditXaml = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\Windows\CustomerEditWindow.xaml')
$customerEditSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\Windows\CustomerEditWindow.xaml.cs')
$knowledgeProcessingSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'desktop\WAFlow.Core\Services\KnowledgeProcessingComponents.cs')
$velopackBuildSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'scripts\build-velopack-release.ps1')
$coreProjectSource = Get-Content -Raw -Encoding utf8 -LiteralPath $coreProject
$bridgePackageSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'bridge\package.json')
$bridgeSourceArchiveSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root 'scripts\build-bridge-source-archive.ps1')
$releaseWorkflowSource = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root '.github\workflows\release.yml')
$allDesktopXaml = (Get-ChildItem -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop') -Recurse -Filter '*.xaml' |
  ForEach-Object { Get-Content -Raw -Encoding utf8 -LiteralPath $_.FullName }) -join "`n"
$desktopVersion = ([xml](Get-Content -Raw -Encoding utf8 -LiteralPath $desktopProject)).Project.PropertyGroup.Version | Select-Object -First 1
$coreVersion = ([xml](Get-Content -Raw -Encoding utf8 -LiteralPath $coreProject)).Project.PropertyGroup.Version | Select-Object -First 1
$macVersion = ([xml](Get-Content -Raw -Encoding utf8 -LiteralPath $macProject)).Project.PropertyGroup.Version | Select-Object -First 1
$windowsIcon = Join-Path $root 'desktop\WAFlow.Desktop\Assets\AI-Sales-OS.ico'
Add-Type -AssemblyName PresentationCore
$iconStream = [IO.File]::OpenRead($windowsIcon)
try {
  try {
    $iconDecoder = [Windows.Media.Imaging.BitmapDecoder]::Create(
      $iconStream,
      [Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat,
      [Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
  }
  catch {
    throw "Windows application icon must be decodable by WPF: $($_.Exception.GetBaseException().Message)"
  }
  if ($iconDecoder.Frames.Count -lt 1) {
    throw 'Windows application icon must contain at least one WPF-decodable frame.'
  }
}
finally {
  $iconStream.Dispose()
}
Write-Host "PASS  Windows application icon is WPF-decodable ($($iconDecoder.Frames.Count) frames)"
if ($desktopVersion -notmatch '^\d+\.\d+\.\d+$' -or $desktopVersion -ne $coreVersion) {
  throw "Desktop/Core versions must be the same semantic version. desktop=$desktopVersion core=$coreVersion"
}
if ($releaseCatalogSource -notmatch [regex]::Escape("new(`"$desktopVersion`"")) {
  throw "ReleaseCatalog must contain the current Desktop/Core semantic version. version=$desktopVersion"
}
if ($env:ENABLE_MACOS_RELEASE -eq 'true') {
  if ($desktopVersion -ne $macVersion) {
    throw "macOS release is enabled, so Desktop/Core/macOS versions must match. desktop=$desktopVersion core=$coreVersion mac=$macVersion"
  }
  Write-Host "PASS  cross-platform version contract: $desktopVersion"
}
else {
  Write-Host "PASS  Windows release version contract: $desktopVersion (macOS release paused at $macVersion)"
}

if ($coreProjectSource -match 'WAFlow\.WhatsApp\.Bridge\.exe' -or
    $bridgePackageSource -notmatch '"license"\s*:\s*"GPL-3\.0-only"' -or
    $whatsAppBridgeClientSource -notmatch 'AI_SALES_OS_WHATSAPP_BRIDGE_PATH' -or
    $whatsAppBridgeClientSource -match 'GetManifestResourceStream\("WAFlow\.WhatsApp\.Bridge\.exe"\)' -or
    $bridgeSourceArchiveSource -notmatch 'pnpm.*deploy.*--legacy' -or
    $velopackBuildSource -notmatch 'test-compliance\.ps1' -or
    $releaseWorkflowSource -notmatch 'dist/source/\*\.zip') {
  throw 'Windows releases must keep the GPL Bridge separate, user-replaceable and accompanied by complete corresponding source.'
}
Write-Host 'PASS  separate GPL Bridge, replacement path and corresponding-source release contract'

$desktopProductSourceFiles = Get-ChildItem -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop') -Recurse -File |
  Where-Object { $_.Extension -in @('.xaml', '.cs') -and $_.FullName -notmatch '[\\/](?:bin|obj)[\\/]' }
$legacyPlatformBrandInDesktopCopy = $desktopProductSourceFiles |
  Select-String -Pattern '\bDHgate\b' -CaseSensitive:$false
if ($legacyPlatformBrandInDesktopCopy) {
  $matches = ($legacyPlatformBrandInDesktopCopy | ForEach-Object { "$($_.Path):$($_.LineNumber)" }) -join ', '
  throw "Desktop product copy must remain platform-neutral; remove the legacy platform brand from fixed UI copy: $matches"
}
Write-Host 'PASS  platform-neutral desktop product-copy contract'

$publicBrandCopyFiles = @(
  Get-ChildItem -LiteralPath (Join-Path $root 'docs') -Recurse -Filter '*.md'
  Get-Item -LiteralPath (Join-Path $root 'pwa\src\store.tsx')
)
$legacyBrandInPublicCopy = $publicBrandCopyFiles |
  Select-String -Pattern '\b(?:DHgate|MyyBiz)\b' -CaseSensitive:$false
if ($legacyBrandInPublicCopy) {
  $matches = ($legacyBrandInPublicCopy | ForEach-Object { "$($_.Path):$($_.LineNumber)" }) -join ', '
  throw "Public product docs and default examples must remain company- and platform-neutral: $matches"
}
$neutralProductCopyFiles = @(
  Get-ChildItem -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop') -Recurse -Filter '*.xaml'
  Get-Item -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\GuideCatalog.cs')
  Get-Item -LiteralPath (Join-Path $root 'desktop\WAFlow.Desktop\ReleaseCatalog.cs')
  $publicBrandCopyFiles
)
$legacyIndustryCopy = $neutralProductCopyFiles |
  Select-String -Pattern '采购需求|采购信号|采购五要素|电商|跨境贸易|下单未付款|纠纷订单|买家询问'
if ($legacyIndustryCopy) {
  $matches = ($legacyIndustryCopy | ForEach-Object { "$($_.Path):$($_.LineNumber)" }) -join ', '
  throw "Public UI, guides, release notes and docs must use generic customer, transaction and cooperation language: $matches"
}
if (-not ($domainModelsSource.Contains('BusinessRoleProfile BusinessRoleProfile')) -or
    -not ($businessRoleProfileSource.Contains('DefaultRoleName = "通用销售"')) -or
    -not ($businessRoleProfileSource.Contains('DefaultRoleSkillDescription')) -or
    -not ($businessRoleContextPolicySource.Contains('workspace_profile is user-managed descriptive business context')) -or
    -not ($businessRoleContextPolicySource.Contains('Never assume a marketplace')) -or
    -not ($deepSeekSource.Contains('BusinessRoleContextPolicy.ApplyPayload')) -or
    -not ($settingsXaml.Contains('x:Name="BusinessOrganizationNameBox"')) -or
    -not ($settingsXaml.Contains('x:Name="BusinessDescriptionBox"')) -or
    -not ($settingsXaml.Contains('x:Name="BusinessRoleBox"')) -or
    -not ($settingsXaml.Contains('x:Name="RoleSkillDescriptionBox"')) -or
    -not ($settingsXaml.Contains('AutomationProperties.Name="公司与 AI 工作角色设置"')) -or
    -not ($appXaml.Contains('x:Name="PART_EditableTextBox"')) -or
    -not ($appXaml.Contains('Path=(AutomationProperties.Name)')) -or
    -not ($appXaml.Contains('<Trigger Property="IsEditable" Value="True">')) -or
    -not ($appXaml.Contains('<Trigger Property="IsKeyboardFocusWithin" Value="True"><Setter TargetName="DropDownToggle" Property="BorderBrush" Value="{DynamicResource Primary}"/></Trigger>')) -or
    -not ($settingsSource.Contains('_settings.BusinessRoleProfile = BusinessRoleProfile.Normalize')) -or
    -not ($guideCatalogSource.Contains('设置公司与工作角色'))) {
  throw 'Company and role Skill settings must persist and enter every structured AI request through a guarded, platform-neutral workspace profile.'
}
$neutralVisibleCopy = @(
  $whatsAppInboxXaml,
  $leadIntelligenceXaml,
  $customerEditXaml,
  $customerAnalysisSource,
  $customerReportExportSource
) -join "`n"
if ($neutralVisibleCopy -match '采购需求五要素|尚未建立采购需求|采购信号|电商基础|供应链稳定性|预计订单额') {
  throw 'Primary desktop UI and generated-report copy must use generic customer, cooperation, business and opportunity language.'
}
Write-Host 'PASS  company-neutral UI copy plus guarded company and role Skill context contract'

$requiredBrushes = @(
  'Ink', 'InkSecondary', 'Muted', 'Primary', 'OnPrimary', 'AiAccent', 'OnAi', 'AiProcessing',
  'Surface', 'Canvas', 'Line', 'Success', 'Warning', 'Danger', 'Info',
  'OnDanger', 'UnreadBadgeBackground', 'UnreadBadgeText',
  'LogoSurface', 'LogoBorder',
  'GradeA', 'GradeB', 'GradeC', 'GradeD', 'ChatOutbound', 'ChatInbound'
)
foreach ($key in $requiredBrushes) {
  if ($appXaml -notmatch "x:Key=`"$key`"" -or $themeSource -notmatch [regex]::Escape("[`"$key`"]")) {
    throw "AI Sales OS 2.0 semantic brush is missing from App.xaml or ThemeManager: $key"
  }
}
$requiredStyles = @(
  'HolographicCard', 'ConfidenceMeter', 'ReasoningStepCard', 'PriorityCard',
  'InboundMessageBubble', 'OutboundMessageBubble', 'WorkflowNodeCard',
  'PageTitle', 'SectionTitle', 'BodyText', 'LabelText', 'MicroText',
  'GlassCard', 'AmbientHeroCard', 'IntelligenceGlassCard', 'ElevatedMetricCard',
  'NavText'
)
foreach ($key in $requiredStyles) {
  if ($appXaml -notmatch "x:Key=`"$key`"") { throw "AI Sales OS 2.0 component style is missing: $key" }
}
Write-Host 'PASS  AI Sales OS 2.x Figma/Stitch/WPF design-system contract'

$allSemanticBrushes = @(
  'Ink', 'InkSecondary', 'Muted', 'MutedSubtle', 'Primary', 'PrimaryDark', 'PrimaryHover', 'OnPrimary',
  'PrimarySoft', 'PrimarySurface', 'AiAccent', 'AiAccentDeep', 'OnAi', 'AiProcessing', 'AiSoft',
  'AiSurface', 'Surface', 'SurfaceElevated', 'SurfaceMuted', 'SurfaceInput', 'Canvas',
  'CanvasDeep', 'Line', 'LineStrong', 'Sidebar', 'SidebarElevated', 'SidebarHover',
  'SidebarActive', 'SidebarText', 'SidebarMuted', 'LogoSurface', 'LogoBorder', 'UnreadBadgeBackground', 'UnreadBadgeText', 'Success', 'SuccessSoft', 'Warning',
  'WarningSoft', 'Danger', 'DangerSoft', 'OnDanger', 'Info', 'InfoSoft', 'GradeA', 'GradeB', 'GradeC',
  'GradeD', 'ChatOutbound', 'ChatInbound', 'Overlay', 'GlassSurface', 'GlassSurfaceStrong',
  'GlassLine', 'AuroraAmbient', 'AuroraBorder'
)
$semanticStaticPattern = '\{StaticResource\s+(?:' + (($allSemanticBrushes | ForEach-Object { [regex]::Escape($_) }) -join '|') + ')\}'
if ([regex]::IsMatch($allDesktopXaml, $semanticStaticPattern)) {
  throw 'Theme-sensitive semantic brushes must use DynamicResource so an in-app theme switch cannot retain light-theme colors.'
}
$hardcodedColorPattern = '(?:Foreground|Background|BorderBrush)="(?:#[0-9A-Fa-f]{3,8}|Black|White|Gray|DarkGray|LightGray)"'
if ([regex]::IsMatch($allDesktopXaml, $hardcodedColorPattern)) {
  throw 'Desktop XAML contains a hard-coded foreground/background/border color that bypasses the semantic light/dark theme.'
}
if ($appXaml -notmatch '<Style TargetType="\{x:Type TextBlock\}">[\s\S]*?<Setter Property="Foreground" Value="\{DynamicResource Ink\}"' -or
    $appXaml -notmatch '<Style TargetType="\{x:Type Label\}">[\s\S]*?<Setter Property="Foreground" Value="\{DynamicResource InkSecondary\}"' -or
    $appXaml -notmatch '<Style TargetType="\{x:Type RadioButton\}">[\s\S]*?<Setter Property="Foreground" Value="\{DynamicResource Ink\}"' -or
    $appXaml -notmatch '<Style TargetType="\{x:Type GroupBox\}">[\s\S]*?<Setter Property="Foreground" Value="\{DynamicResource Ink\}"') {
  throw 'Implicit WPF text controls must inherit high-contrast semantic foregrounds in dark mode.'
}
$navTextCount = ([regex]::Matches($mainWindowXaml, 'Style="\{StaticResource NavText\}"')).Count
$navIconCount = ([regex]::Matches($mainWindowXaml, 'Style="\{StaticResource NavIconFrame\}"')).Count
if ($navTextCount -ne 10 -or
    $navIconCount -ne 10 -or
    $appXaml -notmatch 'x:Key="NavText"[\s\S]*?Binding Foreground,\s*RelativeSource=\{RelativeSource AncestorType=\{x:Type Button\}\}' -or
    $appXaml -notmatch 'x:Key="NavIconFrame"[\s\S]*?<Setter Property="Width" Value="18"/>' -or
    $appXaml -notmatch 'x:Key="NavIconPath"[\s\S]*?Binding Foreground,\s*RelativeSource=\{RelativeSource AncestorType=\{x:Type Button\}\}[\s\S]*?<Setter Property="StrokeThickness" Value="2"/>' -or
    $appXaml -notmatch 'x:Key="NavIconRectangle"[\s\S]*?Binding Foreground,\s*RelativeSource=\{RelativeSource AncestorType=\{x:Type Button\}\}' -or
    $appXaml -notmatch 'x:Key="NavIconEllipse"[\s\S]*?Binding Foreground,\s*RelativeSource=\{RelativeSource AncestorType=\{x:Type Button\}\}') {
  throw "Every sidebar module and the single settings entry must use one aligned 18px vector icon and a semantic foreground. expected_text=10 actual_text=$navTextCount expected_icons=10 actual_icons=$navIconCount"
}
$customerEnrichmentContractFailures = @()
if (-not $mainWindowXaml.Contains('x:Name="CustomerEnrichmentButton" Tag="customer-enrichment"') -or
    -not $mainWindowXaml.Contains('Text="客户外部调查" Style="{StaticResource NavText}"') -or
    -not $mainWindowXaml.Contains('AutomationProperties.Name="客户外部调查"')) {
  $customerEnrichmentContractFailures += 'sidebar_button'
}
if (-not $mainWindowSource.Contains('private readonly CustomerEnrichmentView _customerEnrichment;') -or
    -not $mainWindowSource.Contains('_customerEnrichment = new CustomerEnrichmentView(services);') -or
    -not $mainWindowSource.Contains('"customer-enrichment" => CustomerEnrichmentButton') -or
    -not $mainWindowSource.Contains('"customer-enrichment" => ((object)_customerEnrichment') -or
    -not $mainWindowSource.Contains('case "customer-enrichment": await NavigateAsync(action, CustomerEnrichmentButton); break;') -or
    -not $mainWindowSource.Contains('Key.D4 => ("customer-enrichment", CustomerEnrichmentButton)')) {
  $customerEnrichmentContractFailures += 'shell_routing'
}
if (-not $guideCatalogSource.Contains('"customer-enrichment"') -or
    -not $guideCatalogSource.Contains('["customer-enrichment"] = ModuleGuideVersion + 1') -or
    -not $guideCatalogSource.Contains('"customer-enrichment", "OPEN WEB INTELLIGENCE", "客户外部调查使用手册"') -or
    -not $guideCatalogSource.Contains('Ctrl+1 至 Ctrl+9')) {
  $customerEnrichmentContractFailures += 'guide_coverage'
}
if (-not $domainModelsSource.Contains('public const string CustomerEnrichment = "customer_operations.customer_enrichment";') -or
    $domainModelsSource -notmatch 'Configurable\s*=\s*\[[\s\S]*?CustomerEnrichment' -or
    -not $customerEnrichmentAnalyzerSource.Contains('CompleteStructuredWithAttemptLimitAsync<CustomerEnrichmentAnalysisResult>') -or
    -not $customerEnrichmentAnalyzerSource.Contains('AiModuleKeys.CustomerEnrichment') -or
    -not $customerEnrichmentAnalyzerSource.Contains('maximumAttempts: 2') -or
    -not $customerEnrichmentServiceSource.Contains('_analyzer = new CustomerEnrichmentAnalyzer(deepSeek);') -or
    -not $customerEnrichmentServiceSource.Contains('_analyzer.AnalyzeAsync(identity, sources, cancellationToken)') -or
    -not $appServicesSource.Contains('public CustomerEnrichmentService CustomerEnrichment { get; }') -or
    -not $appServicesSource.Contains('CustomerEnrichment = new CustomerEnrichmentService(')) {
  $customerEnrichmentContractFailures += 'module_ai_routing'
}
if (-not $customerEnrichmentXaml.Contains('AutomationProperties.Name="客户外部调查"') -or
    -not $customerEnrichmentSource.Contains('_enrichmentService = services.CustomerEnrichment;') -or
    -not $customerEnrichmentRepositorySource.Contains('customer_enrichment_fact_sources')) {
  $customerEnrichmentContractFailures += 'page_backend_binding'
}
$commandCenterIndex = $mainWindowXaml.IndexOf('Text="COMMAND CENTER"', [StringComparison]::Ordinal)
$customersIndex = $mainWindowXaml.IndexOf('x:Name="CustomersButton"', [StringComparison]::Ordinal)
$customerOperationsIndex = $mainWindowXaml.IndexOf('Text="CUSTOMER OPERATIONS"', [StringComparison]::Ordinal)
$customerEnrichmentIndex = $mainWindowXaml.IndexOf('x:Name="CustomerEnrichmentButton"', [StringComparison]::Ordinal)
$insightsIndex = $mainWindowXaml.IndexOf('Text="INSIGHTS"', [StringComparison]::Ordinal)
if ($commandCenterIndex -lt 0 -or $customersIndex -le $commandCenterIndex -or $customerOperationsIndex -le $customersIndex -or
    $customerEnrichmentIndex -le $customerOperationsIndex -or $insightsIndex -le $customerEnrichmentIndex -or
    -not $mainWindowXaml.Contains('Text="看板" Style="{StaticResource NavText}"') -or
    -not $mainWindowXaml.Contains('Text="WhatsApp" Style="{StaticResource NavText}"') -or
    -not $mainWindowXaml.Contains('Text="邮件箱" Style="{StaticResource NavText}"') -or
    -not $dashboardXaml.Contains('AutomationProperties.Name="看板"') -or
    -not $whatsAppInboxXaml.Contains('AutomationProperties.Name="WhatsApp"') -or
    -not $emailInboxXaml.Contains('AutomationProperties.Name="邮件箱"') -or
    -not $guideCatalogSource.Contains('"customers", "COMMAND CENTER", "客户列表使用手册"') -or
    -not $guideCatalogSource.Contains('"inbox", "CONVERSATION INTELLIGENCE", "WhatsApp 使用手册"') -or
    -not $guideCatalogSource.Contains('"email", "CUSTOMER EMAIL INTELLIGENCE", "邮件箱使用手册"')) {
  $customerEnrichmentContractFailures += 'sidebar_grouping_and_visible_names'
}
$enrichmentAdvancedIndex = $settingsXaml.IndexOf('Header="高级选项（通常无需修改）"', [StringComparison]::Ordinal)
$enrichmentReservationIndex = $settingsXaml.IndexOf('x:Name="EnrichmentAiReservationBox"', [StringComparison]::Ordinal)
if (-not $settingsXaml.Contains('x:Name="EnrichmentSettingsSection"') -or
    -not $settingsXaml.Contains('x:Name="EnrichmentAllowAiAnalysisBox"') -or
    -not $settingsXaml.Contains('x:Name="EnrichmentBudgetBox"') -or
    -not $settingsXaml.Contains('x:Name="EnrichmentAllowPaidBox"') -or
    -not $settingsXaml.Contains('Text="本地月度估算提醒额度 USD"') -or
    $enrichmentAdvancedIndex -lt 0 -or $enrichmentReservationIndex -le $enrichmentAdvancedIndex -or
    -not $settingsSource.Contains('bool focusCustomerEnrichment = false') -or
    -not $settingsSource.Contains('EnrichmentSettingsSection.BringIntoView()') -or
    -not $settingsSource.Contains('填写一个联网搜索密钥即可收集公开来源；需要生成事实时，再启用 AI 并设置本程序的月度估算提醒额度。') -or
    -not $guideCatalogSource.Contains('本地估算只统计本机调用，不含账号在其他工具中的用量，实际账单以 Provider 为准')) {
  $customerEnrichmentContractFailures += 'novice_setup_and_honest_local_estimate'
}
if (-not $customerEnrichmentRepositorySource.Contains('GetCustomerEnrichmentQueueSummariesAsync') -or
    -not $customerEnrichmentRepositorySource.Contains("'job' AS row_kind") -or
    -not $customerEnrichmentRepositorySource.Contains('latestCurrentJob') -or
    -not $customerEnrichmentRepositorySource.Contains('CustomerEnrichmentJob? LatestHistoricalJob') -or
    -not $customerEnrichmentRepositorySource.Contains('LatestJob is null && HasHistoricalJob') -or
    -not $customerEnrichmentSource.Contains('来源已保存 · 待启用 AI 整理') -or
    -not $customerEnrichmentSource.Contains('来源已保存 · 待完成 AI 对接') -or
    -not $customerEnrichmentSource.Contains('完成 AI API 对接，并为“客户外部调查”选择可用模型')) {
  $customerEnrichmentContractFailures += 'batch_queue_current_identity_summary'
}
if ($whatsAppInboxSource -notmatch 'if \(brain\.HasCurrentDecision\)[\s\S]{0,500}?AiSidebarProfileText' -or
    $emailInboxSource -notmatch 'if \(brain\.HasCurrentDecision\)[\s\S]{0,500}?AiSidebarProfileText' -or
    $leadIntelligenceSource -notmatch 'if \(brain\.HasCurrentDecision\)[\s\S]{0,500}?ProfileText' -or
    -not $whatsAppInboxSource.Contains('打开客户详情，点击“AI 分析并生成行动”') -or
    -not $emailInboxSource.Contains('打开客户详情，点击“AI 分析并生成行动”') -or
    -not $leadIntelligenceSource.Contains('打开客户详情，点击“AI 分析并生成行动”')) {
  $customerEnrichmentContractFailures += 'stale_brain_ui_gate'
}
if ($customerEnrichmentContractFailures.Count -gt 0) {
  throw "Customer external enrichment must have complete sidebar, guide, page/backend and module-AI routing. missing=$($customerEnrichmentContractFailures -join ',')"
}
Write-Host 'PASS  customer external enrichment sidebar, guide, page/backend and module-AI routing contract'
$customerEnrichmentUsageGateFailures = @()
if ($customerSearchProviderSource -notmatch [regex]::Escape('Interlocked.Increment(ref _lastAttemptCount);') -or
    $customerEnrichmentServiceSource -notmatch [regex]::Escape('var actualAttempts = GetActualAttempts(provider, reservedAttempts);') -or
    $customerEnrichmentServiceSource -notmatch [regex]::Escape('usage.Requests = actualAttempts;') -or
    $customerEnrichmentServiceSource -notmatch [regex]::Escape('EstimateSearchCost(provider.Id, settings, currentUsage, actualAttempts)')) {
  $customerEnrichmentUsageGateFailures += 'actual_attempt_metering'
}
if ($customerEnrichmentServiceSource -notmatch 'SaveCustomerEnrichmentUsageAsync\(usage, cancellationToken\);[\s\S]{0,900}?provider\.SearchAsync' -or
    $customerEnrichmentRepositorySource -notmatch 'ON CONFLICT\(id\) DO UPDATE SET[\s\S]{0,500}?requests=excluded\.requests' -or
    $customerEnrichmentRepositorySource -notmatch 'request_month < \$currentMonth') {
  $customerEnrichmentUsageGateFailures += 'durable_reservation_and_month_ledger'
}
if ($customerEnrichmentServiceSource -notmatch '!settings\.AllowAiAnalysisRequests[\s\S]{0,180}?settings\.MonthlyBudgetUsd <= 0' -or
    $customerEnrichmentServiceSource -notmatch 'AiAnalysisPaymentNotAuthorized[\s\S]{0,1200}?return;[\s\S]{0,3000}?_analyzer\.AnalyzeAsync') {
  $customerEnrichmentUsageGateFailures += 'ai_payment_authorization'
}
if ($customerEnrichmentRepositorySource -notmatch '_timeProvider\.GetLocalNow\(\)' -or
    $customerEnrichmentRepositorySource -notmatch 'TimeZoneInfo\.ConvertTime\(usage\.CreatedAt, _timeProvider\.LocalTimeZone\)' -or
    $customerEnrichmentRepositorySource -notmatch 'TimeZoneInfo\.ConvertTime\(item\.CreatedAt, _timeProvider\.LocalTimeZone\)\.Date == now\.Date') {
  $customerEnrichmentUsageGateFailures += 'user_local_usage_period'
}
if ($customerEnrichmentServiceSource -notmatch 'RequestState\.Equals\("reserved"[\s\S]{0,500}?CustomerEnrichmentJobStatus\.Failed[\s\S]{0,500}?RecoveryReviewRequired[\s\S]{0,500}?continue;' -or
    $customerEnrichmentServiceSource -notmatch 'ResolveReviewedJobStatus\(reviewedFacts\)[\s\S]{0,500}?SaveCustomerEnrichmentJobAsync\(job, cancellationToken\)') {
  $customerEnrichmentUsageGateFailures += 'restart_and_review_status'
}
if (-not $customerExternalFactPolicySource.Contains('GetDisplaySelectionRank(fact, now)') -or
    -not $customerExternalFactPolicySource.Contains('HumanConfirmed when current => 600') -or
    -not $customerExternalFactPolicySource.Contains('Verified when current => 500') -or
    -not $customerExternalFactPolicySource.Contains('PossibleMatch => 300')) {
  $customerEnrichmentUsageGateFailures += 'verified_fact_precedence'
}
if ($customerEnrichmentAnalyzerSource -notmatch 'Where\(source => ContainsEvidence\(source, fact\.EvidenceQuote\)\)' -or
    $customerEnrichmentAnalyzerSource -notmatch '!result\.EntityMatch\.Status\.Equals\("verified"[\s\S]{0,180}?result\.EntityMatch\.Conflicts\.Count > 0[\s\S]{0,120}?Math\.Min\(result\.EntityMatch\.Score, 89\)') {
  $customerEnrichmentUsageGateFailures += 'evidence_source_and_entity_gate'
}
if ($customerEnrichmentServiceSource -notmatch 'EditAndConfirm:[\s\S]{0,700}?fact\.SourceIds = \[\];[\s\S]{0,120}?fact\.EvidenceQuote = "";' -or
    $customerEnrichmentServiceSource -notmatch 'fact\.HumanReviewId = review\.Id' -or
    $customerEnrichmentRepositorySource -notmatch 'DELETE FROM customer_enrichment_fact_sources WHERE fact_id=\$fact') {
  $customerEnrichmentUsageGateFailures += 'human_edit_provenance'
}
if ($customerEnrichmentRepositorySource -notmatch 'DELETE FROM customer_intelligence_profiles WHERE customer_id=\$customer' -or
    $customerBrainSource -notmatch 'HasStaleExternalFactDependencies\(latestReportCandidate, verifiedExternalFacts\)' -or
    $customerAnalysisSource -notmatch 'EnsureExternalFactsStillCurrentAsync\(snapshot, cancellationToken\)' -or
    $customerAnalysisSource -notmatch 'report\.Status = CustomerReportStatus\.RetryableFailed' -or
    $customerExternalFactPolicySource -notmatch 'GetCurrentIdentityHashAsync[\s\S]{0,1800}?capturedIdentityHash\.Equals\(currentIdentityHash' -or
    $customerExternalFactPolicySource -notmatch 'report\.Status = CustomerReportStatus\.Stale' -or
    $customerReportExportSource -notmatch 'CustomerAnalysisFreshness\.GetCurrentAsync') {
  $customerEnrichmentUsageGateFailures += 'brain_and_report_invalidation'
}
if ($customerEnrichmentUsageGateFailures.Count -gt 0) {
  throw "Customer external enrichment usage guards must preserve pre-network local estimates, actual-attempt metering, user-local monthly ledgers, AI authorization, safe restart and review status. missing=$($customerEnrichmentUsageGateFailures -join ',')"
}
Write-Host 'PASS  customer external enrichment local-estimate, reservation, restart and review guard contract'
if ($mainWindowXaml -match 'x:Name="ThemeButton"' -or
    $mainWindowXaml -match 'Click="CommandButton_Click"' -or
    $mainWindowXaml -match '<Button Content="设置"' -or
    $mainWindowXaml -notmatch 'x:Name="ProviderBadge"' -or
    $mainWindowXaml -notmatch 'x:Name="PageGuideButton"' -or
    $mainWindowXaml -notmatch 'x:Name="VersionButton"[\s\S]*?Visibility="Collapsed"' -or
    $settingsXaml -notmatch 'Content="版本与更新"[\s\S]*?Click="VersionHistory_Click"') {
  throw 'The shell must show only Settings at the sidebar footer and only AI status plus the page guide at top-right; version/update remains inside Settings.'
}
if ($mainWindowSource -match 'Foreground\s*=\s*\(Brush\)FindResource\("SidebarText"\)' -or
    $mainWindowSource -notmatch [regex]::Escape('SetResourceReference(Button.ForegroundProperty, "SidebarText")') -or
    $mainWindowSource -notmatch [regex]::Escape('ProviderText.SetResourceReference(TextBlock.ForegroundProperty')) {
  throw 'Theme-sensitive shell foregrounds must keep dynamic resource references instead of caching a light or dark brush instance.'
}
$overlaySidebarFailures = @()
if ($mainWindowXaml -notmatch '<ColumnDefinition Width="60"/>\s*<ColumnDefinition Width="\*"/>') { $overlaySidebarFailures += 'stable_60px_content_offset' }
if ($mainWindowXaml -notmatch 'x:Name="SidebarHost"[\s\S]*?Grid\.ColumnSpan="2"[\s\S]*?Panel\.ZIndex="40"[\s\S]*?Width="60"') { $overlaySidebarFailures += 'overlay_host' }
if ($mainWindowXaml -notmatch 'MouseEnter="SidebarHost_MouseEnter"' -or
    $mainWindowXaml -notmatch 'MouseLeave="SidebarHost_MouseLeave"' -or
    $mainWindowXaml -notmatch 'PreviewMouseDown="SidebarHost_PreviewMouseDown"' -or
    $mainWindowXaml -notmatch 'PreviewMouseUp="SidebarHost_PreviewMouseUp"') { $overlaySidebarFailures += 'hover_hit_area' }
if ($mainWindowXaml -notmatch 'GotKeyboardFocus="SidebarHost_GotKeyboardFocus"' -or $mainWindowXaml -notmatch 'LostKeyboardFocus="SidebarHost_LostKeyboardFocus"') { $overlaySidebarFailures += 'keyboard_expansion' }
if ($appXaml -notmatch 'x:Key="SidebarSectionLabel"[\s\S]*?TextWrapping" Value="NoWrap"' -or
    $appXaml -notmatch 'x:Key="NavText"[\s\S]*?TextWrapping" Value="NoWrap"') { $overlaySidebarFailures += 'no_wrap_reveal_text' }
if ($mainWindowSource -notmatch [regex]::Escape('SidebarCollapsedWidth = 60') -or
    $mainWindowSource -notmatch [regex]::Escape('SidebarExpandedWidth = 240') -or
    $mainWindowSource -notmatch [regex]::Escape('SidebarExpandDuration = TimeSpan.FromMilliseconds(240)') -or
    $mainWindowSource -notmatch [regex]::Escape('SidebarCollapseDuration = TimeSpan.FromMilliseconds(220)') -or
    $mainWindowSource -notmatch [regex]::Escape('TimeSpan.FromMilliseconds(48)') -or
    $mainWindowSource -notmatch [regex]::Escape('SystemParameters.ClientAreaAnimation') -or
    $mainWindowSource -notmatch [regex]::Escape('new SineEase { EasingMode = EasingMode.EaseInOut }') -or
    $mainWindowSource -notmatch [regex]::Escape('HandoffBehavior.SnapshotAndReplace') -or
    $mainWindowSource -notmatch [regex]::Escape('animatable.BeginAnimation(property, null)') -or
    $mainWindowSource -notmatch [regex]::Escape('DropShadowEffect.OpacityProperty') -or
    $mainWindowSource -notmatch [regex]::Escape('_sidebarFocusFromPointer') -or
    $mainWindowSource -notmatch [regex]::Escape('_sidebarKeyboardExpanded') -or
    $mainWindowSource -notmatch [regex]::Escape('_sidebarKeyboardNavigationPending') -or
    $mainWindowSource -notmatch [regex]::Escape('programmatic focus restoration, not keyboard navigation') -or
    $mainWindowSource -notmatch [regex]::Escape('UpdateSidebarExpansionState()')) { $overlaySidebarFailures += 'motion_contract' }
if ($mainWindowSource -match 'ColumnDefinition.*Width\s*=' -or $mainWindowSource -match 'GridLength\(') { $overlaySidebarFailures += 'content_reflow' }
if ($overlaySidebarFailures.Count -gt 0) {
  throw "Hover overlay sidebar must expand from 60px to 240px without changing the product canvas, with delayed text, keyboard support and reduced-motion fallback. missing=$($overlaySidebarFailures -join ',')"
}
Write-Host 'PASS  60px hover-to-expand overlay sidebar without product-canvas reflow'

$pageViewportFailures = @()
if ($appXaml -notmatch 'x:Key="PassivePageViewport"[\s\S]*?TargetType="ScrollViewer"' -or
    $appXaml -notmatch 'x:Key="PassivePageViewport"[\s\S]*?<Setter Property="IsTabStop" Value="False"/>' -or
    $appXaml -notmatch 'x:Key="PassivePageViewport"[\s\S]*?<Setter Property="FocusVisualStyle" Value="\{x:Null\}"/>' -or
    $dashboardXaml -notmatch '<ScrollViewer Style="\{StaticResource PassivePageViewport\}"') {
  $pageViewportFailures += 'dashboard_passive_viewport'
}
if ($pageViewportFailures.Count -gt 0) {
  throw "Passive page viewports must never draw WPF's full-canvas dotted focus rectangle. missing=$($pageViewportFailures -join ',')"
}
Write-Host 'PASS  passive Dashboard viewport without full-canvas dotted focus rectangle'

$motionFailures = @()
if ($appXaml -match 'TargetName="NavRoot" Property="BorderBrush"' -or
    $appXaml -match 'TargetName="NavRoot" Property="BorderThickness"') { $motionFailures += 'nav_perimeter_border' }
if ($appXaml -notmatch 'x:Name="ActiveSurface"[\s\S]*?Background="\{DynamicResource SidebarActive\}"' -or
    $appXaml -notmatch 'x:Name="ActiveRail"[\s\S]*?Background="\{DynamicResource Primary\}"' -or
    $appXaml -match 'x:Name="FocusDash"' -or
    $appXaml -notmatch 'x:Name="FocusSurface"[\s\S]*?Background="\{DynamicResource SidebarHover\}"' -or
    $appXaml -notmatch 'x:Name="FocusRail"[\s\S]*?Background="\{DynamicResource Primary\}"' -or
    $appXaml -notmatch 'Property="local:MotionAssist.IsSelected"') { $motionFailures += 'borderless_nav_states' }
if ($appXaml -notmatch '<Setter Property="local:MotionAssist.IsEnabled" Value="True"/>' -or
    $motionAssistSource -notmatch [regex]::Escape('HandoffBehavior.SnapshotAndReplace') -or
    $motionAssistSource -notmatch [regex]::Escape('SystemParameters.ClientAreaAnimation') -or
    $motionAssistSource -notmatch [regex]::Escape('SineEase { EasingMode = EasingMode.EaseOut }') -or
    $motionAssistSource -notmatch [regex]::Escape('target.BeginAnimation(property, null)')) { $motionFailures += 'interruptible_button_motion' }
if ($mainWindowXaml -notmatch 'x:Name="ContentHostTranslate"' -or
    $mainWindowSource -notmatch [regex]::Escape('TimeSpan.FromMilliseconds(95)') -or
    $mainWindowSource -notmatch [regex]::Escape('TimeSpan.FromMilliseconds(255)') -or
    $mainWindowSource -notmatch [regex]::Escape('_navigationMotionCancellation?.Cancel()')) { $motionFailures += 'page_transition' }
if ($mainWindowXaml -notmatch 'x:Name="CommandPanelScale"' -or
    $mainWindowXaml -notmatch 'x:Name="CommandPanelTranslate"' -or
    $mainWindowSource -notmatch [regex]::Escape('RestoreCommandOverlayFocus()') -or
    $mainWindowSource -notmatch [regex]::Escape('TimeSpan.FromMilliseconds(245)')) { $motionFailures += 'command_overlay_motion' }
if ($motionFailures.Count -gt 0) {
  throw "Shell motion must be smooth, interruptible, reduced-motion aware, and keep navigation selection borderless. missing=$($motionFailures -join ',')"
}
Write-Host 'PASS  borderless navigation selection and interruption-safe shell motion system'
if ($domainModelsSource -notmatch 'enum WhatsAppRegistrationStatus' -or
    $domainModelsSource -notmatch 'WhatsAppRegistrationStatus\.Registered when WhatsAppRegistrationMatchesCurrentPhone => "有效"' -or
    $domainModelsSource -notmatch 'WhatsAppRegistrationStatus\.NotRegistered when WhatsAppRegistrationMatchesCurrentPhone => "无效"' -or
    $importServiceSource -notmatch 'lead\.QueueWhatsAppRegistrationCheck\(\)' -or
    $whatsAppNumberValidationSource -notmatch 'LookupRegistrationAsync' -or
    $whatsAppNumberValidationSource -notmatch 'WhatsAppRegistrationStatus\.RetryableFailed' -or
    $whatsAppNumberValidationSource -notmatch 'TimeSpan\.FromMilliseconds\(900\)' -or
    $whatsAppManagerSource -notmatch 'LookupRegistrationAsync' -or
    $customersXaml -notmatch 'Header="WhatsApp 状态"') {
  throw 'Spreadsheet imports must queue rate-limited real WhatsApp registration checks and only explicit provider results may become valid or invalid.'
}
if ($dashboardXaml -notmatch 'Text="\{Binding CustomerLabel\}"' -or
    $dashboardXaml -notmatch 'Text="\{Binding ActionLabel\}"' -or
    $dashboardXaml -notmatch 'Text="\{Binding ReasonLabel\}"' -or
    $todayBriefSource -notmatch [regex]::Escape('CustomerName = customerName') -or
    $todayBriefSource -match [regex]::Escape('CustomerName = customerId') -or
    $todayBriefSource -notmatch [regex]::Escape('digits[^4..]')) {
  throw 'Today Brief must show readable customer identity, explicit next action and supporting reason instead of internal buyer IDs.'
}
$buyerIdentityContracts = [ordered]@{
  domain_field = $domainModelsSource.Contains('public string BuyerId { get; set; } = "";')
  import_field = $importModelsSource.Contains('Ignore, Custom, BuyerId, Name')
  import_priority = $importServiceSource.Contains('byBuyerId.TryGetValue(buyerKey')
  conflict_guard = $importServiceSource.Contains('BuyerIdentity.Resolve(duplicate)') -and
    $importServiceSource.Contains('duplicate = null;')
  identity_api = $localRepositorySource.Contains('GetLeadByIdentityAsync')
  buyer_lookup = $localRepositorySource.Contains('GetLeadsByBuyerIdAsync')
  canonical_key = $buyerIdentitySource.Contains('return $"buyer:{buyerId}"')
  whatsapp_resolution = $customerIdentitySource.Contains('CustomerIdentityMatchMethod.ExactBuyerId')
  edit_field = $customerEditXaml.Contains('x:Name="BuyerIdBox"')
  edit_guard = $customerEditSource.Contains('IsBuyerIdAvailableAsync')
}
$missingBuyerIdentityContracts = $buyerIdentityContracts.GetEnumerator() |
  Where-Object { -not $_.Value } |
  ForEach-Object { $_.Key }
if ($missingBuyerIdentityContracts.Count -gt 0) {
  throw "Buyer ID must be the authoritative cross-module customer identity, with phone fallback and conflict-safe writes. missing=$($missingBuyerIdentityContracts -join ',')"
}
Write-Host 'PASS  Buyer ID primary identity, phone fallback and cross-module memory contract'

if ($customersXaml -match 'TagFilterBox|OwnerFilterBox|CustomValueFilterBox' -or
    $customersXaml -notmatch 'x:Name="PageSizeBox"' -or
    $customersXaml -notmatch 'x:Name="PreviousPageButton"' -or
    $customersXaml -notmatch 'x:Name="NextPageButton"' -or
    $customersXaml -notmatch 'x:Name="SelectAllCheckBox"' -or
    $customersSource -notmatch 'new PageSizeOption\([^,]+,\s*10\)' -or
    $customersSource -notmatch 'new PageSizeOption\([^,]+,\s*30\)' -or
    $customersSource -notmatch 'new PageSizeOption\([^,]+,\s*50\)' -or
    $customersSource -notmatch [regex]::Escape('Skip(startIndex).Take(_pageSize)') -or
    $customersSource -match [regex]::Escape('条 / 页') -or
    $customersXaml -notmatch 'x:Name="PageSizeBox" Width="112"' -or
    $customersXaml -notmatch 'x:Key="CustomerScrollBar"' -or
    $customersXaml -notmatch 'BasedOn="\{StaticResource CustomerScrollBar\}"' -or
    $customersSource -notmatch [regex]::Escape('CustomerDimensionCatalog.Build(leads)') -or
    $customerDimensionSource -notmatch [regex]::Escape('ImportService.IsCoreDimension(sourceKey)') -or
    $customersSource -notmatch [regex]::Escape('ImportService.ResolveField(header) == ImportField.Name') -or
    $customersXaml -notmatch 'Header="客户 ID" Binding="\{Binding BuyerId\}"' -or
    $customersXaml -notmatch 'Header="公司" Binding="\{Binding Company\}"' -or
    $customersXaml -notmatch 'Header="邮箱" Binding="\{Binding Email\}"' -or
    $customersXaml -notmatch 'Header="WhatsApp 状态" Binding="\{Binding PhoneState\}"[^>]*Width="128"' -or
    $customersSource -notmatch [regex]::Escape('nameof(CustomerRow.BuyerId) => row.BuyerId') -or
    $customersSource -notmatch [regex]::Escape('nameof(CustomerRow.Company) => row.Company') -or
    $customersSource -notmatch [regex]::Escape('nameof(CustomerRow.Email) => row.Email')) {
  throw 'Customer list must remove advanced text filters, merge canonical source aliases into system fields, and paginate by 10, 30 or 50 rows.'
}
Write-Host 'PASS  complete customer identity dimensions, readable WhatsApp status, Buyer Nickname merge and 10/30/50 pagination contract'

if ($leadIntelligenceXaml -match 'DataGridTextColumn Header="公司"' -or
    $leadIntelligenceXaml -notmatch 'DataGridTextColumn Header="客户"' -or
    $leadIntelligenceXaml -notmatch 'DataGridTextColumn Header="市场"') {
  throw 'Lead Intelligence opportunity queue must omit the company column while retaining customer and market dimensions.'
}
Write-Host 'PASS  Lead Intelligence compact opportunity dimensions without company column'

if ($campaignsXaml -notmatch 'x:Name="AudienceFilterActionsRow"' -or
    $campaignsXaml -notmatch 'Grid\.Column="6"\s+Orientation="Horizontal"' -or
    $campaignsXaml -notmatch 'Click="SelectAllEligible_Click"' -or
    $campaignsXaml -notmatch 'Click="ClearSelection_Click"' -or
    $leadIntelligenceXaml -notmatch 'x:Name="PageSizeBox"[\s\S]*?Width="112"' -or
    $leadIntelligenceXaml -notmatch 'x:Name="PreviousPageButton"' -or
    $leadIntelligenceXaml -notmatch 'x:Name="NextPageButton"' -or
    $leadIntelligenceSource -notmatch 'new PageSizeOption\([^,]+,\s*10\)' -or
    $leadIntelligenceSource -notmatch 'new PageSizeOption\([^,]+,\s*30\)' -or
    $leadIntelligenceSource -notmatch 'new PageSizeOption\([^,]+,\s*50\)' -or
    $leadIntelligenceSource -notmatch [regex]::Escape('Skip(startIndex).Take(_pageSize)')) {
  throw 'Campaign audience actions must share the filter row and Lead Intelligence must paginate by 10, 30 or 50 rows.'
}
Write-Host 'PASS  compact campaign audience actions and Lead Intelligence 10/30/50 pagination contract'

if ($todayBriefSource -match '"identity"' -or
    $todayBriefSource -match 'identityPending' -or
    $dashboardSource -match '身份确认' -or
    $customerBrainModelsSource -match 'IdentityPendingCount') {
  throw 'Dashboard Today Brief must not create or count identity-confirmation work; unmatched Inbox contacts wait for manual CRM creation.'
}
Write-Host 'PASS  Today Brief known-customer workflow without identity-confirmation tasks'

if ($dashboardXaml -notmatch 'x:Name="UnreadDigestItems"' -or
    $dashboardXaml -notmatch 'Text="\{Binding ChannelLabel\}"' -or
    $dashboardXaml -notmatch 'Text="\{Binding SuggestedAction, StringFormat=下一步：\{0\}\}"' -or
    $dashboardXaml -match 'x:Name="WhatsAppUnreadText"' -or
    $dashboardXaml -match 'x:Name="EmailUnreadText"' -or
    $dashboardXaml -match 'x:Name="UnreadDigestStatusText"' -or
    $dashboardXaml -match 'x:Name="UnreadDigestModelText"' -or
    $dashboardXaml -match 'x:Name="UnreadDigestCoverageText"' -or
    $dashboardSource -match 'Dashboard 模型未配置' -or
    $dashboardSource -notmatch [regex]::Escape('_services.DashboardUnreadDigest.GetAsync(forceRefresh)') -or
    $dashboardSource -notmatch [regex]::Escape('NotifyUnreadChanged()') -or
    $dashboardUnreadDigestSource -notmatch [regex]::Escape('AiModuleKeys.Dashboard') -or
    $dashboardUnreadDigestSource -notmatch [regex]::Escape('QueueBackgroundRefresh()') -or
    $dashboardUnreadDigestSource -notmatch [regex]::Escape('GetDashboardUnreadDigestCacheAsync') -or
    $deepSeekSource -notmatch [regex]::Escape('SelectLowestTierModel') -or
    $mainWindowSource -notmatch [regex]::Escape('_services.DashboardUnreadDigest.QueueBackgroundRefresh()') -or
    $localRepositorySource -notmatch [regex]::Escape('GetDashboardUnreadSnapshotAsync') -or
    $localRepositorySource -notmatch [regex]::Escape('LastReadAt') -or
    $settingsSource -match [regex]::Escape('Command Center · Dashboard') -or
    $guideCatalogSource -notmatch [regex]::Escape('最低配模型')) {
  throw 'Dashboard Today Brief must merge WhatsApp and email unread originals into one source-labelled list, hide counters/model status, and refresh through the cached lowest-tier background AI route.'
}
Write-Host 'PASS  merged cached Dashboard unread digest with silent lowest-tier background AI routing'

if ($customerDimensionSource -notmatch [regex]::Escape('RemoveDuplicateSuffix(visibleLabel)') -or
    $customerDimensionSource -notmatch [regex]::Escape('$"未命名维度 {unnamedOrdinal}"') -or
    $customerDimensionSource -notmatch [regex]::Escape('NormalizeForStorage') -or
    $customersSource -notmatch [regex]::Escape('CustomerDimensionCatalog.ResolveValue(fields, dimension)') -or
    $importServiceSource -notmatch [regex]::Escape('$"未命名列 {index + 1}"')) {
  throw 'Customer dynamic columns must merge equivalent visible headers and replace blank or invisible headers with stable labels.'
}
Write-Host 'PASS  customer dynamic-column deduplication and nonblank-header contract'

$customerEditCountryFailures = @()
if ($customerEditXaml.Contains('{StaticResource AiLine}')) { $customerEditCountryFailures += 'undefined_ai_line' }
if (-not $customerEditXaml.Contains('BorderBrush="{DynamicResource AiAccent}"')) { $customerEditCountryFailures += 'dynamic_ai_border' }
if ($customersXaml -notmatch 'Header="[^"]+" Binding="\{Binding Country\}"') { $customerEditCountryFailures += 'customer_country_header' }
if ($customersXaml -match 'Header="[^"]*/[^"]*" Binding="\{Binding Country\}"') { $customerEditCountryFailures += 'legacy_customer_country_header' }
if ($emailInboxXaml -match 'Text="[^"]*/[^"]+"[^>]*?/>\s*<TextBox x:Name="CountryBox"') { $customerEditCountryFailures += 'legacy_email_country_label' }
if ($customerEditXaml -match 'Text="[^"]*/[^"]+"[^>]*?/>\s*<TextBox x:Name="CountryBox"') { $customerEditCountryFailures += 'legacy_editor_country_label' }
if (-not $importServiceSource.Contains('"countryemail"')) { $customerEditCountryFailures += 'country_email_alias' }
if (-not $customerEditSource.Contains('ImportService.IsCoreDimension(pair.Key)')) { $customerEditCountryFailures += 'canonical_dimension_filter' }
if ($customerEditCountryFailures.Count -gt 0) {
  throw "Customer editing must load only defined theme resources and unify country aliases into one canonical country field. missing=$($customerEditCountryFailures -join ',')"
}
Write-Host 'PASS  customer editor theme resource and canonical country-field contract'

if ($importServiceSource -notmatch [regex]::Escape('MergeCustomDimensions(lead, customValues, isNew)') -or
    $importServiceSource -match [regex]::Escape('lead.CustomFields.Clear()') -or
    $importServiceSource -notmatch [regex]::Escape('SetExact(ImportField.Name, x => lead.Name = x);') -or
    $importServiceSource -notmatch [regex]::Escape('SetExact(ImportField.Notes, x => lead.ManualNotes = x);') -or
    $guideCatalogSource -notmatch [regex]::Escape('["customers"] = ModuleGuideVersion + 6')) {
  throw 'Spreadsheet reimports must merge by Buyer ID: update present columns, add new dimensions and preserve absent old dimensions.'
}
Write-Host 'PASS  Buyer ID incremental spreadsheet-merge contract'

if ($mainWindowSource -notmatch [regex]::Escape('_updates.StartMonitoring();') -or
    $mainWindowSource -notmatch [regex]::Escape('ApplyAndRestart();') -or
    $mainWindowSource -notmatch [regex]::Escape('已下载 · 点击更新并重启') -or
    $mainWindowSource -match [regex]::Escape('现在关闭 AI Sales OS、安装更新并自动重启吗？') -or
    $mainWindowSource -match [regex]::Escape('MessageBoxButton.YesNo') -or
    $updateStateSource -notmatch 'IAsyncDisposable' -or
    $updateStateSource -notmatch [regex]::Escape('void StartMonitoring();') -or
    $updateServiceSource -notmatch [regex]::Escape('TimeSpan.FromMinutes(2)') -or
    $updateServiceSource -notmatch [regex]::Escape('private async Task MonitorAsync') -or
    $updateServiceSource -notmatch [regex]::Escape('await CheckAndDownloadAsync(cancellationToken: cancellationToken);') -or
    $updateServiceSource -notmatch [regex]::Escape('if (versionComparison <= 0)') -or
    $appStartupSource -notmatch [regex]::Escape('DisposeForExit("updates", () => Updates.DisposeAsync())')) {
  throw 'The app must continuously monitor GitHub Releases, download only newer versions, expose a direct update-and-restart action and stop the monitor cleanly.'
}
Write-Host 'PASS  continuous GitHub update monitoring, automatic download and one-click restart contract'

if ($customerDimensionSource -notmatch [regex]::Escape('PrimaryCategoryPreferenceLabel = "一级品类偏好"') -or
    $customerDimensionSource -notmatch [regex]::Escape('ResolvePrimaryCategoryPreference') -or
    $customersXaml -notmatch 'x:Name="CategoryPreferenceFilter"' -or
    $customersXaml -notmatch 'Header="一级品类偏好" Binding="\{Binding PrimaryCategoryPreference\}"' -or
    $customersSource -notmatch [regex]::Escape('Where(dimension => !CustomerDimensionCatalog.IsPrimaryCategoryPreference(dimension))') -or
    $customersSource -notmatch [regex]::Escape('CustomerDimensionCatalog.ResolvePrimaryCategoryPreference(lead)') -or
    $campaignsXaml -notmatch 'x:Name="CustomerCategoryPreferenceFilterBox"' -or
    $campaignsXaml -notmatch 'Header="一级品类偏好" Binding="\{Binding PrimaryCategoryPreference\}"' -or
    $campaignsSource -notmatch [regex]::Escape('row.PrimaryCategoryPreference.Equals(category') -or
    $guideCatalogSource -notmatch [regex]::Escape('["broadcast"] = ModuleGuideVersion + 2')) {
  throw 'Customer List and Automation Campaigns must share one primary-category preference resolver, column and filter without duplicating the dynamic source field.'
}
Write-Host 'PASS  shared primary-category preference display and filtering contract'

if (-not ($leadAutomationSource.Contains('public event EventHandler<LeadBulkAnalysisProgress>? BulkAnalysisProgressChanged;')) -or
    -not ($leadAutomationSource.Contains('public bool IsBulkAnalysisRunning')) -or
    -not ($leadAutomationSource.Contains('public LeadBulkAnalysisProgress? CurrentBulkProgress')) -or
    -not ($leadAutomationSource.Contains('private void PublishBulkProgress')) -or
    -not ($leadIntelligenceSource.Contains('BulkAnalyzeButton.Content = $"使用 {model} 模型分析";')) -or
    $leadIntelligenceSource.Contains('位分析失败客户') -or
    $leadIntelligenceSource.Contains('位未完成客户') -or
    -not ($leadIntelligenceSource.Contains('if (TryRestoreActiveBulkProgress()) return;')) -or
    -not ($leadIntelligenceSource.Contains('private bool TryRestoreActiveBulkProgress()')) -or
    -not ($leadIntelligenceSource.Contains('private void ApplyBulkProgress(LeadBulkAnalysisProgress progress)')) -or
    -not ($leadIntelligenceSource.Contains('_services.LeadAutomation.CurrentBulkProgress')) -or
    -not ($dashboardSource.Contains('BulkAnalysisProgressChanged += LeadAutomation_BulkAnalysisProgressChanged')) -or
    -not ($dashboardSource.Contains('private void ApplyAnalysisCoverage(LeadBulkAnalysisProgress progress)')) -or
    -not ($dashboardSource.Contains('_services.LeadAutomation.CurrentBulkProgress')) -or
    -not ($dashboardXaml.Contains('AutomationProperties.Name="AI 分析进度"')) -or
    -not ($guideCatalogSource.Contains('["intelligence"] = ModuleGuideVersion + 5'))) {
  throw 'Lead Intelligence and Dashboard must share one live bulk-progress snapshot, while the model action keeps a stable label without retry or remaining-customer counts.'
}
Write-Host 'PASS  shared Lead Intelligence and Dashboard live-progress truth source with stable model-action text contract'

if (-not ($leadIntelligenceXaml.Contains('x:Name="OpportunityImportButton"')) -or
    -not ($leadIntelligenceXaml.Contains('x:Name="OpportunitySignalFilter"')) -or
    -not ($leadIntelligenceXaml.Contains('x:Name="OpportunityActivityFilter"')) -or
    -not ($leadIntelligenceXaml.Contains('x:Name="OpportunityCategoryFilter"')) -or
    -not ($leadIntelligenceXaml.Contains('x:Name="OpportunityAmountFilter"')) -or
    -not ($opportunityImportModelsSource.Contains('class OpportunityImportPreview')) -or
    -not ($opportunityImportServiceSource.Contains('BuildPreviewAsync')) -or
    -not ($opportunityImportServiceSource.Contains('CommitAsync')) -or
    -not ($opportunityImportServiceSource.Contains('ConflictBuyerIds')) -or
    -not ($opportunityImportServiceSource.Contains('BuildSnapshot')) -or
    -not ($opportunityImportXaml.Contains('AutomationProperties.Name="商机补充数据导入"')) -or
    -not ($opportunityImportXaml.Contains('Text="选择商机补充数据工作簿"')) -or
    -not ($opportunityImportSource.Contains('Title = "选择商机补充数据工作簿"')) -or
    $opportunityImportXaml.Contains('Myybiz') -or
    $opportunityImportSource.Contains('Myybiz') -or
    $releaseCatalogSource.Contains('Myybiz') -or
    -not ($opportunityImportXaml.Contains('重复交易零写入')) -or
    -not ($opportunityImportSource.Contains('BrowseButton.IsEnabled = !busy;'))) {
  throw 'Opportunity supplement import must preserve the exact-Buyer-ID preview/commit contract, local evidence filters, deduplication and accessible non-reentrant UI.'
}
Write-Host 'PASS  opportunity supplement import whitelist, evidence and accessible preview contract'

function Convert-HexToRgb([string]$hex) {
  $value = $hex.TrimStart('#')
  return @(
    [Convert]::ToInt32($value.Substring(0, 2), 16),
    [Convert]::ToInt32($value.Substring(2, 2), 16),
    [Convert]::ToInt32($value.Substring(4, 2), 16)
  )
}
function Get-RelativeLuminance([string]$hex) {
  $linear = Convert-HexToRgb $hex | ForEach-Object {
    $channel = $_ / 255.0
    if ($channel -le 0.04045) { $channel / 12.92 } else { [Math]::Pow(($channel + 0.055) / 1.055, 2.4) }
  }
  return 0.2126 * $linear[0] + 0.7152 * $linear[1] + 0.0722 * $linear[2]
}
function Get-ContrastRatio([string]$foreground, [string]$background) {
  $first = Get-RelativeLuminance $foreground
  $second = Get-RelativeLuminance $background
  $lighter = [Math]::Max($first, $second)
  $darker = [Math]::Min($first, $second)
  return ($lighter + 0.05) / ($darker + 0.05)
}
$lightPalette = @{}
$darkPalette = @{}
[regex]::Matches($themeSource, '\["(?<key>[^"]+)"\]\s*=\s*\("(?<light>#[0-9A-Fa-f]{6})",\s*"(?<dark>#[0-9A-Fa-f]{6})"\)') |
  ForEach-Object {
    $lightPalette[$_.Groups['key'].Value] = $_.Groups['light'].Value
    $darkPalette[$_.Groups['key'].Value] = $_.Groups['dark'].Value
  }
$contrastPairs = @(
  @('Ink', 'Canvas'), @('Ink', 'Surface'), @('Ink', 'SurfaceElevated'),
  @('Ink', 'SurfaceMuted'), @('Ink', 'AiSurface'), @('InkSecondary', 'Surface'),
  @('InkSecondary', 'SurfaceElevated'), @('Muted', 'Canvas'), @('Muted', 'Surface'),
  @('Muted', 'SurfaceElevated'), @('Warning', 'WarningSoft'), @('Danger', 'DangerSoft'),
  @('OnPrimary', 'Primary'), @('OnAi', 'AiAccent'), @('OnDanger', 'Danger'),
  @('SidebarText', 'Sidebar'), @('SidebarText', 'SidebarActive'),
  @('SidebarMuted', 'Sidebar'), @('SidebarMuted', 'SidebarElevated'),
  @('UnreadBadgeText', 'UnreadBadgeBackground')
)
foreach ($paletteEntry in @(@('Light', $lightPalette), @('Dark', $darkPalette))) {
  $mode = $paletteEntry[0]
  $palette = $paletteEntry[1]
  foreach ($pair in $contrastPairs) {
    $ratio = Get-ContrastRatio $palette[$pair[0]] $palette[$pair[1]]
    if ($ratio -lt 4.5) {
      throw "$mode theme contrast is below WCAG AA for $($pair[0]) on $($pair[1]): $([Math]::Round($ratio, 2)):1"
    }
  }
}
if ($mainWindowSource -notmatch [regex]::Escape('RefreshShellThemeState();') -or
    $mainWindowSource -notmatch [regex]::Escape('await UpdateProviderStateAsync();') -or
    $mainWindowSource -notmatch 'if \(ContentHost\.Content is IRefreshableView (?:view|currentView)\)') {
  throw 'Theme switching must refresh code-assigned shell badges, active navigation and the current page in addition to DynamicResource values.'
}
if ($appXaml -notmatch 'ContentPresenter[\s\S]*?TextElement\.Foreground="\{Binding Foreground,\s*RelativeSource=\{RelativeSource TemplatedParent\}\}"' -or
    $appXaml -notmatch 'ContentPresenter\.Resources[\s\S]*?TargetType="\{x:Type TextBlock\}"[\s\S]*?AncestorType=\{x:Type Button\}' -or
    $appXaml -notmatch 'ContentPresenter\.Resources[\s\S]*?TargetType="\{x:Type AccessText\}"[\s\S]*?AncestorType=\{x:Type Button\}') {
  throw 'The shared button template must forward Button.Foreground so semantic on-color tokens reach string content in every theme.'
}
Write-Host 'PASS  light/dark-theme dynamic resources, aligned vector navigation icons and high-contrast text contract'

if ($appStartupSource -notmatch [regex]::Escape('DesktopShortcutService.EnsureForInstalledApp();') -or
    $desktopShortcutSource -notmatch [regex]::Escape('ShortcutLocation.Desktop') -or
    $desktopShortcutSource -notmatch [regex]::Escape('ShortcutLocation.StartMenuRoot') -or
    $desktopShortcutSource -notmatch [regex]::Escape('VelopackLocator.IsCurrentSet') -or
    $desktopShortcutSource -notmatch [regex]::Escape('updateOnly: false') -or
    $desktopShortcutSource -notmatch [regex]::Escape('WScript.Shell') -or
    $desktopShortcutSource -notmatch [regex]::Escape('Environment.SpecialFolder.DesktopDirectory') -or
    $desktopShortcutSource -notmatch [regex]::Escape('Environment.SpecialFolder.Programs') -or
    $desktopShortcutSource -notmatch [regex]::Escape('File.Exists(shortcutPath)') -or
    $windowsInstallerTestSource -notmatch [regex]::Escape('$shortcutBackups[$shortcutPath] = [IO.File]::ReadAllBytes($shortcutPath)') -or
    $windowsInstallerTestSource -notmatch [regex]::Escape('[IO.File]::WriteAllBytes($shortcutPath, $shortcutBackups[$shortcutPath])') -or
    $windowsInstallerTestSource -notmatch [regex]::Escape('ShortcutsVerified = $shortcutTargets.Count -eq 2') -or
    $velopackBuildSource -notmatch [regex]::Escape("--shortcuts 'Desktop,StartMenuRoot'")) {
  throw 'Windows install/update must recreate and verify shortcuts, while isolated QA restores the real user links.'
}
Write-Host 'PASS  Velopack plus verified Windows-native post-update shortcut repair contract'
if ($appStartupSource -notmatch [regex]::Escape('LocalUpdateCacheMaintenance.Run();') -or
    $updateServiceSource -notmatch [regex]::Escape('LocalUpdateCacheMaintenance.Run();') -or
    $updateCacheRetentionSource -notmatch [regex]::Escape('public const int RollbackVersionLimit = 3;') -or
    $updateCacheRetentionSource -notmatch [regex]::Escape('package.Version >= installedVersion || rollbackVersions.Contains(package.Version)') -or
    $updateCacheMaintenanceSource -notmatch [regex]::Escape('Path.Combine(locator.PackagesDir, "VelopackTemp")') -or
    $updateCacheMaintenanceSource -notmatch [regex]::Escape('Path.Combine(Path.GetTempPath(), "AI Sales OS Updates")') -or
    $trimUpdateCacheSource -notmatch [regex]::Escape('[int]$RollbackVersionLimit = 3') -or
    $velopackBuildSource -notmatch [regex]::Escape("scripts\trim-local-update-cache.ps1")) {
  throw 'Every update must prune only recognized update caches while retaining the current package and three rollback versions.'
}
Write-Host 'PASS  installed, portable and local-build update-cache retention contract'
if ($domainModelsSource -notmatch [regex]::Escape('public int UiScalePercentage { get; set; } = 100;') -or
    $uiScaleSource -notmatch [regex]::Escape('SupportedPercentages = [80, 90, 100, 110, 125]') -or
    $uiScaleHostSource -notmatch 'class UiScaleHost : Decorator' -or
    $mainWindowXaml -notmatch 'x:Name="MainScaleHost"' -or
    $settingsXaml -notmatch 'x:Name="SettingsScaleHost"' -or
    $settingsXaml -notmatch 'x:Name="UiScaleBox"' -or
    $mainWindowXaml -notmatch 'x:Name="SettingsButton"' -or
    $mainWindowXaml -notmatch '<TextBlock Grid.Column="1" Text="设置" Style="\{StaticResource NavText\}"' -or
    $mainWindowSource -notmatch [regex]::Escape('ApplyUiScale(settings.UiScalePercentage);') -or
    $settingsSource -notmatch [regex]::Escape('_settings.UiScalePercentage = UiScaleManager.Normalize(') -or
    $settingsSource -match [regex]::Escape('throw new InvalidOperationException("当前 Provider 的 API Key 未通过验证。")')) {
  throw 'Unified Settings must persist and apply 80-125% UI scaling without requiring an API key.'
}
Write-Host 'PASS  unified sidebar Settings and persisted responsive UI scaling contract'
if ($velopackBuildSource -notmatch [regex]::Escape('[Text.UTF8Encoding]::new($true)') -or
    $velopackBuildSource -notmatch [regex]::Escape('NotesMarkdown.Contains([char]0xFFFD)')) {
  throw 'Velopack release notes must be BOM-marked UTF-8 and reject replacement characters before publishing.'
}
Write-Host 'PASS  Velopack Chinese release-notes encoding contract'

$profileTextMatch = [regex]::Match(
  $whatsAppInboxXaml,
  '<TextBlock\s+x:Name="AiSidebarProfileText"(?<attributes>[\s\S]*?)/>'
)
if (-not $profileTextMatch.Success) {
  throw 'WhatsApp Inbox AI Sales Brief profile text control is missing.'
}
$profileTextAttributes = $profileTextMatch.Groups['attributes'].Value
if ($profileTextAttributes -notmatch 'TextWrapping="Wrap"' -or
    $profileTextAttributes -match 'MaxHeight=' -or
    $profileTextAttributes -match 'TextTrimming="CharacterEllipsis"') {
  throw 'WhatsApp Inbox AI Sales Brief must show the full customer profile without fixed-height or ellipsis clipping.'
}
if ($whatsAppInboxXaml -match 'Margin="0,108,0,0"') {
  throw 'WhatsApp Inbox AI Sales Brief next action must use adaptive rows instead of a fixed overlay margin.'
}
Write-Host 'PASS  WhatsApp Inbox AI Sales Brief adaptive full-text layout contract'

if ($mainWindowXaml -notmatch 'Tag="knowledge"' -or
    $knowledgeBaseXaml -notmatch 'x:Name="UploadButton"' -or
    $knowledgeBaseXaml -notmatch 'x:Name="RetrievalQueryBox"' -or
    $knowledgeBaseXaml -notmatch 'x:Name="ConflictGrid"' -or
    $knowledgeBaseSource -notmatch [regex]::Escape('ResolveConflictAsync') -or
    $knowledgeServiceSource -notmatch [regex]::Escape('MaximumFileSize = 50L * 1024 * 1024') -or
    $knowledgeServiceSource -notmatch [regex]::Escape('ValidateOfficeArchive') -or
    $knowledgeServiceSource -notmatch [regex]::Escape('SourceKind == KnowledgeSourceKind.AiDraft') -or
    $knowledgeModelsSource -notmatch 'KnowledgeScopeKind[\s\S]*?Global[\s\S]*?Account[\s\S]*?Customer[\s\S]*?Conversation[\s\S]*?Temporary' -or
    $localRepositorySource -notmatch 'CREATE TABLE IF NOT EXISTS knowledge_documents' -or
    $localRepositorySource -notmatch 'CREATE TABLE IF NOT EXISTS knowledge_retrieval_logs') {
  throw 'Knowledge Base must expose governed upload/review/search/conflict UI, immutable local storage, five strict scopes and durable retrieval audit.'
}
Write-Host 'PASS  Knowledge Base governed RAG, scope and audit contract'

if ($bridgeSource -notmatch 'receiptBelongsToPhone' -or
    $bridgeSource -notmatch 'targetVerified:\s*true' -or
    $bridgeSource -notmatch 'whatsapp_target_mismatch' -or
    $bridgeSource -notmatch 'whatsapp_server_message_id_missing') {
  throw 'WhatsApp bridge must verify the recipient and require a server message id before confirming a send.'
}

if ($bridgeConnectionBootstrapSource -notmatch 'version_lookup_timeout' -or
    $bridgeConnectionBootstrapSource -notmatch "cachedVersion \? 'cached' : 'bundled'" -or
    $bridgeSource -notmatch 'fetchLatestWaWebVersion' -or
    $bridgeSource -notmatch "Browsers\.windows\('Chrome'\)" -or
    $bridgeSource -notmatch 'desktopUpgradeRequested' -or
    $bridgeSource -notmatch 'desktop_history_upgrade' -or
    $bridgeSource -notmatch 'QR_GENERATION_WATCHDOG_MS' -or
    $bridgeSource -notmatch 'qr_generation_timeout' -or
    $bridgeSource -notmatch 'qr_render_failed' -or
    $bridgeSource -notmatch 'state\.socket !== socket' -or
    $whatsAppBridgeClientSource -notmatch 'LatestQrDataUrl' -or
    $whatsAppBridgeClientSource -notmatch 'TimeSpan\.FromSeconds\(95\)' -or
    $whatsAppBridgeClientSource -notmatch 'qr_generation_timeout' -or
    $whatsAppManagerSource -match 'ConnectionState is "connected" or "connecting"' -or
    $whatsAppInboxXaml -notmatch 'x:Name="QrProgressBar"' -or
    $whatsAppInboxXaml -notmatch 'AutomationProperties\.Name="WhatsApp 登录二维码"' -or
    $whatsAppInboxSource -notmatch 'connection_stage' -or
    $whatsAppInboxSource -notmatch 'connection_issue' -or
    $whatsAppInboxSource -notmatch 'RestoreLatestQr') {
  throw 'WhatsApp first-connect QR must use bounded protocol discovery, bundled fallback, automatic retry, cached QR restoration and visible inline status.'
}
& $node (Join-Path $root 'bridge\scripts\connection-bootstrap-smoke.mjs')
if ($LASTEXITCODE -ne 0) { throw 'WhatsApp connection-bootstrap smoke test failed.' }
Write-Host 'PASS  WhatsApp first-connect QR timeout, fallback, retry and visible recovery contract'

if ($bridgeSource -notmatch "case 'catch_up_history'" -or
    $bridgeSource -notmatch 'receivedPendingNotifications' -or
    $bridgeSource -notmatch "connect\('reconnect'\)" -or
    $bridgeSource -notmatch "Browsers\.macOS\('Desktop'\)" -or
    $bridgeSource -notmatch 'desktop-history-profile\.json' -or
    $bridgeSource -notmatch 'offline_history_profile' -or
    $bridgeSource -notmatch 'requiresHistoryRepair' -or
    $bridgeSource -notmatch 'fetchMessageHistory' -or
    $bridgeSource -notmatch 'groupFetchAllParticipating' -or
    $bridgeOfflineCatchupSource -notmatch "sendPresenceUpdate\('available'\)" -or
    $bridgeOfflineCatchupSource -notmatch "sendPresenceUpdate\('unavailable'\)" -or
    $whatsAppBridgeClientSource -notmatch 'CatchUpHistoryAsync' -or
    $whatsAppBridgeClientSource -notmatch 'WhatsAppHistoryCursor' -or
    $whatsAppManagerSource -notmatch 'CatchUpHistoryAsync' -or
    $messagingSyncSource -notmatch 'BuildHistoryCursors' -or
    $messagingSyncSource -notmatch [regex]::Escape('CatchUpHistoryAsync(account.Id, cursors') -or
    $whatsAppInboxSource -notmatch '正在重新连接并补齐离线期间的新消息' -or
    $whatsAppInboxSource -match '_automaticSyncRequested') {
  throw 'WhatsApp must request Desktop full history, discover groups and recover each persisted chat gap after reconnect instead of refreshing cached contacts only.'
}
& $node (Join-Path $root 'bridge\scripts\offline-catchup-smoke.mjs')
if ($LASTEXITCODE -ne 0) { throw 'WhatsApp offline-message catch-up smoke test failed.' }
& $node (Join-Path $root 'bridge\scripts\history-recovery-smoke.mjs')
if ($LASTEXITCODE -ne 0) { throw 'WhatsApp per-chat history recovery smoke test failed.' }
Write-Host 'PASS  WhatsApp startup, Desktop history and per-chat offline-message recovery contract'

if ($networkProxyResolverSource -notmatch 'WAFLOW_PROXY_URL' -or
    $networkProxyResolverSource -notmatch 'WebRequest\.DefaultWebProxy' -or
    $networkProxyResolverSource -notmatch 'AllowDirectFallback' -or
    $emailServiceSource -notmatch 'ConnectImapAsync' -or
    $emailServiceSource -notmatch 'ConnectionRoutes' -or
    $whatsAppBridgeClientSource -notmatch 'proxyUrl = networkRoute\.ProxyUrl' -or
    $bridgeNetworkRoutingSource -notmatch 'HttpsProxyAgent' -or
    $bridgeNetworkRoutingSource -notmatch 'SocksProxyAgent' -or
    $bridgeSource -notmatch 'proxy_route_timeout' -or
    $emailInboxXaml -notmatch 'x:Name="EmailSyncStatusText"' -or
    $emailInboxSource -match '"邮件暂时无法同步"') {
  throw 'New-computer Gmail and WhatsApp connections must inherit Windows/environment proxies, fall back to direct access and report recovery inline.'
}
& $node (Join-Path $root 'bridge\scripts\network-routing-smoke.mjs')
if ($LASTEXITCODE -ne 0) { throw 'Network proxy routing smoke test failed.' }
if ($bridgeSource -notmatch [regex]::Escape('state.connectionGeneration !== generation || state.socket !== socket || state.manualDisconnect') -or
    $bridgeSource -notmatch 'credentials_save_failed' -or
    $whatsAppBridgeClientSource -notmatch 'public async Task ResetForPairingAsync' -or
    $whatsAppBridgeClientSource -notmatch '(?s)finally\s*\{\s*//.*?await ResetForPairingAsync\(\);\s*\}' -or
    $whatsAppManagerSource -notmatch 'error\.Code == "logged_out"' -or
    $whatsAppManagerSource -notmatch '(?s)await client\.ResetForPairingAsync\(\);\s*await client\.StartAsync\(accountId, cancellationToken\);\s*return await client\.ConnectAsync\(cancellationToken\);') {
  throw 'WhatsApp logout must terminate the old bridge, block late credential rewrites and recover a remotely unlinked device into fresh QR pairing in the same click.'
}
& $node (Join-Path $root 'bridge\scripts\connection-lifecycle-smoke.mjs')
if ($LASTEXITCODE -ne 0) { throw 'WhatsApp connection-lifecycle smoke test failed.' }
Write-Host 'PASS  Gmail and WhatsApp Windows proxy inheritance, forced session reset, fresh QR pairing and non-blocking recovery contract'

if ($bridgeOutboundRoutingSource -notmatch 'jidNormalizedUser' -or
    $bridgeSource -notmatch [regex]::Escape('getUSyncDevices([senderIdentity, targetJid], false, false)') -or
    $bridgeSource -notmatch 'senderDeviceSyncPrepared:\s*true' -or
    $bridgeSource -notmatch 'whatsapp_sender_device_sync_unavailable') {
  throw 'WhatsApp outbound sends must strip device-qualified JIDs and refresh sender/recipient devices before customer delivery.'
}
& $node (Join-Path $root 'bridge\scripts\outbound-routing-smoke.mjs')
if ($LASTEXITCODE -ne 0) { throw 'WhatsApp bridge outbound-routing smoke test failed.' }
Write-Host 'PASS  WhatsApp sender multi-device synchronization contract'

$groupContractFailures = @()
if ($bridgeConversationRoutingSource -notmatch 'normalizeGroupJid') { $groupContractFailures += 'group JID normalizer' }
if ($bridgeConversationRoutingSource -notmatch [regex]::Escape('@g.us')) { $groupContractFailures += 'group JID server' }
if ($bridgeSource -notmatch [regex]::Escape('resolveConversationJid')) { $groupContractFailures += 'inbound conversation resolver' }
if ($bridgeSource -notmatch 'isGroup:\s*true') { $groupContractFailures += 'group chat normalization' }
if ($bridgeSource -notmatch 'participantName') { $groupContractFailures += 'participant attribution' }
if ($whatsAppSyncSource -notmatch 'GetWhatsAppConversationByIdAsync') { $groupContractFailures += 'group persistence lookup' }
if ($whatsAppSyncSource -notmatch [regex]::Escape('IsGroup = isGroup')) { $groupContractFailures += 'group persistence flag' }
if ($customerSuccessSource -notmatch [regex]::Escape('if (message.IsGroup) return;')) { $groupContractFailures += 'agent isolation' }
if ($whatsAppInboxXaml -notmatch [regex]::Escape('Text="{Binding SenderLabel}"')) { $groupContractFailures += 'member label UI' }
if ($whatsAppInboxSource -notmatch [regex]::Escape('ChatModeBadgeText.Text = conversation.IsGroup ? "GROUP VIEW"') -or
    $whatsAppInboxSource -notmatch [regex]::Escape('Customer Brain')) { $groupContractFailures += 'group safety explanation' }
if ($groupContractFailures.Count -gt 0) {
  throw "WhatsApp groups must synchronize as durable unread conversations with participant labels and strict CRM/AI isolation. missing=$($groupContractFailures -join ', ')"
}
& $node (Join-Path $root 'bridge\scripts\conversation-routing-smoke.mjs')
if ($LASTEXITCODE -ne 0) { throw 'WhatsApp bridge conversation-routing smoke test failed.' }
Write-Host 'PASS  WhatsApp group receive, unread, participant attribution and CRM/AI isolation contract'

if ($whatsAppInboxSource -notmatch 'string\.IsNullOrWhiteSpace\(id\)' -or
    $whatsAppInboxSource -notmatch '!Bool\(result, "targetVerified"\)' -or
    $whatsAppInboxSource -notmatch 'WhatsAppMessageStatus\.Pending') {
  throw 'WhatsApp Inbox must keep unconfirmed sends pending instead of inventing a successful message.'
}

if ($whatsAppSyncSource -notmatch 'WhatsAppMessageStatus\.Pending' -or
    $campaignAutomationSource -notmatch 'target_not_verified' -or
    $customerSuccessSource -notmatch 'customer_success_auto_reply_pending') {
  throw 'All WhatsApp sending paths must share the real acknowledgement and target-verification contract.'
}

if ($customerSuccessSource -match 'holding-\{Guid\.NewGuid') {
  throw 'Customer Success Agent must not invent a provider message id for an unconfirmed send.'
}

Write-Host 'PASS  WhatsApp real-send acknowledgement contract'

if ($customerSuccessSource -notmatch 'requestedMode == ConversationAgentMode\.CopilotActive' -or
    $customerSuccessSource -notmatch 'CustomerSuccessRunStatus\.CopilotDraftReady' -or
    $customerSuccessSource -notmatch 'RaiseRunCompleted') {
  throw 'Customer Success Agent copilot mode must auto-generate a review draft and publish a visible completion event without entering the send path.'
}
foreach ($requiredControl in @(
  'AgentModeGuideTitleText',
  'AgentModeTriggerText',
  'AgentModeOutputText',
  'AgentModeSendText',
  'AgentRunStatusText',
  'AgentRunReplyText',
  'GenerateAgentSuggestionButton',
  'UseAgentDraftButton'
)) {
  if ($whatsAppInboxXaml -notmatch [regex]::Escape("x:Name=`"$requiredControl`"")) {
    throw "WhatsApp Inbox agent-mode explanation or output control is missing: $requiredControl"
  }
}
if ($whatsAppInboxSource -notmatch 'AgentModeCombo_SelectionChanged' -or
    $whatsAppInboxSource -notmatch 'CustomerSuccessCoordinator_RunCompleted' -or
    $whatsAppInboxSource -notmatch 'UseAgentDraft_Click') {
  throw 'WhatsApp Inbox must explain each agent mode, refresh background output and let the user place review drafts into the composer.'
}
if ($whatsAppInboxXaml -match 'LinkedAccountsText|AccountRelationshipText|关联账号：|本账号关系：' -or
    $whatsAppInboxSource -match 'LinkedAccountsText|AccountRelationshipText') {
  throw 'WhatsApp Customer Success card must not expose linked-account, primary-account or per-account relationship internals.'
}
Write-Host 'PASS  Customer Success Agent mode behavior and visible-output contract'

if ($bridgeSource -notmatch [regex]::Escape('proto.WebMessageInfo.StubType.CIPHERTEXT') -or
    $bridgeSource -notmatch [regex]::Escape('update.update?.message') -or
    $bridgeSource -notmatch [regex]::Escape('if (numericStatus == null) continue') -or
    $bridgeMessageSource -notmatch [regex]::Escape('normalizeMessageContent') -or
    $whatsAppInboxSource -match '媒体消息' -or
    $whatsAppInboxSource -notmatch 'ShouldReplaceContentWith') {
  throw 'WhatsApp Inbox must recover ciphertext placeholders, replace stale bubbles and never mislabel empty text as media.'
}
& $node (Join-Path $root 'bridge\scripts\message-content-smoke.mjs')
if ($LASTEXITCODE -ne 0) { throw 'WhatsApp bridge message-content smoke test failed.' }
Write-Host 'PASS  WhatsApp placeholder recovery and accurate message classification contract'

$emailProviderContractTokens = @(
  'EmailProviderGuide',
  'https://myaccount.google.com/apppasswords',
  'Outlook.com',
  'OAuth2 / Modern Auth',
  'https://login.yahoo.com/account/security',
  'https://account.apple.com/account/manage/section/security',
  'SecureSocketOptions.StartTls;'
)
$missingEmailProviderTokens = @($emailProviderContractTokens | Where-Object { -not $emailServiceSource.Contains($_) })
if ($missingEmailProviderTokens.Count -gt 0) {
  throw "Email providers must expose accurate platform-specific setup, credential and encrypted transport guidance. missing=$($missingEmailProviderTokens -join ', ')"
}
if ($emailAccountXaml -notmatch 'x:Name="GuideStepsText"' -or
    $emailAccountXaml -notmatch 'x:Name="ProviderSetupButton"' -or
    $emailAccountXaml -notmatch 'x:Name="PasswordHintText"' -or
    $emailAccountXaml -notmatch 'x:Name="ResetPresetButton"' -or
    -not ($emailAccountSource.Contains('account?.Provider ?? EmailProviderKind.Gmail')) -or
    -not ($emailAccountSource.Contains('UserNameBox.Text = EmailBox.Text.Trim()')) -or
    -not ($emailAccountSource.Contains('ImapHostBox.Clear()')) -or
    -not ($emailAccountSource.Contains('UseShellExecute = true')) -or
    -not ($guideCatalogSource.Contains('["email"] = ModuleGuideVersion + 6'))) {
  throw 'Email account window must provide provider steps, direct official links, field hints, username sync and preset recovery.'
}
Write-Host 'PASS  provider-specific email onboarding and compatibility guidance contract'

if ($windowsCredentialStoreSource -notmatch [regex]::Escape('public bool Exists()') -or
    $emailServiceSource -notmatch [regex]::Escape('HasLocalCredential') -or
    $emailServiceSource -notmatch [regex]::Escape('LocalAuthorizationMessage') -or
    $emailInboxSource -notmatch [regex]::Escape('UpdateSelectedAccountAuthorizationStatus') -or
    $emailAccountSource -notmatch [regex]::Escape('_hasStoredCredential') -or
    $whatsAppManagerSource -notmatch [regex]::Escape('RequiresLocalAuthorization') -or
    $whatsAppManagerSource -notmatch [regex]::Escape('WAFlow/WhatsAppSessionKey/{accountId}') -or
    $whatsAppBridgeClientSource -notmatch [regex]::Escape('PrepareFreshLocalSession') -or
    $whatsAppBridgeClientSource -notmatch [regex]::Escape('local_authorization_required') -or
    $networkProxyResolverSource -notmatch [regex]::Escape('WinHttpGetIEProxyConfigForCurrentUser') -or
    $networkProxyResolverSource -notmatch [regex]::Escape('WinHttpGetProxyForUrl') -or
    -not ($guideCatalogSource.Contains('["inbox"] = ModuleGuideVersion + 8')) -or
    -not ($guideCatalogSource.Contains('["email"] = ModuleGuideVersion + 6'))) {
  throw 'A copied workspace on a new computer must detect missing machine-local Gmail/WhatsApp credentials, require safe reauthorization and inherit Windows PAC/WPAD proxy routing.'
}
Write-Host 'PASS  new-computer credential reauthorization and Windows automatic-proxy contract'

if ($appStartupSource -notmatch [regex]::Escape('Services.MessagingSync.StartAsync()') -or
    $appStartupSource -notmatch [regex]::Escape('Services.MessagingSync.DisposeAsync()') -or
    $appStartupSource -notmatch [regex]::Escape('Services.Email.DisposeAsync()') -or
    $messagingSyncSource -notmatch [regex]::Escape('GetWhatsAppAccountsAsync') -or
    $messagingSyncSource -notmatch [regex]::Escape('EnsureConnectedAsync(account.Id') -or
    $messagingSyncSource -notmatch [regex]::Escape('HasStoredSession(account.Id)') -or
    $whatsAppManagerSource -notmatch [regex]::Escape('IsAutoReconnectEnabled') -or
    $emailServiceSource -notmatch [regex]::Escape('IdleAsync') -or
    $emailServiceSource -notmatch [regex]::Escape('ImapCapabilities.Idle') -or
    $emailServiceSource -notmatch [regex]::Escape('TimeSpan.FromSeconds(30)')) {
  throw 'All saved WhatsApp and email accounts must remain connected and synchronize globally for the application lifetime.'
}
if ($mainWindowXaml -notmatch 'x:Name="WhatsAppUnreadBadge"' -or
    $mainWindowXaml -notmatch 'x:Name="EmailUnreadBadge"' -or
    $mainWindowXaml -notmatch 'DynamicResource UnreadBadgeBackground' -or
    $mainWindowSource -notmatch [regex]::Escape('GetInboxUnreadTotalsAsync') -or
    $mainWindowSource -notmatch [regex]::Escape('_services.WhatsAppSync.MessageSynchronized += MessagingUnreadChanged') -or
    $mainWindowSource -notmatch [regex]::Escape('_services.WhatsAppSync.SynchronizationChanged += WhatsAppSynchronizationChanged') -or
    $mainWindowSource -notmatch [regex]::Escape('Interval = TimeSpan.FromSeconds(5)') -or
    $localRepositorySource -notmatch [regex]::Escape('allowUnreadIncrease') -or
    $whatsAppSyncSource -notmatch [regex]::Escape('allowUnreadIncrease: unreadIncreased') -or
    $whatsAppInboxSource -notmatch [regex]::Escape('FindOwnedPeerAccount') -or
    $mainWindowSource -notmatch [regex]::Escape('_services.Email.SynchronizationChanged += EmailSynchronizationChanged')) {
  throw 'WhatsApp and email sidebar navigation must expose durable live application-wide unread counters with periodic reconciliation.'
}
Write-Host 'PASS  application-wide account synchronization, durable unread cursor and sidebar badge contract'

if ($whatsAppNamingSource -notmatch [regex]::Escape('lead?.DisplayName') -or
    $whatsAppSyncSource -notmatch 'WhatsAppConversationNaming\.Resolve' -or
    $localRepositorySource -notmatch 'WhatsAppConversationNaming\.Resolve' -or
    $whatsAppInboxSource -notmatch 'WhatsAppConversationNaming\.Resolve' -or
    $localRepositorySource -notmatch [regex]::Escape('owners.Count > 0') -or
    $localRepositorySource.Contains('!account.Id.Equals(currentAccountId') -or
    $customerIdentitySource -notmatch [regex]::Escape('ownedPeer.Name')) {
  throw 'WhatsApp conversation naming must prefer the unique CRM phone match and fall back to the native phone remark.'
}
Write-Host 'PASS  WhatsApp conversation CRM-name priority, owned-account guard and phone-remark fallback contract'

if ($whatsAppInboxXaml -notmatch 'x:Name="TranslationBar"' -or
    $whatsAppInboxXaml -notmatch [regex]::Escape('Content="翻译最近消息"') -or
    $whatsAppInboxXaml -notmatch [regex]::Escape('Content="译成客户语言"') -or
    $whatsAppInboxXaml -notmatch [regex]::Escape('Content="采用译文"') -or
    $whatsAppInboxXaml -notmatch [regex]::Escape('Text="{Binding TranslatedText}"') -or
    $whatsAppInboxXaml -notmatch [regex]::Escape('AutomationProperties.LiveSetting="Polite"') -or
    $whatsAppInboxXaml -notmatch [regex]::Escape('VerticalScrollBarVisibility="Auto"') -or
    $whatsAppInboxXaml -match 'x:Name="TranslateDraftButton"[^>]*Focusable="False"' -or
    $whatsAppInboxSource -notmatch [regex]::Escape('_services.WhatsAppTranslation.GetContextAsync') -or
    $whatsAppInboxSource -notmatch [regex]::Escape('_services.WhatsAppTranslation.TranslateRecentMessagesAsync') -or
    $whatsAppInboxSource -notmatch [regex]::Escape('_services.WhatsAppTranslation.TranslateOutgoingAsync') -or
    $whatsAppInboxSource -notmatch [regex]::Escape('"human_translated"') -or
    $whatsAppInboxSource -notmatch [regex]::Escape('"desktop_translated"') -or
    $whatsAppTranslationSource -notmatch [regex]::Escape('CultureInfo.CurrentUICulture') -or
    $whatsAppTranslationSource -notmatch [regex]::Escape('AiModuleKeys.WhatsAppInbox') -or
    $whatsAppTranslationSource -notmatch [regex]::Escape('message.Direction == WhatsAppMessageDirection.Incoming') -or
    $whatsAppTranslationSource -notmatch [regex]::Escape('Where(IsTranslatableMessage)') -or
    $whatsAppTranslationSource -notmatch [regex]::Escape('RecentTranslationLimit = 30') -or
    $whatsAppTranslationSource -notmatch [regex]::Escape('Take(RecentTranslationLimit)') -or
    $whatsAppTranslationSource -notmatch [regex]::Escape('TranslateBatchWithRecoveryAsync') -or
    $whatsAppTranslationSource -notmatch [regex]::Escape('BuildFallbackProfile') -or
    $whatsAppTranslationSource -notmatch [regex]::Escape('GetWhatsAppTranslationStateAsync') -or
    $whatsAppInboxSource -notmatch [regex]::Escape('_translationContextCts') -or
    $whatsAppInboxSource -notmatch [regex]::Escape('_translationRunCts') -or
    $whatsAppInboxSource -notmatch [regex]::Escape('ScrollMessages(conversation)') -or
    $whatsAppInboxSource -notmatch [regex]::Escape('CompleteTranslationRun(cts)') -or
    $whatsAppTranslationModelsSource -notmatch [regex]::Escape('SourceTextHash') -or
    $localRepositorySource -notmatch [regex]::Escape('whatsapp_translation:{conversationId}') -or
    -not ($guideCatalogSource.Contains('["inbox"] = ModuleGuideVersion + 8'))) {
  throw 'WhatsApp Inbox must translate only the latest message window, recover malformed structured output, separate context/run cancellation, cache bilingual translations, preserve originals and require manual adoption plus send.'
}
Write-Host 'PASS  WhatsApp latest-message translation, structured-output recovery, repeat-click state and manual-send contract'

if ($emailInboxXaml -notmatch [regex]::Escape('Visibility="{Binding UnreadVisibility}"') -or
    $emailInboxXaml -notmatch [regex]::Escape('Text="{Binding UnreadLabel}"') -or
    $emailInboxXaml -notmatch [regex]::Escape('DynamicResource PrimarySoft') -or
    $emailInboxSource -notmatch [regex]::Escape('class EmailConversationItem') -or
    $emailInboxSource -notmatch [regex]::Escape('Unread > 99 ? "99+"') -or
    $emailInboxSource -notmatch [regex]::Escape('item.MarkRead(DateTimeOffset.Now)') -or
    $emailInboxSource -notmatch [regex]::Escape('MarkEmailConversationReadAsync(conversation.Id)')) {
  throw 'Email Inbox must show a per-conversation unread badge, cap it at 99+, and clear it immediately with the durable read cursor.'
}
Write-Host 'PASS  Email Inbox per-conversation unread badge and immediate read-state contract'

if ($emailInboxXaml -notmatch 'x:Name="NewEmailButton"' -or
    $emailInboxXaml -notmatch 'x:Name="RecipientBox"' -or
    $emailInboxXaml -notmatch 'x:Name="EmailAiInstructionBox"' -or
    $emailInboxXaml -notmatch 'x:Name="AiSidebarScoreRing"' -or
    $emailInboxSource -notmatch [regex]::Escape('NewEmail_Click') -or
    $emailInboxSource -notmatch [regex]::Escape('_services.EmailAssistant.AnalyzeAsync') -or
    $emailInboxSource -notmatch [regex]::Escape('UseEmailDraft_Click') -or
    $emailAssistantSource -notmatch [regex]::Escape('AiModuleKeys.EmailInbox') -or
    $emailAssistantSource -notmatch [regex]::Escape('userInstruction') -or
    $emailAssistantSource -notmatch [regex]::Escape('CompleteStructuredAsync<EmailAssistantResult>') -or
    $settingsSource -notmatch [regex]::Escape('Email Sales Copilot') -or
    $guideCatalogSource -notmatch [regex]::Escape('Email Sales Copilot') -or
    $guideCatalogSource -notmatch [regex]::Escape('GlobalGuideVersion = 10') -or
    $guideCatalogSource -notmatch 'Ctrl\+1 . Ctrl\+9' -or
    $guideCatalogSource -match 'Ctrl\+1 . Ctrl\+8' -or
    $guideCatalogSource -notmatch [regex]::Escape('["customers"] = ModuleGuideVersion + 6') -or
    $guideCatalogSource -notmatch [regex]::Escape('["customer-enrichment"] = ModuleGuideVersion + 1') -or
    $guideCatalogSource -notmatch [regex]::Escape('["broadcast"] = ModuleGuideVersion + 2') -or
    $guideCatalogSource -notmatch [regex]::Escape('["analytics"] = ModuleGuideVersion + 2') -or
    $guideCatalogSource -notmatch [regex]::Escape('["settings"] = ModuleGuideVersion + 9')) {
  throw 'Email Inbox must support new-message composition, CRM/Customer Brain-aware AI drafting, manual-send safety and current module guidance.'
}
Write-Host 'PASS  Email Inbox new-message, Customer Intelligence, AI draft and all-module guide audit contract'

if ($whatsAppInboxSource -notmatch [regex]::Escape('_conversationSelectionGeneration') -or
    $whatsAppInboxSource -notmatch [regex]::Escape('IsCurrentConversationSelection') -or
    $whatsAppInboxSource -notmatch [regex]::Escape('var sendBinding = resolved.Binding') -or
    $whatsAppInboxSource -notmatch [regex]::Escape('var sendLead = sendBinding.Lead') -or
    $whatsAppInboxSource -notmatch [regex]::Escape('PersistAcknowledgedOutgoingWhatsAppAsync') -or
    $whatsAppInboxSource -notmatch [regex]::Escape('_pendingAgentDraftContextToken') -or
    $whatsAppInboxSource -match [regex]::Escape('string.Equals(ComposerBox.Text.Trim(), _pendingKnowledgeDecision.ReplyText.Trim()') -or
    $whatsAppInboxSource -notmatch [regex]::Escape('expectedCustomerIdentityHash: draftContextToken?.CustomerIdentityHash') -or
    $whatsAppInboxSource -notmatch [regex]::Escape('expectedSourceMessageToken: draftContextToken?.SourceMessageToken') -or
    $whatsAppInboxSource -notmatch [regex]::Escape('accepted = true;') -or
    $emailInboxSource -notmatch [regex]::Escape('IsCurrentEmailTarget') -or
    $emailInboxSource -notmatch [regex]::Escape('IsCurrentEmailDraftTarget') -or
    $emailInboxSource -notmatch [regex]::Escape('var sendLead = await ResolveEmailLeadAsync') -or
    $emailInboxSource -notmatch [regex]::Escape('EmailComposerAiBinding') -or
    $emailInboxSource -notmatch [regex]::Escape('ContextChangedAfterSend') -or
    $emailInboxSource -notmatch [regex]::Escape('expectedCustomerDependencyHash: appliedBinding?.DependencyHash') -or
    $emailInboxSource -notmatch '_appliedEmailAiBinding is not null[\s\S]{0,240}SubjectBox\.Clear\(\);[\s\S]{0,120}ComposerBox\.Clear\(\);' -or
    $emailServiceSource -notmatch [regex]::Escape('ExpectedCustomerDependencyHash') -or
    $emailServiceSource -notmatch [regex]::Escape('binding.ExpectedCustomerDependencyHash') -or
    $emailAssistantSource -notmatch [regex]::Escape('email_assistant_source_changed') -or
    $emailAssistantSource -notmatch [regex]::Escape('EnsureSourceCurrentAsync') -or
    $customerSuccessSource -notmatch [regex]::Escape('EnsureRunContextCurrentAsync') -or
    $customerSuccessSource -notmatch [regex]::Escape('post_send_context_changed') -or
    $customerSuccessSource -notmatch [regex]::Escape('PersistAcknowledgedOutgoingWhatsAppAsync') -or
    $customerSuccessSource -notmatch [regex]::Escape('expectedActiveFactSetToken: result.ContextToken.ActiveFactSetToken') -or
    $customerSuccessSource -notmatch [regex]::Escape('expectedSourceMessageToken: result.ContextToken.SourceMessageToken') -or
    $campaignAutomationSource -notmatch [regex]::Escape('PrepareRecipientForNetworkSend') -or
    $campaignAutomationSource -notmatch [regex]::Escape('recipient.CustomerAttributionIsolated = true') -or
    $localRepositorySource -notmatch [regex]::Escape('TryUpdateConversationAgentRunOutcomeAsync') -or
    $localRepositorySource -notmatch [regex]::Escape('PersistAcknowledgedOutgoingWhatsAppAsync') -or
    $localRepositorySource -notmatch [regex]::Escape('PersistAcknowledgedOutgoingEmailAsync') -or
    $localRepositorySource -notmatch [regex]::Escape('LeadAttributionFinal') -or
    $localRepositorySource -notmatch [regex]::Escape('DeliveryAcknowledged') -or
    $localRepositorySource -notmatch [regex]::Escape('CaptureCustomerExternalFactDependencyAsync') -or
    $localRepositorySource -notmatch [regex]::Escape('expectedCustomerDependencyHash') -or
    $localRepositorySource -notmatch [regex]::Escape('dependencyIsCurrent') -or
    $localRepositorySource -notmatch [regex]::Escape('CustomerSuccessAgentService.BuildSourceMessageToken') -or
    $localRepositorySource -notmatch [regex]::Escape('ApplySynchronizedWhatsAppMessageOutcomeAsync') -or
    $localRepositorySource -notmatch [regex]::Escape('MarkWhatsAppMessageRevokedAsync') -or
    $localRepositorySource -notmatch [regex]::Escape('item.LeadAttributionFinal') -or
    $whatsAppSyncSource -notmatch [regex]::Escape('ResolveConversationLeadAsync') -or
    $whatsAppSyncSource -notmatch [regex]::Escape('ApplySynchronizedWhatsAppMessageOutcomeAsync') -or
    $whatsAppSyncSource -notmatch [regex]::Escape('"whatsapp_message_revoked"') -or
    $localRepositorySource -notmatch [regex]::Escape('db.BeginTransaction(deferred: false)')) {
  throw 'WhatsApp, Email and Customer Success AI results must remain bound to the selected account, conversation, customer identity and current external-fact source before UI apply, send and outcome persistence.'
}
Write-Host 'PASS  WhatsApp, Email and Customer Success stale-target and cross-customer fail-closed contract'

if ($whatsAppInboxXaml -match 'x:Name="CompanyBox"' -or
    $emailInboxXaml -match 'x:Name="CompanyBox"' -or
    $whatsAppInboxXaml -match 'x:Name="OptInSourceBox"' -or
    $whatsAppInboxXaml -notmatch 'x:Name="NotesBox"' -or
    $whatsAppInboxXaml -notmatch 'x:Name="AiContextSummaryText"' -or
    $emailInboxXaml -notmatch 'x:Name="AiContextSummaryText"' -or
    $domainModelsSource -notmatch [regex]::Escape('public string ManualNotes { get; set; }') -or
    $customerBrainModelsSource -notmatch [regex]::Escape('CustomerConversationContext') -or
    $customerBrainSource -notmatch [regex]::Escape('UpdateConversationContextAsync') -or
    $customerBrainSource -notmatch [regex]::Escape('AiModuleKeys.Customers') -or
    $customerAnalysisSource -notmatch [regex]::Escape('_customerBrain.UpdateConversationContextAsync') -or
    $customerAnalysisSource -notmatch [regex]::Escape('ConversationContext = customerBrain?.ConversationContext') -or
    $customerAnalysisSource -notmatch [regex]::Escape('manualNotes = lead.ManualNotes') -or
    -not ($guideCatalogSource.Contains('["inbox"] = ModuleGuideVersion + 8')) -or
    -not ($guideCatalogSource.Contains('["email"] = ModuleGuideVersion + 6'))) {
  throw 'Customer Intelligence must separate manual notes from AI context, remove company/source blanks, update cross-channel context incrementally and feed both into customer analysis.'
}
Write-Host 'PASS  Customer Intelligence manual-note and cross-channel AI-context contract'

$workspaceMigrationFailures = @()
if ($settingsXaml -notmatch 'x:Name="WorkspaceUsageText"' -or
    $settingsXaml -notmatch 'x:Name="WorkspaceStatusText"' -or
    $settingsXaml -notmatch 'x:Name="MoveWorkspaceButton"' -or
    $settingsXaml -notmatch 'Content="打开位置"') {
  $workspaceMigrationFailures += 'settings_controls'
}
if ($settingsSource -notmatch [regex]::Escape('PreviewMigrationAsync') -or
    $settingsSource -notmatch [regex]::Escape('ScheduleMigrationAsync') -or
    $settingsSource -notmatch [regex]::Escape('--wait-for-pid') -or
    $settingsSource -notmatch [regex]::Escape('RequestWorkspaceMigrationShutdown') -or
    $settingsSource -notmatch [regex]::Escape('Application.Current.Shutdown()')) {
  $workspaceMigrationFailures += 'settings_restart_flow'
}
if ($dataWorkspaceSource -notmatch [regex]::Escape('WAFLOW_DATABASE_PATH') -or
    $dataWorkspaceSource -notmatch [regex]::Escape('SHA256.HashDataAsync') -or
    $dataWorkspaceSource -notmatch [regex]::Escape('PRAGMA integrity_check') -or
    $dataWorkspaceSource -notmatch [regex]::Escape('PRAGMA foreign_key_check') -or
    $dataWorkspaceSource -notmatch [regex]::Escape('RewriteInternalWorkspacePathsAsync') -or
    $dataWorkspaceSource -notmatch [regex]::Escape('EnsureWorkspaceDatabaseNotInUse') -or
    $dataWorkspaceSource -notmatch [regex]::Escape('SourceFingerprint') -or
    $dataWorkspaceSource -notmatch [regex]::Escape('DeleteVerifiedSourceWorkspace') -or
    $dataWorkspaceSource -notmatch [regex]::Escape('RollbackAfterStartupFailureAsync')) {
  $workspaceMigrationFailures += 'copy_verify_rollback'
}
if ($appStartupSource -notmatch [regex]::Escape('ApplyPendingMigrationAsync') -or
    $appStartupSource -notmatch [regex]::Escape('AcquireLease') -or
    $appStartupSource -notmatch [regex]::Escape('CompletePendingMigrationAsync') -or
    $appStartupSource -notmatch [regex]::Escape('MigrationShutdownStepTimeout') -or
    $appStartupSource -notmatch [regex]::Escape('IsProcessRunning') -or
    $appStartupSource -notmatch [regex]::Escape('RollbackAfterStartupFailureAsync')) {
  $workspaceMigrationFailures += 'startup_switch'
}
if ($bridgeSource -notmatch [regex]::Escape('WAFLOW_DATA_ROOT') -or
    $bridgeBootstrapSource -notmatch [regex]::Escape('WAFLOW_DATA_ROOT') -or
    $whatsAppBridgeClientSource -notmatch [regex]::Escape('start.Environment["WAFLOW_DATA_ROOT"]')) {
  $workspaceMigrationFailures += 'whatsapp_root'
}
if ($guideCatalogSource -notmatch [regex]::Escape('["settings"] = ModuleGuideVersion + 9') -or
    $guideCatalogSource -notmatch [regex]::Escape('迁移本地数据工作区')) {
  $workspaceMigrationFailures += 'settings_guide'
}
if ($workspaceMigrationFailures.Count -gt 0) {
  throw "Local data workspace migration must expose a recoverable copy, verify, restart, switch and rollback flow. missing=$($workspaceMigrationFailures -join ',')"
}
Write-Host 'PASS  recoverable local data workspace migration and WhatsApp root contract'

$syncInboxBlock = [regex]::Match(
  $emailServiceSource,
  'public async Task<int> SyncInboxAsync.*?public Task StartBackgroundSyncAsync',
  [System.Text.RegularExpressions.RegexOptions]::Singleline).Value
if ([string]::IsNullOrWhiteSpace($syncInboxBlock) -or
    $syncInboxBlock -match 'new ImapClient' -or
    -not $syncInboxBlock.Contains('monitor.RequestSyncAsync(maxMessages') -or
    -not $emailServiceSource.Contains('monitor.AttachWake(idleDone)') -or
    -not $emailServiceSource.Contains('monitor.AttachWake(pollDone)') -or
    -not $emailServiceSource.Contains('monitor.Requeue(requests)')) {
  throw 'Manual Email Inbox sync must reuse and wake the account background IMAP session instead of opening a competing connection.'
}
Write-Host 'PASS  serialized manual/background IMAP synchronization contract'

if ($mainWindowSource.Contains('RefreshAllAsync') -or
    -not $mainWindowSource.Contains('QueueUnreadBadgeRefresh();') -or
    -not $mainWindowSource.Contains('Task.WhenAll(enterTask, refreshTask)') -or
    -not $whatsAppInboxSource.Contains('if (!IsVisible) return;') -or
    -not $whatsAppInboxSource.Contains('await Task.Run(async () =>') -or
    -not $whatsAppInboxSource.Contains('_conversations.ReplaceAll(snapshot.Conversations)') -or
    -not $emailInboxSource.Contains('await Task.Run(async () =>') -or
    -not $emailInboxSource.Contains('_conversations.ReplaceAll(snapshot.Conversations)') -or
    -not $emailInboxSource.Contains('_messages.ReplaceAll(messages)') -or
    -not $emailInboxSource.Contains('Task.Delay(250, debounce.Token)') -or
    -not $batchCollectionSource.Contains('NotifyCollectionChangedAction.Reset')) {
  throw 'Inbox refreshes must remain hidden-page aware, background-loaded, event-coalesced and batch-applied without rebuilding every module.'
}
Write-Host 'PASS  non-blocking hidden-page Inbox refresh, coalescing and batch-update contract'

$aiRoutingUiTokens = @(
  'x:Name="ReasoningEffortBox"',
  'x:Name="UseGlobalAiConfigurationBox"',
  'x:Name="ModuleRoutingItems"',
  'x:Name="AiRoutingSummaryText"',
  'x:Name="ModuleRoutingPanel"'
)
$missingAiRoutingUiTokens = @($aiRoutingUiTokens | Where-Object { -not $settingsXaml.Contains($_) })
if ($missingAiRoutingUiTokens.Count -gt 0) {
  throw "AI settings must expose global/per-module model routing and fail-safe reasoning guidance. missing=$($missingAiRoutingUiTokens -join ', ')"
}
if (($settingsXaml.Split('UpdateSourceTrigger=PropertyChanged').Count - 1) -lt 3 -or
    -not $settingsSource.Contains('CommitPendingModuleSelections(ModuleRoutingItems)') -or
    -not $settingsSource.Contains('AiModulePreferencePersistence.FindMismatches') -or
    -not $settingsSource.Contains('板块模型保存校验失败') -or
    -not $modulePreferencePersistenceSource.Contains('AiModuleKeys.Configurable')) {
  throw 'AI module selectors must commit every row immediately and verify all persisted routes before closing settings.'
}
Write-Host 'PASS  AI module selector commit and all-route persistence verification contract'
$aiRoutingCoreTokens = @(
  'UseGlobalAiConfiguration',
  'DefaultReasoningEffort',
  'AiModulePreferences',
  'ModelCapabilities',
  'supported_efforts',
  'ApplyReasoningEffort',
  'ReasoningEffort == AiReasoningEfforts.Auto'
)
$combinedAiRoutingSource = $domainModelsSource + $deepSeekSource + $settingsSource
$missingAiRoutingCoreTokens = @($aiRoutingCoreTokens | Where-Object { -not $combinedAiRoutingSource.Contains($_) })
if ($missingAiRoutingCoreTokens.Count -gt 0) {
  throw "AI routing must persist module overrides, parse API capabilities and suppress undeclared reasoning parameters. missing=$($missingAiRoutingCoreTokens -join ', ')"
}
if (-not $conversationAssistantSource.Contains('AiModuleKeys.WhatsAppInbox') -or
    -not $customerSuccessAgentSource.Contains('AiModuleKeys.WhatsAppInbox') -or
    -not $dashboardUnreadDigestSource.Contains('AiModuleKeys.Dashboard') -or
    -not $domainModelsSource.Contains('AiModuleKeys') -or
    -not $domainModelsSource.Contains('public const string Dashboard') -or
    -not $domainModelsSource.Contains('public const string LeadIntelligence') -or
    -not $settingsSource.Contains('AiModuleKeys.LeadIntelligence') -or
    -not $deepSeekSource.Contains('ResolveExecutionProfileAsync(AiModuleKeys.LeadIntelligence') -or
    -not $leadAutomationSource.Contains('ResolveExecutionProfileAsync(AiModuleKeys.LeadIntelligence') -or
    -not $customerBrainSource.Contains('AiModuleKeys.Customers') -or
    -not $customerAnalysisSource.Contains('AiModuleKeys.CustomerAnalytics') -or
    -not $knowledgeProcessingSource.Contains('AiModuleKeys.KnowledgeBase')) {
  throw 'Every current AI workload, including Lead Intelligence, must route through its own module key.'
}
Write-Host 'PASS  global/per-module AI model, token-cost and declared reasoning-depth routing contract'

if (-not $aiProviderCatalogSource.Contains('"anthropic", "Anthropic Claude"') -or
    -not $aiProviderCatalogSource.Contains('AiProviderProtocol.AnthropicMessages') -or
    -not $deepSeekSource.Contains('"x-api-key"') -or
    -not $deepSeekSource.Contains('"anthropic-version"') -or
    -not $deepSeekSource.Contains('execution.BaseUrl + "/messages"') -or
    -not $deepSeekSource.Contains('"output_config.effort"') -or
    -not $aiModelCapabilityResolverSource.Contains('ResolveDeepSeek') -or
    -not $aiModelCapabilityResolverSource.Contains('["low", "high", "max"]') -or
    -not $aiModelCapabilityResolverSource.Contains('ResolveOpenAi') -or
    -not $aiModelCapabilityResolverSource.Contains('ResolveAnthropic') -or
    -not $aiModelCapabilityResolverSource.Contains('ResolveGemini') -or
    -not $aiModelCapabilityResolverSource.Contains('ResolveXai') -or
    -not $aiModelCapabilityResolverSource.Contains('ResolveGroq') -or
    -not $aiModelCapabilityResolverSource.Contains('ResolveMistral') -or
    -not $aiModelCapabilityResolverSource.Contains('ResolveZhipu') -or
    -not $aiModelCapabilityResolverSource.Contains('ResolveQwen') -or
    -not $aiModelCapabilityResolverSource.Contains('ResolveTogether') -or
    -not $settingsSource.Contains('自动（官方默认 high）') -or
    -not $settingsSource.Contains('自动（官方默认 max）') -or
    -not $settingsSource.Contains('自动（官方默认 xhigh）') -or
    $settingsSource.Contains('自动（API 未声明档位）')) {
  throw 'Provider settings must combine live capability metadata with official fallbacks and route Anthropic through the native Claude protocol.'
}
Write-Host 'PASS  live-first provider reasoning catalog and native Anthropic Claude protocol contract'

if (-not $leadIntelligenceSource.Contains('ResolveExecutionProfileAsync(AiModuleKeys.LeadIntelligence') -or
    -not $leadIntelligenceSource.Contains('execution.Model') -or
    $leadIntelligenceSource.Contains('settings.DeepSeekModel') -or
    $settingsSource -match 'if \(!string\.IsNullOrWhiteSpace\(activeKey\)\)\s*await _services\.LeadAutomation\.NotifyProviderConfiguredAsync\(\);' -or
    -not $settingsSource.Contains('_ = ResumeQueuedLeadAnalysisAsync()') -or
    $mainWindowSource -notmatch [regex]::Escape('await _intelligence.RefreshAiRouteAsync();') -or
    $mainWindowSource -match 'await UpdateThemeStateAsync\(\);\s*await RefreshAllAsync\(\);') {
  throw 'Saving module AI routes must be non-blocking, and Lead Intelligence must display the same resolved route used by its real requests.'
}
Write-Host 'PASS  non-blocking module-route save and actual Lead Intelligence route display contract'

if ($todayBriefSource.Contains('"cross_account"') -or
    $todayBriefSource.Contains('GetAgentStatesAsync') -or
    -not $todayBriefSource.Contains('CrossAccountFollowUpCount = 0')) {
  throw 'Today Brief must treat one customer appearing in multiple WhatsApp accounts as normal unified customer context, not a follow-up task.'
}
Write-Host 'PASS  unified cross-account customer context without duplicate-responsibility reminders'

& $dotnet build (Join-Path $root 'desktop\WAFlow.sln') -c Release
if ($LASTEXITCODE -ne 0) { throw 'WAFlow desktop build failed.' }
& $dotnet run --project (Join-Path $root 'desktop\WAFlow.SmokeTests\WAFlow.SmokeTests.csproj') -c Release --no-build
if ($LASTEXITCODE -ne 0) { throw 'WAFlow desktop smoke tests failed.' }
