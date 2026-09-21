import { useState } from 'react'
import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { attachments as api } from '../api/endpoints'
import { ApiError } from '../api/errors'
import type { Attachment } from '../api/types'
import { ErrorNotice, Loading } from '../components/notices'
import { formatBytes, formatDateTime } from '../lib/useDebounced'
import { validateImageFile } from '../lib/validation'
import { altTextOf } from '../lib/markdownImage'

const PAGE_SIZE = 50

export function AttachmentsPage() {
  const client = useQueryClient()
  const [page, setPage] = useState(0)
  const [copied, setCopied] = useState<string | null>(null)
  const list = useQuery({
    queryKey: ['attachments', 'list', page], placeholderData: keepPreviousData,
    queryFn: ({ signal }) => api.list(page * PAGE_SIZE, PAGE_SIZE, signal),
  })
  const refresh = () => client.invalidateQueries({ queryKey: ['attachments'] })
  const remove = useMutation({ mutationFn: (item: Attachment) => api.remove(item.id), onSettled: refresh })
  const upload = useMutation({
    mutationFn: async (files: File[]) => {
      for (const file of files) { // 순차: 서버의 업로드 동시 실행 한도는 전역 2
        const problem = validateImageFile(file)
        if (problem) throw new ApiError(400, '올릴 수 없는 파일', `${file.name}: ${problem}`)
        await api.upload(file, file.name || 'image.png')
      }
    },
    onSettled: refresh,
  })

  const confirmRemove = (item: Attachment) => {
    // 서버는 글↔첨부 참조를 추적하지 않는다. 지우면 그 이미지를 쓰는 글에서 깨진 이미지가 된다.
    if (window.confirm(`"${item.fileName}"을(를) 삭제할까요?\n이 이미지를 쓰는 글이 있으면 그 글에서 이미지가 깨집니다. 이미 받아 간 브라우저 캐시의 사본은 회수되지 않습니다.`)) remove.mutate(item)
  }
  const copyMarkdown = async (item: Attachment) => {
    try { await navigator.clipboard.writeText(`![${altTextOf(item.fileName)}](${item.url})`); setCopied(item.id) } catch { setCopied(null) }
  }

  const total = list.data?.total ?? 0
  const lastPage = Math.max(0, Math.ceil(total / PAGE_SIZE) - 1)
  // 렌더 중 상태 보정(effect 아님): 마지막 쪽에서 지워 총 개수가 줄면 서버가 그 skip에는 빈 목록을 준다.
  // list.data가 있을 때만 본다 — 없으면(첫 로딩) total이 0으로 계산돼 오탐한다. 보정 뒤에는 page<=lastPage가 되어
  // 같은 조건이 다시 참이 되지 않으므로 무한 렌더로 이어지지 않는다.
  if (list.data && page > lastPage) setPage(lastPage)

  return (
    <main className="mx-auto max-w-5xl space-y-4 p-4">
      <div className="flex items-center gap-3">
        <h1 className="text-xl font-bold">첨부</h1>
        <label className="cursor-pointer rounded bg-black px-3 py-2 text-sm text-white">이미지 올리기
          <input type="file" accept="image/png,image/jpeg,image/gif,image/webp" multiple hidden
            onChange={e => { const files = Array.from(e.target.files ?? []); e.target.value = ''; if (files.length > 0) upload.mutate(files) }} /></label>
        {upload.isPending && <span role="status" className="text-sm">올리는 중…</span>}
      </div>
      <p className="text-xs text-gray-600">올린 이미지는 글에 넣지 않아도 주소를 아는 사람은 볼 수 있습니다. 메타데이터(EXIF·GPS)는 서버가 제거합니다.</p>
      <ErrorNotice error={list.error} onRetry={() => void list.refetch()} />
      <ErrorNotice error={upload.error} />
      <ErrorNotice error={remove.error} />
      {list.isPending ? <Loading /> : (
        <ul className="grid grid-cols-2 gap-3 md:grid-cols-4">
          {list.data?.items.map(item => (
            <li key={item.id} className="space-y-1 rounded border p-2 text-xs">
              {/* 같은 출처의 /attachments 경로다(CSP img-src 'self'). 응답은 nosniff + sandbox CSP로 온다. */}
              <img src={item.url} alt={item.fileName} loading="lazy" className="h-32 w-full rounded bg-gray-50 object-contain" />
              <div className="truncate" title={item.fileName}>{item.fileName}</div>
              <div className="text-gray-500">{formatBytes(item.sizeBytes)} · {formatDateTime(item.createdAt)}</div>
              <div className="flex gap-2">
                <button type="button" className="underline" onClick={() => void copyMarkdown(item)}>{copied === item.id ? '복사됨' : '마크다운 복사'}</button>
                <button type="button" className="text-red-700 underline" disabled={remove.isPending} onClick={() => confirmRemove(item)}>삭제</button>
              </div>
            </li>
          ))}
          {list.data?.items.length === 0 && <li className="col-span-full text-sm text-gray-500">첨부가 없습니다.</li>}
        </ul>
      )}
      <div className="flex items-center gap-3 text-sm">
        <button type="button" disabled={page === 0} onClick={() => setPage(p => p - 1)} className="underline disabled:opacity-40">이전</button>
        <span>{page + 1} / {lastPage + 1} (총 {total}건)</span>
        <button type="button" disabled={page >= lastPage} onClick={() => setPage(p => p + 1)} className="underline disabled:opacity-40">다음</button>
      </div>
    </main>
  )
}
