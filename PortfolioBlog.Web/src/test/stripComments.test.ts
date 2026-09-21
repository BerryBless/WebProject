// @vitest-environment node
import { describe, expect, it } from 'vitest'
import { stripComments } from './stripComments'

describe('stripComments', () => {
  it('줄 주석 안의 /* 뒤의 코드와 그 뒤 JSDoc 뒤의 코드가 살아남는다', () => {
    const input = [
      '// 서버 DTO(PortfolioBlog.Api/Contracts/*.cs)의 JSON 모양. ASP.NET Core 기본 직렬화라 속성은 camelCase,',
      '// Guid·DateTimeOffset은 문자열, uint Version은 number(최대 4,294,967,295 — Number.MAX_SAFE_INTEGER 안)다.',
      '',
      'export interface AuthStatus { authenticated: boolean }',
      '',
      '/** 생성·수정 공용 요청. 연결(tagNames·seriesId·seriesOrder)은 통째로 교체된다. version은 수정에서만 필수. */',
      'export interface UpsertPostRequest {',
      '  slug: string; title: string',
      '}',
    ].join('\n')
    const stripped = stripComments(input)
    expect(stripped).toContain('export interface AuthStatus { authenticated: boolean }')
    expect(stripped).toContain('export interface UpsertPostRequest {')
    expect(stripped).toContain('slug: string; title: string')
  })

  it('URL·템플릿 문자열·문자열 리터럴에 들어있는 // 뒤의 코드가 살아남는다', () => {
    const input = [
      "const a = 'https://a.test/x'; const afterA = 1",
      'const b = `//x`; const afterB = 2',
      'const c = "//b"; const afterC = 3',
    ].join('\n')
    const stripped = stripComments(input)
    expect(stripped).toContain('afterA = 1')
    expect(stripped).toContain('afterB = 2')
    expect(stripped).toContain('afterC = 3')
  })

  it('JSX 주석과 JSDoc은 지워진다', () => {
    const input = '<div>{/* JSX 주석 */}</div>\n/** JSDoc */\nconst x = 1'
    const stripped = stripComments(input)
    expect(stripped).not.toContain('JSX 주석')
    expect(stripped).not.toContain('JSDoc')
    expect(stripped).toContain('const x = 1')
  })

  it('줄 끝 라인 주석은 지워지고 앞의 코드는 남는다', () => {
    const stripped = stripComments('const x = 1 // 설명')
    expect(stripped).toContain('const x = 1')
    expect(stripped).not.toContain('설명')
  })

  it('정규식 리터럴 뒤의 코드가 살아남는다', () => {
    const stripped = stripComments('const doubleSlash = /\\/\\//\nconst after = 1')
    expect(stripped).toContain('const after = 1')
  })
})
