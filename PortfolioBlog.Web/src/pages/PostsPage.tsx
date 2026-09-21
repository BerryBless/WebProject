import { useState } from 'react'
import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link } from 'react-router'
import { posts } from '../api/endpoints'
import type { PostSummary } from '../api/types'
import { ErrorNotice, Loading } from '../components/notices'
import { LIMITS } from '../lib/validation'
import { formatDateTime, useDebounced } from '../lib/useDebounced'

const PAGE_SIZE = 50 // 서버 기본 take

export function PostsPage() {
  const [search, setSearch] = useState('')
  const [page, setPage] = useState(0)
  const q = useDebounced(search.trim(), 300)
  const client = useQueryClient()

  const list = useQuery({
    queryKey: ['posts', 'list', q, page],
    queryFn: ({ signal }) => posts.list(q, page * PAGE_SIZE, PAGE_SIZE, signal),
    placeholderData: keepPreviousData,
  })
  const remove = useMutation({
    mutationFn: (post: PostSummary) => posts.remove(post.id, post.version),
    onSettled: () => client.invalidateQueries({ queryKey: ['posts'] }),
  })

  const confirmRemove = (post: PostSummary) => {
    // 글은 상태가 없다 — 삭제는 즉시 공개 사이트에서 사라지고 되돌릴 수 없다.
    if (window.confirm(`"${post.title}" 글을 삭제할까요?\n공개 사이트에서 즉시 사라지며 되돌릴 수 없습니다.`)) remove.mutate(post)
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
        <h1 className="text-xl font-bold">글</h1>
        <input type="search" aria-label="글 검색" placeholder="제목·요약·본문 검색" maxLength={LIMITS.queryMax} value={search}
          onChange={e => { setSearch(e.target.value); setPage(0) }} className="flex-1 rounded border p-2 text-sm" />
        <Link to="/posts/new" className="rounded bg-black px-3 py-2 text-sm text-white">새 글</Link>
      </div>
      <ErrorNotice error={list.error} onRetry={() => void list.refetch()} />
      <ErrorNotice error={remove.error} />
      {list.isPending ? <Loading /> : (
        <table className="w-full text-left text-sm">
          <thead><tr className="border-b"><th className="p-2">제목</th><th className="p-2">태그</th><th className="p-2">수정</th><th /></tr></thead>
          <tbody>
            {list.data?.items.map(post => (
              <tr key={post.id} className="border-b">
                <td className="p-2"><Link to={`/posts/${post.id}`} className="font-medium underline">{post.title}</Link><div className="text-xs text-gray-500">/posts/{post.slug}</div></td>
                <td className="p-2 text-xs">{post.tags.join(', ')}</td>
                <td className="p-2 text-xs">{formatDateTime(post.updatedAt)}</td>
                <td className="p-2 text-right"><button type="button" className="text-red-700 underline" disabled={remove.isPending} onClick={() => confirmRemove(post)}>삭제</button></td>
              </tr>
            ))}
            {list.data?.items.length === 0 && <tr><td colSpan={4} className="p-6 text-center text-gray-500">글이 없습니다.</td></tr>}
          </tbody>
        </table>
      )}
      <div className="flex items-center gap-3 text-sm">
        <button type="button" disabled={page === 0} onClick={() => setPage(p => p - 1)} className="underline disabled:opacity-40">이전</button>
        <span>{page + 1} / {lastPage + 1} (총 {total}건)</span>
        <button type="button" disabled={page >= lastPage} onClick={() => setPage(p => p + 1)} className="underline disabled:opacity-40">다음</button>
      </div>
    </main>
  )
}
