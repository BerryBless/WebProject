import { QueryClientProvider } from '@tanstack/react-query'
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { createQueryClient } from '../app/queryClient'
import { PREVIEW_DEBOUNCE_MS, PreviewPane } from '../components/PreviewPane'
import { LIMITS } from '../lib/validation'
import { stubApi } from './harness'

afterEach(() => { vi.useRealTimers(); vi.unstubAllGlobals(); vi.restoreAllMocks() })

// 클라이언트는 한 번만 만든다: rerender마다 새로 만들면 PreviewPane의 effect 의존성이 바뀌어 디바운스와 무관한 요청이 나간다.
const client = createQueryClient()
const pane = (markdown: string) => <QueryClientProvider client={client}><PreviewPane markdown={markdown} /></QueryClientProvider>
const frame = () => screen.getByTitle('미리보기') as HTMLIFrameElement

describe('미리보기', () => {
  it('서버 HTML은 sandbox=""(토큰 없음) iframe의 srcdoc으로만 들어간다', async () => {
    stubApi({ 'POST /api/preview': { status: 200, body: { html: '<p id="from-server">본문</p>' } } })
    const view = render(pane('# 제목'))
    await waitFor(() => expect(frame().getAttribute('srcdoc')).toContain('<p id="from-server">본문</p>'))
    expect(frame().getAttribute('sandbox')).toBe('')
    expect(frame().getAttribute('srcdoc')).toContain(`img-src ${window.location.origin}; style-src ${window.location.origin}`)
    expect(view.container.querySelector('#from-server')).toBeNull() // 부모 문서의 DOM에는 서버 HTML이 없다
  })

  it('입력이 멎은 뒤 500ms에 한 번만 요청한다', async () => {
    vi.useFakeTimers()
    const calls = stubApi({ 'POST /api/preview': { status: 200, body: { html: '' } } })
    const view = render(pane('a'))
    await act(() => vi.advanceTimersByTimeAsync(0))
    const before = calls.length // 첫 렌더의 요청
    view.rerender(pane('ab')); await act(() => vi.advanceTimersByTimeAsync(PREVIEW_DEBOUNCE_MS - 1))
    view.rerender(pane('abc')); await act(() => vi.advanceTimersByTimeAsync(PREVIEW_DEBOUNCE_MS - 1))
    expect(calls.length).toBe(before)
    await act(() => vi.advanceTimersByTimeAsync(1))
    expect(calls.length).toBe(before + 1)
    expect(calls.at(-1)?.body).toEqual({ markdown: 'abc' })
  })

  it('429의 Retry-After 동안은 요청을 보내지 않고, 마지막으로 성공한 미리보기를 남긴다', async () => {
    vi.useFakeTimers()
    let limited = false
    const calls = stubApi({ 'POST /api/preview': () => limited ? { status: 429, body: { title: 'x' }, headers: { 'Retry-After': '5' } } : { status: 200, body: { html: '<p>good</p>' } } })
    const view = render(pane('one'))
    await act(() => vi.advanceTimersByTimeAsync(0))
    expect(frame().getAttribute('srcdoc')).toContain('<p>good</p>')

    limited = true
    view.rerender(pane('two')); await act(() => vi.advanceTimersByTimeAsync(PREVIEW_DEBOUNCE_MS))
    expect(screen.getByRole('alert')).toHaveTextContent('5초')
    expect(frame().getAttribute('srcdoc')).toContain('<p>good</p>')
    const afterLimit = calls.length

    view.rerender(pane('three')); await act(() => vi.advanceTimersByTimeAsync(PREVIEW_DEBOUNCE_MS + 1000))
    expect(calls.length).toBe(afterLimit) // 대기 중에는 보내지 않는다
    limited = false
    await act(() => vi.advanceTimersByTimeAsync(5000))
    expect(calls.length).toBe(afterLimit + 1)
    expect(calls.at(-1)?.body).toEqual({ markdown: 'three' })
  })

  it('본문이 UTF-8 204,800바이트면 요청을 보내고, 204,801바이트면 보내지 않고 안내한다', async () => {
    vi.useFakeTimers()
    const calls = stubApi({ 'POST /api/preview': { status: 200, body: { html: '<p>ok</p>' } } })
    const atLimit = 'a'.repeat(LIMITS.contentMaxBytes) // 'a'는 UTF-8에서 1바이트다 — 글자 수 그대로가 바이트 수다
    const view = render(pane(atLimit))
    await act(() => vi.advanceTimersByTimeAsync(0))
    expect(calls.length).toBe(1)
    expect(screen.queryByRole('alert')).toBeNull()

    view.rerender(pane(atLimit + 'a'))
    await act(() => vi.advanceTimersByTimeAsync(PREVIEW_DEBOUNCE_MS))
    expect(calls.length).toBe(1) // 한도를 넘은 값으로는 보내지 않는다
    expect(screen.getByRole('alert')).toHaveTextContent('KB를 넘어')
  })

  it('Retry-After 없는 실패(500)는 다시 시도 버튼으로 입력을 바꾸지 않고도 재요청한다', async () => {
    vi.useFakeTimers()
    let fail = true
    const calls = stubApi({ 'POST /api/preview': () => fail ? { status: 500, body: { title: 'x' } } : { status: 200, body: { html: '<p>fixed</p>' } } })
    render(pane('one'))
    await act(() => vi.advanceTimersByTimeAsync(0))
    expect(screen.getByRole('alert')).toBeInTheDocument()
    const before = calls.length

    fail = false
    fireEvent.click(screen.getByRole('button', { name: '다시 시도' }))
    await act(() => vi.advanceTimersByTimeAsync(0))
    expect(calls.length).toBe(before + 1) // Retry-After가 없으므로 대기 없이 바로 다시 보낸다
    expect(frame().getAttribute('srcdoc')).toContain('<p>fixed</p>')
  })

  it('429 대기 중에는 다시 시도 버튼을 눌러도 요청을 보내지 않는다', async () => {
    vi.useFakeTimers()
    const calls = stubApi({ 'POST /api/preview': { status: 429, body: { title: 'x' }, headers: { 'Retry-After': '5' } } })
    render(pane('one'))
    await act(() => vi.advanceTimersByTimeAsync(0))
    expect(screen.getByRole('alert')).toHaveTextContent('5초')
    const afterFirst = calls.length

    fireEvent.click(screen.getByRole('button', { name: '다시 시도' }))
    await act(() => vi.advanceTimersByTimeAsync(0))
    expect(calls.length).toBe(afterFirst) // blockedUntil 대기 중이라 버튼을 눌러도 보내지 않는다
  })

  it('먼저 보낸 요청의 응답이 나중에 도착해도(그새 입력이 바뀌었으면) 화면을 덮지 않는다', async () => {
    vi.useFakeTimers()
    let releaseFirst: (() => void) | undefined
    const firstGate = new Promise<void>(resolve => { releaseFirst = resolve })
    const calls = stubApi({
      'POST /api/preview': async call => {
        const markdown = (call.body as { markdown: string }).markdown
        if (markdown === 'first') await firstGate // 수동으로 풀 때까지 응답하지 않는다
        return { status: 200, body: { html: markdown === 'first' ? '<p>stale-first</p>' : '<p>second</p>' } }
      },
    })
    const view = render(pane('first'))
    await act(() => vi.advanceTimersByTimeAsync(0)) // 첫 요청을 보낸다(응답은 firstGate에 묶여 대기 중)
    expect(calls.length).toBe(1)

    view.rerender(pane('second'))
    await act(() => vi.advanceTimersByTimeAsync(PREVIEW_DEBOUNCE_MS)) // 두 번째 요청을 보내고 응답을 받는다(첫 요청은 여전히 대기 중)
    expect(calls.length).toBe(2)
    expect(frame().getAttribute('srcdoc')).toContain('<p>second</p>')

    // 첫 요청의 응답을 이제 푼다 — aborted 가드가 없으면 이 결과가 두 번째 결과 위에 덮인다.
    releaseFirst?.()
    await act(() => vi.advanceTimersByTimeAsync(0))
    expect(frame().getAttribute('srcdoc')).toContain('<p>second</p>')
    expect(frame().getAttribute('srcdoc')).not.toContain('stale-first')
  })
})
