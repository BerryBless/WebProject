import { useEffect, useRef, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link, useNavigate, useParams } from 'react-router'
import { attachments, posts, series as seriesApi, tags as tagsApi } from '../api/endpoints'
import { ApiError, type FieldErrors } from '../api/errors'
import type { PostDetail } from '../api/types'
import { noteAuthFailure } from '../app/queryClient'
import { ConflictPanel } from '../components/ConflictPanel'
import { MarkdownEditor, type MarkdownEditorHandle } from '../components/MarkdownEditor'
import { ErrorNotice, FieldError, Loading } from '../components/notices'
import { PreviewPane } from '../components/PreviewPane'
import { TagInput } from '../components/TagInput'
import { NEW_POST_KEY, clearDraft, loadDraft, sameFields, saveDraft, type Draft, type DraftFields } from '../lib/drafts'
import { altTextOf } from '../lib/markdownImage'
import { formatDateTime, useDebounced } from '../lib/useDebounced'
import { LIMITS, hasErrors, utf8ByteLength, validateImageFile, validatePost } from '../lib/validation'

const EMPTY: DraftFields = { slug: '', title: '', summary: '', contentMarkdown: '', tagNames: [], seriesId: null, seriesOrder: null }
const fromServer = (post: PostDetail): DraftFields => ({
  slug: post.slug, title: post.title, summary: post.summary, contentMarkdown: post.contentMarkdown,
  tagNames: post.tags, seriesId: post.seriesId, seriesOrder: post.seriesOrder,
})
const detailKey = (postId: string) => ['posts', 'detail', postId] as const

export function PostEditorPage() {
  const { id } = useParams()
  const postId = id ?? null
  const detail = useQuery({
    queryKey: detailKey(postId ?? NEW_POST_KEY), enabled: postId !== null,
    queryFn: ({ signal }) => posts.get(postId!, signal),
    // 편집 중에는 다시 불러오지 않는다: 입력을 덮어쓸 수 있다. 최신본은 저장 응답과 409 처리에서만 받는다.
    staleTime: Infinity, gcTime: 0,
  })
  if (postId !== null && detail.isPending) return <Loading />
  if (postId !== null && detail.isError) return <main className="p-4"><ErrorNotice error={detail.error} onRetry={() => void detail.refetch()} /> <Link to="/" className="underline">목록으로</Link></main>
  // key: /posts/new ↔ /posts/:id 사이를 오갈 때 편집 상태를 통째로 새로 만든다.
  return <Editor key={postId ?? NEW_POST_KEY} postId={postId} server={detail.data ?? null} />
}
export { PostEditorPage as Component } // react-router의 route.lazy가 찾는 이름

function Editor({ postId, server }: { postId: string | null; server: PostDetail | null }) {
  const draftKey = postId ?? NEW_POST_KEY
  const navigate = useNavigate()
  const client = useQueryClient()
  const editor = useRef<MarkdownEditorHandle>(null)

  const [baseline, setBaseline] = useState(() => ({ fields: server ? fromServer(server) : EMPTY, version: server?.version ?? null }))
  const [fields, setFields] = useState<DraftFields>(baseline.fields)
  const [editorKey, setEditorKey] = useState(0)
  const [pendingDraft, setPendingDraft] = useState<Draft | null>(() => {
    const draft = loadDraft(draftKey)
    return draft && !sameFields(draft, baseline.fields) ? draft : null
  })
  const [fieldErrors, setFieldErrors] = useState<FieldErrors>({})
  const [conflict, setConflict] = useState<PostDetail | null>(null)
  const [uploadError, setUploadError] = useState<unknown>(null)
  const [uploading, setUploading] = useState(false)
  const [draftFailed, setDraftFailed] = useState(false)

  const dirty = !sameFields(fields, baseline.fields)
  const set = <K extends keyof DraftFields>(key: K, value: DraftFields[K]) => setFields(prev => ({ ...prev, [key]: value }))

  const seriesList = useQuery({ queryKey: ['series', 'list'], queryFn: ({ signal }) => seriesApi.list(signal) })
  const tagList = useQuery({ queryKey: ['tags', 'list'], queryFn: ({ signal }) => tagsApi.list(signal) })

  // 임시본 자동 저장(1초 디바운스). 복원 여부를 아직 고르지 않았으면(pendingDraft) 기존 임시본을 건드리지 않는다.
  const settled = useDebounced(fields, 1000)
  useEffect(() => {
    if (pendingDraft !== null) return
    // oxlint-disable-next-line react/set-state-in-effect
    if (sameFields(settled, baseline.fields)) { clearDraft(draftKey); setDraftFailed(false); return }
    // 저장소 쓰기(부수 효과)의 성공 여부를 화면에 알려야 한다 — 렌더 중에 파생할 수 있는 값이 아니다.
    // oxlint-disable-next-line react/set-state-in-effect
    setDraftFailed(!saveDraft(draftKey, { ...settled, baseVersion: baseline.version, savedAt: new Date().toISOString() }))
  }, [settled, baseline, pendingDraft, draftKey])

  // 임시본을 저장하지 못했는데(용량 초과 등) 바뀐 내용이 있으면 창을 닫기 전에 한 번 묻는다.
  useEffect(() => {
    if (!(dirty && draftFailed)) return
    const warn = (event: BeforeUnloadEvent) => event.preventDefault()
    window.addEventListener('beforeunload', warn)
    return () => window.removeEventListener('beforeunload', warn)
  }, [dirty, draftFailed])

  const replaceAll = (next: DraftFields) => { setFields(next); setEditorKey(k => k + 1) } // 편집기는 비제어라 다시 마운트해야 본문이 바뀐다

  const save = useMutation({
    mutationFn: () => postId === null ? posts.create(fields) : posts.update(postId, { ...fields, version: baseline.version ?? undefined }),
    onSuccess: saved => {
      clearDraft(draftKey)
      client.setQueryData(detailKey(saved.id), saved)
      void client.invalidateQueries({ queryKey: ['posts', 'list'] })
      void client.invalidateQueries({ queryKey: ['tags'] })
      void client.invalidateQueries({ queryKey: ['series'] })
      if (postId === null) { void navigate(`/posts/${saved.id}`, { replace: true }); return }
      const next = fromServer(saved)
      setBaseline({ fields: next, version: saved.version })
      if (next.contentMarkdown === fields.contentMarkdown) setFields(next); else replaceAll(next)
    },
    onError: async error => {
      if (!(error instanceof ApiError)) return
      if (error.status === 400) setFieldErrors(error.fieldErrors)
      // 409는 본문 검증(400)보다 먼저 올 수 있다(서버는 version을 렌더보다 먼저 본다). 최신본을 받아 나란히 보여 준다.
      if (error.status === 409 && postId !== null) {
        try { setConflict(await posts.get(postId)) } catch (cause) { noteAuthFailure(client, cause) }
      }
    },
  })

  const submit = () => {
    const errors = validatePost(fields)
    setFieldErrors(errors)
    if (!hasErrors(errors)) save.mutate()
  }

  const uploadImages = async (files: File[]) => {
    setUploadError(null); setUploading(true)
    try {
      for (const file of files) { // 순차 업로드: 서버의 업로드 동시 실행 한도는 전역 2다
        const problem = validateImageFile(file)
        if (problem) { setUploadError(new ApiError(400, '올릴 수 없는 파일', problem)); continue }
        const uploaded = await attachments.upload(file, file.name || 'image.png')
        editor.current?.insertAtCursor(`![${altTextOf(uploaded.fileName)}](${uploaded.url})\n`)
      }
    } catch (cause) { noteAuthFailure(client, cause); setUploadError(cause) } finally { setUploading(false) }
  }

  const bytes = utf8ByteLength(fields.contentMarkdown)
  const input = 'mt-1 w-full rounded border p-2 text-sm'

  return (
    <main className="space-y-3 p-4">
      <div className="flex items-center gap-3">
        <h1 className="text-xl font-bold">{postId === null ? '새 글' : '글 수정'}</h1>
        <Link to="/" className="text-sm underline">목록</Link>
        <span className="flex-1" />
        {draftFailed && <span className="text-xs text-amber-700">임시본을 저장하지 못했습니다(브라우저 저장 공간).</span>}
        {/* 글에는 초안 상태가 없다 — 저장이 곧 발행이다. */}
        <span className="text-sm font-medium text-red-700">저장하면 즉시 공개됩니다</span>
        <button type="button" onClick={submit} disabled={save.isPending || conflict !== null || (postId !== null && !dirty)}
          className="rounded bg-black px-4 py-2 text-sm text-white disabled:opacity-40">{save.isPending ? '저장 중…' : '저장'}</button>
      </div>

      {pendingDraft && (
        <div role="status" className="flex flex-wrap items-center gap-3 rounded border border-blue-300 bg-blue-50 p-3 text-sm">
          <span>{formatDateTime(pendingDraft.savedAt)}에 저장된 임시본이 있습니다.
            {pendingDraft.baseVersion !== baseline.version && ' 그 뒤에 서버본이 바뀌었습니다 — 복원 후 저장하면 서버의 변경을 덮어씁니다.'}</span>
          <button type="button" className="underline" onClick={() => { replaceAll(pendingDraft); setPendingDraft(null) }}>임시본 복원</button>
          <button type="button" className="underline" onClick={() => { clearDraft(draftKey); setPendingDraft(null) }}>버리기</button>
        </div>
      )}
      {conflict && (
        <ConflictPanel server={conflict} mineMarkdown={fields.contentMarkdown}
          onTakeServer={() => { const next = fromServer(conflict); setBaseline({ fields: next, version: conflict.version }); replaceAll(next); clearDraft(draftKey); setConflict(null); save.reset() }}
          onKeepMine={() => { setBaseline({ fields: fromServer(conflict), version: conflict.version }); setConflict(null); save.reset() }} />
      )}
      {!conflict && <ErrorNotice error={save.error instanceof ApiError && save.error.status === 400 ? null : save.error} />}

      <div className="grid gap-4 lg:grid-cols-2">
        <div className="space-y-3">
          <label className="block text-sm">제목
            <input className={input} value={fields.title} maxLength={LIMITS.titleMax + 50} onChange={e => set('title', e.target.value)} />
            <FieldError errors={fieldErrors} field="title" /></label>
          <label className="block text-sm">slug (공개 주소 /posts/&lt;slug&gt; — 만든 뒤에는 바꿀 수 없습니다)
            <input className={input} value={fields.slug} readOnly={postId !== null} spellCheck={false} autoCapitalize="none"
              placeholder="my-first-post" onChange={e => set('slug', e.target.value)} />
            <FieldError errors={fieldErrors} field="slug" /></label>
          <label className="block text-sm">요약 (목록·검색 결과·피드에 쓰입니다)
            <textarea className={input} rows={2} value={fields.summary} onChange={e => set('summary', e.target.value)} />
            <FieldError errors={fieldErrors} field="summary" /></label>
          <div className="text-sm">태그
            <TagInput value={fields.tagNames} onChange={next => set('tagNames', next)} suggestions={tagList.data?.map(t => t.name) ?? []} />
            <FieldError errors={fieldErrors} field="tagNames" /></div>
          <div className="flex gap-3 text-sm">
            <label className="flex-1">시리즈
              <select className={input} value={fields.seriesId ?? ''} onChange={e => {
                const value = e.target.value === '' ? null : e.target.value
                setFields(prev => ({ ...prev, seriesId: value, seriesOrder: value === null ? null : prev.seriesOrder ?? 1 }))
              }}>
                <option value="">(없음)</option>
                {seriesList.data?.map(s => <option key={s.id} value={s.id}>{s.title}</option>)}
              </select></label>
            <label className="w-28">순서
              <input className={input} type="number" min={1} step={1} disabled={fields.seriesId === null} value={fields.seriesOrder ?? ''}
                onChange={e => set('seriesOrder', e.target.value === '' ? null : Number(e.target.value))} /></label>
          </div>
          <FieldError errors={fieldErrors} field="seriesOrder" />
          <FieldError errors={fieldErrors} field="seriesId" />

          <div className="flex items-center gap-3 text-sm">
            <span>본문 (마크다운)</span>
            <label className="cursor-pointer underline">이미지 올리기
              <input type="file" accept="image/png,image/jpeg,image/gif,image/webp" multiple hidden
                onChange={e => { const list = Array.from(e.target.files ?? []); e.target.value = ''; if (list.length > 0) void uploadImages(list) }} /></label>
            {uploading && <span role="status">올리는 중…</span>}
            <span className="flex-1" />
            <span className={bytes > LIMITS.contentMaxBytes ? 'text-red-700' : 'text-gray-500'}>{(bytes / 1024).toFixed(1)} / {LIMITS.contentMaxBytes / 1024}KB</span>
          </div>
          <ErrorNotice error={uploadError} />
          <MarkdownEditor key={editorKey} ref={editor} initialValue={fields.contentMarkdown}
            onChange={value => set('contentMarkdown', value)} onImageFiles={files => void uploadImages(files)} />
          <FieldError errors={fieldErrors} field="contentMarkdown" />
        </div>
        <PreviewPane markdown={fields.contentMarkdown} />
      </div>
    </main>
  )
}
