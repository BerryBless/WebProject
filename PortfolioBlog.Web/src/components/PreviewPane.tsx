import { useEffect, useMemo, useRef, useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { preview } from '../api/endpoints'
import { ApiError } from '../api/errors'
import { noteAuthFailure } from '../app/queryClient'
import { buildPreviewDocument } from '../lib/previewDoc'
import { useDebounced } from '../lib/useDebounced'
import { LIMITS, utf8ByteLength } from '../lib/validation'
import { ErrorNotice } from './notices'

export const PREVIEW_DEBOUNCE_MS = 500
/**
 * 두 미리보기 요청 사이의 최소 간격. 디바운스만으로는 모자란다(실측): 620ms 간격으로 40타를 치면 25초 만에
 * 요청 40건이 나가 429(Retry-After 60)에 닿았다 — 500ms 넘게 멈출 때마다 요청 1건이기 때문이다.
 * 서버의 미리보기 한도는 전역 60회/분·동시 2이고 탭·사람이 그것을 나눠 쓴다. 1500ms면 한 탭이 분당 40건을
 * 넘지 않는다.
 */
export const PREVIEW_MIN_INTERVAL_MS = 1500

/**
 * 공개 페이지와 같은 렌더러(/api/preview)의 결과를 sandbox="" iframe에 보여 준다.
 * - 서버 HTML은 srcDoc 문자열로만 간다. dangerouslySetInnerHTML을 쓰지 않는다.
 * - 실패해도 마지막으로 성공한 미리보기는 남긴다(입력 중 한 번의 429·503으로 화면이 비지 않게).
 * - 429·503의 Retry-After 동안과 직전 요청 뒤 PREVIEW_MIN_INTERVAL_MS 동안은 요청을 보내지 않는다.
 */
export function PreviewPane({ markdown }: { markdown: string }) {
  const debounced = useDebounced(markdown, PREVIEW_DEBOUNCE_MS)
  const client = useQueryClient()
  const [html, setHtml] = useState('')
  const [error, setError] = useState<unknown>(null)
  const [retryTick, setRetryTick] = useState(0)
  const blockedUntil = useRef(0)
  const tooLarge = utf8ByteLength(debounced) > LIMITS.contentMaxBytes

  useEffect(() => {
    if (tooLarge) return // 서버가 400으로 거부할 크기다. 보내지 않는다(안내는 렌더에서 파생한다)
    const wait = blockedUntil.current - Date.now()
    if (wait > 0) {
      const timer = window.setTimeout(() => setRetryTick(t => t + 1), wait)
      return () => window.clearTimeout(timer)
    }
    const controller = new AbortController()
    // 보내기 직전에 다음 요청을 막을 시각을 예약한다 — 429의 Retry-After와 같은 대기 타이머 경로를 그대로 쓴다.
    // Math.max: 이미 더 먼 시각까지 막혀 있으면(Retry-After) 그 시각을 앞당기지 않는다.
    blockedUntil.current = Math.max(blockedUntil.current, Date.now() + PREVIEW_MIN_INTERVAL_MS)
    preview.render(debounced, controller.signal).then(
      result => {
        if (controller.signal.aborted) return // 이 effect가 끝난 뒤 도착한 응답이다 — 최신 요청의 결과만 반영한다
        setHtml(result.html); setError(null)
      },
      (cause: unknown) => {
        if (controller.signal.aborted) return
        noteAuthFailure(client, cause)
        if (cause instanceof ApiError && cause.retryAfterSeconds !== null) {
          blockedUntil.current = Date.now() + cause.retryAfterSeconds * 1000
          setRetryTick(t => t + 1)
        }
        setError(cause)
      })
    return () => controller.abort()
  }, [debounced, tooLarge, retryTick, client])

  const srcDoc = useMemo(() => buildPreviewDocument(html), [html])
  return (
    <section aria-label="미리보기" className="flex h-full flex-col gap-2">
      {tooLarge
        ? <p role="alert" className="text-sm text-red-700">본문이 UTF-8 기준 {LIMITS.contentMaxBytes / 1024}KB를 넘어 미리보기를 만들 수 없습니다.</p>
        // retryTick을 올리기만 한다: blockedUntil 대기 중이면 effect가 wait를 다시 계산해 타이머만 재조정하고,
        // 대기가 끝났으면 그제서야 실제 요청을 보낸다 — 버튼을 눌러도 Retry-After를 건너뛰지 않는다.
        : <ErrorNotice error={error} onRetry={() => setRetryTick(t => t + 1)} />}
      {/* sandbox=""(토큰 없음): 스크립트·폼·팝업·같은 출처 접근이 전부 꺼진다. 토큰을 추가하지 말 것. */}
      <iframe title="미리보기" sandbox="" srcDoc={srcDoc} className="min-h-[24rem] w-full flex-1 rounded border bg-white" />
    </section>
  )
}
