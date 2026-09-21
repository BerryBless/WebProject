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
})
