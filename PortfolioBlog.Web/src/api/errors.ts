/** 필드별 검증 오류. 키는 서버가 주는 camelCase 필드 이름이다(예: contentMarkdown, tagNames). */
export type FieldErrors = Record<string, string[]>

/**
 * 관리 API의 실패 1건. status 0은 네트워크 실패(응답을 받지 못함)다.
 * 서버가 준 문자열(title·detail·fieldErrors)은 화면에 **텍스트로만** 넣는다 — HTML로 해석하지 않는다.
 */
export class ApiError extends Error {
  readonly status: number
  readonly title: string
  readonly detail: string | null
  readonly fieldErrors: FieldErrors
  /** Retry-After(초). 헤더가 없거나 해석할 수 없으면 null. */
  readonly retryAfterSeconds: number | null

  constructor(status: number, title: string, detail: string | null = null, fieldErrors: FieldErrors = {}, retryAfterSeconds: number | null = null) {
    super(`${status} ${title}`)
    this.name = 'ApiError'
    this.status = status
    this.title = title
    this.detail = detail
    this.fieldErrors = fieldErrors
    this.retryAfterSeconds = retryAfterSeconds
  }
}

const isRecord = (v: unknown): v is Record<string, unknown> => typeof v === 'object' && v !== null && !Array.isArray(v)

/** Retry-After 헤더(초 단위 정수만 — 서버는 HTTP-date 형식을 쓰지 않는다)를 1~3600초로 읽는다. */
export function parseRetryAfter(value: string | null): number | null {
  if (value === null || !/^\d{1,6}$/.test(value.trim())) return null
  return Math.min(3600, Math.max(1, Number(value.trim())))
}

function parseFieldErrors(raw: unknown): FieldErrors {
  if (!isRecord(raw)) return {}
  const out: FieldErrors = {}
  for (const [field, messages] of Object.entries(raw)) {
    if (Array.isArray(messages)) out[field] = messages.filter((m): m is string => typeof m === 'string')
  }
  return out
}

/**
 * 실패 응답을 ApiError로 바꾼다. 본문이 ProblemDetails가 아닐 수 있다(Caddy 계층의 404, 프레임워크가 직접 낸 413,
 * 본문 없는 401) — 그때는 상태 코드만으로 만든다. 본문 파싱 실패로 예외를 던지지 않는다.
 */
export async function toApiError(res: Response): Promise<ApiError> {
  const retryAfter = parseRetryAfter(res.headers.get('Retry-After'))
  let body: unknown = null
  try {
    const text = await res.text()
    if (text.length > 0 && text.length <= 65_536) body = JSON.parse(text)
  } catch { /* ProblemDetails가 아닌 본문 */ }
  if (!isRecord(body)) return new ApiError(res.status, res.statusText || '요청 실패', null, {}, retryAfter)
  const title = typeof body.title === 'string' ? body.title : (res.statusText || '요청 실패')
  const detail = typeof body.detail === 'string' ? body.detail : null
  return new ApiError(res.status, title, detail, parseFieldErrors(body.errors), retryAfter)
}

/** 사용자에게 보일 한 줄 설명. 서버 detail이 있으면 그것을 우선한다(서버 메시지는 이미 한국어다). */
export function describeError(error: unknown): string {
  if (!(error instanceof ApiError)) return '알 수 없는 오류가 발생했습니다.'
  const wait = error.retryAfterSeconds === null ? '' : ` ${error.retryAfterSeconds}초 뒤에 다시 시도하세요.`
  switch (error.status) {
    case 0: return '서버에 연결할 수 없습니다. 네트워크를 확인하세요.'
    case 400: return error.detail ?? '입력값을 확인하세요.'
    case 401: return '로그인이 필요합니다.'
    case 403: return error.detail ?? '이 네트워크 또는 출처에서는 관리 기능을 쓸 수 없습니다.'
    case 404: return '대상을 찾을 수 없습니다. 이미 삭제되었을 수 있습니다.'
    case 409: return error.detail ?? '다른 곳에서 먼저 변경되었습니다. 다시 불러온 뒤 시도하세요.'
    case 413: return error.detail ?? '요청이 너무 큽니다.'
    case 415: return error.detail ?? '지원하지 않는 형식입니다.'
    case 429: return `요청이 너무 잦습니다.${wait}`
    case 503: return `서버가 잠시 바쁩니다.${wait}`
    default: return error.detail ?? `요청이 실패했습니다(${error.status}).`
  }
}
