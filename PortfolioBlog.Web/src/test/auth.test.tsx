import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { LOGGED_IN, renderApp, stubApi } from './harness'

afterEach(() => vi.unstubAllGlobals())
const EMPTY_LIST = { status: 200, body: { items: [], total: 0 } }

describe('인증 흐름', () => {
  it('로그인하지 않았으면 가려던 경로를 next에 담아 로그인 화면으로 보낸다', async () => {
    stubApi({ 'GET /api/auth/me': { status: 200, body: { authenticated: false } } })
    const { router } = renderApp('/series?x=1')
    await screen.findByRole('heading', { name: '관리자 로그인' })
    expect(router.state.location.pathname).toBe('/login')
    expect(router.state.location.search).toBe('?next=' + encodeURIComponent('/series?x=1'))
  })

  it('로그인에 성공하면 next로 가고, 비밀번호 입력은 지워진다', async () => {
    const calls = stubApi({ 'POST /api/auth/login': { status: 204 }, ...LOGGED_IN, 'GET /api/tags': { status: 200, body: [] } })
    const { router } = renderApp('/login?next=%2Ftags')
    await userEvent.type(screen.getByLabelText('비밀번호'), 'dummy-pass')
    await userEvent.click(screen.getByRole('button', { name: '로그인' }))
    await screen.findByRole('heading', { name: '태그' })
    expect(router.state.location.pathname).toBe('/tags')
    expect(calls.find(c => c.url === '/api/auth/login')?.body).toEqual({ password: 'dummy-pass' })
  })

  it.each(['https://evil.test/', '//evil.test', '/\\evil.test', '/login'])('적대적 next(%s)는 무시하고 "/"로 간다', async (next) => {
    stubApi({ 'POST /api/auth/login': { status: 204 }, ...LOGGED_IN, 'GET /api/posts': EMPTY_LIST })
    const { router } = renderApp('/login?next=' + encodeURIComponent(next))
    await userEvent.type(screen.getByLabelText('비밀번호'), 'dummy-pass')
    await userEvent.click(screen.getByRole('button', { name: '로그인' }))
    await screen.findByRole('heading', { name: '글' })
    expect(router.state.location.pathname).toBe('/')
  })

  it('비밀번호가 틀리면 안내하고 입력을 지운다. 429는 대기 시간을 알린다', async () => {
    let status = 401
    stubApi({ 'POST /api/auth/login': () => status === 401 ? { status, body: { title: '로그인 실패' } } : { status, body: { title: 'x' }, headers: { 'Retry-After': '42' } } })
    renderApp('/login')
    const input = screen.getByLabelText('비밀번호') as HTMLInputElement
    await userEvent.type(input, 'wrong-dummy')
    await userEvent.click(screen.getByRole('button', { name: '로그인' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('비밀번호가 맞지 않습니다.')
    expect(input.value).toBe('')
    status = 429
    await userEvent.type(input, 'wrong-dummy')
    await userEvent.click(screen.getByRole('button', { name: '로그인' }))
    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent('42초'))
  })

  it('비밀번호는 mutation 캐시에 변수로 남지 않는다(실패·성공 모두)', async () => {
    // useState를 지우는 것만으로는 부족하다: mutate에 넘긴 변수는 MutationCache의 항목에 그대로 남아
    // 관찰자가 사라진 뒤에도 기본 gcTime(5분) 동안 메모리에 있다(실패한 시도도 마찬가지).
    let status = 401
    stubApi({ 'POST /api/auth/login': () => status === 401 ? { status, body: { title: '로그인 실패' } } : { status: 204 }, ...LOGGED_IN, 'GET /api/posts': EMPTY_LIST })
    const { client } = renderApp('/login')
    const input = screen.getByLabelText('비밀번호')
    await userEvent.type(input, 'wrong-dummy')
    await userEvent.click(screen.getByRole('button', { name: '로그인' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('비밀번호가 맞지 않습니다.')
    expect(client.getMutationCache().getAll().length).toBeGreaterThan(0) // 검사할 항목이 실제로 있다
    expect(client.getMutationCache().getAll().every(m => m.state.variables === undefined)).toBe(true)

    status = 204
    await userEvent.type(input, 'dummy-pass')
    await userEvent.click(screen.getByRole('button', { name: '로그인' }))
    await screen.findByRole('heading', { name: '글' })
    expect(client.getMutationCache().getAll().length).toBeGreaterThan(0)
    expect(client.getMutationCache().getAll().every(m => m.state.variables === undefined)).toBe(true)
  })

  it('화면을 쓰는 중에 401이 오면(세션 만료) 로그인 화면으로 돌아간다', async () => {
    stubApi({ ...LOGGED_IN, 'GET /api/tags': { status: 401 } })
    const { router } = renderApp('/tags')
    await screen.findByRole('heading', { name: '관리자 로그인' })
    expect(router.state.location.search).toBe('?next=%2Ftags')
  })

  it('시리즈를 만드는 중에 401이 오면 로그인 화면으로 간다', async () => {
    // SeriesForm은 useMutation 없이 onSubmit을 직접 await한다 — MutationCache.onError를 거치지 않으므로
    // 401을 스스로 기록해야 한다(고치기 전에는 /series에 머문다).
    stubApi({ ...LOGGED_IN, 'GET /api/series': { status: 200, body: [] }, 'POST /api/series': { status: 401 } })
    const { router } = renderApp('/series')
    await userEvent.type(await screen.findByLabelText('제목'), '제목입니다')
    await userEvent.type(screen.getByLabelText(/^slug/), 'my-series')
    await userEvent.click(screen.getByRole('button', { name: '만들기' }))
    await screen.findByRole('heading', { name: '관리자 로그인' })
    expect(router.state.location.search).toBe('?next=%2Fseries')
  })

  it('로그아웃하면 로그인 화면으로 간다', async () => {
    let loggedIn = true // 실제 서버처럼 로그아웃 뒤의 /me는 false를 돌려준다
    const calls = stubApi({
      'GET /api/auth/me': () => ({ status: 200, body: { authenticated: loggedIn } }),
      'GET /api/tags': { status: 200, body: [] },
      'POST /api/auth/logout': () => { loggedIn = false; return { status: 204 } },
    })
    const { client } = renderApp('/tags')
    await userEvent.click(await screen.findByRole('button', { name: '로그아웃' }))
    await screen.findByRole('heading', { name: '관리자 로그인' })
    expect(calls.some(c => c.method === 'POST' && c.url === '/api/auth/logout')).toBe(true)
    // 다음 사람이 같은 브라우저를 쓸 수 있다 — 앞사람의 mutation 기록(변수·결과)도 함께 버린다.
    expect(client.getMutationCache().getAll()).toEqual([])
  })
})
