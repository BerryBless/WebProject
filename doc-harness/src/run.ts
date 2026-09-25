import { existsSync } from 'node:fs';
import { cp, mkdir, readdir, rename, rm, stat } from 'node:fs/promises';
import path from 'node:path';
import type { HarnessPaths } from './config.js';
import { atomicWriteText } from './fsx.js';
import { loadRunState, newRunState, saveRunState, setItem } from './state.js';
import type { ItemStatus, RunMode, RunState } from './types.js';

export interface CommitOptions {
  /** docs/generated/에서 지워야 하는 문서(상대 경로). staging에 없는 파일은 지우지 않는다(사람이 만든 파일 보존). */
  deletedDocuments?: string[];
}

/**
 * 한 번의 문서화 실행. 모든 산출물은 `staging/`에 쓰고, `commit()`이 검증 통과 후에만 정본으로 옮긴다(스펙 §9).
 * 정본 교체 순서: docs 파일 복사 → current.prev로 대피 → staging/current를 current로 → prev 삭제 → 상태 SUCCESS.
 * 중간에 죽으면 `Run.recover()`가 prev 유무로 롤포워드/롤백한다.
 */
export class Run {
  readonly dir: string;
  readonly staging: { root: string; current: string; docs: string };
  readonly logs: string;
  readonly stateFile: string;

  private constructor(readonly paths: HarnessPaths, readonly name: string, public state: RunState) {
    this.dir = path.join(paths.runs, name);
    this.staging = { root: path.join(this.dir, 'staging'), current: path.join(this.dir, 'staging', 'current'), docs: path.join(this.dir, 'staging', 'docs') };
    this.logs = path.join(this.dir, 'logs');
    this.stateFile = path.join(this.dir, 'state.json');
  }

  get number(): number {
    return Number(this.name.replace('run-', ''));
  }

  static async listRuns(paths: HarnessPaths): Promise<string[]> {
    if (!existsSync(paths.runs)) return [];
    return (await readdir(paths.runs)).filter((n) => /^run-\d{4}$/.test(n)).sort();
  }

  static async create(paths: HarnessPaths, mode: RunMode, fingerprint: string): Promise<Run> {
    const runs = await Run.listRuns(paths);
    const next = runs.length ? Number(runs[runs.length - 1].replace('run-', '')) + 1 : 1;
    const name = `run-${String(next).padStart(4, '0')}`;
    const run = new Run(paths, name, newRunState(name, mode, fingerprint));
    await mkdir(run.staging.current, { recursive: true });
    await mkdir(run.staging.docs, { recursive: true });
    await mkdir(run.logs, { recursive: true });
    await run.save();
    return run;
  }

  static async open(paths: HarnessPaths, name: string): Promise<Run> {
    const state = await loadRunState(path.join(paths.runs, name, 'state.json'));
    if (!state) throw new Error(`Run 상태 파일이 없다: ${name}`);
    const run = new Run(paths, name, state);
    await mkdir(run.staging.current, { recursive: true });
    await mkdir(run.staging.docs, { recursive: true });
    await mkdir(run.logs, { recursive: true });
    return run;
  }

  /** 가장 최근 Run이 RUNNING 또는 FAILED(한도·일시 오류로 중단)면 그것을, SUCCESS·ABANDONED면 null. */
  static async latestIncomplete(paths: HarnessPaths): Promise<Run | null> {
    const runs = await Run.listRuns(paths);
    if (!runs.length) return null;
    const last = runs[runs.length - 1];
    const state = await loadRunState(path.join(paths.runs, last, 'state.json'));
    if (!state || (state.status !== 'RUNNING' && state.status !== 'FAILED')) return null;
    return new Run(paths, last, state);
  }

  /** FAILED Run을 다시 RUNNING으로 돌린다(성공한 항목은 그대로 두고 실패·미시작 항목만 다시 돈다). */
  async reopen(): Promise<void> {
    if (this.state.status === 'RUNNING') return;
    this.state.status = 'RUNNING';
    this.state.abandonReason = undefined;
    for (const item of Object.values(this.state.items)) if (item.status === 'RUNNING') item.status = 'PENDING';
    await this.save();
  }

  static async latest(paths: HarnessPaths): Promise<Run | null> {
    const runs = await Run.listRuns(paths);
    if (!runs.length) return null;
    return Run.open(paths, runs[runs.length - 1]);
  }

  /** 정본 교체 중 남은 `current.prev`를 정리한다. current가 없으면 prev를 되돌린다(롤백), 둘 다 있으면 prev를 지운다(롤포워드). */
  static async recover(paths: HarnessPaths): Promise<'none' | 'rolled-forward' | 'rolled-back'> {
    const prev = `${paths.current}.prev`;
    if (!existsSync(prev)) return 'none';
    if (existsSync(paths.current)) {
      await rm(prev, { recursive: true, force: true });
      return 'rolled-forward';
    }
    await rename(prev, paths.current);
    return 'rolled-back';
  }

  private saveChain: Promise<void> = Promise.resolve();

  /** 상태 저장은 직렬화한다. 워커 풀이 동시에 항목 상태를 바꿔도 같은 파일을 겹쳐 쓰지 않는다. */
  save(): Promise<void> {
    const next = this.saveChain.then(() => saveRunState(this.stateFile, this.state));
    this.saveChain = next.catch(() => undefined);
    return next;
  }

  item(id: string): ItemStatus {
    return this.state.items[id]?.status ?? 'PENDING';
  }

  /**
   * 항목 하나를 실행한다. 같은 inputHash로 이미 SUCCESS면 `load`가 있을 때 저장된 결과를 돌려주고 fn을 부르지 않는다.
   * fn이 던지면 FAILED로 기록하고 다시 던진다. `cost`는 fn이 소비한 비용을 보고할 콜백이다.
   */
  async runItem<T>(id: string, inputHash: string, fn: (report: (costUsd: number, attempts?: number) => void) => Promise<T>, load?: () => Promise<T>): Promise<T> {
    const cur = this.state.items[id];
    if (cur?.status === 'SUCCESS' && cur.inputHash === inputHash && load) return load();
    setItem(this.state, id, 'RUNNING', { inputHash });
    await this.save();
    let cost = 0;
    let attempts = 0;
    try {
      const result = await fn((c, a) => { cost += c; attempts += a ?? 1; });
      setItem(this.state, id, 'SUCCESS', { costUsd: cost, attempts });
      await this.save();
      return result;
    } catch (e) {
      setItem(this.state, id, 'FAILED', { error: (e as Error).message, costUsd: cost, attempts });
      await this.save();
      throw e;
    }
  }

  async markSkipped(id: string): Promise<void> {
    setItem(this.state, id, 'SKIPPED');
    await this.save();
  }

  async fail(error: string): Promise<void> {
    this.state.status = 'FAILED';
    this.state.abandonReason = error;
    await this.save();
  }

  async abandon(reason: string): Promise<void> {
    this.state.status = 'ABANDONED';
    this.state.abandonReason = reason;
    await this.save();
  }

  async writeStagingText(rel: string, text: string): Promise<void> {
    await atomicWriteText(path.join(this.staging.root, rel), text);
  }

  /** 검증을 통과한 뒤에만 부른다. 정본(current·docs·baseline·depgraph)을 교체하고 SUCCESS로 끝낸다. */
  async commit(opts: CommitOptions = {}): Promise<void> {
    // 1) 문서: staging/docs에 있는 파일만 덮어쓰고, 삭제 목록만 지운다.
    await mkdir(this.paths.docsOut, { recursive: true });
    await copyTree(this.staging.docs, this.paths.docsOut);
    for (const rel of opts.deletedDocuments ?? []) {
      await rm(path.join(this.paths.docsOut, rel), { force: true });
    }
    // 2) current 교체(rename 두 번, 같은 볼륨).
    const prev = `${this.paths.current}.prev`;
    await rm(prev, { recursive: true, force: true });
    if (existsSync(this.paths.current)) await rename(this.paths.current, prev);
    await mkdir(path.dirname(this.paths.current), { recursive: true });
    await rename(this.staging.current, this.paths.current);
    await rm(prev, { recursive: true, force: true });
    // 3) baseline·depgraph.
    for (const f of ['baseline.json', 'depgraph.json']) {
      const src = path.join(this.staging.root, f);
      if (existsSync(src)) await rename(src, path.join(this.paths.workspace, f));
    }
    await mkdir(this.staging.current, { recursive: true });
    this.state.status = 'SUCCESS';
    await this.save();
  }
}

async function copyTree(src: string, dst: string): Promise<void> {
  if (!existsSync(src)) return;
  for (const entry of await readdir(src, { withFileTypes: true })) {
    const s = path.join(src, entry.name);
    const d = path.join(dst, entry.name);
    if (entry.isDirectory()) {
      await mkdir(d, { recursive: true });
      await copyTree(s, d);
    } else if ((await stat(s)).isFile()) {
      const tmp = `${d}.${process.pid}.tmp`;
      await mkdir(path.dirname(d), { recursive: true });
      await cp(s, tmp);
      await rename(tmp, d);
    }
  }
}
