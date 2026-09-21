import '@testing-library/jest-dom/vitest'
import { QueryClientProvider } from '@tanstack/react-query'
import { render } from '@testing-library/react'
import { createMemoryRouter, RouterProvider } from 'react-router'
import { vi } from 'vitest'
import { routes } from '../app/routes'
import { createQueryClient } from '../app/queryClient'
import { noteUnexpectedCall } from './unexpected'

export interface Call { method: string; url: string; body: unknown }
type Reply = { status: number; body?: unknown; headers?: Record<string, string> }
type Handler = Reply | ((call: Call) => Reply)

/**
 * fetch를 "METHOD 경로" 표로 대신한다. 표에 없는 호출은 예외를 던지지만, request()가 그 예외를 네트워크 오류로
 * 바꿔 삼키므로 그것만으로는 테스트를 실패시키지 않는다 — 그래서 noteUnexpectedCall로도 남긴다. setup.ts의
 * afterEach가 테스트마다 그 목록이 비어 있는지 확인해 실제로 실패시킨다.
 * 경로는 쿼리 문자열을 뺀 값으로 찾는다. 기록된 호출은 calls로 확인한다.
 */
export function stubApi(table: Record<string, Handler>) {
  const calls: Call[] = []
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input)
    const method = init?.method ?? 'GET'
    const raw = init?.body
    const call: Call = { method, url, body: typeof raw === 'string' ? JSON.parse(raw) : raw ?? null }
    calls.push(call)
    const handler = table[`${method} ${url.split('?')[0]}`]
    if (!handler) {
      noteUnexpectedCall(`${method} ${url}`)
      throw new Error(`stubApi: 예상하지 못한 호출 ${method} ${url}`)
    }
    const reply = typeof handler === 'function' ? handler(call) : handler
    return new Response(reply.body === undefined ? null : JSON.stringify(reply.body), { status: reply.status, headers: reply.headers })
  }))
  return calls
}

/** 실제 라우트 표(app/routes)를 메모리 라우터로 띄운다. */
export function renderApp(path: string) {
  const router = createMemoryRouter(routes, { initialEntries: [path] })
  const view = render(<QueryClientProvider client={createQueryClient()}><RouterProvider router={router} /></QueryClientProvider>)
  return { router, ...view }
}

export const LOGGED_IN = { 'GET /api/auth/me': { status: 200, body: { authenticated: true } } } as const
