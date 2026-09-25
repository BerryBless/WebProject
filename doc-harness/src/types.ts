// 문서화 하네스의 공통 타입. 스펙(docs/superpowers/specs/2026-09-23-doc-harness-design.md) §2·§6·§7·§8의 필드명을 그대로 쓴다.

export type Status = 'CONFIRMED' | 'INFERRED' | 'UNKNOWN' | 'POSSIBLE_LEGACY' | 'POTENTIAL_ISSUE';

export interface Evidence {
  file: string;
  symbol?: string;
  lines?: string;
  commit?: string;
}

export interface Claim {
  text: string;
  status: Status;
  evidence: Evidence[];
}

export type DiagramType = 'architecture' | 'sequence' | 'flowchart' | 'dataflow' | 'state' | 'er' | 'class';

export interface DiagramNode {
  node: string;
  code: string;
  symbol?: string;
}

export interface Diagram {
  id: string;
  type: DiagramType;
  title: string;
  summary: string;
  mermaid: string;
  details: string;
  nodes: DiagramNode[];
  status: Status;
}

export type FeatureStatus = 'ACTIVE' | 'DEPRECATED' | 'REMOVED';
export type Importance = 'CORE' | 'SUPPORTING' | 'INFRA';
export type AnalysisStatus = 'PENDING' | 'SUCCESS' | 'FAILED' | 'STALE';

export interface FeatureSummary {
  id: string;
  name: string;
  slug: string;
  summary: string;
  entryPoints: string[];
  relatedFiles: string[];
  dependencies: string[];
  importance: Importance;
  analysisStatus: AnalysisStatus;
  status: FeatureStatus;
}

export type Significance = 'MAJOR' | 'MINOR' | 'TRIVIAL';

export interface FeatureHistoryEntry {
  date: string;
  title: string;
  classification: string[];
  before: string;
  after: string;
  reason: Claim;
  impact: string[];
  significance: Significance;
  commits: string[];
}

export interface ExecutionStep {
  step: number;
  component: string;
  file: string;
  symbol?: string;
  description: string;
}

export interface FeatureAnalysis {
  feature: FeatureSummary;
  summary: string;
  entryPoints: Claim[];
  executionFlow: ExecutionStep[];
  relatedCode: { file: string; symbol?: string; role: string }[];
  dataFlow: Claim[];
  stateTransitions: { from: string; to: string; trigger: string; evidence: Evidence[] }[];
  databaseAccess: { entity: string; operation: string; file: string; symbol?: string }[];
  externalDependencies: Claim[];
  failurePoints: { where: string; condition: string; handling: string; status: Status; evidence: Evidence[] }[];
  edgeCases: Claim[];
  logging: Claim[];
  diagrams: Diagram[];
  unknowns: string[];
  evidence: Evidence[];
  history: FeatureHistoryEntry[];
}

export interface FeaturesFile {
  features: FeatureSummary[];
  excludedCandidates: { name: string; reason: string }[];
  unknowns: string[];
}

export interface Inventory {
  project: { name: string; description: string; status: Status; evidence: Evidence[] };
  languages: Claim[];
  frameworks: Claim[];
  runtimes: Claim[];
  entryPoints: Claim[];
  directories: { path: string; role: string; status: Status }[];
  configs: Claim[];
  environments: Claim[];
  build: Claim[];
  dependencies: Claim[];
  databases: Claim[];
  caches: Claim[];
  queues: Claim[];
  externalSystems: Claim[];
  containers: Claim[];
  ci: Claim[];
  tests: Claim[];
  scripts: Claim[];
  migrations: Claim[];
  staticResources: Claim[];
  tooling: Claim[];
  unknowns: string[];
}

export interface ArchitectureComponent {
  id: string;
  name: string;
  path: string;
  layer: string;
  responsibilities: string;
  status: Status;
  evidence: Evidence[];
}

export interface Architecture {
  components: ArchitectureComponent[];
  relations: { from: string; to: string; kind: string; status: Status; evidence: Evidence[] }[];
  runtime: { processes: Claim[]; threads: Claim[]; network: Claim[] };
  storage: Claim[];
  externalSystems: Claim[];
  controlFlows: { name: string; steps: string[]; status: Status; evidence: Evidence[] }[];
  diagrams: Diagram[];
  decisions: { title: string; decision: string; rationale: string; status: Status; evidence: Evidence[] }[];
  unknowns: string[];
}

export interface DataModel {
  entities: { name: string; table?: string; file: string; fields: { name: string; type: string; constraints: string[] }[]; keys: string[]; indexes: string[]; status: Status; evidence: Evidence[] }[];
  relations: { from: string; to: string; cardinality: string; status: Status; evidence: Evidence[] }[];
  dtos: { name: string; file: string; usedBy: string[]; status: Status }[];
  migrations: Claim[];
  transactions: Claim[];
  diagrams: Diagram[];
  unknowns: string[];
}

export interface ApiEndpoint {
  method: string;
  path: string;
  host: string;
  caller: string;
  file: string;
  symbol?: string;
  request: string;
  response: string;
  validation: string[];
  authentication: string;
  sideEffects: string[];
  dbChanges: string[];
  errors: string[];
  featureIds: string[];
  status: Status;
  evidence: Evidence[];
}

export interface ApiModel {
  endpoints: ApiEndpoint[];
  otherInterfaces: Claim[];
  unknowns: string[];
}

export type CauseConfidence = 'CONFIRMED' | 'INFERRED' | 'UNKNOWN';

export interface FailureRecord {
  id: string;
  title: string;
  problem: string;
  attempt: string;
  symptom: string;
  cause: string;
  causeConfidence: CauseConfidence;
  failedSolutions: string[];
  currentWorkaround: string;
  finalSolution: string;
  relatedFiles: string[];
  relatedCommits: string[];
  recurrenceProcedure: string[];
  longTermSolution: string;
  troubleshootingWorthy: boolean;
  sources: ('code' | 'commit' | 'doc' | 'session')[];
  discoveredAt: string;
}

export interface FailuresFile {
  failures: FailureRecord[];
  unknowns: string[];
}

export type IssueClass = 'CONFIRMED_ISSUE' | 'POTENTIAL_RISK' | 'IMPROVEMENT';

export interface OperationsItem {
  id: string;
  title: string;
  classification: IssueClass;
  description: string;
  recommendation: string;
  evidence: Evidence[];
  featureIds: string[];
}

export interface OperationsArea {
  items: OperationsItem[];
  summary: string;
  unknowns: string[];
}

export interface Operations {
  errorHandling: OperationsArea;
  security: OperationsArea;
  performance: OperationsArea;
  techDebt: OperationsArea;
}

export type DiagramIssueType =
  | 'INCORRECT_DIAGRAM_RELATION'
  | 'DIAGRAM_NODE_NOT_IN_CODE'
  | 'MERMAID_SYNTAX_ERROR'
  | 'MERMAID_UNCLOSED_BLOCK'
  | 'MERMAID_STYLE_FORBIDDEN'
  | 'DIAGRAM_ABSTRACT_NODE'
  | 'DIAGRAM_TOO_COMPLEX'
  | 'SECTION_ANCHOR_MISMATCH'
  | 'DIAGRAM_MISSING';

export interface Issue {
  document: string;
  section?: string;
  type: string;
  description: string;
  evidence: Evidence[];
  /** 결정적 검사가 낸 이슈인지(없으면 LLM). 발행 판정에서 결정적 차단 이슈만 필수 해결로 본다. */
  source?: 'deterministic' | 'llm';
}

export interface DiagramIssue extends Issue {
  diagram: string;
  type: DiagramIssueType;
}

export interface Verification {
  score: { coverage: number; accuracy: number };
  hallucinations: Issue[];
  missingItems: Issue[];
  incorrectRelations: Issue[];
  diagramIssues: DiagramIssue[];
  unsupportedClaims: Issue[];
  fixRequired: { doc: string; section?: string; issue: string; evidence: Evidence[] }[];
  mermaidParser: 'VERIFIED' | 'MERMAID_RENDER_NOT_VERIFIED';
  iterations: number;
  passed: boolean;
  /** 결정적 검사가 확신하지 못하는 관찰. 보고만 하고 차단·수정 루프에 넣지 않는다. */
  warnings?: Issue[];
}

export type ChangeType = 'ADDED' | 'MODIFIED' | 'DELETED' | 'RENAMED' | 'UNTRACKED';

export interface ChangedFile {
  file: string;
  changeType: ChangeType;
  oldPath?: string;
  untracked: boolean;
  addedLines: string[];
  removedLines: string[];
  relatedSymbols: string[];
  possibleFeatures: string[];
  requiresAnalysis: boolean;
}

export interface ChangeSet {
  fingerprint: string;
  baselineCommit: string | null;
  headCommit: string;
  files: ChangedFile[];
}

export type ChangeClass =
  | 'NEW_FEATURE' | 'FEATURE_CHANGE' | 'BUG_FIX' | 'REFACTOR' | 'API_CHANGE' | 'DATA_MODEL_CHANGE'
  | 'CONFIG_CHANGE' | 'DEPENDENCY_CHANGE' | 'ARCHITECTURE_CHANGE' | 'ERROR_HANDLING_CHANGE'
  | 'PERFORMANCE_CHANGE' | 'SECURITY_CHANGE' | 'TEST_CHANGE' | 'DEPLOYMENT_CHANGE' | 'DOCUMENTATION_ONLY' | 'UNKNOWN';

export interface ClassificationItem {
  file: string;
  classifications: ChangeClass[];
  significance: Significance;
  possibleFeatures: string[];
  rationale: string;
}

export interface Classification {
  items: ClassificationItem[];
  summary: string;
  changelogCandidates: { title: string; classification: ChangeClass[]; description: string; impact: string[]; files: string[] }[];
}

export interface Impact {
  affectedFeatures: string[];
  candidateNewFeatureFiles: string[];
  removalCandidates: string[];
  affectedDocs: string[];
  affectedDiagrams: string[];
  needsArchitecture: boolean;
  needsData: boolean;
  needsApi: boolean;
  widenReason: string[];
}

export type RemovalDisposition = 'REMOVED' | 'DEPRECATED' | 'MIGRATED' | 'PARTIAL';

export interface FeatureDelta {
  newFeatures: FeatureSummary[];
  changedFeatureIds: string[];
  removedFeatures: { id: string; disposition: RemovalDisposition; replacedBy?: string; reason: Claim }[];
  unknowns: string[];
}

export interface DiagramUpdate {
  decision: 'UNCHANGED' | 'UPDATED';
  mermaid: string;
  changedEdges: string[];
  nodes: DiagramNode[];
  reason: string;
}

export interface Baseline {
  documentationVersion: number;
  lastSuccessfulRun: string;
  lastSuccessfulAt: string;
  baselineCommit: string;
  workingTreeFingerprint: string;
  files: Record<string, { hash: string; features: string[]; documents: string[]; diagrams: string[] }>;
  features: Record<string, { analysisHash: string; sourceHash: string; analyzedAt: string; status: FeatureStatus }>;
  documents: Record<string, { hash: string; inputsHash: string; sections: Record<string, string> }>;
  diagrams: Record<string, { document: string; hash: string }>;
}

export type ItemStatus = 'PENDING' | 'RUNNING' | 'SUCCESS' | 'FAILED' | 'STALE' | 'SKIPPED';
export type RunMode = 'INITIAL' | 'INCREMENTAL' | 'VERIFY_ONLY';
export type RunStatus = 'RUNNING' | 'SUCCESS' | 'FAILED' | 'ABANDONED';

export interface RunItemState {
  status: ItemStatus;
  inputHash?: string;
  attempts: number;
  costUsd: number;
  error?: string;
  updatedAt: string;
}

export interface RunState {
  run: string;
  mode: RunMode;
  fingerprint: string;
  startedAt: string;
  items: Record<string, RunItemState>;
  status: RunStatus;
  abandonReason?: string;
}

export interface ManualOverride {
  document: string;
  section: string;
  reason: string;
}

export interface RunRecord {
  run: number;
  name: string;
  mode: RunMode;
  status: RunStatus;
  startedAt: string;
  completedAt: string;
  baselineBefore: string | null;
  baselineAfter: string | null;
  changedFiles: string[];
  affectedFeatures: string[];
  newFeatures: string[];
  removedFeatures: string[];
  updatedDocuments: string[];
  deletedDocuments: string[];
  updatedDiagrams: string[];
  unchangedDiagrams: string[];
  failuresDiscovered: string[];
  manualEditsOverridden: ManualOverride[];
  verification: Verification | null;
  costUsd: number;
  claudeCalls: number;
  error?: string;
}

export interface CurrentWorkspace {
  inventory: Inventory;
  architecture: Architecture;
  features: FeaturesFile;
  featureAnalyses: Map<string, FeatureAnalysis>;
  data: DataModel;
  api: ApiModel;
  failures: FailuresFile;
  operations: Operations;
}

export interface Depgraph {
  files: Record<string, { features: string[]; apis: string[]; entities: string[]; diagrams: string[]; documents: string[] }>;
  features: Record<string, { files: string[]; apis: string[]; entities: string[]; diagrams: string[]; documents: string[]; dependencies: string[] }>;
  documents: Record<string, { inputs: string[]; features: string[]; files: string[] }>;
}
