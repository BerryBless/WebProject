import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { LOGGED_IN, renderApp, stubApi } from './harness'

afterEach(() => { vi.unstubAllGlobals(); vi.restoreAllMocks() })

describe('글 목록', () => {
  const post = { id: 'p1', slug: 'hello', title: '안녕', summary: '', tags: ['a'], seriesId: null, seriesOrder: null, createdAt: '2026-09-01T00:00:00Z', updatedAt: '2026-09-02T00:00:00Z', version: 41 }

  it('삭제는 확인을 거치고, 목록에서 받은 version을 쿼리로 보낸다', async () => {
    const calls = stubApi({ ...LOGGED_IN, 'GET /api/posts': { status: 200, body: { items: [post], total: 1 } }, 'DELETE /api/posts/p1': { status: 204 } })
    const confirm = vi.spyOn(window, 'confirm').mockReturnValueOnce(false).mockReturnValueOnce(true)
    renderApp('/')
    await userEvent.click(await screen.findByRole('button', { name: '삭제' }))
    expect(calls.some(c => c.method === 'DELETE')).toBe(false) // 취소하면 보내지 않는다
    await userEvent.click(screen.getByRole('button', { name: '삭제' }))
    await waitFor(() => expect(calls.find(c => c.method === 'DELETE')?.url).toBe('/api/posts/p1?version=41'))
    expect(confirm).toHaveBeenCalledTimes(2)
  })

  it('서버가 준 제목은 텍스트로만 그려진다(마크업으로 해석되지 않는다)', async () => {
    stubApi({ ...LOGGED_IN, 'GET /api/posts': { status: 200, body: { items: [{ ...post, title: '<img src=x onerror=alert(1)>' }], total: 1 } } })
    const view = renderApp('/')
    expect(await screen.findByText('<img src=x onerror=alert(1)>')).toBeInTheDocument()
    expect(view.container.querySelector('img')).toBeNull()
  })

  it('마지막 쪽에서 삭제하면 다음 조회 결과에 맞춰 첫 쪽으로 돌아간다', async () => {
    const page2Post = { ...post, id: 'p2' }
    let total = 51 // 페이지 크기(50)보다 1개 많아 2쪽이 생긴다
    const calls = stubApi({
      ...LOGGED_IN,
      // skip으로 쪽을 가른다: 0쪽은 항상 post 1개, 그 다음 쪽은 total이 페이지 크기를 넘을 때만 1개를 준다
      // (삭제 뒤 total이 50으로 줄면 2쪽 조회는 빈 목록이 된다 — 서버가 실제로 그렇게 응답한다).
      'GET /api/posts': (call) => {
        const skip = Number(new URLSearchParams(call.url.split('?')[1] ?? '').get('skip') ?? '0')
        return skip === 0
          ? { status: 200, body: { items: [post], total } }
          : { status: 200, body: { items: total > 50 ? [page2Post] : [], total } }
      },
      'DELETE /api/posts/p2': () => { total = 50; return { status: 204 } },
    })
    vi.spyOn(window, 'confirm').mockReturnValue(true)
    renderApp('/')
    await screen.findByText('안녕')
    await userEvent.click(screen.getByRole('button', { name: '다음' }))
    await waitFor(() => expect(screen.getByText('2 / 2 (총 51건)')).toBeInTheDocument())
    await userEvent.click(screen.getByRole('button', { name: '삭제' }))
    await waitFor(() => expect(calls.some(c => c.method === 'DELETE')).toBe(true))
    await waitFor(() => expect(screen.getByText('1 / 1 (총 50건)')).toBeInTheDocument())
    const afterDelete = calls.slice(calls.findIndex(c => c.method === 'DELETE'))
    expect(afterDelete.some(c => c.method === 'GET' && c.url.includes('skip=0'))).toBe(true)
  })
})
