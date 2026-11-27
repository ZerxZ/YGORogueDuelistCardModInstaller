export type Lang = 'zh' | 'en' | (string & {});

export interface ModStats {
  hasLegacyModJson: boolean;
  hasLuaDsl: boolean;
  hasJsDsl: boolean;
  cardCount: number;
  characterCount: number;
  relicCount: number;
  packCount: number;
  preconstructedDeckCount: number;
  scriptCount: number;
  luaLibCount: number;
  jsPluginCount: number;
  cssStyleCount: number;
  deckCount: number;
  artCount: number;
  campaignFileCount: number;
  overrideFileCount: number;
  hasTrainer: boolean;
  overridePaths: string[];
}

export type ModCategoryKey =
  | 'all'
  | 'cards'
  | 'characters'
  | 'relics'
  | 'packs_decks'
  | 'ui_theme'
  | 'script_plugin'
  | 'trainer'
  | 'overhaul'
  | 'other';

export interface ModEntry {
  id: string;
  name: string;
  version: string;
  author: string;
  description: string;
  category: string | null;
  effectiveCategory: string;
  cover: string | null;
  coverUrl: string | null;
  autoDiscoverEntries: boolean;
  resolvedJsEntries: string[];
  resolvedCssEntries: string[];
  resolvedLuaLibs: string[];
  settingsCount: number;
  gameVersion: string | null;
  minGameVersion: string | null;
  maxGameVersion: string | null;
  minManagerVersion: string | null;
  directoryPath: string;
  enabled: boolean;
  order: number;
  tags: string[];
  stats: ModStats;
}

export interface ConflictEntry {
  category: string;
  resourceKey: string;
  modIds: string[];
  winnerModId: string;
}

export interface VersionIssue {
  modId: string;
  modName: string;
  message: string;
}

export interface HotReloadStatus {
  running: boolean;
  port: number;
  url: string | null;
  lanBound?: boolean;
  lanUrls?: string[];
  revision: number;
  lastError: string | null;
}

export interface ManagerStatus {
  ok: boolean;
  apiBaseUrl: string;
  managerVersion: string;
  workspaceRoot: string;
  modsRoot: string;
  installDir: string;
  gameBaseDir: string;
  hasGameExe?: boolean;
  hasWebBundle?: boolean;
  gameInstalled?: boolean;
  picsDir: string;
  repo: string;
  githubProxy: string;
  managerRepo: string;
  mcpUrl: string;
  mcpSseUrl: string;
  mcpToolCount: number;
  isDownloading: boolean;
  activeDownloadLabel: string | null;
  downloadProgress?: {
    active: boolean;
    label: string | null;
    status: string | null;
    percent: number | null;
  };
  activeBaseGameVersion: string | null;
  launcherBuild: number;
  mode: 'VirtualBundle' | 'LauncherManifest';
  developerMode: boolean;
  language: Lang;
  syncState: string;
  syncSummary: string;
  isBusy: boolean;
  hotReload: HotReloadStatus;
  mods: ModEntry[];
  conflicts: ConflictEntry[];
  versionIssues: VersionIssue[];
  installedVirtualVersions: string[];
}

export interface WorkspaceProfile {
  id: string;
  name: string;
  path: string;
  description: string;
  isActive: boolean;
}

export interface SubCardThumb {
  id: number;
  name: string;
  imageUrl: string | null;
}

export interface StudioCardItem {
  key: string;
  id: number;
  name: string;
  zhName: string;
  desc: string;
  zhDesc: string;
  displayType: string;
  race: string;
  attribute: string;
  level: number;
  atk: number;
  def: number;
  typeMask: number;
  raceMask: string;
  attributeMask: number;
  packId: string;
  luaScript: string;
  sourceTag: string;
  sourceFilePath: string | null;
  artLocalPath: string;
  imageUrl: string | null;
}

export interface StudioCharacterItem {
  key: string;
  index: number | null;
  name: string;
  zhName: string;
  sprite: string;
  race: string;
  attribute: string;
  color: string;
  startingRelic: string;
  startingPacks: string[];
  deckCards: string[];
  unlockedByDefault: boolean;
  sourceTag: string;
  sourceFilePath: string | null;
  imageUrl: string | null;
  subCards: SubCardThumb[];
}

export interface StudioRelicItem {
  key: string;
  name: string;
  zhName: string;
  cost: number;
  tag: string;
  description: string;
  zhDescription: string;
  art: string;
  luaEffect: string;
  sourceTag: string;
  sourceFilePath: string | null;
  imageUrl: string | null;
}

export interface StudioPackItem {
  key: string;
  id: string;
  name: string;
  zhName: string;
  code: string;
  size: number;
  commonIds: number[];
  rareIds: number[];
  superIds: number[];
  ultraIds: number[];
  totalCards: number;
  sourceTag: string;
  sourceFilePath: string | null;
  artLocalPath?: string;
  imageUrl: string | null;
  subCards: SubCardThumb[];
}

export interface StudioDeckItem {
  key: string;
  id: string;
  name: string;
  zhName: string;
  art: string;
  artLocalPath?: string;
  cardIds: number[];
  isYdk: boolean;
  sourceTag: string;
  sourceFilePath: string | null;
  imageUrl: string | null;
  subCards: SubCardThumb[];
}

export interface StudioCodeItem {
  key: string;
  path: string;
  ext: string;
}

export interface StudioTrainerState {
  exists: boolean;
  unlockAllCharacters: boolean;
  unlockAllVariants: boolean;
  startingGold: number | null;
  startingGoldBonus: number | null;
  startingLp: number | null;
  freeShop: boolean;
  shopPriceMultiplier: number | null;
  rewardGoldMultiplier: number | null;
  startingRelics: string[];
  inGameOverlay: boolean;
}

export interface StudioCampaignConfig {
  duelRules: string;
  startingGold: number | null;
  startingLp: number | null;
  bossEvery: number | null;
  shopPackCount: number | null;
  rerollBasePrice: number | null;
  banishPrice: number | null;
  disableBossCurses: boolean;
  disableShadowRelics: boolean;
  disableRelicSeal: boolean;
  disableRelicBanish: boolean;
  disableRelicFusions: boolean;
  removeCardIds: number[];
  removeCharacterIndices: number[];
  removeCharacterNames: string[];
  removePackIds: string[];
  removeDeckIds: string[];
  removeRelicKeys: string[];
  removeCurseKeys: string[];
}

export interface StudioCampaignResponse {
  ok: boolean;
  exists: boolean;
  config: StudioCampaignConfig;
  campaignFiles: string[];
}

export type StudioCategoryKey = 'cards' | 'characters' | 'relics' | 'packs' | 'decks' | 'trainer' | 'campaign' | 'code';

export interface StudioCatalogResponse<T = Record<string, unknown>> {
  ok: boolean;
  modId: string;
  category: StudioCategoryKey;
  counts: Record<StudioCategoryKey, number>;
  total: number;
  offset: number;
  hasMore: boolean;
  items: T[];
  trainer: StudioTrainerState;
}

export interface CardTypePreset {
  label: string;
  displayType: string;
  typeMask: number;
  isMonster: boolean;
  defaultRace: string;
}

export interface LuaTemplateOpt {
  key: string;
  label: string;
  code: string;
}

export interface StudioPresetsResponse {
  ok: boolean;
  cardPresets: CardTypePreset[];
  monsterRaces: Record<string, string>;
  attributes: Record<string, number>;
  luaCardTemplates: LuaTemplateOpt[];
  luaRelicTemplates: LuaTemplateOpt[];
}

export type VanillaCategoryKey = 'cards' | 'characters' | 'relics' | 'packs' | 'decks';

export interface VanillaCardSummary {
  key: string;
  title: string;
  subtitle: string;
  badge: string;
  imageUrl: string | null;
}

export interface VanillaCatalogResponse {
  ok: boolean;
  version: string;
  category: VanillaCategoryKey;
  total: number;
  offset: number;
  hasMore: boolean;
  counts: Record<VanillaCategoryKey, number>;
  items: VanillaCardSummary[];
}

export interface VanillaDetailResponse {
  ok: boolean;
  category: VanillaCategoryKey;
  key: string;
  title: string;
  subtitle: string;
  meta: string;
  description: string;
  imageUrl: string | null;
  luaScript?: string;
  subCards?: SubCardThumb[];
  raw?: Record<string, unknown>;
}

export interface StudioDraftFromVanilla {
  category: VanillaCategoryKey;
  keepVanillaIdentity: boolean;
  detail: VanillaDetailResponse;
}

export interface LogLine {
  id: number;
  time: string;
  level: string;
  message: string;
}

export interface ApiEndpointDoc {
  method: string;
  path: string;
  description: string;
}

export interface McpToolDef {
  name: string;
  description: string;
  inputSchema?: {
    type?: string;
    properties?: Record<string, { type?: string; description?: string }>;
    required?: string[];
  };
}

export interface McpCallRecord {
  id: string;
  tool: string;
  args: string;
  ok: boolean;
  durationMs: number;
  preview: string;
  time: string;
}

export interface McpFocusContext {
  modId: string | null;
  modName: string | null;
  category: string | null;
  itemKey: string | null;
  itemName: string | null;
  scriptPath: string | null;
  updatedAtUtc: string | null;
}

export interface McpInfoPayload {
  ok: boolean;
  serverName: string;
  serverVersion: string;
  protocolVersion: string;
  httpUrl: string;
  sseUrl: string;
  stdioCommand: string;
  stdioArgs: string[];
  activeSessions: number;
  workspaceRoot: string;
  focus: McpFocusContext;
  tools: McpToolDef[];
  recentCalls: McpCallRecord[];
}

export interface StudioSyncEvent {
  modId: string;
  path: string | null;
  tool: string;
  time: string;
}

export interface ProxyPresetItem {
  id: string;
  nameZh: string;
  nameEn: string;
  prefix: string;
}

export interface ReleaseAssetInfo {
  name: string;
  size: number;
  downloadUrl: string;
  sha256: string | null;
}

export interface UpdateCheckPayload {
  ok: boolean;
  proxy: string;
  proxyPresets: ProxyPresetItem[];
  downloadsDir: string;
  partialDownloads: { fileName: string; downloadedBytes: number; partPath: string }[];
  game: {
    repo: string;
    installed?: boolean;
    localVersion: string | null;
    launcherBuild: number;
    latestTag: string | null;
    hasUpdate: boolean;
    publishedAt: string | null;
    htmlUrl: string;
    body?: string;
    hasWebBundle: boolean;
    hasDeltaPatch?: boolean;
    deltaFromVersion?: string | null;
    deltaAsset?: ReleaseAssetInfo | null;
    webBundleAsset: ReleaseAssetInfo | null;
    installerAsset: ReleaseAssetInfo | null;
    linuxAsset?: ReleaseAssetInfo | null;
    androidAsset?: ReleaseAssetInfo | null;
  };
  manager: {
    repo: string;
    localVersion: string;
    latestTag: string | null;
    hasUpdate: boolean;
    publishedAt: string | null;
    htmlUrl: string;
    body?: string;
    assets: ReleaseAssetInfo[];
  };
}

export interface VanillaDllInfo {
  ok: boolean;
  found: boolean;
  expectedPath?: string;
  fullPath?: string;
  fileSizeBytes?: number;
  lastWriteTimeUtc?: string;
  sha256?: string;
  assemblyName?: string;
  assemblyVersion?: string;
  launcherBuild?: number;
  initialBundleVersion?: string;
  initialBundleFileName?: string;
  initialBundleSha256?: string;
  hasStartupHashesResource?: boolean;
  startupHashesSizeBytes?: number;
  hasBackupOrig?: boolean;
  embeddedResources?: string[];
  updateEndpoints?: string[];
  desktopBootScript?: string;
}
