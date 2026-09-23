import { pathList } from '../context.js';
import { listSourceFiles } from '../fsx.js';
import { formatMarkers, type GitExtract } from '../git-extract.js';
import { buildPrompt } from '../prompts.js';
import type { FeatureAnalysis, Operations, OperationsArea } from '../types.js';
import { callClaude, writeStaging, type PhaseContext } from './common.js';

interface AreaSpec { key: keyof Operations; title: string; idPrefix: string; checklist: string }

export const AREAS: AreaSpec[] = [
  { key: 'errorHandling', title: 'Error Handling', idPrefix: 'ERR', checklist: '- 예외 종류와 전파 경로\n- 전역 핸들러(미들웨어·ExceptionHandler·ErrorBoundary)\n- 오류 코드/응답 형식의 일관성\n- 재시도·타임아웃·롤백\n- 삼켜진 예외(빈 catch, 로그만 하고 계속)\n- 사용자에게 내부 정보가 새는 오류 메시지' },
  { key: 'security', title: 'Security', idPrefix: 'SEC', checklist: '- 인증(세션·쿠키 속성·만료)\n- 인가(경로·호스트·IP 허용 목록)\n- 비밀값 관리(설정·환경변수·로그 노출)\n- 인젝션(SQL·명령·마크다운/HTML)\n- Path Traversal, 파일 업로드(유형·크기·저장 위치)\n- CORS, CSRF, XSS(CSP·정제 파이프라인), SSRF\n- 속도 제한, 헤더(HSTS 등)' },
  { key: 'performance', title: 'Performance', idPrefix: 'PERF', checklist: '- N+1 쿼리, 인덱스 없는 조회, 대량 로드\n- 동기 I/O·블로킹 호출(`.Result`, `.Wait()`, 동기 파일 I/O)\n- async 오용, 스레드 풀 고갈\n- 락·경합\n- 메모리(큰 버퍼, 캐시 무한 성장, 문자열 연산)\n- 캐시 전략과 무효화\n- 네트워크 왕복, 직렬화 비용' },
  { key: 'techDebt', title: 'Technical Debt', idPrefix: 'DEBT', checklist: '- TODO/FIXME/HACK 마커\n- 죽은 코드, 중복 코드\n- 강한 결합, 순환 의존\n- 하드코딩·매직 넘버\n- 테스트 없는 영역(핵심 경로인데 테스트가 없는 곳)\n- 임시 workaround가 영구화된 곳' },
];

const OPS_FILE_RE = /\.(cs|cshtml|ts|tsx)$/;

function failurePointsText(analyses: Map<string, FeatureAnalysis>): string {
  const lines: string[] = [];
  for (const [id, fa] of analyses) {
    for (const fp of fa.failurePoints.slice(0, 12)) lines.push(`- [${id}] ${fp.where}: ${fp.condition} → ${fp.handling} (${fp.status})`);
  }
  return lines.length ? lines.slice(0, 200).join('\n') : '(없음)';
}

export async function runOperations(ctx: PhaseContext, analyses: Map<string, FeatureAnalysis>, extract: GitExtract): Promise<Operations> {
  const files = listSourceFiles(ctx.paths.projectRoot, ctx.cfg).filter((f) => OPS_FILE_RE.test(f) && !/(\.Tests\/|\/test\/|\.test\.|\.spec\.|\/e2e\/)/.test(f));
  const result: Partial<Operations> = {};
  const failurePoints = failurePointsText(analyses);
  for (const area of AREAS) {
    const prompt = buildPrompt('07_operations', {
      areaTitle: area.title,
      idPrefix: area.idPrefix,
      checklist: area.checklist,
      files: pathList(files, ctx.cfg),
      failurePoints,
      markers: area.key === 'techDebt' ? formatMarkers(extract.markers) : '(techDebt 영역에서만 제공)',
    });
    result[area.key] = await callClaude<OperationsArea>(ctx, { itemId: `operations:${area.key}`, phase: 'operations', schemaName: 'operations_area', prompt, outFile: `operations_${area.key}.json` });
  }
  const ops = result as Operations;
  await writeStaging(ctx, 'operations.json', ops);
  return ops;
}
