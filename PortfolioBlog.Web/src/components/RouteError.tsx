import { Link } from 'react-router'

/**
 * 라우트에서 던져진 오류(지연 청크 로드 실패·렌더 예외)를 대신 그린다.
 * react-router의 기본 화면은 원시 오류 메시지와 스택을 그대로 보여 주고 돌아갈 길을 주지 않는다.
 * 오류 내용(useRouteError의 값)은 화면에도 콘솔에도 쓰지 않는다 — 번들·서버 내부 사정을 화면으로 흘리지 않는다.
 * 배포로 청크 해시가 바뀌면 열려 있던 탭이 편집 화면을 처음 열 때 이 경로를 밟는다(추론).
 */
export function RouteError() {
  return (
    <main className="mx-auto mt-24 max-w-md space-y-3 p-4 text-sm">
      <h1 className="text-xl font-bold">화면을 불러오지 못했습니다.</h1>
      <p>새 버전이 배포됐을 수 있습니다.</p>
      <div className="flex items-center gap-4">
        <button type="button" className="rounded bg-black px-3 py-2 text-white" onClick={() => window.location.reload()}>새로고침</button>
        <Link to="/" className="underline">글 목록으로</Link>
      </div>
    </main>
  )
}
