import { fireEvent, render, screen } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { TagInput } from '../components/TagInput'

describe('태그 입력', () => {
  it('쉼표로 구분해 붙여넣은 값은 각각 태그가 된다(빈 항목·대소문자만 다른 중복은 버린다)', () => {
    const onChange = vi.fn()
    render(<TagInput value={[]} onChange={onChange} suggestions={[]} />)
    const input = screen.getByLabelText('태그 추가')
    // 붙여넣기는 input의 값을 한 번에 바꾼다(키 하나하나가 아니다) — fireEvent.change로 그 모양을 그대로 낸다.
    fireEvent.change(input, { target: { value: 'a, b,,A' } })
    fireEvent.keyDown(input, { key: 'Enter' })
    expect(onChange).toHaveBeenCalledTimes(1)
    expect(onChange).toHaveBeenCalledWith(['a', 'b'])
  })
})
