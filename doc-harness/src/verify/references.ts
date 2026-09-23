import { existsSync } from 'node:fs';
import path from 'node:path';
import type { CurrentWorkspace, Issue } from '../types.js';

const PATH_RE = /`([A-Za-z0-9_][A-Za-z0-9_./-]*\.(?:cs|cshtml|ts|tsx|mjs|json|yml|yaml|sh|ps1|toml|css|props|csproj|slnx|sql|http|md|Dockerfile))`/g;
const BARE_FILE_RE = /`((?:[A-Za-z0-9_.-]+\/)+(?:Dockerfile|Caddyfile|\.env\.example|\.gitattributes|\.dockerignore))`/g;
const FEATURE_ID_RE = /\bF\d{3}\b/g;
const LINK_RE = /\[[^\]]*\]\(([^)\s]+)\)/g;

/**
 * 문서가 가리키는 파일 경로·기능 id·내부 링크가 실재하는지(비용 0).
 * 경로는 저장소 루트 기준으로 본다. 생성 문서끼리의 링크는 문서 맵에서 확인한다.
 */
export function checkReferences(docs: Map<string, string>, projectRoot: string, ws: CurrentWorkspace): Issue[] {
  const issues: Issue[] = [];
  const featureIds = new Set(ws.features.features.map((f) => f.id));
  const docNames = new Set(docs.keys());
  for (const [name, md] of docs) {
    const seenPaths = new Set<string>();
    for (const re of [PATH_RE, BARE_FILE_RE]) {
      re.lastIndex = 0;
      for (const m of md.matchAll(re)) {
        const p = m[1];
        if (seenPaths.has(p) || p.startsWith('http')) continue;
        seenPaths.add(p);
        if (p.endsWith('.md') && (docNames.has(p) || docNames.has(p.replace(/^\.\.\//, '')))) continue;
        if (!existsSync(path.join(projectRoot, p))) issues.push({ document: name, type: 'HALLUCINATED_PATH', description: `문서가 가리키는 파일이 저장소에 없다: ${p}`, evidence: [{ file: p }] });
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
  return issues;
}
