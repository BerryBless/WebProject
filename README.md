# WebProject

`ClaudeCodeStudy`의 개발 하네스를 그대로 이식한 .NET 10 솔루션 템플릿입니다.

## 포함된 하네스

| 구성 | 위치 | 역할 |
|------|------|------|
| Claude 에이전트 25종 | `.claude/agents/` | 코드 리뷰·동시성·GC·파이프라인·TDD·교차 검증 전문 에이전트 |
| Claude 스킬 28종 | `.claude/skills/` | `/commitandpush`, `code-review-orchestrator`, `cross-verify`, `codex` 등 |
| Codex 미러 | `.agents/skills/`, `.codex/` | Codex CLI가 동일 규칙을 읽도록 한 미러(`cross-verify`·`codex`는 의도적으로 제외) |
| Stop 훅 자동 커밋 | `.claude/settings.json`, `scripts/auto-commit.ps1` | 턴 종료 시 `.git/auto_commit_msg.txt`를 읽어 커밋·푸시 |
| 커밋 메시지 훅 | `scripts/git-hooks/commit-msg` | `{접두사}: {제목}` 형식 강제 (클론 후 `.git/hooks/`에 복사) |
| CI | `.github/workflows/ci.yml` | push/PR 시 restore → build → test (Windows, .NET 10) |

## 시작하기

```powershell
git clone <repo> && cd WebProject
Copy-Item scripts/git-hooks/commit-msg .git/hooks/
dotnet build WebProject.sln
```

프로젝트 규칙은 `CLAUDE.md`(Claude Code) / `AGENTS.md`(Codex)를 참고하세요.
