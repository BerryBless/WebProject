import { useCallback, useEffect, useRef, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link, useNavigate, useParams } from 'react-router'
import { attachments, posts, series as seriesApi, tags as tagsApi } from '../api/endpoints'
import { ApiError, describeError, type FieldErrors } from '../api/errors'
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

/** 409 뒤 최신본 재조회가 실패했을 때 보여줄 문구. 404는 "그 사이 삭제됐다"는 우리 쪽 해석이라 서버 문자열이
    아니라 고정 문구를 쓴다(401은 noteAuthFailure가 이미 로그인 화면으로 보내 여기까지 오지 않는다). 404 문구는
    "임시본에 남아 있다"고 단정하지 않는다 — 디바운스 전이면 임시본이 아직 없고, 그 글을 다시 열어도 상세 조회
    자체가 404라 복원할 화면이 없다. 이 화면에 지금 보이는 내용만은 확실히 참이라 그것만 말한다. */
const conflictRefetchMessage = (error: unknown): string =>
  error instanceof ApiError && error.status === 404
    ? '이 글은 다른 곳에서 삭제되었습니다. 이 화면의 내용은 그대로 있으니 필요하면 복사해 새 글로 저장하세요.'
    : describeError(error)

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
  const [conflictRefetchError, setConflictRefetchError] = useState<unknown>(null)
  const [uploadErrors, setUploadErrors] = useState<string[]>([])
  const [uploadBlockedNotice, setUploadBlockedNotice] = useState<string | null>(null)
  const [uploading, setUploading] = useState(false)
  const [draftFailed, setDraftFailed] = useState(false)
  // 새 글이 서버에 이미 만들어졌지만(이 Editor 인스턴스는 여전히 postId===null인 "새 글" 화면) 그 뒤에 친
  // 내용을 임시본으로 남기지 못했을 때만 그 글의 id로 채워진다 — 아래 onSuccess 참고. 채워지면 'new' 키 자동
  // 저장을 멈추고 저장 버튼을 막는다(서버에는 이미 글이 있으므로 다시 누르면 중복 생성으로 이어진다).
  const [createdPostId, setCreatedPostId] = useState<string | null>(null)

  const dirty = !sameFields(fields, baseline.fields)
  const set = <K extends keyof DraftFields>(key: K, value: DraftFields[K]) => setFields(prev => ({ ...prev, [key]: value }))

  // 저장은 네트워크 왕복이고 그동안 입력란·편집기를 막지 않는다 — 응답이 도착했을 때 그사이 친 내용을 덮지 않으려면
  // 그 시점의 "진짜 최신" fields가 필요한데, onSuccess 콜백은 mutate를 호출한 시점의 클로저(fields)만 본다.
  // 매 렌더 뒤 최신값으로 갱신되는 ref를 따로 두고 onSuccess에서는 이 ref만 읽는다.
  const fieldsRef = useRef(fields)
  useEffect(() => { fieldsRef.current = fields })
  // 자동 저장 effect가 기준선을 읽을 때 의존성으로 넣지 않기 위한 ref(아래 effect 참고).
  const baselineRef = useRef(baseline)
  useEffect(() => { baselineRef.current = baseline })
  // flushDraft는 언마운트 cleanup에서도 불린다. 그 cleanup은 마운트 시점 클로저만 보므로 state를 그대로 읽으면
  // 두 가드가 영영 마운트 당시 값(각각 null)에 묶인다 — 아래 두 ref로 최신값을 읽는다.
  const pendingDraftRef = useRef(pendingDraft)
  useEffect(() => { pendingDraftRef.current = pendingDraft })
  const createdPostIdRef = useRef(createdPostId)
  useEffect(() => { createdPostIdRef.current = createdPostId })
  // 이 화면을 떠나며 'new' 키에 다시 쓰면 안 되는 상태(새 글 생성 성공·잠금)를 그 자리에서 표시한다.
  const skipFlushRef = useRef(false)

  const seriesList = useQuery({ queryKey: ['series', 'list'], queryFn: ({ signal }) => seriesApi.list(signal) })
  const tagList = useQuery({ queryKey: ['tags', 'list'], queryFn: ({ signal }) => tagsApi.list(signal) })

  // 임시본 자동 저장(1초 디바운스). 복원 여부를 아직 고르지 않았으면(pendingDraft) 기존 임시본을 건드리지 않는다.
  // baseline은 의존성에 넣지 않는다(실측 결함): 저장 성공으로 onSuccess가 setBaseline을 부르면, baseline이
  // 의존성에 있을 때 이 effect가 같은 커밋에서 한 번 더 도는데, 그 시점의 settled는 아직 디바운스 전 값(저장 전
  // 내용)이다 — 그 낡은 값이 방금 받은 새 baseVersion과 함께 임시본으로 다시 쓰여, 저장 직후 화면을 떠나면
  // "저장 전 내용을 복원하시겠습니까"를 경고 없이 제안하게 된다. 저장 뒤의 임시본 처리는 onSuccess가 그 시점의
  // 실제 입력으로 직접 한다(아래 save 참고).
  const settled = useDebounced(fields, 1000)
  useEffect(() => {
    // createdPostId가 채워진 뒤에는 'new' 키에 다시 쓰지 않는다 — 그 내용은 이미 서버에 제출됐고, 이 화면이
    // 보여주는 것은 방금 만든 별개의 글(createdPostId)을 위한 내용이라 'new' 임시본과 뒤섞이면 안 된다.
    if (pendingDraft !== null || createdPostId !== null) return
    const currentBaseline = baselineRef.current
    if (sameFields(settled, currentBaseline.fields)) { clearDraft(draftKey); setDraftFailed(false); return }
    // 저장소 쓰기(부수 효과)의 성공 여부를 화면에 알려야 한다 — 렌더 중에 파생할 수 있는 값이 아니다.
    setDraftFailed(!saveDraft(draftKey, { ...settled, baseVersion: currentBaseline.version, savedAt: new Date().toISOString() }))
  }, [settled, pendingDraft, draftKey, createdPostId])

  /**
   * 지금 화면에 있는 입력을 임시본에 즉시 한 번 쓴다(디바운스를 기다리지 않는다).
   * 자동 저장은 1초 디바운스라, 마지막으로 1초 이상 멈춘 뒤에 친 내용은 아직 임시본에 없다 — 미리보기(500ms)의
   * 401이 이 화면을 먼저 언마운트하거나 사용자가 링크·탭 닫기로 떠나면 그 입력이 사라진다(실측, Chromium·Firefox).
   * 쓰지 않는 조건은 자동 저장 effect와 같다: 복원 여부를 아직 고르지 않았으면 기존 임시본을 덮지 않고, 잠긴
   * 상태(createdPostId)의 내용은 이미 서버에 제출된 'new' 키와 뒤섞이면 안 되며, 기준선과 같으면 지킬 내용이 없다.
   * state가 아니라 ref만 읽는다 — 언마운트 cleanup은 마운트 시점의 클로저를 실행한다.
   * useCallback으로 신원을 고정한다: 아래 두 effect가 이 함수를 의존성으로 받는데, 렌더마다 새 함수가 되면
   * 매 렌더 cleanup이 돌아 flush가 반복된다. draftKey는 이 인스턴스에서 바뀌지 않는다(Editor가 키로 나뉜다).
   */
  const flushDraft = useCallback(() => {
    if (skipFlushRef.current || pendingDraftRef.current !== null || createdPostIdRef.current !== null) return
    const currentBaseline = baselineRef.current
    const current = fieldsRef.current
    if (sameFields(current, currentBaseline.fields)) return
    // 실패해도 알릴 화면이 없는 자리다(떠나는 중이다) — 반환값은 자동 저장 effect와 onSuccess가 이미 화면에 반영한다.
    saveDraft(draftKey, { ...current, baseVersion: currentBaseline.version, savedAt: new Date().toISOString() })
  }, [draftKey])
  // 언마운트(401로 로그인 화면 전환·링크 이동·라우트 교체)에서 마지막으로 한 번 쓴다.
  useEffect(() => () => flushDraft(), [flushDraft])
  // 탭·창 닫기와 bfcache 진입은 언마운트를 일으키지 않는다. pagehide는 그 두 경우에 모두 오는 이벤트다.
  useEffect(() => {
    const onPageHide = () => flushDraft()
    window.addEventListener('pagehide', onPageHide)
    return () => window.removeEventListener('pagehide', onPageHide)
  }, [flushDraft])

  // 임시본을 저장하지 못했는데(용량 초과 등) 바뀐 내용이 있으면 창을 닫기 전에 한 번 묻는다.
  useEffect(() => {
    if (!(dirty && draftFailed)) return
    const warn = (event: BeforeUnloadEvent) => event.preventDefault()
    window.addEventListener('beforeunload', warn)
    return () => window.removeEventListener('beforeunload', warn)
  }, [dirty, draftFailed])

  const replaceAll = (next: DraftFields) => { setFields(next); setEditorKey(k => k + 1) } // 편집기는 비제어라 다시 마운트해야 본문이 바뀐다

  const save = useMutation({
    // 요청 본문은 제출 시점의 submitted로 만든다(클로저의 fields가 아니다) — 응답이 오는 동안 fields가 더 바뀌어도
    // 이미 나간 요청의 내용은 제출 시점 그대로여야 한다.
    mutationFn: (submitted: DraftFields) => postId === null ? posts.create(submitted) : posts.update(postId, { ...submitted, version: baseline.version ?? undefined }),
    onSuccess: (saved, submitted) => {
      // 저장 왕복 중에도 입력을 막지 않았다. fieldsRef는 렌더 뒤 effect로 갱신되므로 아직 flush되지 않았으면
      // 한 렌더 뒤처질 수 있다 — 그래서 임시본·재마운트처럼 렌더 밖에서 한 번만 읽으면 되는 판단에만 쓰고,
      // 화면 입력란에 서버 값을 대입할지는 React가 직접 건네주는 prev(커밋된 최신 state)로 판단한다.
      // 기준선·version은 이 판단과 무관하게 항상 서버값으로 맞춘다 — 다음 저장은 이 version을 그대로 실어 보내야
      // 서버가 "최신 위에 쓰는 것"으로 받아들인다.
      const unchanged = sameFields(fieldsRef.current, submitted)
      const next = fromServer(saved)
      setBaseline({ fields: next, version: saved.version })
      client.setQueryData(detailKey(saved.id), saved)
      void client.invalidateQueries({ queryKey: ['posts', 'list'] })
      void client.invalidateQueries({ queryKey: ['tags'] })
      void client.invalidateQueries({ queryKey: ['series'] })
      if (postId === null) {
        if (unchanged) {
          clearDraft(NEW_POST_KEY)
          // 떠나면서 'new' 키에 다시 쓰지 않는다: 서버가 제목의 공백을 다듬는 것만으로도 현재 입력이 기준선과
          // 달라져 언마운트 flush가 방금 지운 임시본을 되살리고, 그 복원이 곧 중복 발행으로 이어진다.
          skipFlushRef.current = true
          void navigate(`/posts/${saved.id}`, { replace: true })
          return
        }
        // 그사이 친 내용이 있으면 새 글 id 키로 임시본을 남긴다 — 이동한 편집 화면이 "임시본 복원"을 제안한다.
        // slug는 생성 뒤 바꿀 수 없으므로 서버가 확정한 값으로 고정한다. 반환값을 확인한다: 저장에 실패했는데
        // 그대로 이동하면 이 Editor 인스턴스가 곧바로 언마운트돼 안내를 화면에 보일 틈이 없다 — 그 경우는
        // 이동하지 않고 남는다.
        const savedToStorage = saveDraft(saved.id, { ...fieldsRef.current, slug: saved.slug, baseVersion: saved.version, savedAt: new Date().toISOString() })
        if (savedToStorage) {
          clearDraft(NEW_POST_KEY)
          skipFlushRef.current = true // 위와 같은 이유 — 이 화면의 내용은 방금 새 글 id 키로 옮겨 적었다
          void navigate(`/posts/${saved.id}`, { replace: true })
          return
        }
        // 저장에 실패했다 — 서버에는 이미 글이 생겼으므로(saved.id) 'new' 임시본은 더 쓸모가 없다: 그 내용은
        // 이미 제출됐고 그 뒤에 친 것은 지금 화면에 그대로 있다. 지금 지운다 — 남겨 두면 새로고침·"새 글"
        // 재진입에서 그 임시본이 복원을 제안하고, 복원 후 다시 저장하면 POST /api/posts가 또 나가 slug 중복
        // 409(또는 slug를 바꾼 중복 글)로 이어진다. clearDraft는 이미 예외를 삼킨다(lib/drafts.ts) — 이 호출
        // 자체가 조용히 실패하면(스토리지가 완전히 막힌 극단적 경우) 'new' 임시본이 남아 그 경로가 여전히
        // 열려 있을 수 있다(미검증 잔여 위험).
        clearDraft(NEW_POST_KEY)
        skipFlushRef.current = true // 떠날 때도 'new' 키에 다시 쓰지 않는다(자동 저장을 멈추는 것과 같은 이유)
        setCreatedPostId(saved.id)
        // 이 화면의 내용은 어디에도 남아 있지 않다 — 창을 닫기 전에 경고가 뜨도록 저장 실패를 표시한다.
        setDraftFailed(true)
        // 방금 지운 'new' 임시본을 가리키던 복원 제안도 내린다 — 누르면 복사하라고 안내한 내용을 덮는다.
        setPendingDraft(null)
        return
      }
      // 응답이 오기까지 아무것도 안 바뀌었을 때만(prev가 submitted와 같을 때만) 서버 값을 대입한다 — 업데이터
      // 안에서는 다른 setState를 부를 수 없어 재마운트는 아래에서 별도로 처리한다.
      setFields(prev => sameFields(prev, submitted) ? next : prev)
      if (unchanged) {
        // 응답이 오기까지 아무것도 안 바뀌었을 때만 임시본을 지운다(더 지킬 내용이 없을 때만).
        clearDraft(draftKey)
        if (next.contentMarkdown !== submitted.contentMarkdown) setEditorKey(k => k + 1) // 편집기는 비제어라 다시 마운트해야 본문이 바뀐다
      } else {
        // 응답이 오는 동안 더 바뀐 내용이 있다 — 자동 저장 틱(최대 1초)을 기다리지 않고 지금 내용으로 임시본을
        // 다시 쓴다. baseVersion을 방금 받은 새 version으로 맞춰야 다음에 열었을 때 정확히 이어서 복원된다.
        setDraftFailed(!saveDraft(draftKey, { ...fieldsRef.current, baseVersion: saved.version, savedAt: new Date().toISOString() }))
      }
    },
    onError: async error => {
      if (!(error instanceof ApiError)) return
      if (error.status === 400) setFieldErrors(error.fieldErrors)
      // 409는 본문 검증(400)보다 먼저 올 수 있다(서버는 version을 렌더보다 먼저 본다). 최신본을 받아 나란히 보여 준다.
      if (error.status === 409 && postId !== null) {
        try { setConflict(await posts.get(postId)) }
        catch (cause) {
          noteAuthFailure(client, cause)
          setConflictRefetchError(cause) // 401 외의 원인(404·네트워크)은 감추지 않고 보여준다
        }
      }
    },
  })

  const submit = () => {
    const errors = validatePost(fields)
    setFieldErrors(errors)
    setConflictRefetchError(null)
    // 저장 요청이 401로 돌아오면 이 화면은 곧바로 언마운트된다 — 보내기 전에 지금 입력을 임시본에 남긴다.
    if (!hasErrors(errors)) { flushDraft(); save.mutate(fields) }
  }

  // ref는 그 자리에서 바로 읽고 쓴다(state는 다음 렌더에 반영된다 — 추론, 이 저장소에서 직접 측정하지는 않았다).
  // 업로드 도중 빠르게 또 uploadImages가 불리는 경우(붙여넣기·드롭·파일 선택이 겹칠 때)를 놓치지 않으려고 ref로 막는다.
  const uploadingRef = useRef(false)
  const uploadImages = async (files: File[]) => {
    if (uploadingRef.current) {
      setUploadBlockedNotice('이미 업로드가 진행 중입니다 — 끝난 뒤 다시 시도하세요.')
      return
    }
    uploadingRef.current = true
    // 차단 안내는 새 업로드를 시작할 때만 지운다 — 진행 중이던 업로드가 끝나며(아래 uploadErrors 교체) 함께
    // 지우면, 그 업로드가 끝나는 순간 이 안내가 사라져 무엇이 막혔는지 알 길이 없어진다.
    setUploadBlockedNotice(null)
    setUploadErrors([]); setUploading(true)
    const errors: string[] = []
    for (let index = 0; index < files.length; index++) { // 순차 업로드: 서버의 업로드 동시 실행 한도는 전역 2다
      const file = files[index]
      const problem = validateImageFile(file)
      if (problem) { errors.push(`${file.name}: ${problem}`); continue } // 편의 검사 실패는 그 파일만 건너뛰고 계속한다
      try {
        const uploaded = await attachments.upload(file, file.name || 'image.png')
        editor.current?.insertAtCursor(`![${altTextOf(uploaded.fileName)}](${uploaded.url})\n`)
      } catch (cause) {
        noteAuthFailure(client, cause) // useMutation을 거치지 않는 직접 await이라 401을 스스로 기록해야 한다
        const remaining = files.length - index - 1
        errors.push(`${file.name}: ${describeError(cause)}${remaining > 0 ? ` (나머지 ${remaining}개는 올리지 않았습니다.)` : ''}`)
        break // 서버 호출 실패는 여기서 멈춘다(다음 파일이 또 실패할 가능성이 높다) — 남은 파일은 올리지 않는다
      }
    }
    uploadingRef.current = false
    setUploading(false)
    setUploadErrors(errors)
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
        <button type="button" onClick={submit} disabled={save.isPending || conflict !== null || createdPostId !== null || (postId !== null && !dirty)}
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
      {!conflict && conflictRefetchError !== null && (
        <div role="alert" className="rounded border border-red-300 bg-red-50 p-3 text-sm text-red-800">
          <span>{conflictRefetchMessage(conflictRefetchError)}</span>
        </div>
      )}
      {/* 재조회 실패 안내가 이미 409 실패의 원인을 설명하므로, 같은 실패에 대한 일반 안내(ErrorNotice)를 겹쳐 보여주지 않는다. */}
      {!conflict && conflictRefetchError === null && <ErrorNotice error={save.error instanceof ApiError && save.error.status === 400 ? null : save.error} />}
      {createdPostId !== null && (
        <div role="alert" className="rounded border border-amber-400 bg-amber-50 p-3 text-sm text-amber-900">
          글은 이미 만들어져 공개됐습니다. 이 화면에서 그 뒤에 고친 내용은 임시 저장하지 못했습니다(브라우저 저장
          공간). 그 내용을 먼저 복사해 두고 <Link to={`/posts/${createdPostId}`} className="underline">방금 만든 글 열기</Link>에서
          이어서 고치세요.
        </div>
      )}

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
          {uploadBlockedNotice !== null && (
            <div role="alert" className="rounded border border-amber-400 bg-amber-50 p-3 text-sm text-amber-900">{uploadBlockedNotice}</div>
          )}
          {uploadErrors.length > 0 && (
            <div role="alert" className="rounded border border-red-300 bg-red-50 p-3 text-sm text-red-800">
              <ul className="list-disc pl-4">{uploadErrors.map((msg, i) => <li key={i}>{msg}</li>)}</ul>
            </div>
          )}
          <MarkdownEditor key={editorKey} ref={editor} initialValue={fields.contentMarkdown}
            onChange={value => set('contentMarkdown', value)} onImageFiles={files => void uploadImages(files)} />
          <FieldError errors={fieldErrors} field="contentMarkdown" />
        </div>
        <PreviewPane markdown={fields.contentMarkdown} />
      </div>
    </main>
  )
}
