import { describe, expect, it } from 'vitest';
import { loadConfig } from '../../src/config.js';
import { assembleDoc } from '../../src/render/sections.js';
import { renderFeatureDoc } from '../../src/render/templates.js';
import { checkMermaid, loadMermaidParser, mermaidStats } from '../../src/verify/mermaid.js';
import { miniProjectRoot } from '../helpers/context.js';
import { sampleFeatureAnalysis } from '../helpers/samples.js';
import { sampleWorkspace } from './depgraph.test.js';

const cfg = loadConfig();

describe('mermaid parser', () => {
  it('jsdom 위에서 정상 4종을 통과시키고 오류를 잡는다', async () => {
    const parse = await loadMermaidParser();
    expect(parse).not.toBeNull();
    for (const ok of ['flowchart LR\n  PostEndpoints --> AppDbContext', 'sequenceDiagram\n  participant C as Client\n  C->>A: hi', 'erDiagram\n  USER ||--o{ POST : writes', 'stateDiagram-v2\n  [*] --> Draft']) {
      expect((await parse!(ok)).ok).toBe(true);
    }
    expect((await parse!('flowchartt LR\n  A --> B')).ok).toBe(false);
    expect((await parse!('sequenceDiagram\n  C->>: hi')).ok).toBe(false);
  }, 60_000);
});

describe('mermaidStats', () => {
  it('노드·간선 수와 타입을 센다', () => {
    const s = mermaidStats('flowchart LR\n  A[Req] --> B{Valid?}\n  B -->|Yes| C[Process]\n  B -->|No| D[Err]');
    expect(s.type).toBe('flowchart');
    expect([...s.nodes].sort()).toEqual(['A', 'B', 'C', 'D']);
    expect(s.edges).toBe(3);
    const seq = mermaidStats('sequenceDiagram\n  participant PostEndpoints\n  PostEndpoints->>AppDbContext: save\n  AppDbContext-->>PostEndpoints: ok');
    expect([...seq.nodes].sort()).toEqual(['AppDbContext', 'PostEndpoints']);
    expect(seq.edges).toBe(2);
  });
});

describe('checkMermaid', () => {
  const doc = (id: string, mermaid: string, nodes = '| PostEndpoints | `Api/Features/Posts/PostEndpoints.cs` |') =>
    assembleDoc('t', [{ id, body: `## 제목\n\n요약\n\n\`\`\`mermaid\n${mermaid}\n\`\`\`\n\n### 코드 근거\n\n| 구성 요소 | 코드 |\n|---|---|\n${nodes}` }]);

  it('정상 기능 문서는 이슈가 없다', async () => {
    const ws = sampleWorkspace();
    const { name, md } = renderFeatureDoc(sampleFeatureAnalysis());
    const r = await checkMermaid(new Map([[name, md]]), ws, cfg, miniProjectRoot);
    expect(r.parser).toBe('VERIFIED');
    expect(r.issues).toEqual([]);
  }, 60_000);

  it('문법 오류·금지 스타일·난수 id·추상 이름·복잡도·노드↔코드 불일치를 잡는다', async () => {
    const ws = sampleWorkspace();
    const docs = new Map<string, string>([
      ['a.md', doc('X_SYNTAX', 'flowchartt LR\n  PostEndpoints --> AppDbContext')],
      ['b.md', doc('X_STYLE', 'flowchart LR\n  PostEndpoints --> AppDbContext\n  classDef x fill:#f00')],
      ['c.md', doc('X_IDS', 'flowchart LR\n  A1 --> B2\n  B2 --> C3', '| A1 | `Api/Program.cs` |')],
      ['d.md', doc('X_ABSTRACT', 'flowchart LR\n  Controller --> Service\n  Service --> Repository', '| Controller | `Api/Program.cs` |')],
      ['e.md', doc('X_BIG', 'flowchart LR\n' + Array.from({ length: 30 }, (_, i) => `  N${i}x --> N${i + 1}x`).join('\n'), '| N0x | `Api/Program.cs` |')],
      ['f.md', doc('X_NODE', 'classDiagram\n  PostEndpoints --> Nope\n  PostEndpoints --> Ghost', '| PostEndpoints | `Api/Features/Posts/PostEndpoints.cs` |\n| Nope | `Api/Nope.cs` |\n| Ghost | `Api/Program.cs` |')],
    ]);
    const r = await checkMermaid(docs, ws, cfg, miniProjectRoot);
    const types = (d: string) => r.issues.filter((i) => i.document === d).map((i) => i.type);
    expect(types('a.md')).toContain('MERMAID_SYNTAX_ERROR');
    expect(types('b.md')).toContain('MERMAID_STYLE_FORBIDDEN');
    expect(types('c.md')).toContain('DIAGRAM_ABSTRACT_NODE');
    expect(types('d.md')).toContain('DIAGRAM_ABSTRACT_NODE');
    expect(types('e.md')).toContain('DIAGRAM_TOO_COMPLEX');
    // 없는 파일은 차단 이슈, 이름이 파일 본문에 없는 것은 경고(개념 노드 오탐 방지)
    expect(r.issues.filter((i) => i.document === 'f.md').map((i) => i.description)).toEqual(expect.arrayContaining([expect.stringContaining('Api/Nope.cs')]));
    expect(r.warnings.filter((i) => i.document === 'f.md').map((i) => i.description)).toEqual(expect.arrayContaining([expect.stringContaining('Ghost')]));
    expect(r.issues.filter((i) => i.document === 'f.md').some((i) => i.description.includes('Ghost'))).toBe(false);
  }, 60_000);

  it('코드 근거가 디렉터리면 존재만 확인하고 이름 대조는 건너뛴다(EISDIR 회귀)', async () => {
    const ws = sampleWorkspace();
    const docs = new Map([['arch.md', doc('ARCH_SYSTEM', 'flowchart LR\n  AdminSpa --> PostEndpoints', '| AdminSpa | `Web/src` |\n| PostEndpoints | `Api/Features/Posts/PostEndpoints.cs` |')]]);
    const r = await checkMermaid(docs, ws, cfg, miniProjectRoot);
    expect(r.issues).toEqual([]);
  }, 60_000);

  it('짧은 파일 표기는 접미 일치로 통과하고, 근거 없음 표기와 표에만 있는 노드는 경고다', async () => {
    const ws = sampleWorkspace();
    const docs = new Map([['s.md', doc('X_SHORT', 'classDiagram\n  PostEndpoints --> AppDbContext', '| PostEndpoints | `Posts/PostEndpoints.cs` |\n| AppDbContext | `AppDbContext.cs` |\n| Extra | `Api/Program.cs` |\n| Mystery | `UNKNOWN` |')]]);
    const r = await checkMermaid(docs, ws, cfg, miniProjectRoot);
    expect(r.issues).toEqual([]);
    expect(r.warnings.map((w) => w.type).sort()).toEqual(['DIAGRAM_MISSING', 'DIAGRAM_NODE_NOT_IN_CODE', 'DIAGRAM_NODE_NOT_IN_CODE']);
  }, 60_000);

  it('기능 id 노드와 개념 노드(flowchart)는 오탐하지 않는다', async () => {
    const ws = sampleWorkspace();
    const docs = new Map([
      ['deps.md', doc('DEPS', 'flowchart LR\n  F001["F001 글"] --> F002\n  F002["F002 로그인"]', '| F001 | `Api/Features/Posts/PostEndpoints.cs` |')],
      ['flow.md', doc('F001_FLOW', 'flowchart TD\n  Request --> Validate{Valid?}\n  Validate -->|no| Unauthorized401', '| Unauthorized401 | `Api/Features/Auth/AuthEndpoints.cs` |')],
      ['seq.md', doc('F001_SEQUENCE', 'sequenceDiagram\n  participant Operator\n  participant BackupSh\n  Operator->>BackupSh: run', '| BackupSh | `Api/Program.cs` |')],
    ]);
    const r = await checkMermaid(docs, ws, cfg, miniProjectRoot);
    expect(r.issues.filter((i) => i.document !== 'seq.md')).toEqual([]);
    expect(r.issues.map((i) => i.type)).not.toContain('DIAGRAM_ABSTRACT_NODE');
  }, 60_000);

  it('sequence participant가 실행 흐름에 없으면 경고(INCORRECT_DIAGRAM_RELATION), 닫히지 않은 펜스는 MERMAID_UNCLOSED_BLOCK', async () => {
    const ws = sampleWorkspace();
    const fa = sampleFeatureAnalysis();
    fa.diagrams[0].mermaid = 'sequenceDiagram\n  participant PostEndpoints\n  participant MysteryService\n  PostEndpoints->>MysteryService: call';
    const { name, md } = renderFeatureDoc(fa);
    const r = await checkMermaid(new Map([[name, md]]), ws, cfg, miniProjectRoot);
    expect(r.warnings.map((i) => i.type)).toContain('INCORRECT_DIAGRAM_RELATION');
    expect(r.issues.map((i) => i.type)).not.toContain('INCORRECT_DIAGRAM_RELATION');
    const unclosed = new Map([['u.md', '# t\n\n```mermaid\nflowchart LR\n  A --> B\n']]);
    expect((await checkMermaid(unclosed, ws, cfg, miniProjectRoot)).issues.map((i) => i.type)).toContain('MERMAID_UNCLOSED_BLOCK');
  }, 60_000);
});
