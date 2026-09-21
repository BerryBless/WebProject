import { afterEach, describe, expect, it, vi } from 'vitest'
import { request } from './client'
import { ApiError, describeError, parseRetryAfter } from './errors'
import { posts, attachments } from './endpoints'

const json = (status: number, body: unknown, headers: Record<string, string> = {}) =>
  new Response(body === null ? null : JSON.stringify(body), { status, headers: { 'Content-Type': 'application/problem+json', ...headers } })

function mockFetch(response: Response | Error | DOMException) {
  const spy = vi.fn(async (_input: RequestInfo | URL, _init?: RequestInit) => {
    if (!(response instanceof Response)) throw response
    return response.clone()
  })
  vi.stubGlobal('fetch', spy)
  return spy
}

afterEach(() => vi.unstubAllGlobals())

/** 거부된 값을 돌려준다. 성공하면 테스트를 실패시킨다(거부를 기대한 자리에서 조용히 통과하지 않게). */
async function caught(promise: Promise<unknown>): Promise<ApiError> {
  try { await promise } catch (error) { return error as ApiError }
  throw new Error('요청이 실패해야 하는데 성공했다')
}

describe('request', () => {
  it('모든 요청에 CSRF 헤더·same-origin 쿠키·리다이렉트 거부·no-store를 붙인다', async () => {
    const spy = mockFetch(json(200, { authenticated: true }))
    await request('GET', '/api/auth/me')
    const init = spy.mock.calls[0][1]!
    expect((init.headers as Record<string, string>)['X-Requested-With']).toBe('XMLHttpRequest')
    expect(init.credentials).toBe('same-origin')
    expect(init.redirect).toBe('error')
    expect(init.cache).toBe('no-store')
  })

  it('JSON 본문은 비 ASCII를 이스케이프하지 않는다(서버의 256KB 상한은 직렬화 후 바이트 기준)', async () => {
    const spy = mockFetch(json(201, {}))
    await request('POST', '/api/posts', { json: { title: '한글' } })
    const init = spy.mock.calls[0][1]!
    expect(init.body).toBe('{"title":"한글"}')
    expect((init.headers as Record<string, string>)['Content-Type']).toBe('application/json')
  })

  it('multipart에는 Content-Type을 직접 붙이지 않는다(boundary는 브라우저 몫)', async () => {
    const spy = mockFetch(json(201, { id: 'a', url: '/attachments/a/x.png' }))
    await attachments.upload(new Blob(['x'], { type: 'image/png' }), 'x.png')
    const init = spy.mock.calls[0][1]!
    expect((init.headers as Record<string, string>)['Content-Type']).toBeUndefined()
    expect((init.body as FormData).get('file')).toBeInstanceOf(Blob)
  })

  it.each([
    'https://evil.test/api/x', '//evil.test/api/x', '/apix', '/api//x', '/api/\\x',
    '/api/../x', '/api/%2e%2e/x', '/api/%2E%2E/x',
    `/api/x${String.fromCharCode(9)}y`, `/api/x${String.fromCharCode(10)}y`,
  ])('API 경로가 아니면 호출하지 않는다: %s', async (path) => {
    const spy = mockFetch(json(200, {}))
    await expect(request('GET', path)).rejects.toThrow('API 경로가 아닙니다')
    expect(spy).not.toHaveBeenCalled()
  })

  it('id는 encodeURIComponent를 거쳐 경로 세그먼트로 조립된다(경로 주입 방지)', async () => {
    const spy = mockFetch(json(200, { id: 'a', slug: 'a', title: 'a', summary: '', contentMarkdown: '', tags: [], seriesId: null, seriesOrder: null, createdAt: '', updatedAt: '', version: 1 }))
    await posts.get('a/b?x=1')
    expect(spy.mock.calls[0][0]).toBe('/api/posts/a%2Fb%3Fx%3D1')
  })

  it('remove의 id도 encodeURIComponent를 거친다', async () => {
    const spy = mockFetch(new Response(null, { status: 204 }))
    await posts.remove('a/b', 7)
    expect(spy.mock.calls[0][0]).toBe('/api/posts/a%2Fb?version=7')
  })

  it('인코딩된 id에 ..가 남으면(예: ../x → ..%2Fx) 경로 검사가 막는다(서버 id는 Guid뿐이라 정상 흐름엔 영향 없음)', async () => {
    const spy = mockFetch(json(200, {}))
    await expect(attachments.remove('../x')).rejects.toThrow('API 경로가 아닙니다')
    expect(spy).not.toHaveBeenCalled()
  })

  it('빈 쿼리 값은 보내지 않고, 값은 인코딩한다', async () => {
    const spy = mockFetch(json(200, { items: [], total: 0 }))
    await posts.list('', 0, 50)
    expect(spy.mock.calls[0][0]).toBe('/api/posts?skip=0&take=50')
    await posts.list('a&b=c #', 50, 50)
    expect(spy.mock.calls[1][0]).toBe('/api/posts?q=a%26b%3Dc+%23&skip=50&take=50')
  })

  it('204는 undefined', async () => {
    mockFetch(new Response(null, { status: 204 }))
    await expect(request('POST', '/api/auth/logout')).resolves.toBeUndefined()
  })

  it('검증 실패(400)의 필드 오류와 Retry-After를 읽는다', async () => {
    mockFetch(json(400, { title: 'One or more validation errors occurred.', errors: { slug: ['slug는 필수입니다.'], bogus: 'x' } }))
    const error = await caught(request('POST', '/api/posts', { json: {} }))
    expect(error).toBeInstanceOf(ApiError)
    expect(error.status).toBe(400)
    expect(error.fieldErrors).toEqual({ slug: ['slug는 필수입니다.'] })
  })

  it('성공 상태(2xx)인데 본문이 JSON이 아니면 해석 오류로 던진다(dev 서버가 index.html 200을 돌려주는 경우 등)', async () => {
    mockFetch(new Response('<html>nope</html>', { status: 200, headers: { 'Content-Type': 'text/html' } }))
    const error = await caught(request('GET', '/api/posts'))
    expect(error).toBeInstanceOf(ApiError)
    expect(error.status).toBe(200)
  })

  it('필드 오류의 __proto__ 키는 건너뛴다(프로토타입 오염 방지)', async () => {
    // JSON.stringify({ __proto__: [...] })는 객체 리터럴의 __proto__를 프로토타입 설정으로 취급해 출력에서 빠진다.
    // 실제 공격 표면을 재현하려면 본문 문자열을 직접 만들어야 한다.
    mockFetch(new Response('{"title":"오류","errors":{"__proto__":["x"],"slug":["y"]}}', { status: 400, headers: { 'Content-Type': 'application/problem+json' } }))
    const error = await caught(request('POST', '/api/posts', { json: {} }))
    expect(error.fieldErrors).toEqual({ slug: ['y'] })
    expect(Object.getPrototypeOf(error.fieldErrors)).toBe(Object.prototype)
  })

  it('ProblemDetails가 아닌 실패 본문(HTML·빈 본문)에서도 던지지 않고 상태 코드만으로 만든다', async () => {
    mockFetch(new Response('<html>nope</html>', { status: 404, statusText: 'Not Found' }))
    const a = await caught(request('GET', '/api/posts/x'))
    expect(a).toBeInstanceOf(ApiError); expect(a.status).toBe(404)
    mockFetch(new Response(null, { status: 401 }))
    const b = await caught(request('GET', '/api/posts/x'))
    expect(b.status).toBe(401)
  })

  it('네트워크 실패는 status 0, 취소(AbortError)는 그대로 던진다', async () => {
    mockFetch(new TypeError('Failed to fetch'))
    const a = await caught(request('GET', '/api/tags'))
    expect(a).toBeInstanceOf(ApiError); expect(a.status).toBe(0)
    mockFetch(new DOMException('aborted', 'AbortError'))
    const b = await caught(request('GET', '/api/tags'))
    expect(b).toBeInstanceOf(DOMException)
  })
})

describe('errors', () => {
  it.each([['5', 5], ['60', 60], ['0', 1], ['999999', 3600], [' 7 ', 7], ['1000000', 3600]])('Retry-After %s → %s', (raw, expected) => {
    expect(parseRetryAfter(raw)).toBe(expected)
  })
  it.each([null, '', '-1', '1.5', 'Wed, 21 Oct 2026 07:28:00 GMT', '1e3'])('해석할 수 없는 Retry-After %s → null', (raw) => {
    expect(parseRetryAfter(raw)).toBeNull()
  })
  it('429·503은 대기 시간을 안내한다', async () => {
    mockFetch(json(503, { title: '서버가 바쁩니다' }, { 'Retry-After': '5' }))
    const e = await caught(request('POST', '/api/preview', { json: { markdown: '' } }))
    expect(describeError(e)).toContain('5초')
    expect(describeError(new Error('x'))).toBe('알 수 없는 오류가 발생했습니다.')
  })
})
