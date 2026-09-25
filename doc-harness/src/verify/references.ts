import { existsSync } from 'node:fs';
import path from 'node:path';
import type { HarnessConfig } from '../config.js';
import { listSourceFiles } from '../fsx.js';
import type { CurrentWorkspace, Issue } from '../types.js';

const PATH_RE = /`([A-Za-z0-9_][A-Za-z0-9_./-]*\.(?:cs|cshtml|ts|tsx|mjs|json|yml|yaml|sh|ps1|toml|css|props|csproj|slnx|sql|http|md|Dockerfile))`/g;
const BARE_FILE_RE = /`((?:[A-Za-z0-9_.-]+\/)+(?:Dockerfile|Caddyfile|\.env\.example|\.gitattributes|\.dockerignore))`/g;
const FEATURE_ID_RE = /\bF\d{3}\b/g;
const LINK_RE = /\[[^\]]*\]\(([^)\s]+)\)/g;

/**
 * 문서가 가리키는 파일 경로·기능 id·내부 링크가 실재하는지(비용 0).
 * 경로는 저장소 루트 기준 실존, 또는 저장소 파일 목록의 접미(하위 경로·파일명)와 일치하면 통과한다
 * (`main.tsx`, `lib/drafts.ts` 같은 짧은 표기를 오탐하지 않기 위해 — 2026-09-24 실측 42건).
 */
/** "X는 없다/존재하지 않는다/사용하지 않는다"처럼 부재를 말하는 문장에서의 경로 언급. */
const NEGATION_RE = /없다|없음|존재하지\s*않|사용하지\s*않|쓰지\s*않|만들지\s*않|생성하지\s*않|제거|삭제|not exist|does not|doesn't|no longer|removed|absent/;

export interface ReferenceCheckResult { issues: Issue[]; warnings: Issue[] }

export function checkReferences(docs: Map<string, string>, projectRoot: string, ws: CurrentWorkspace, cfg?: HarnessConfig): ReferenceCheckResult {
  const issues: Issue[] = [];
  const warnings: Issue[] = [];
  const featureIds = new Set(ws.features.features.map((f) => f.id));
  const docNames = new Set(docs.keys());
  const repoFiles = cfg ? listSourceFiles(projectRoot, cfg) : [];
  const exists = (p: string): boolean => {
    if (existsSync(path.join(projectRoot, p))) return true;
    if (!repoFiles.length) return false;
    const suffix = '/' + p.replace(/^\.\//, '');
    return repoFiles.some((f) => f === p || f.endsWith(suffix));
  };
  for (const [name, md] of docs) {
    const seenPaths = new Set<string>();
    const lines = md.split('\n');
    const lineOf = (index: number) => md.slice(0, index).split('\n').length - 1;
    for (const re of [PATH_RE, BARE_FILE_RE]) {
      re.lastIndex = 0;
      for (const m of md.matchAll(re)) {
        const p = m[1];
        if (seenPaths.has(p) || p.startsWith('http')) continue;
        seenPaths.add(p);
        if (p.endsWith('.md') && (docNames.has(p) || docNames.has(p.replace(/^\.\.\//, '')))) continue;
        if (exists(p)) continue;
        const index = m.index ?? 0;
        const lineNo = lineOf(index);
        const line = lines[lineNo] ?? '';
        const col = index - (md.slice(0, index).lastIndexOf('\n') + 1);
        // 부재를 서술하는 문장("`x`은 저장소에 없다.")은 환각이 아니다 — 언급이 든 문장(다음 마침표까지)만 본다.
        const SENTENCE_SPLIT = /(?<=[.!?])\s|(?<=다\.)/;
        const before = line.slice(0, col).split(SENTENCE_SPLIT).pop() ?? '';
        const sentence = before + (line.slice(col).split(SENTENCE_SPLIT)[0] ?? '');
        if (NEGATION_RE.test(sentence)) continue;
        // 경로 없는 파일명만의 표기는 확신할 수 없어 경고로 낸다.
        const issue: Issue = { document: name, type: 'HALLUCINATED_PATH', description: `문서가 가리키는 파일이 저장소에 없다: ${p}`, evidence: [{ file: p }] };
        (p.includes('/') ? issues : warnings).push(issue);
      }
    }
    for (const m of new Set([...md.matchAll(FEATURE_ID_RE)].map((x) => x[0]))) {
      if (!featureIds.has(m)) issues.push({ document: name, type: 'UNKNOWN_FEATURE_ID', description: `features.json에 없는 기능 id를 언급한다: ${m}`, evidence: [] });
    }
    LINK_RE.lastIndex = 0;
    for (const m of md.matchAll(LINK_RE)) {
      const target = m[1].split('#')[0];
      if (!target || target.startsWith('http') || target.startsWith('mailto:')) continue;
      if (!target.endsWith('.md')) continue;
      const resolved = path.posix.normalize(path.posix.join(path.posix.dirname(name), target));
      if (!docNames.has(resolved)) issues.push({ document: name, type: 'BROKEN_LINK', description: `내부 링크 대상 문서가 없다: ${target} (→ ${resolved})`, evidence: [] });
    }
  }
  return { issues, warnings };
}
