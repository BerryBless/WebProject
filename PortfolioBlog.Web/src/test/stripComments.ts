import ts from 'typescript'

/** 주석 하나의 범위(UTF-16 코드 유닛 오프셋, TypeScript AST의 pos/end와 같은 단위). end는 배타적이다. */
export interface CommentRange { pos: number; end: number }

const isJsDocKind = (kind: ts.SyntaxKind): boolean => kind >= ts.SyntaxKind.FirstJSDocNode && kind <= ts.SyntaxKind.LastJSDocNode

/**
 * 소스 트리의 리프 토큰(자식이 없는 노드)을 전부 모은다. `getChildren()`은 콘크리트 구문 트리라
 * `{`·`}`·`(`·`)`·`[`·`]` 같은 구두점도 노드로 노출한다(포워드 순회용 `forEachChild`는 이런 토큰을 건너뛴다) —
 * 그래서 닫는 구분자 바로 앞의 트리비아(주석)도 그 구분자 토큰의 리딩 트리비아로 잡힌다.
 * JSDoc 노드(`FirstJSDocNode`~`LastJSDocNode`)는 내려가지 않는다 — `/** … *\/`는 그 앞뒤 실제 코드 토큰
 * 사이의 트리비아 구간에 통째로 들어 있으므로, 여기서 별도로 다루지 않아도 트리비아 스캔이 그대로 주석으로 찾는다.
 */
function collectLeafTokens(node: ts.Node, sourceFile: ts.SourceFile, out: ts.Node[]): void {
  if (isJsDocKind(node.kind)) return
  const children = node.getChildren(sourceFile)
  if (children.length === 0) { out.push(node); return }
  for (const child of children) collectLeafTokens(child, sourceFile, out)
}

/**
 * `[start, end)` 구간(리프 토큰 하나의 `getFullStart()`에서 `getStart()` 사이 — 문법상 트리비아만 온다) 안의
 * 주석을 찾는다. 공백·개행은 건너뛴다. `//`는 줄바꿈 직전까지, `/*`는 `*\/`까지(구간 끝을 넘지 않는다)를 담는다.
 * 그 밖의 문자(파일 맨 앞 `#!` shebang 등)는 한 글자씩 건너뛴다 — 이 구간에 실제 코드가 올 수 없다는 문법
 * 보장 덕분에, 문자열·정규식·JSX 텍스트 안의 `//`·`/*`가 섞일 걱정이 없다(그것들은 리프 토큰 자신의 텍스트다).
 */
function scanTriviaForComments(text: string, start: number, end: number, ranges: CommentRange[]): void {
  let i = start
  while (i < end) {
    const ch = text[i]
    if (/\s/.test(ch)) { i++; continue }
    if (ch === '/' && i + 1 < end && text[i + 1] === '/') {
      const commentStart = i
      let j = i + 2
      while (j < end && text[j] !== '\n' && text[j] !== '\r') j++
      ranges.push({ pos: commentStart, end: j })
      i = j
      continue
    }
    if (ch === '/' && i + 1 < end && text[i + 1] === '*') {
      const commentStart = i
      let j = i + 2
      while (j < end && !(text[j] === '*' && j + 1 < end && text[j + 1] === '/')) j++
      j = (j < end && text[j] === '*') ? j + 2 : end // 닫는 */를 포함해 끝낸다. 구간 안에 없으면(문법상 없어야 정상) 구간 끝에서 멈춘다.
      ranges.push({ pos: commentStart, end: j })
      i = j
      continue
    }
    i++
  }
}

/**
 * 소스에서 실제 주석의 범위만 찾는다. TypeScript 파서로 소스를 분석해 모든 리프 토큰(구두점 포함)을 모으고,
 * 각 리프 토큰 앞의 트리비아 구간(`[getFullStart(), getStart())`)만 훑어 그 안의 `//`·`/*…*\/`를 주석으로 담는다.
 * 이 구간은 문법상 공백·개행·주석(·파일 맨 앞 shebang)만 올 수 있다 — 문자열 리터럴·정규식 리터럴·JSX 텍스트는
 * 전부 그 자체가 리프 토큰의 본문이라 이 구간에 섞이지 않는다(JSX 텍스트가 `//`나 `/*`로 시작해도 주석으로
 * 읽히지 않는다 — 텍스트 자체가 리프 토큰이므로 애초에 트리비아 스캔 대상이 아니다).
 * 구두점 토큰도 리프로 잡히므로 닫는 `}`·`)`·`]` 바로 앞의 주석(빈 블록·빈 객체·빈 배열·빈 인자 목록·빈 인터페이스
 * 안의 주석, 마지막 문장 뒤의 꼬리 주석)도 놓치지 않는다. `EndOfFileToken`도 리프라 파일 끝의 주석은 그 토큰의
 * 리딩 트리비아로 잡힌다.
 */
export function collectCommentRanges(text: string, fileName = 'x.tsx'): CommentRange[] {
  const sourceFile = ts.createSourceFile(fileName, text, ts.ScriptTarget.Latest, /* setParentNodes */ true, ts.ScriptKind.TSX)
  const leaves: ts.Node[] = []
  collectLeafTokens(sourceFile, sourceFile, leaves)
  const ranges: CommentRange[] = []
  for (const leaf of leaves) {
    const triviaStart = leaf.getFullStart()
    // includeJsDocComment 인자를 안 줘서(기본 false) JSDoc을 지나쳐 실제 토큰에서 멈춘다 — 그래서 JSDoc이
    // 이 트리비아 구간 안에 들어와 아래 스캔이 블록 주석으로 잡는다(실측: JSDoc 한 줄 뒤에 선언이 오는 입력에서
    // 기본값은 11, true는 0을 준다 — true를 주면 JSDoc 앞에서 멈춰 구간이 비고 JSDoc이 지워지지 않는다).
    const triviaEnd = leaf.getStart(sourceFile)
    if (triviaEnd > triviaStart) scanTriviaForComments(text, triviaStart, triviaEnd, ranges)
  }
  return ranges
}

/**
 * 주석을 뺀다. 각 주석 범위(`collectCommentRanges`)를 같은 길이의 공백으로 치환한다 — 범위 안의 개행(`\n`·`\r`)은
 * 그대로 두므로 출력의 줄 수·각 줄의 나머지 문자의 열 위치는 입력과 같다.
 * 보장: 출력 길이는 입력과 같다. 실제 코드 토큰 안의 문자는 그대로 남는다. 코드 토큰 밖의 비주석 트리비아(파일
 * 맨 앞 shebang 등)까지 보존한다고는 보장하지 않는다. `source-guards.test.ts`는 이 두 가지를 `stripComments`의
 * 내부 구현과 무관하게(자체 파싱으로 얻은 토큰 span과 대조해) 파일마다 직접 확인한다.
 * 보장하지 않음: 문법 오류가 있는 입력에서 TypeScript 파서가 어떻게 복구하는지는 검사하지 않았다(미검증) —
 * 이 함수가 훑는 대상은 빌드를 통과하는(`tsc -b` 0 오류) .ts/.tsx 파일뿐이라 실전에서 부딪힐 가능성은 낮다고
 * 본다(추론).
 */
export function stripComments(text: string, fileName = 'x.tsx'): string {
  const ranges = collectCommentRanges(text, fileName)
  let result = ''
  let cursor = 0
  for (const { pos, end } of ranges) {
    result += text.slice(cursor, pos)
    result += blank(text.slice(pos, end))
    cursor = end
  }
  result += text.slice(cursor)
  return result
}

function blank(segment: string): string {
  let out = ''
  for (let i = 0; i < segment.length; i++) out += (segment[i] === '\n' || segment[i] === '\r') ? segment[i] : ' '
  return out
}
