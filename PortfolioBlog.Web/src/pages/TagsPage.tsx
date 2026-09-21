import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { tags as api } from '../api/endpoints'
import type { Tag } from '../api/types'
import { ErrorNotice, Loading } from '../components/notices'

/** 태그는 글을 저장할 때 자동으로 만들어진다. 여기서는 쓰이지 않게 된 태그를 지우는 일만 한다. */
export function TagsPage() {
  const client = useQueryClient()
  const list = useQuery({ queryKey: ['tags', 'list'], queryFn: ({ signal }) => api.list(signal) })
  const remove = useMutation({ mutationFn: (tag: Tag) => api.remove(tag.id), onSettled: () => client.invalidateQueries({ queryKey: ['tags'] }) })

  const confirmRemove = (tag: Tag) => {
    if (window.confirm(`태그 "${tag.name}"을(를) 삭제할까요?\n이 태그가 붙은 글 ${tag.postCount}편에서 태그만 떨어집니다(글은 남습니다).`)) remove.mutate(tag)
  }

  return (
    <main className="mx-auto max-w-3xl space-y-4 p-4">
      <h1 className="text-xl font-bold">태그</h1>
      <ErrorNotice error={list.error} onRetry={() => void list.refetch()} />
      <ErrorNotice error={remove.error} />
      {list.isPending ? <Loading /> : (
        <ul className="divide-y text-sm">
          {list.data?.map(tag => (
            <li key={tag.id} className="flex items-center gap-3 py-2">
              <span className="flex-1">{tag.name} <span className="text-xs text-gray-500">/tags/{tag.normalizedName} · 글 {tag.postCount}편</span></span>
              <button type="button" className="text-red-700 underline" disabled={remove.isPending} onClick={() => confirmRemove(tag)}>삭제</button>
            </li>
          ))}
          {list.data?.length === 0 && <li className="py-2 text-gray-500">태그가 없습니다.</li>}
        </ul>
      )}
    </main>
  )
}
