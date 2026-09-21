import { ApiError, toApiError } from './errors'

type Query = Record<string, string | number | null | undefined>

export interface RequestOptions {
  /** JSON 본문. JSON.stringify는 비 ASCII 문자를 이스케이프하지 않는다 — 서버의 256KB 상한은 직렬화 후 바이트 기준이다. */
  json?: unknown
  /** multipart 본문. Content-Type(boundary 포함)은 브라우저가 붙이므로 직접 지정하지 않는다. */
  form?: FormData
  query?: Query
  signal?: AbortSignal
}

/** 서버의 AdminSurfaceMiddleware가 모든 /api 요청에 요구하는 CSRF 헤더. */
export const CSRF_HEADER = 'X-Requested-With'
export const CSRF_VALUE = 'XMLHttpRequest'

function buildUrl(path: string, query?: Query): string {
  // 같은 출처의 /api 경로만 허용한다: 절대 URL·프로토콜 상대 URL(//host)로 쿠키 없는 교차 출처 호출이 섞여 들어오는 실수를 막는다.
  if (!path.startsWith('/api/') || path.includes('//') || path.includes('\\')) throw new Error(`API 경로가 아닙니다: ${path}`)
  if (!query) return path
  const params = new URLSearchParams()
  for (const [key, value] of Object.entries(query)) {
    if (value !== null && value !== undefined && value !== '') params.set(key, String(value))
  }
  const qs = params.toString()
  return qs ? `${path}?${qs}` : path
}

/**
 * 관리 API 호출의 단일 통로. 성공이면 JSON(204는 undefined)을, 실패면 ApiError를 던진다.
 * - credentials 'same-origin': 세션 쿠키(__Host-AdminSession)는 관리 출처에만 붙는다.
 * - redirect 'error': 이 API는 리다이렉트하지 않는다. 리다이렉트가 오면 중간자·오설정이므로 따라가지 않는다.
 * - cache 'no-store': 서버도 no-store를 주지만 브라우저 HTTP 캐시를 한 번 더 배제한다.
 */
export async function request<T>(method: 'GET' | 'POST' | 'PUT' | 'DELETE', path: string, options: RequestOptions = {}): Promise<T> {
  const headers: Record<string, string> = { [CSRF_HEADER]: CSRF_VALUE, Accept: 'application/json' }
  let body: BodyInit | undefined
  if (options.json !== undefined) {
    headers['Content-Type'] = 'application/json'
    body = JSON.stringify(options.json)
  } else if (options.form) {
    body = options.form
  }

  // 경로 검사는 try 밖에서 한다: 잘못된 경로는 프로그래밍 오류이지 "네트워크 오류"가 아니다.
  const url = buildUrl(path, options.query)
  let res: Response
  try {
    res = await fetch(url, {
      method, headers, body, signal: options.signal, credentials: 'same-origin', cache: 'no-store', redirect: 'error',
    })
  } catch (cause) {
    // 취소는 오류가 아니다: TanStack Query가 signal로 끊은 요청은 그대로 흘려보낸다.
    if ((cause as { name?: unknown } | null)?.name === 'AbortError') throw cause
    throw new ApiError(0, '네트워크 오류')
  }
  if (!res.ok) throw await toApiError(res)
  if (res.status === 204) return undefined as T
  return (await res.json()) as T
}
