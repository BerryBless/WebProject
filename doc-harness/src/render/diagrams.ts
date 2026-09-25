import type { Diagram } from '../types.js';

export const DIAGRAM_TYPE_LABEL: Record<Diagram['type'], string> = {
  architecture: 'Architecture', sequence: 'Sequence Diagram', flowchart: 'Flowchart', dataflow: 'Data Flow Diagram', state: 'State Diagram', er: 'ER Diagram', class: 'Class Diagram',
};

/** 결론 → Mermaid → 상세 → 코드 근거 순서(스펙 §6.5). 섹션 id는 다이어그램 id다. */
export function diagramBody(d: Diagram, headingLevel = 2): string {
  const h = '#'.repeat(headingLevel);
  const lines: string[] = [];
  lines.push(`${h} ${d.title} (${DIAGRAM_TYPE_LABEL[d.type]})`, '', d.summary.trim(), '', '```mermaid', d.mermaid.trim(), '```', '');
  if (d.details.trim()) lines.push(d.details.trim(), '');
  lines.push(`${h}# 코드 근거`, '', '| 구성 요소 | 코드 |', '|---|---|');
  for (const n of d.nodes) lines.push(`| ${n.node} | \`${n.code}\`${n.symbol ? ` (${n.symbol})` : ''} |`);
  if (d.status !== 'CONFIRMED') lines.push('', `> 상태: ${d.status}`);
  return lines.join('\n');
}

export function renderDiagramSection(d: Diagram, headingLevel = 2): { id: string; body: string } {
  return { id: d.id, body: diagramBody(d, headingLevel) };
}

/** mermaid 본문 비교용 정규화(공백·줄끝 차이 무시). */
export function normalizeMermaid(code: string): string {
  return code.replace(/\r\n/g, '\n').split('\n').map((l) => l.trim()).filter(Boolean).join('\n');
}

/** 섹션 본문에서 첫 mermaid 코드만 꺼낸다. */
export function mermaidFromBody(body: string): string | null {
  const m = /```mermaid\s*\n([\s\S]*?)\n```/.exec(body);
  return m ? m[1] : null;
}
