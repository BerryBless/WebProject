// @vitest-environment node
import { readdirSync, readFileSync, statSync } from 'node:fs'
import { join, relative } from 'node:path'
import { describe, expect, it } from 'vitest'
import { stripComments } from './stripComments'

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
  return { path: relative(ROOT, path).replaceAll('\\', '/'), raw, text: stripComments(raw) }
})
const offenders = (pattern: RegExp, allow: (path: string) => boolean = () => false) =>
  FILES.filter(f => !allow(f.path) && pattern.test(f.text)).map(f => f.path)
const countLines = (text: string, pattern: RegExp) => (text.match(pattern) ?? []).length

describe('소스 가드', () => {
  it('검사 대상이 비어 있지 않다(경로가 바뀌어 공집합으로 통과하지 않게)', () => {
    expect(FILES.length).toBeGreaterThan(15)
    expect(FILES.map(f => f.path)).toContain('components/PreviewPane.tsx')
  })

  it('주석 제거가 export·import 줄을 지우지 않는다(줄 주석 안의 /*가 블록 주석 정규식을 잘못 열어 그 뒤 코드를 삼키는 결함 재현)', () => {
    const broken = FILES.filter(f =>
      countLines(f.text, /^export /gm) !== countLines(f.raw, /^export /gm) ||
      countLines(f.text, /^import /gm) !== countLines(f.raw, /^import /gm))
    expect(broken.map(f => f.path)).toEqual([])
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
    expect(offenders(/allow-(scripts|same-origin|forms|popups|top-navigation|modals|downloads)/)).toEqual([])
  })

  it('fetch는 API 클라이언트 한 곳에서만 부른다(CSRF 헤더·same-origin·redirect 거부가 빠진 호출이 생기지 않게)', () => {
    expect(offenders(/\bfetch\s*\(|XMLHttpRequest\s*\(|navigator\.sendBeacon|new\s+WebSocket|new\s+EventSource/)).toEqual(['api/client.ts'])
  })

  it('외부 출처를 가리키는 URL이 없다(CSP default-src none — 글꼴·스크립트·이미지 CDN 금지, index.html도 포함)', () => {
    const indexHtml = { path: 'index.html', text: stripComments(readFileSync(join(ROOT, '..', 'index.html'), 'utf8')) }
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
