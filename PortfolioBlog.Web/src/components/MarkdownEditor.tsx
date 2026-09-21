import { useEffect, useImperativeHandle, useRef, type Ref } from 'react'
import { EditorView, basicSetup } from 'codemirror'
import { markdown } from '@codemirror/lang-markdown'

export interface MarkdownEditorHandle {
  /** 현재 커서(선택 영역) 자리에 텍스트를 넣는다. 이미지 업로드가 끝났을 때 마크다운 이미지 문법을 넣는 데 쓴다. */
  insertAtCursor: (text: string) => void
}

interface Props {
  /** 처음 문서. 이후의 변경은 onChange로만 나간다(비제어). 외부에서 내용을 통째로 바꾸려면 key를 바꿔 다시 마운트한다. */
  initialValue: string
  onChange: (value: string) => void
  /** 붙여넣기·끌어다 놓기로 들어온 이미지 파일. */
  onImageFiles: (files: File[]) => void
  ref?: Ref<MarkdownEditorHandle>
}

const imagesOf = (list: DataTransfer | null): File[] =>
  list ? Array.from(list.files).filter(file => file.type.startsWith('image/')) : []

/**
 * CodeMirror 6 마크다운 편집기. CodeMirror는 <style> 요소 하나를 문서에 주입한다(실측) — 관리 SPA의 CSP가
 * style-src-elem에 'unsafe-inline'을 두는 유일한 이유다. 스크립트 실행 경로는 없다(입력은 텍스트로만 다뤄진다).
 */
export function MarkdownEditor({ initialValue, onChange, onImageFiles, ref }: Props) {
  const host = useRef<HTMLDivElement>(null)
  const view = useRef<EditorView | null>(null)
  // 콜백은 ref로 들고 있는다: 부모가 매 렌더마다 새 함수를 줘도 편집기를 다시 만들지 않는다.
  const callbacks = useRef({ onChange, onImageFiles })
  useEffect(() => { callbacks.current = { onChange, onImageFiles } })

  useEffect(() => {
    const editor = new EditorView({
      doc: initialValue,
      parent: host.current!,
      extensions: [
        basicSetup, markdown(), EditorView.lineWrapping,
        EditorView.contentAttributes.of({ 'aria-label': '본문(마크다운)' }),
        EditorView.updateListener.of(update => { if (update.docChanged) callbacks.current.onChange(update.state.doc.toString()) }),
        EditorView.domEventHandlers({
          paste: event => {
            const files = imagesOf(event.clipboardData)
            if (files.length === 0) return false
            event.preventDefault(); callbacks.current.onImageFiles(files); return true
          },
          drop: event => {
            const files = imagesOf(event.dataTransfer)
            if (files.length === 0) return false
            event.preventDefault(); callbacks.current.onImageFiles(files); return true
          },
        }),
      ],
    })
    view.current = editor
    return () => { editor.destroy(); view.current = null }
    // initialValue는 마운트 시점 값만 쓴다(비제어). 바꾸려면 key로 다시 마운트한다.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  useImperativeHandle(ref, () => ({
    insertAtCursor: (text: string) => {
      const editor = view.current
      if (!editor) return
      const { from, to } = editor.state.selection.main
      editor.dispatch({ changes: { from, to, insert: text }, selection: { anchor: from + text.length } })
      editor.focus()
    },
  }), [])

  return <div ref={host} className="min-h-[24rem] rounded border text-sm" data-testid="markdown-editor" />
}
