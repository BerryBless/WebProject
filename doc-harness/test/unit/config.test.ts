import path from 'node:path';
import { describe, expect, it } from 'vitest';
import { defaultHarnessDir, loadConfig, phaseClaudeConfig, resolvePaths } from '../../src/config.js';

describe('config', () => {
  it('harness.yaml을 읽고 경로를 doc-harness 기준으로 계산한다', () => {
    const cfg = loadConfig();
    const paths = resolvePaths(cfg);
    const harnessDir = defaultHarnessDir();
    expect(path.basename(harnessDir)).toBe('doc-harness');
    expect(paths.projectRoot).toBe(path.resolve(harnessDir, '..'));
    expect(paths.docsOut).toBe(path.join(paths.projectRoot, 'docs', 'generated'));
    expect(paths.current).toBe(path.join(harnessDir, 'workspace', 'current'));
    expect(cfg.project.exclude).toContain('doc-harness/');
  });

  it('phase 설정은 default 위에 덮어쓴다', () => {
    const cfg = loadConfig();
    const arch = phaseClaudeConfig(cfg, 'architecture');
    expect(arch.model).toBe('opus');
    expect(arch.timeout_sec).toBe(cfg.claude.default.timeout_sec);
    expect(phaseClaudeConfig(cfg, 'inventory').model).toBe('sonnet');
  });
});
