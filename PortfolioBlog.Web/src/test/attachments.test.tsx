import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { altTextOf } from '../lib/markdownImage'
import { LOGGED_IN, renderApp, stubApi } from './harness'

afterEach(() => { vi.unstubAllGlobals(); vi.restoreAllMocks() })

const ITEM = { id: 'a1', url: '/attachments/a1/%EA%B7%B8%EB%A6%BC%20%281%29.png', fileName: '그림 (1).png', contentType: 'image/png', sizeBytes: 2048, sha256: 'x', createdAt: '2026-09-01T00:00:00Z' }

describe('첨부', () => {
  it.each([
    ['그림 (1).png', '그림 1'], ['a[b]c.webp', 'abc'], ['.png', 'image'], ['no-extension', 'no-extension'],
    ['x'.repeat(150) + '.png', 'x'.repeat(100)], ['줄' + String.fromCharCode(10) + '바꿈.png', '줄바꿈'],
  ])('대체 텍스트는 마크다운 문법을 깨는 문자를 뺀다: %s', (fileName, expected) => expect(altTextOf(fileName)).toBe(expected))

  it('목록을 그리고, 삭제는 확인을 거친다', async () => {
    const calls = stubApi({ ...LOGGED_IN, 'GET /api/attachments': { status: 200, body: { items: [ITEM], total: 1 } }, 'DELETE /api/attachments/a1': { status: 204 } })
    vi.spyOn(window, 'confirm').mockReturnValueOnce(false).mockReturnValueOnce(true)
    renderApp('/attachments')
    expect(await screen.findByRole('img', { name: '그림 (1).png' })).toHaveAttribute('src', ITEM.url)
    expect(screen.getByText(/2\.0KB/)).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: '삭제' }))
    expect(calls.some(c => c.method === 'DELETE')).toBe(false)
    await userEvent.click(screen.getByRole('button', { name: '삭제' }))
    await waitFor(() => expect(calls.some(c => c.method === 'DELETE' && c.url === '/api/attachments/a1')).toBe(true))
  })

  it('이미지가 아닌 파일은 서버로 보내지 않고 이유를 알린다', async () => {
    const calls = stubApi({ ...LOGGED_IN, 'GET /api/attachments': { status: 200, body: { items: [], total: 0 } } })
    const view = renderApp('/attachments')
    await screen.findByText('첨부가 없습니다.')
    const input = view.container.querySelector('input[type=file]') as HTMLInputElement
    // applyAccept: false — accept 속성은 파일 선택 창의 힌트일 뿐이다. 끌어다 놓기·이름 바꾸기로 들어오는 파일을 흉내 낸다.
    await userEvent.upload(input, new File(['<svg/>'], 'x.svg', { type: 'image/svg+xml' }), { applyAccept: false })
    expect(await screen.findByRole('alert')).toHaveTextContent('PNG·JPEG·GIF·WebP')
    expect(calls.some(c => c.method === 'POST')).toBe(false)
  })

  it('마지막 쪽에서 삭제하면 다음 조회 결과에 맞춰 첫 쪽으로 돌아간다', async () => {
    const item2 = { ...ITEM, id: 'a2' }
    let total = 51 // 페이지 크기(50)보다 1개 많아 2쪽이 생긴다
    const calls = stubApi({
      ...LOGGED_IN,
      // skip으로 쪽을 가른다: 0쪽은 항상 항목 1개, 그 다음 쪽은 total이 페이지 크기를 넘을 때만 1개를 준다
      // (삭제 뒤 total이 50으로 줄면 2쪽 조회는 빈 목록이 된다 — 서버가 실제로 그렇게 응답한다).
      'GET /api/attachments': (call) => {
        const skip = Number(new URLSearchParams(call.url.split('?')[1] ?? '').get('skip') ?? '0')
        return skip === 0
          ? { status: 200, body: { items: [ITEM], total } }
          : { status: 200, body: { items: total > 50 ? [item2] : [], total } }
      },
      'DELETE /api/attachments/a2': () => { total = 50; return { status: 204 } },
    })
    vi.spyOn(window, 'confirm').mockReturnValue(true)
    renderApp('/attachments')
    await screen.findByRole('img', { name: '그림 (1).png' })
    await userEvent.click(screen.getByRole('button', { name: '다음' }))
    await waitFor(() => expect(screen.getByText('2 / 2 (총 51건)')).toBeInTheDocument())
    await userEvent.click(screen.getByRole('button', { name: '삭제' }))
    await waitFor(() => expect(calls.some(c => c.method === 'DELETE')).toBe(true))
    await waitFor(() => expect(screen.getByText('1 / 1 (총 50건)')).toBeInTheDocument())
    const afterDelete = calls.slice(calls.findIndex(c => c.method === 'DELETE'))
    expect(afterDelete.some(c => c.method === 'GET' && c.url.includes('skip=0'))).toBe(true)
  })
})
