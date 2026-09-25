#!/usr/bin/env node
import { existsSync } from 'node:fs';
import { readFile, rm } from 'node:fs/promises';
import path from 'node:path';
import { Command } from 'commander';
import { loadConfig, resolvePaths } from './config.js';
import { runPipeline, statusText, verifyOnly } from './pipeline.js';
import { Run } from './run.js';

const program = new Command();
program.name('doc-harness').description('프로젝트 기술 문서를 다단계 Claude 파이프라인으로 생성·검증·증분 갱신한다.');

program.command('run')
  .description('문서화 실행. 기본은 자동 판정(최초면 INITIAL, 이후 INCREMENTAL, 변경 없으면 종료).')
  .option('--auto', '자동 판정(기본)')
  .option('--full', 'baseline과 무관하게 전체 재분석')
  .option('--phase <phase>', '이 phase까지만 실행하고 커밋하지 않음(inventory|architecture|discovery|features|data|api|failures|operations|docs)')
  .option('--feature <id>', '이 기능만 분석하고 멈춤(예: F003)')
  .option('--session-context <file>', '직전 개발 세션 요약 파일')
  .action(async (o: { full?: boolean; phase?: string; feature?: string; sessionContext?: string }) => {
    const r = await runPipeline({ mode: o.full ? 'full' : 'auto', phase: o.phase, feature: o.feature, sessionContextPath: o.sessionContext });
    console.log('\n' + r.report);
    process.exitCode = r.status === 'FAILED' ? 1 : 0;
  });

program.command('resume').description('가장 최근 미완료 Run을 이어간다(성공한 항목은 건너뜀).').action(async () => {
  const cfg = loadConfig();
  const paths = resolvePaths(cfg);
  const incomplete = await Run.latestIncomplete(paths);
  if (!incomplete) { console.log('이어갈 미완료 Run이 없다.'); return; }
  const r = await runPipeline({ mode: incomplete.state.mode === 'INITIAL' ? 'full' : 'auto' });
  console.log('\n' + r.report);
  process.exitCode = r.status === 'FAILED' ? 1 : 0;
});

program.command('status').description('문서와 코드의 동기화 상태(LLM 호출 없음).').action(async () => {
  console.log(await statusText());
});

program.command('verify').description('문서를 수정하지 않고 Verification만 실행.').option('--no-llm', '결정적 검사만').action(async (o: { llm: boolean }) => {
  const r = await verifyOnly({ llm: o.llm });
  console.log(r.report);
  process.exitCode = r.verification.passed ? 0 : 1;
});

program.command('report').description('Run의 리포트를 다시 출력한다.').argument('[run]', 'run-NNNN (기본: 최신)').action(async (name?: string) => {
  const cfg = loadConfig();
  const paths = resolvePaths(cfg);
  const run = name ? await Run.open(paths, name) : await Run.latest(paths);
  if (!run) { console.log('Run이 없다.'); return; }
  const file = path.join(run.dir, 'report.txt');
  console.log(existsSync(file) ? await readFile(file, 'utf8') : `${run.name}: 리포트 없음(상태 ${run.state.status})`);
});

program.command('clean').description('스테이징·로그를 지운다. --runs는 Run 기록 전체, --all은 workspace 전체(baseline 포함).').option('--runs', 'runs/ 전체 삭제').option('--all', 'workspace 전체 삭제').action(async (o: { runs?: boolean; all?: boolean }) => {
  const cfg = loadConfig();
  const paths = resolvePaths(cfg);
  if (o.all) { await rm(paths.workspace, { recursive: true, force: true }); console.log('workspace 전체 삭제'); return; }
  if (o.runs) { await rm(paths.runs, { recursive: true, force: true }); console.log('runs/ 삭제'); return; }
  for (const name of await Run.listRuns(paths)) {
    await rm(path.join(paths.runs, name, 'staging'), { recursive: true, force: true });
    await rm(path.join(paths.runs, name, 'logs'), { recursive: true, force: true });
  }
  console.log('각 Run의 staging/·logs/ 삭제(run.json·state.json 유지)');
});

program.parseAsync(process.argv).catch((e) => {
  console.error(`오류: ${(e as Error).message}`);
  process.exitCode = 1;
});
