import path from 'node:path';
import type { HarnessPaths } from './config.js';
import { atomicWriteJson, readJsonOrNull, sha256 } from './fsx.js';
import type { Baseline } from './types.js';

export function baselineFile(paths: HarnessPaths): string {
  return path.join(paths.workspace, 'baseline.json');
}

export async function loadBaseline(paths: HarnessPaths): Promise<Baseline | null> {
  return readJsonOrNull<Baseline>(baselineFile(paths));
}

export async function saveBaseline(file: string, baseline: Baseline): Promise<void> {
  await atomicWriteJson(file, baseline);
}

/** 정렬된 (path, hash) 전체의 해시. working tree 전체의 지문이다. */
export function fingerprint(files: { path: string; hash: string }[]): string {
  const lines = [...files].sort((a, b) => a.path.localeCompare(b.path)).map((f) => `${f.path}:${f.hash}`);
  return sha256(lines.join('\n'));
}

export function emptyBaseline(commit: string, fp: string): Baseline {
  return {
    documentationVersion: 0,
    lastSuccessfulRun: '',
    lastSuccessfulAt: '',
    baselineCommit: commit,
    workingTreeFingerprint: fp,
    files: {},
    features: {},
    documents: {},
    diagrams: {},
  };
}
