import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { series as api } from '../api/endpoints'
import { ApiError, type FieldErrors } from '../api/errors'
import type { Series, UpsertSeriesRequest } from '../api/types'
import { ErrorNotice, FieldError, Loading } from '../components/notices'
import { hasErrors, validateSeries } from '../lib/validation'

const EMPTY: UpsertSeriesRequest = { slug: '', title: '', description: '' }
const input = 'mt-1 w-full rounded border p-2 text-sm'

function SeriesForm({ initial, slugLocked, submitLabel, onSubmit, onCancel }: {
  initial: UpsertSeriesRequest; slugLocked: boolean; submitLabel: string
  onSubmit: (value: UpsertSeriesRequest) => Promise<unknown>; onCancel?: () => void
}) {
  const [value, setValue] = useState(initial)
  const [errors, setErrors] = useState<FieldErrors>({})
  const [failure, setFailure] = useState<unknown>(null)
  const [busy, setBusy] = useState(false)

  const submit = async () => {
    const local = validateSeries(value)
    setErrors(local); setFailure(null)
    if (hasErrors(local)) return
    setBusy(true)
    try { await onSubmit(value); if (!slugLocked) setValue(EMPTY) }
    catch (cause) { if (cause instanceof ApiError && cause.status === 400) setErrors(cause.fieldErrors); else setFailure(cause) }
    finally { setBusy(false) }
  }
  return (
    <div className="space-y-2 rounded border p-3">
      <label className="block text-sm">제목<input className={input} value={value.title} onChange={e => setValue({ ...value, title: e.target.value })} /><FieldError errors={errors} field="title" /></label>
      <label className="block text-sm">slug (공개 주소 /series/&lt;slug&gt; — 만든 뒤에는 바꿀 수 없습니다)
        <input className={input} value={value.slug} readOnly={slugLocked} spellCheck={false} onChange={e => setValue({ ...value, slug: e.target.value })} /><FieldError errors={errors} field="slug" /></label>
      <label className="block text-sm">설명<textarea className={input} rows={3} value={value.description} onChange={e => setValue({ ...value, description: e.target.value })} /><FieldError errors={errors} field="description" /></label>
      <ErrorNotice error={failure} />
      <div className="flex gap-3 text-sm">
        <button type="button" disabled={busy} onClick={() => void submit()} className="rounded bg-black px-3 py-1 text-white disabled:opacity-40">{submitLabel}</button>
        {onCancel && <button type="button" className="underline" onClick={onCancel}>취소</button>}
      </div>
    </div>
  )
}

export function SeriesPage() {
  const client = useQueryClient()
  const [editing, setEditing] = useState<string | null>(null)
  const list = useQuery({ queryKey: ['series', 'list'], queryFn: ({ signal }) => api.list(signal) })
  const refresh = () => client.invalidateQueries({ queryKey: ['series'] })
  const remove = useMutation({ mutationFn: (item: Series) => api.remove(item.id), onSettled: refresh })

  const confirmRemove = (item: Series) => {
    if (window.confirm(`시리즈 "${item.title}"을(를) 삭제할까요?\n소속 글 ${item.postCount}편은 삭제되지 않고 시리즈에서만 빠집니다.`)) remove.mutate(item)
  }

  return (
    <main className="mx-auto max-w-3xl space-y-4 p-4">
      <h1 className="text-xl font-bold">시리즈</h1>
      <ErrorNotice error={list.error} onRetry={() => void list.refetch()} />
      <ErrorNotice error={remove.error} />
      {list.isPending ? <Loading /> : (
        <ul className="space-y-2">
          {list.data?.map(item => (
            <li key={item.id}>
              {editing === item.id ? (
                <SeriesForm initial={{ slug: item.slug, title: item.title, description: item.description }} slugLocked submitLabel="수정 저장"
                  onSubmit={async value => { await api.update(item.id, value); setEditing(null); await refresh() }} onCancel={() => setEditing(null)} />
              ) : (
                <div className="flex items-start gap-3 rounded border p-3 text-sm">
                  <div className="flex-1"><strong>{item.title}</strong> <span className="text-xs text-gray-500">/series/{item.slug} · 글 {item.postCount}편</span>
                    <p className="whitespace-pre-wrap text-gray-700">{item.description}</p></div>
                  <button type="button" className="underline" onClick={() => setEditing(item.id)}>수정</button>
                  <button type="button" className="text-red-700 underline" disabled={remove.isPending} onClick={() => confirmRemove(item)}>삭제</button>
                </div>
              )}
            </li>
          ))}
          {list.data?.length === 0 && <li className="text-sm text-gray-500">시리즈가 없습니다.</li>}
        </ul>
      )}
      <h2 className="font-bold">새 시리즈</h2>
      <SeriesForm initial={EMPTY} slugLocked={false} submitLabel="만들기" onSubmit={async value => { await api.create(value); await refresh() }} />
    </main>
  )
}
