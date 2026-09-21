import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { altTextOf } from '../lib/markdownImage'
import { LOGGED_IN, renderApp, stubApi } from './harness'

afterEach(() => {
  vi.unstubAllGlobals(); vi.restoreAllMocks()
  // navigator.clipboard는 jsdom에 기본으로 없다(own property가 아니다) — Object.defineProperty로 넣은 값을
  // 지워 다음 테스트가 원래 상태(undefined)에서 시작하게 한다. vi.unstubAllGlobals는 vi.stubGlobal만 되돌린다.
  Reflect.deleteProperty(navigator, 'clipboard')
})

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
    expect(input).not.toHaveAttribute('hidden') // hidden 속성은 요소를 키보드 Tab 순서에서 빼 버린다
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

  it('업로드가 도는 동안 입력을 막아 재진입을 못 하게 한다', async () => {
    let resolveUpload: (reply: { status: number; body?: unknown }) => void = () => {}
    const pending = new Promise<{ status: number; body?: unknown }>(resolve => { resolveUpload = resolve })
    stubApi({ ...LOGGED_IN, 'GET /api/attachments': { status: 200, body: { items: [], total: 0 } }, 'POST /api/attachments': () => pending })
    const view = renderApp('/attachments')
    await screen.findByText('첨부가 없습니다.')
    const input = view.container.querySelector('input[type=file]') as HTMLInputElement
    await userEvent.upload(input, new File(['x'.repeat(10)], 'ok.png', { type: 'image/png' }))
    await waitFor(() => expect(input).toBeDisabled())
    expect(screen.getByRole('status')).toHaveTextContent('올리는 중…')
    resolveUpload({ status: 200, body: { id: 'a3', url: '/attachments/a3/ok.png', fileName: 'ok.png', contentType: 'image/png', sizeBytes: 10, sha256: 'y', createdAt: '2026-09-01T00:00:00Z' } })
    await waitFor(() => expect(input).not.toBeDisabled())
  })

  it('마크다운 복사는 대체 텍스트와 서버 주소를 담은 문자열을 클립보드에 넣는다', async () => {
    stubApi({ ...LOGGED_IN, 'GET /api/attachments': { status: 200, body: { items: [ITEM], total: 1 } } })
    const writeText = vi.fn().mockResolvedValue(undefined)
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true })
    renderApp('/attachments')
    await userEvent.click(await screen.findByRole('button', { name: '마크다운 복사' }))
    await waitFor(() => expect(screen.getByRole('button', { name: '복사됨' })).toBeInTheDocument())
    expect(writeText).toHaveBeenCalledWith(expect.stringMatching(/^!\[[^\]]*\]\(\/attachments\/[^)]*\)$/))
  })

  it('클립보드 복사가 거부되면 대신 복사하라고 알린다', async () => {
    stubApi({ ...LOGGED_IN, 'GET /api/attachments': { status: 200, body: { items: [ITEM], total: 1 } } })
    Object.defineProperty(navigator, 'clipboard', { value: { writeText: vi.fn().mockRejectedValue(new Error('denied')) }, configurable: true })
    renderApp('/attachments')
    await userEvent.click(await screen.findByRole('button', { name: '마크다운 복사' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('클립보드에 복사하지 못했습니다')
  })

  it('서버 계약과 다른 url을 가진 첨부는 이미지를 그리지 않고 복사를 막는다', async () => {
    const external = { ...ITEM, id: 'b1', url: 'https://evil.test/x.png' }
    const protocolRelative = { ...ITEM, id: 'b2', url: '//evil.test/x.png' }
    stubApi({ ...LOGGED_IN, 'GET /api/attachments': { status: 200, body: { items: [external, protocolRelative], total: 2 } } })
    renderApp('/attachments')
    await screen.findAllByText('주소 형식이 올바르지 않은 첨부')
    expect(screen.queryByRole('img')).toBeNull()
    for (const button of screen.getAllByRole('button', { name: '마크다운 복사' })) expect(button).toBeDisabled()
  })
})
