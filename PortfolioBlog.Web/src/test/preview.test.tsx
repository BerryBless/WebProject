import { QueryClientProvider } from '@tanstack/react-query'
import { act, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { createQueryClient } from '../app/queryClient'
import { PREVIEW_DEBOUNCE_MS, PreviewPane } from '../components/PreviewPane'
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
})
