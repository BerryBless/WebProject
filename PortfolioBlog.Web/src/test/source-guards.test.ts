// @vitest-environment node
import { readdirSync, readFileSync, statSync } from 'node:fs'
import { join, relative } from 'node:path'
import ts from 'typescript'
import { describe, expect, it } from 'vitest'
import { collectCommentRanges, stripComments } from './stripComments'

// 소스를 정규식으로 훑는 검사다: 실수로 들어오는 금지 패턴을 잡는다. 문자열 조립(예: el['inner' + 'HTML'])이나
// 별칭(예: const f = fetch)처럼 의도적으로 정규식을 피해 가는 코드는 잡지 못한다 — 그런 우회를 막는 일은 사람이 하는
// 코드 검토와 CSP(Content-Security-Policy)의 몫이다.
const ROOT = join(__dirname, '..')

function sources(dir: string): string[] {
  return readdirSync(dir).flatMap(name => {
    const path = join(dir, name)
    if (statSync(path).isDirectory()) return name === 'test' ? [] : sources(path)
    return /\.(ts|tsx)$/.test(name) && !/\.test\.tsx?$/.test(name) ? [path] : []
  })
}

const FILES = sources(ROOT).map(path => {
  const raw = readFileSync(path, 'utf8')
  const relPath = relative(ROOT, path).replaceAll('\\', '/')
  return { path: relPath, raw, text: stripComments(raw, relPath) }
})
const offenders = (pattern: RegExp, allow: (path: string) => boolean = () => false) =>
  FILES.filter(f => !allow(f.path) && pattern.test(f.text)).map(f => f.path)

/**
 * stripComments.ts와 무관하게(따로 옮겨 적은 코드로) 파일 하나의 실제 코드 토큰 span 목록을 구한다.
 * "stripComments가 스스로 무엇을 주석이라 선언했는가"를 믿지 않고, 같은 typescript 패키지로 이 테스트가
 * 직접 다시 파싱해 "진짜 코드가 어디 있는가"를 구한다 — 그래서 stripComments가 주석이 아닌 구간을 주석으로
 * 잘못 선언해도(예: 문자열 안의 //부터를 가짜 범위로 선언) 이 검사는 속지 않는다.
 */
function isJsDocKind(kind: ts.SyntaxKind): boolean {
  return kind >= ts.SyntaxKind.FirstJSDocNode && kind <= ts.SyntaxKind.LastJSDocNode
}
function codeTokenSpans(text: string, fileName: string): { start: number; end: number }[] {
  const sourceFile = ts.createSourceFile(fileName, text, ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX)
  const spans: { start: number; end: number }[] = []
  const visit = (node: ts.Node): void => {
    if (isJsDocKind(node.kind)) return
    const children = node.getChildren(sourceFile)
    if (children.length === 0) {
      const start = node.getStart(sourceFile)
      const end = node.getEnd()
      if (end > start) spans.push({ start, end }) // 폭이 0인 토큰(EndOfFileToken 등)은 뺀다
      return
    }
    for (const child of children) visit(child)
  }
  visit(sourceFile)
  return spans
}

describe('소스 가드', () => {
  it('검사 대상이 비어 있지 않다(경로가 바뀌어 공집합으로 통과하지 않게)', () => {
    expect(FILES.length).toBeGreaterThan(15)
    expect(FILES.map(f => f.path)).toContain('components/PreviewPane.tsx')
  })

  it('주석 제거가 실제 코드 토큰을 건드리지 않는다(토큰 span과 대조 — stripComments의 범위 선언을 믿지 않는다)', () => {
    const violations: string[] = []
    for (const f of FILES) {
      if (f.text.length !== f.raw.length) { violations.push(`${f.path}: 길이가 다르다(원본 ${f.raw.length}, 제거본 ${f.text.length})`); continue }
      const spans = codeTokenSpans(f.raw, f.path)
      for (let i = 0; i < f.raw.length; i++) {
        if (f.text[i] === f.raw[i]) continue
        if (f.text[i] !== ' ') { violations.push(`${f.path}:${i} 공백이 아닌 다른 문자로 바뀌었다(원본 ${JSON.stringify(f.raw[i])})`); break }
        const inCodeToken = spans.some(s => i >= s.start && i < s.end)
        if (inCodeToken) { violations.push(`${f.path}:${i} 실제 코드 토큰 안을 지웠다`); break }
      }
    }
    expect(violations).toEqual([])
  })

  it('주석 제거가 모든 주석을 지운다(완전성 — 제거본을 다시 파싱하면 남은 주석이 0개)', () => {
    const incomplete = FILES
      .map(f => ({ path: f.path, remaining: collectCommentRanges(f.text, f.path) }))
      .filter(f => f.remaining.length > 0)
    expect(incomplete.map(f => `${f.path}:${f.remaining.length}`)).toEqual([])
  })

  it('서버 HTML을 React DOM에 넣는 경로가 없다', () => {
    expect(offenders(/dangerouslySetInnerHTML|\.innerHTML|\.outerHTML|insertAdjacentHTML|document\.write|DOMParser|createContextualFragment|setHTMLUnsafe|parseHTMLUnsafe/)).toEqual([])
  })

  it('문자열을 코드로 실행하는 경로가 없다', () => {
    expect(offenders(/\beval\s*\(|new\s+Function\s*\(|set(Timeout|Interval)\s*\(\s*['"`]/)).toEqual([])
  })

  it('iframe은 PreviewPane 한 곳이고 sandbox 토큰이 비어 있다', () => {
    expect(offenders(/<iframe|createElement\(\s*['"`]iframe|\.srcdoc\s*=|setAttribute\(\s*['"`]srcdoc/i)).toEqual(['components/PreviewPane.tsx'])
    const pane = FILES.find(f => f.path === 'components/PreviewPane.tsx')!.text
    expect(pane).toMatch(/<iframe[^>]*\ssandbox=""/)
    // sandbox 토큰 이름을 낱낱이 나열하지 않는다 — 새 토큰(pointer-lock 등)이나 대문자 표기가 추가돼도 놓치지 않게
    // 접두사로 잡는다(브라우저는 sandbox 토큰의 대소문자를 가리지 않는다).
    expect(offenders(/allow-[a-z-]+/i)).toEqual([])
  })

  it('fetch는 API 클라이언트 한 곳에서만 부른다(CSRF 헤더·same-origin·redirect 거부가 빠진 호출이 생기지 않게)', () => {
    expect(offenders(/\bfetch\s*\(|XMLHttpRequest\s*\(|navigator\.sendBeacon|new\s+WebSocket|new\s+EventSource/)).toEqual(['api/client.ts'])
  })

  it('외부 출처를 가리키는 URL이 없다(CSP default-src none — 글꼴·스크립트·이미지 CDN 금지, index.html도 포함)', () => {
    // index.html은 JS/TS가 아니다 — stripComments(TypeScript 파서)를 돌리지 않고 원문 그대로 검사한다.
    const indexHtml = { path: 'index.html', text: readFileSync(join(ROOT, '..', 'index.html'), 'utf8') }
    const targets = [...FILES, indexHtml]
    const found = targets.filter(f => /["'`](https?:)?\/\/[a-z0-9]/i.test(f.text)).map(f => f.path)
    expect(found).toEqual([])
  })

  it('세션·비밀번호를 브라우저 저장소에 두지 않는다: 저장소 접근은 임시본 모듈뿐이다', () => {
    expect(offenders(/localStorage|sessionStorage|indexedDB|document\.cookie/)).toEqual(['lib/drafts.ts'])
  })

  it('새 창을 여는 링크가 없다(있다면 rel="noopener noreferrer"를 강제하는 검사로 바꿀 것)', () => {
    expect(offenders(/target=["']_blank["']|window\.open\s*\(/)).toEqual([])
  })
})
