import { sha256 } from '../fsx.js';

export interface Section {
  id: string;
  /** 앵커에 적힌 해시(하네스가 마지막으로 쓴 본문의 해시). */
  hash: string;
  /** 시작 앵커 줄 번호(0-based). */
  start: number;
  /** 끝 앵커 줄 번호(0-based, 포함). */
  end: number;
  /** 앵커 사이 본문(앞뒤 공백 제거). */
  body: string;
}

export interface SectionIssue {
  type: 'SECTION_ANCHOR_MISMATCH' | 'MERMAID_UNCLOSED_BLOCK';
  line: number;
  detail: string;
}

const OPEN_RE = /^<!--\s*doc-harness:section\s+id="([^"]+)"\s+hash="([0-9a-f]*)"\s*-->\s*$/;
const CLOSE_RE = /^<!--\s*\/doc-harness:section\s*-->\s*$/;
const FENCE_RE = /^(\s{0,3})(`{3,}|~{3,})(.*)$/;

export function sectionHash(body: string): string {
  return sha256(body.trim().replace(/\r\n/g, '\n'));
}

export function wrapSection(id: string, body: string): string {
  const trimmed = body.trim().replace(/\r\n/g, '\n');
  return `<!-- doc-harness:section id="${id}" hash="${sectionHash(trimmed)}" -->\n${trimmed}\n<!-- /doc-harness:section -->`;
}

/**
 * 문서에서 관리 섹션을 찾는다. 코드 펜스 안의 앵커는 무시한다.
 * 짝이 안 맞는 앵커와 닫히지 않은 mermaid 펜스는 issues로 보고한다.
 */
export function parseSections(md: string): { sections: Section[]; issues: SectionIssue[] } {
  const lines = md.replace(/\r\n/g, '\n').split('\n');
  const sections: Section[] = [];
  const issues: SectionIssue[] = [];
  let fence: { marker: string; mermaid: boolean; line: number } | null = null;
  let open: { id: string; hash: string; start: number } | null = null;
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    const f = FENCE_RE.exec(line);
    if (f) {
      if (!fence) fence = { marker: f[2], mermaid: /^\s*mermaid\b/.test(f[3]), line: i };
      else if (f[2][0] === fence.marker[0] && f[2].length >= fence.marker.length) fence = null;
      continue;
    }
    if (fence) continue;
    const o = OPEN_RE.exec(line);
    if (o) {
      if (open) issues.push({ type: 'SECTION_ANCHOR_MISMATCH', line: i + 1, detail: `섹션 ${open.id}이(가) 닫히기 전에 ${o[1]}이(가) 열렸다` });
      open = { id: o[1], hash: o[2], start: i };
      continue;
    }
    if (CLOSE_RE.test(line)) {
      if (!open) { issues.push({ type: 'SECTION_ANCHOR_MISMATCH', line: i + 1, detail: '열린 섹션 없이 닫는 앵커' }); continue; }
      sections.push({ id: open.id, hash: open.hash, start: open.start, end: i, body: lines.slice(open.start + 1, i).join('\n').trim() });
      open = null;
    }
  }
  if (open) issues.push({ type: 'SECTION_ANCHOR_MISMATCH', line: open.start + 1, detail: `섹션 ${open.id}이(가) 닫히지 않았다` });
  if (fence) issues.push({ type: fence.mermaid ? 'MERMAID_UNCLOSED_BLOCK' : 'SECTION_ANCHOR_MISMATCH', line: fence.line + 1, detail: fence.mermaid ? 'mermaid 코드 블록이 닫히지 않았다' : '코드 블록이 닫히지 않았다' });
  return { sections, issues };
}

/** 사람이 고친 섹션: 앵커 해시와 현재 본문 해시가 다르다. */
export function isManuallyEdited(s: Section): boolean {
  return s.hash !== '' && s.hash !== sectionHash(s.body);
}

/** 섹션 본문을 바꾼다. 없으면 문서 끝에 덧붙인다. 앵커 밖 텍스트는 그대로다. */
export function replaceSection(md: string, id: string, body: string): string {
  const normalized = md.replace(/\r\n/g, '\n');
  const { sections } = parseSections(normalized);
  const target = sections.find((s) => s.id === id);
  const block = wrapSection(id, body);
  if (!target) return normalized.trimEnd() + '\n\n' + block + '\n';
  const lines = normalized.split('\n');
  return [...lines.slice(0, target.start), ...block.split('\n'), ...lines.slice(target.end + 1)].join('\n');
}

export function removeSection(md: string, id: string): string {
  const normalized = md.replace(/\r\n/g, '\n');
  const { sections } = parseSections(normalized);
  const target = sections.find((s) => s.id === id);
  if (!target) return normalized;
  const lines = normalized.split('\n');
  return [...lines.slice(0, target.start), ...lines.slice(target.end + 1)].join('\n').replace(/\n{3,}/g, '\n\n');
}

export interface MermaidBlock {
  sectionId?: string;
  code: string;
  line: number;
}

export function extractMermaidBlocks(md: string): MermaidBlock[] {
  const lines = md.replace(/\r\n/g, '\n').split('\n');
  const { sections } = parseSections(md);
  const sectionAt = (i: number) => sections.find((s) => i > s.start && i < s.end)?.id;
  const out: MermaidBlock[] = [];
  let fence: { marker: string; mermaid: boolean; start: number } | null = null;
  let buf: string[] = [];
  for (let i = 0; i < lines.length; i++) {
    const f = FENCE_RE.exec(lines[i]);
    if (f) {
      if (!fence) { fence = { marker: f[2], mermaid: /^\s*mermaid\b/.test(f[3]), start: i }; buf = []; continue; }
      if (f[2][0] === fence.marker[0] && f[2].length >= fence.marker.length) {
        if (fence.mermaid) out.push({ sectionId: sectionAt(fence.start), code: buf.join('\n'), line: fence.start + 1 });
        fence = null;
        continue;
      }
    }
    if (fence) buf.push(lines[i]);
  }
  return out;
}

/** 제목 + 섹션들을 하나의 문서로 조립한다. */
export function assembleDoc(title: string, sections: { id: string; body: string }[]): string {
  return `# ${title}\n\n${sections.map((s) => wrapSection(s.id, s.body)).join('\n\n')}\n`;
}
