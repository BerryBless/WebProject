// @vitest-environment node
import { readdirSync, readFileSync, statSync } from 'node:fs'
import { join, relative } from 'node:path'
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

describe('소스 가드', () => {
  it('검사 대상이 비어 있지 않다(경로가 바뀌어 공집합으로 통과하지 않게)', () => {
    expect(FILES.length).toBeGreaterThan(15)
    expect(FILES.map(f => f.path)).toContain('components/PreviewPane.tsx')
  })

  // 정규식으로 흉내 낸 "줄 수가 같다" 자기 검사는 구조적으로 무력하다 — 가드가 쫓는 위반은 대개 블록·함수 몸통
  // 안(들여쓴 줄)에 있고, export·import 줄 수는 블록 안에서 몇 줄이 사라지든 바뀌지 않는다(실측: 재현 가능).
  // 그래서 문자 단위 불변식으로 바꾼다 — stripComments의 공개 계약(collectCommentRanges가 찾은 범위만 공백으로
  // 바뀐다) 그 자체를 파일마다 직접 확인한다. stripComments 내부 구현을 신뢰하지 않고 그 출력만 본다.
  it('주석 제거가 코드를 지우지 않는다: 길이·문자·주석 범위의 구조적 불변식', () => {
    const violations: string[] = []
    for (const f of FILES) {
      if (f.text.length !== f.raw.length) {
        violations.push(`${f.path}: 길이가 다르다(원본 ${f.raw.length}, 제거본 ${f.text.length})`)
        continue
      }
      const ranges = collectCommentRanges(f.raw, f.path)
      // (c) 각 주석 범위의 원본 텍스트가 실제로 //나 /*로 시작한다 — collectCommentRanges가 주석이 아닌 것을
      // 주석으로 잘못 판단하지 않았는지 확인한다.
      const blanked = new Set<number>()
      for (const { pos, end } of ranges) {
        const original = f.raw.slice(pos, end)
        if (!(original.startsWith('//') || original.startsWith('/*'))) {
          violations.push(`${f.path}:${pos} 주석이 아닌 구간을 지웠다: ${JSON.stringify(original.slice(0, 30))}`)
        }
        for (let i = pos; i < end; i++) if (f.raw[i] !== '\n' && f.raw[i] !== '\r') blanked.add(i)
      }
      // (a)(b) 바뀐 위치는 전부 공백이고, 바뀐 위치는 전부 선언된 주석 범위 안에 있다(stripComments가
      // collectCommentRanges 밖의 문자를 지우지 않았다는 것을 출력만 보고 확인한다).
      for (let i = 0; i < f.raw.length; i++) {
        if (f.text[i] === f.raw[i]) continue
        if (f.text[i] !== ' ') { violations.push(`${f.path}:${i} 공백이 아닌 다른 문자로 바뀌었다(원본 ${JSON.stringify(f.raw[i])})`); break }
        if (!blanked.has(i)) { violations.push(`${f.path}:${i} 선언된 주석 범위 밖에서 지워졌다`); break }
      }
    }
    expect(violations).toEqual([])
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
    // sandbox 토큰 이름을 낱낱이 나열하지 않는다 — 새 토큰(pointer-lock 등)이 추가돼도 놓치지 않게 접두사로 잡는다.
    expect(offenders(/allow-[a-z-]+/)).toEqual([])
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
