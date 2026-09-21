// @vitest-environment node
import { describe, expect, it } from 'vitest'
import { stripComments } from './stripComments'

describe('stripComments', () => {
  it('줄 주석 안의 /* 뒤의 코드가 살아남는다(1차 결함 재현: 경로 표기 Contracts/*.cs)', () => {
    const input = [
      '// 서버 DTO(PortfolioBlog.Api/Contracts/*.cs)의 JSON 모양. ASP.NET Core 기본 직렬화라 속성은 camelCase,',
      '// Guid·DateTimeOffset은 문자열, uint Version은 number(최대 4,294,967,295 — Number.MAX_SAFE_INTEGER 안)다.',
      '',
      'export interface AuthStatus { authenticated: boolean }',
    ].join('\n')
    const stripped = stripComments(input)
    expect(stripped).toContain('export interface AuthStatus { authenticated: boolean }')
    expect(stripped).not.toContain('서버 DTO')
  })

  it('한 줄로 닫히는 JSDoc 안의 // 뒤에 오는 코드가 살아남는다(2차 결함 재현: client.ts 11~16행을 흉내 낸 입력)', () => {
    const input = [
      'export interface RequestOptions {',
      '  /** 쿼리는 반드시 이 옵션으로 넘긴다 — 경로에 ?를 직접 붙이면 값 안의 //가 경로 검사에 걸린다. */',
      '  query?: Query',
      '  signal?: AbortSignal',
      '}',
    ].join('\n')
    const stripped = stripComments(input)
    expect(stripped).toContain('query?: Query')
    expect(stripped).toContain('signal?: AbortSignal')
    expect(stripped).toContain('}')
    expect(stripped).not.toContain('경로 검사에 걸린다')
  })

  it("'image/*' 문자열 뒤의 함수 본문과 그 뒤 JSDoc 사이의 코드가 살아남는다", () => {
    const input = [
      "export const ACCEPT = 'image/*'",
      'function f() {',
      '  return 1',
      '}',
      '/** doc */',
      'const x = 1',
    ].join('\n')
    const stripped = stripComments(input)
    expect(stripped).toContain("export const ACCEPT = 'image/*'")
    expect(stripped).toContain('function f() {')
    expect(stripped).toContain('return 1')
    expect(stripped).toContain('const x = 1')
    expect(stripped).not.toContain('doc')
  })

  it('문자열 리터럴("a//b"·`x//y`)에 든 // 뒤의 코드가 살아남는다', () => {
    const input = [
      'const a = "a//b"; const afterA = 1',
      'const b = `x//y`; const afterB = 2',
    ].join('\n')
    const stripped = stripComments(input)
    expect(stripped).toContain('afterA = 1')
    expect(stripped).toContain('afterB = 2')
    expect(stripped).toContain('"a//b"')
    expect(stripped).toContain('`x//y`')
  })

  it('정규식 리터럴(/\\/\\//·/[\'"]/) 뒤의 코드가 살아남는다', () => {
    const input = [
      'const a = /\\/\\//',
      "const b = /['\"]/",
      'const after = 1',
    ].join('\n')
    const stripped = stripComments(input)
    expect(stripped).toContain('const after = 1')
    expect(stripped).toContain('const a = /\\/\\//')
  })

  it('여는 괄호와 같은 줄의 주석만 있는 빈 catch 블록도 주석이 지워진다(errors.ts·drafts.ts 실제 코드 재현)', () => {
    const input = [
      'try { doSomething() } catch { /* ProblemDetails가 아닌 본문 */ }',
      'const after = 1',
    ].join('\n')
    const stripped = stripComments(input)
    expect(stripped).toContain('try { doSomething() } catch {')
    expect(stripped).toContain('const after = 1')
    expect(stripped).not.toContain('ProblemDetails')
  })

  it('여는 괄호와 같은 줄에 있고 그 뒤에 실제 문장이 있는 주석도 지워진다(PostEditorPage.tsx의 for 루프 재현)', () => {
    const input = [
      'for (let i = 0; i < 3; i++) { // 순차 업로드',
      '  doUpload(i)',
      '}',
    ].join('\n')
    const stripped = stripComments(input)
    expect(stripped).toContain('doUpload(i)')
    expect(stripped).not.toContain('순차 업로드')
  })

  it('JSX 텍스트 안의 //는 주석이 아니다 — 그대로 남는다', () => {
    const input = 'const el = <p>a // b</p>'
    const stripped = stripComments(input)
    expect(stripped).toContain('a // b')
  })

  it('{/* JSX 주석 */}·/** JSDoc */·줄 끝 // 설명은 공백이 된다', () => {
    const input = '<div>{/* JSX 주석 */}</div>\n/** JSDoc */\nconst x = 1 // 설명'
    const stripped = stripComments(input)
    expect(stripped).not.toContain('JSX 주석')
    expect(stripped).not.toContain('JSDoc')
    expect(stripped).not.toContain('설명')
    expect(stripped).toContain('const x = 1')
  })

  it('출력의 길이와 줄 수가 입력과 같다', () => {
    const input = [
      '// 줄 주석',
      '/** JSDoc',
      ' * 여러 줄',
      ' */',
      'const x = 1 // 끝 주석',
      '',
      '<div>{/* jsx */}</div>',
    ].join('\n')
    const stripped = stripComments(input)
    expect(stripped.length).toBe(input.length)
    expect(stripped.split('\n').length).toBe(input.split('\n').length)
  })
})
