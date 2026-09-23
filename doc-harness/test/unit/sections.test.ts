import { describe, expect, it } from 'vitest';
import { assembleDoc, extractMermaidBlocks, isManuallyEdited, parseSections, removeSection, replaceSection, sectionHash, wrapSection } from '../../src/render/sections.js';

const doc = () => assembleDoc('제목', [{ id: 'summary', body: '## 요약\n\n한 줄.' }, { id: 'F001_SEQUENCE', body: '## 흐름\n\n```mermaid\nsequenceDiagram\n  A->>B: x\n```\n\n상세' }]) + '\n사람이 덧붙인 문단\n';

describe('sections', () => {
  it('앵커를 파싱하고 본문 해시를 검증한다', () => {
    const { sections, issues } = parseSections(doc());
    expect(issues).toEqual([]);
    expect(sections.map((s) => s.id)).toEqual(['summary', 'F001_SEQUENCE']);
    expect(sections[0].body).toBe('## 요약\n\n한 줄.');
    expect(sections.every((s) => !isManuallyEdited(s))).toBe(true);
  });

  it('사람이 본문을 고치면 MANUAL_EDIT로 본다', () => {
    const edited = doc().replace('한 줄.', '한 줄. (수정)');
    const { sections } = parseSections(edited);
    expect(isManuallyEdited(sections[0])).toBe(true);
    expect(isManuallyEdited(sections[1])).toBe(false);
  });

  it('닫히지 않은 mermaid 펜스와 짝 안 맞는 앵커를 잡는다', () => {
    const bad = '<!-- doc-harness:section id="a" hash="" -->\n```mermaid\nflowchart LR\n  A --> B\n';
    const { issues } = parseSections(bad);
    expect(issues.map((i) => i.type)).toContain('MERMAID_UNCLOSED_BLOCK');
    const bad2 = '<!-- doc-harness:section id="a" hash="" -->\nbody\n<!-- doc-harness:section id="b" hash="" -->\nbody\n<!-- /doc-harness:section -->\n<!-- /doc-harness:section -->';
    const r2 = parseSections(bad2);
    expect(r2.issues.length).toBeGreaterThanOrEqual(2);
    expect(r2.issues.every((i) => i.type === 'SECTION_ANCHOR_MISMATCH')).toBe(true);
  });

  it('코드 펜스 안의 앵커는 무시한다', () => {
    const md = '```text\n<!-- doc-harness:section id="fake" hash="" -->\n```\n' + wrapSection('real', 'x');
    expect(parseSections(md).sections.map((s) => s.id)).toEqual(['real']);
  });

  it('replaceSection은 앵커 밖 텍스트를 보존하고 없는 섹션은 끝에 붙이며 removeSection은 지운다', () => {
    const r = replaceSection(doc(), 'summary', '## 요약\n\n새 본문');
    expect(r).toContain('새 본문');
    expect(r).toContain('사람이 덧붙인 문단');
    expect(r).toContain('sequenceDiagram');
    expect(parseSections(r).sections[0].hash).toBe(sectionHash('## 요약\n\n새 본문'));
    const appended = replaceSection(doc(), 'new', 'N');
    expect(parseSections(appended).sections.map((s) => s.id)).toEqual(['summary', 'F001_SEQUENCE', 'new']);
    expect(parseSections(removeSection(doc(), 'summary')).sections.map((s) => s.id)).toEqual(['F001_SEQUENCE']);
  });

  it('extractMermaidBlocks는 섹션 id와 함께 코드를 꺼낸다', () => {
    const blocks = extractMermaidBlocks(doc());
    expect(blocks).toHaveLength(1);
    expect(blocks[0].sectionId).toBe('F001_SEQUENCE');
    expect(blocks[0].code).toBe('sequenceDiagram\n  A->>B: x');
  });

  it('wrapSection 해시는 본문이 바뀌면 달라지고 CRLF에 무관하다', () => {
    expect(wrapSection('a', 'x')).not.toBe(wrapSection('a', 'y'));
    expect(sectionHash('a\r\nb')).toBe(sectionHash('a\nb'));
  });
});
