import { existsSync } from 'node:fs';
import { atomicWriteJson, readJson } from './fsx.js';
import type { ItemStatus, RunMode, RunState } from './types.js';

export function newRunState(run: string, mode: RunMode, fingerprint: string): RunState {
  return { run, mode, fingerprint, startedAt: new Date().toISOString(), items: {}, status: 'RUNNING' };
}

export async function loadRunState(file: string): Promise<RunState | null> {
  if (!existsSync(file)) return null;
  return readJson<RunState>(file);
}

export async function saveRunState(file: string, state: RunState): Promise<void> {
  await atomicWriteJson(file, state);
}

export function setItem(state: RunState, id: string, status: ItemStatus, patch: { inputHash?: string; error?: string; costUsd?: number; attempts?: number } = {}): void {
  const prev = state.items[id] ?? { status: 'PENDING', attempts: 0, costUsd: 0, updatedAt: '' };
  state.items[id] = {
    ...prev,
    status,
    inputHash: patch.inputHash ?? prev.inputHash,
    error: status === 'FAILED' ? patch.error ?? prev.error : undefined,
    costUsd: prev.costUsd + (patch.costUsd ?? 0),
    attempts: prev.attempts + (patch.attempts ?? 0),
    updatedAt: new Date().toISOString(),
  };
}

export function totalCost(state: RunState): number {
  return Object.values(state.items).reduce((s, i) => s + i.costUsd, 0);
}
