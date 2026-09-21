import { describe, expect, it } from 'vitest'
import { hasControlChar, safeNext } from './safeNext'
import { buildPreviewDocument, previewCsp } from './previewDoc'
import { clearDraft, loadDraft, saveDraft, sameFields, type Draft } from './drafts'
import { LIMITS, displayTag, utf8ByteLength, validateImageFile, validatePost, validateSeries } from './validation'
import type { UpsertPostRequest } from '../api/types'

const ORIGIN = 'https://admin.blog.test'

describe('safeNext', () => {
  it.each([
    ['/posts/new', '/posts/new'], ['/posts/0199?x=1#top', '/posts/0199?x=1#top'], ['/', '/'],
  ])('같은 출처 경로는 그대로: %s', (raw, expected) => expect(safeNext(raw, ORIGIN)).toBe(expected))

  it.each([
    null, '', 'posts', 'https://evil.test/', '//evil.test/x', '/\\evil.test/x', 'javascript:alert(1)',
    '/login', '/login?next=/x', '/a' + String.fromCharCode(10) + 'b', '/a' + String.fromCharCode(0), '/' + 'a'.repeat(2048),
  ])('그 밖은 전부 "/": %s', (raw) => expect(safeNext(raw, ORIGIN)).toBe('/'))

  it('제어 문자 판정', () => {
    expect(hasControlChar('abc 한글')).toBe(false)
    expect(hasControlChar('a' + String.fromCharCode(0x1f))).toBe(true)
    expect(hasControlChar('a' + String.fromCharCode(0x7f))).toBe(true)
  })
})

describe('previewDoc', () => {
  it('CSP는 출처를 명시하고 self를 쓰지 않는다(Firefox는 srcdoc의 self를 부모 출처로 보지 않는다)', () => {
    const csp = previewCsp(ORIGIN)
    expect(csp).toBe(`default-src 'none'; img-src ${ORIGIN}; style-src ${ORIGIN}; base-uri 'none'; form-action 'none'`)
    expect(csp).not.toContain("'self'")
    expect(csp).not.toContain('script-src')
  })
  it.each(["https://a.test; script-src *", "https://a.test'", 'https://a.test/path', 'javascript:x', '', 'https://a b'])('이상한 출처는 거부: %s', (origin) => {
    expect(() => previewCsp(origin)).toThrow()
  })
  it('CSP meta가 head의 첫 요소이고, 서버 HTML은 article-body 안에만 들어간다', () => {
    const doc = buildPreviewDocument('<p>본문</p>', 'https://localhost:5173')
    expect(doc.indexOf('<meta http-equiv="Content-Security-Policy"')).toBe(doc.indexOf('<head>') + '<head>'.length)
    expect(doc).toContain('<div class="article-body"><p>본문</p></div>')
    expect(doc).toContain('<link rel="stylesheet" href="/preview/site.css"><link rel="stylesheet" href="/preview/highlight.css">')
    expect(doc).not.toContain('<script')
  })
})

describe('drafts', () => {
  const memory = (): Storage => {
    const map = new Map<string, string>()
    return { getItem: k => map.get(k) ?? null, setItem: (k, v) => void map.set(k, v), removeItem: k => void map.delete(k),
      clear: () => map.clear(), key: i => [...map.keys()][i] ?? null, get length() { return map.size } }
  }
  const draft: Draft = { slug: 's', title: 't', summary: '', contentMarkdown: '# 본문', tagNames: ['a'], seriesId: null, seriesOrder: null, baseVersion: 7, savedAt: '2026-09-21T00:00:00.000Z' }

  it('저장·조회·삭제는 글별 키로 분리된다', () => {
    const s = memory()
    expect(saveDraft('id-1', draft, s)).toBe(true)
    expect(loadDraft('id-1', s)).toEqual(draft)
    expect(loadDraft('id-2', s)).toBeNull()
    clearDraft('id-1', s)
    expect(loadDraft('id-1', s)).toBeNull()
  })
  it.each(['{', '[]', 'null', '{"slug":1}', JSON.stringify({ ...draft, tagNames: [1] }), JSON.stringify({ ...draft, baseVersion: 'x' })])('깨진 값은 없는 것으로 본다: %s', (raw) => {
    const s = memory(); s.setItem('pb.draft.v1:x', raw)
    expect(loadDraft('x', s)).toBeNull()
  })
  it('용량 초과로 저장이 실패해도 던지지 않는다', () => {
    const s = memory(); s.setItem = () => { throw new DOMException('quota', 'QuotaExceededError') }
    expect(saveDraft('x', draft, s)).toBe(false)
  })
  it('sameFields는 태그 순서까지 본다', () => {
    expect(sameFields(draft, { ...draft })).toBe(true)
    expect(sameFields({ ...draft, tagNames: ['a', 'b'] }, { ...draft, tagNames: ['b', 'a'] })).toBe(false)
    expect(sameFields(draft, { ...draft, contentMarkdown: '# 본문 ' })).toBe(false)
  })
})

describe('validation', () => {
  const ok: UpsertPostRequest = { slug: 'my-first-post', title: '제목', summary: '', contentMarkdown: '', tagNames: [], seriesId: null, seriesOrder: null }
  it('정상 입력은 오류가 없다', () => expect(validatePost(ok)).toEqual({}))
  it.each(['', 'My-Post', 'a--b', '-a', 'a-', 'a_b', '한글', 'a'.repeat(101)])('slug 거부: %s', (slug) => {
    expect(validatePost({ ...ok, slug }).slug).toBeDefined()
  })
  it('slug 100자는 통과', () => expect(validatePost({ ...ok, slug: 'a'.repeat(100) })).toEqual({}))
  it('제목은 트림 후 1~200자', () => {
    expect(validatePost({ ...ok, title: '   ' }).title).toBeDefined()
    expect(validatePost({ ...ok, title: ' ' + '가'.repeat(200) + ' ' })).toEqual({})
    expect(validatePost({ ...ok, title: '가'.repeat(201) }).title).toBeDefined()
  })
  it('본문은 글자 수가 아니라 UTF-8 바이트로 잰다', () => {
    expect(utf8ByteLength('가')).toBe(3)
    expect(validatePost({ ...ok, contentMarkdown: '가'.repeat(68_266) })).toEqual({})           // 204,798바이트
    expect(validatePost({ ...ok, contentMarkdown: '가'.repeat(68_267) }).contentMarkdown).toBeDefined() // 204,801바이트
    expect(validatePost({ ...ok, contentMarkdown: 'a'.repeat(LIMITS.contentMaxBytes) })).toEqual({})
  })
  it('NUL은 모든 텍스트 필드에서 거부', () => {
    const nul = String.fromCharCode(0)
    const errors = validatePost({ ...ok, title: 'a' + nul, summary: nul, contentMarkdown: nul, tagNames: ['x' + nul] })
    expect(Object.keys(errors).sort()).toEqual(['contentMarkdown', 'summary', 'tagNames', 'title'])
  })
  it('태그: 공백 정리·빈 값 무시·슬래시 거부·50자·대소문자 무시 20개', () => {
    expect(displayTag('  C#   고급  ')).toBe('C# 고급')
    expect(validatePost({ ...ok, tagNames: ['', '  ', 'a'] })).toEqual({})
    expect(validatePost({ ...ok, tagNames: ['a/b'] }).tagNames).toBeDefined()
    expect(validatePost({ ...ok, tagNames: ['가'.repeat(51)] }).tagNames).toBeDefined()
    const twenty = Array.from({ length: 20 }, (_, i) => `t${i}`)
    expect(validatePost({ ...ok, tagNames: [...twenty, 'T0'] })).toEqual({})   // 대소문자만 다른 중복은 1개로 센다
    expect(validatePost({ ...ok, tagNames: [...twenty, 't20'] }).tagNames).toBeDefined()
  })
  it('시리즈와 순서는 쌍으로, 순서는 1 이상의 정수', () => {
    expect(validatePost({ ...ok, seriesId: 'x', seriesOrder: null }).seriesOrder).toBeDefined()
    expect(validatePost({ ...ok, seriesId: null, seriesOrder: 1 }).seriesOrder).toBeDefined()
    expect(validatePost({ ...ok, seriesId: 'x', seriesOrder: 0 }).seriesOrder).toBeDefined()
    expect(validatePost({ ...ok, seriesId: 'x', seriesOrder: 1.5 }).seriesOrder).toBeDefined()
    expect(validatePost({ ...ok, seriesId: 'x', seriesOrder: 1 })).toEqual({})
  })
  it('시리즈 설명 1000자', () => {
    expect(validateSeries({ slug: 's', title: 't', description: '가'.repeat(1000) })).toEqual({})
    expect(validateSeries({ slug: 's', title: 't', description: '가'.repeat(1001) }).description).toBeDefined()
  })
  it('이미지 사전 검사', () => {
    expect(validateImageFile({ size: 10, type: 'image/png' })).toBeNull()
    expect(validateImageFile({ size: 0, type: 'image/png' })).not.toBeNull()
    expect(validateImageFile({ size: LIMITS.attachmentMaxBytes + 1, type: 'image/png' })).not.toBeNull()
    expect(validateImageFile({ size: 10, type: 'image/svg+xml' })).not.toBeNull()
  })
})
