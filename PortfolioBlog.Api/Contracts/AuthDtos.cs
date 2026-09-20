namespace PortfolioBlog.Api.Contracts;

/// <summary>현재 요청의 로그인 여부. 관리 SPA가 로그인 화면으로 보낼지 결정하는 근거.</summary>
/// <param name="Authenticated">세션이 유효한 관리자로 인증된 요청이면 <c>true</c>.</param>
public sealed record AuthStatusDto(bool Authenticated);
