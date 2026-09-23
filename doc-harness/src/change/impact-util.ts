import type { Depgraph } from '../types.js';

/** 변경 파일이 직접 걸린 문서(depgraph.files[*].documents)의 합집합. */
export function depgraphDocsUnion(graph: Depgraph, files: string[]): string[] {
  const out = new Set<string>();
  for (const f of files) for (const d of graph.files[f]?.documents ?? []) out.add(d);
  return [...out];
}
