import { describeError, type FieldErrors } from '../api/errors'

/** 오류 한 건을 텍스트로 보여 준다. 서버가 준 문자열은 JSX 텍스트 노드로만 들어간다(React가 이스케이프한다). */
export function ErrorNotice({ error, onRetry }: { error: unknown; onRetry?: () => void }) {
  if (!error) return null
  return (
    <div role="alert" className="rounded border border-red-300 bg-red-50 p-3 text-sm text-red-800">
      <span>{describeError(error)}</span>
      {onRetry && <button type="button" className="ml-3 underline" onClick={onRetry}>다시 시도</button>}
    </div>
  )
}

/**
 * 한 필드의 검증 오류 목록. role="alert"로 둔다 — 저장을 눌러 이 목록이 나타나도 포커스는 버튼에 남아 있어서,
 * 그것이 없으면 화면을 보지 않는 사람에게 실패가 전혀 전달되지 않는다.
 */
export function FieldError({ errors, field }: { errors: FieldErrors; field: string }) {
  const messages = errors[field]
  if (!messages || messages.length === 0) return null
  return <ul role="alert" className="mt-1 text-sm text-red-700" data-field-error={field}>{messages.map((m, i) => <li key={i}>{m}</li>)}</ul>
}

export function Loading({ label = '불러오는 중…' }: { label?: string }) {
  return <p role="status" className="p-4 text-sm text-gray-600">{label}</p>
}
