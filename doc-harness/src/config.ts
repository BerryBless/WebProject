import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import YAML from 'yaml';

export interface PhaseClaudeConfig {
  model: string;
  budget_usd: number;
  effort: string;
  timeout_sec: number;
}

export interface HarnessConfig {
  project: {
    root: string;
    exclude: string[];
    source_extensions: string[];
    tooling_dirs: string[];
    secret_globs: string[];
  };
  analysis: { feature_parallelism: number; max_features: number };
  retry: { max_attempts: number };
  verification: { enabled: boolean; max_iterations: number; feature_chunk_docs?: number; publish_on_residual?: boolean };
  git: { analyze_history: boolean; max_diff_bytes_per_commit: number; failure_keywords: string[]; marker_pattern: string };
  diagrams: { format: 'mermaid'; max_nodes: number; max_edges: number };
  output: { docs: string; workspace: string };
  claude: { bin: string; default: PhaseClaudeConfig; phases: Record<string, Partial<PhaseClaudeConfig>> };
}

export interface HarnessPaths {
  harnessDir: string;
  projectRoot: string;
  workspace: string;
  current: string;
  runs: string;
  inbox: string;
  docsOut: string;
  prompts: string;
  schemas: string;
  templates: string;
}

/** 이 파일 기준으로 doc-harness/ 디렉터리를 찾는다(절대 경로 하드코딩 금지 규칙). */
export function defaultHarnessDir(): string {
  return path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
}

export function loadConfig(harnessDir: string = defaultHarnessDir()): HarnessConfig {
  const file = path.join(harnessDir, 'config', 'harness.yaml');
  const raw = YAML.parse(readFileSync(file, 'utf8')) as HarnessConfig;
  if (!raw?.project?.root) throw new Error(`harness.yaml에 project.root가 없다: ${file}`);
  raw.claude.phases ??= {};
  return raw;
}

export function resolvePaths(cfg: HarnessConfig, harnessDir: string = defaultHarnessDir()): HarnessPaths {
  const projectRoot = path.resolve(harnessDir, cfg.project.root);
  const workspace = path.resolve(harnessDir, cfg.output.workspace);
  return {
    harnessDir,
    projectRoot,
    workspace,
    current: path.join(workspace, 'current'),
    runs: path.join(workspace, 'runs'),
    inbox: path.join(workspace, 'inbox'),
    docsOut: path.resolve(harnessDir, cfg.output.docs),
    prompts: path.join(harnessDir, 'prompts'),
    schemas: path.join(harnessDir, 'schemas'),
    templates: path.join(harnessDir, 'templates'),
  };
}

/** phase별 Claude 설정: default 위에 phases[phase]를 덮는다. */
export function phaseClaudeConfig(cfg: HarnessConfig, phase: string): PhaseClaudeConfig {
  return { ...cfg.claude.default, ...(cfg.claude.phases[phase] ?? {}) };
}
