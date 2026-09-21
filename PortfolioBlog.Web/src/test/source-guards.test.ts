// @vitest-environment node
import { readdirSync, readFileSync, statSync } from 'node:fs'
import { join, relative } from 'node:path'
import { describe, expect, it } from 'vitest'

// 소스 전체를 훑는 닫힌 세계 검사: "쓰지 않기로 한 것"이 코드 리뷰를 빠져나가 들어오는 것을 막는다.
const ROOT = join(__dirname, '..')

function sources(dir: string): string[] {
  return readdirSync(dir).flatMap(name => {
    const path = join(dir, name)
    if (statSync(path).isDirectory()) return name === 'test' ? [] : sources(path)
    return /\.(ts|tsx)$/.test(name) && !/\.test\.tsx?$/.test(name) ? [path] : []
  })
}

/** 주석을 뺀다: "쓰지 말 것"을 설명하는 주석이 검사에 걸리지 않게. 줄 주석은 따옴표·콜론 바로 뒤의 //(URL·문자열)는 건드리지 않는다. */
const stripComments = (text: string) => text.replace(/\/\*[\s\S]*?\*\//g, '').replace(/(^|[^:'"`\\])\/\/.*$/gm, '$1')

const FILES = sources(ROOT).map(path => ({ path: relative(ROOT, path).replaceAll('\\', '/'), text: stripComments(readFileSync(path, 'utf8')) }))
const offenders = (pattern: RegExp, allow: (path: string) => boolean = () => false) =>
  FILES.filter(f => !allow(f.path) && pattern.test(f.text)).map(f => f.path)

describe('소스 가드', () => {
  it('검사 대상이 비어 있지 않다(경로가 바뀌어 공집합으로 통과하지 않게)', () => {
    expect(FILES.length).toBeGreaterThan(15)
    expect(FILES.map(f => f.path)).toContain('components/PreviewPane.tsx')
  })

  it('서버 HTML을 React DOM에 넣는 경로가 없다', () => {
    expect(offenders(/dangerouslySetInnerHTML|\.innerHTML|\.outerHTML|insertAdjacentHTML|document\.write|DOMParser/)).toEqual([])
  })

  it('문자열을 코드로 실행하는 경로가 없다', () => {
    expect(offenders(/\beval\s*\(|new\s+Function\s*\(|set(Timeout|Interval)\s*\(\s*['"`]/)).toEqual([])
  })

  it('iframe은 PreviewPane 한 곳이고 sandbox 토큰이 비어 있다', () => {
    expect(offenders(/<iframe/)).toEqual(['components/PreviewPane.tsx'])
    const pane = FILES.find(f => f.path === 'components/PreviewPane.tsx')!.text
    expect(pane).toMatch(/<iframe[^>]*\ssandbox=""/)
    expect(pane).not.toMatch(/allow-(scripts|same-origin|forms|popups|top-navigation)/)
  })

  it('fetch는 API 클라이언트 한 곳에서만 부른다(CSRF 헤더·same-origin·redirect 거부가 빠진 호출이 생기지 않게)', () => {
    expect(offenders(/\bfetch\s*\(|XMLHttpRequest\s*\(|navigator\.sendBeacon|new\s+WebSocket|new\s+EventSource/)).toEqual(['api/client.ts'])
  })

  it('외부 출처를 가리키는 URL이 없다(CSP default-src none — 글꼴·스크립트·이미지 CDN 금지)', () => {
    expect(offenders(/["'`](https?:)?\/\/[a-z0-9]/i)).toEqual([])
  })

  it('세션·비밀번호를 브라우저 저장소에 두지 않는다: 저장소 접근은 임시본 모듈뿐이다', () => {
    expect(offenders(/localStorage|sessionStorage|indexedDB|document\.cookie/)).toEqual(['lib/drafts.ts'])
  })

  it('새 창을 여는 링크가 없다(있다면 rel="noopener noreferrer"를 강제하는 검사로 바꿀 것)', () => {
    expect(offenders(/target=["']_blank["']|window\.open\s*\(/)).toEqual([])
  })
})
