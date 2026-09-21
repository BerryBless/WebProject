import { useId, useState, type KeyboardEvent } from 'react'
import { displayTag } from '../lib/validation'

interface Props { value: string[]; onChange: (next: string[]) => void; suggestions: string[] }

/** 태그 칩 입력. Enter·쉼표로 추가, 대소문자만 다른 중복은 추가하지 않는다(서버도 정규화 이름으로 합친다). */
export function TagInput({ value, onChange, suggestions }: Props) {
  const [text, setText] = useState('')
  const listId = useId()

  const commit = () => {
    const tag = displayTag(text)
    setText('')
    if (tag.length === 0 || value.some(existing => existing.toLowerCase() === tag.toLowerCase())) return
    onChange([...value, tag])
  }
  const onKeyDown = (event: KeyboardEvent<HTMLInputElement>) => {
    // 한글 조합 중의 Enter는 조합 확정이다 — 태그 추가로 처리하지 않는다.
    if (event.nativeEvent.isComposing) return
    if (event.key === 'Enter' || event.key === ',') { event.preventDefault(); commit() }
    else if (event.key === 'Backspace' && text.length === 0 && value.length > 0) onChange(value.slice(0, -1))
  }

  return (
    <div className="flex flex-wrap items-center gap-1 rounded border p-1">
      {value.map(tag => (
        <span key={tag} className="flex items-center gap-1 rounded bg-gray-100 px-2 py-0.5 text-xs">
          {tag}
          <button type="button" aria-label={`태그 ${tag} 제거`} onClick={() => onChange(value.filter(t => t !== tag))}>×</button>
        </span>
      ))}
      <input aria-label="태그 추가" list={listId} value={text} onChange={e => setText(e.target.value)} onKeyDown={onKeyDown} onBlur={commit}
        placeholder="태그 입력 후 Enter" className="min-w-[8rem] flex-1 p-1 text-sm outline-none" />
      <datalist id={listId}>{suggestions.map(name => <option key={name} value={name} />)}</datalist>
    </div>
  )
}
