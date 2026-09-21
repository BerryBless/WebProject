import type { PostDetail } from '../api/types'
import { formatDateTime } from '../lib/useDebounced'

interface Props {
  server: PostDetail
  mineMarkdown: string
  /** 서버본으로 바꾼다(내 변경과 임시본을 버린다). */
  onTakeServer: () => void
  /** 내 내용을 유지하고 서버의 최신 version을 바탕으로 다시 저장할 수 있게 한다(서버의 변경을 덮어쓴다). */
  onKeepMine: () => void
}

/** 409(version 불일치): 다른 탭·기기에서 먼저 저장됐다. 서버본과 내 본문을 나란히 보여 주고 사용자가 고르게 한다 — 자동 병합은 하지 않는다. */
export function ConflictPanel({ server, mineMarkdown, onTakeServer, onKeepMine }: Props) {
  return (
    <section role="alertdialog" aria-label="저장 충돌" className="space-y-3 rounded border border-amber-400 bg-amber-50 p-4 text-sm">
      <p><strong>다른 곳에서 이 글이 먼저 수정되었습니다.</strong> 서버본은 {formatDateTime(server.updatedAt)}에 저장됐습니다. 내 변경은 임시본으로 보관되어 있습니다.</p>
      <div className="grid gap-3 md:grid-cols-2">
        <label className="block"><span className="font-medium">서버본</span>
          <textarea readOnly value={server.contentMarkdown} className="mt-1 h-64 w-full rounded border bg-white p-2 font-mono text-xs" /></label>
        <label className="block"><span className="font-medium">내 본문</span>
          <textarea readOnly value={mineMarkdown} className="mt-1 h-64 w-full rounded border bg-white p-2 font-mono text-xs" /></label>
      </div>
      <div className="flex gap-3">
        <button type="button" className="rounded border px-3 py-1" onClick={onTakeServer}>서버본으로 바꾸기(내 변경 버림)</button>
        <button type="button" className="rounded border px-3 py-1" onClick={onKeepMine}>내 내용 유지(다시 저장하면 서버본을 덮어씀)</button>
      </div>
    </section>
  )
}
