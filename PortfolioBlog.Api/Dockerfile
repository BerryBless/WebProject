# syntax=docker/dockerfile:1
# 빌드 컨텍스트는 저장소 루트다(Directory.Packages.props가 필요하다): docker build -f PortfolioBlog.Api/Dockerfile .
# 이미지 태그는 정확한 버전으로 고정한다. 올릴 때는 deploy/OPERATIONS.md의 "이미지 버전 올리기"를 따른다.
FROM mcr.microsoft.com/dotnet/sdk:10.0.401 AS build
WORKDIR /src
# 복원 계층을 소스 변경과 분리한다(패키지 버전은 Directory.Packages.props가 전이 의존성까지 고정한다).
COPY Directory.Packages.props ./
COPY PortfolioBlog.Api/PortfolioBlog.Api.csproj PortfolioBlog.Api/
RUN dotnet restore PortfolioBlog.Api/PortfolioBlog.Api.csproj
COPY PortfolioBlog.Api/ PortfolioBlog.Api/
# UseAppHost=false: 네이티브 실행 파일을 만들지 않는다(진입점은 dotnet PortfolioBlog.Api.dll 하나).
RUN dotnet publish PortfolioBlog.Api/PortfolioBlog.Api.csproj -c Release -o /app --no-restore -p:UseAppHost=false \
    && rm -f /app/web.config /app/appsettings.Development.json /app/*.pdb
# 게시 결과가 기대한 모양인지 빌드에서 확인한다. 최종 이미지에는 셸이 없어 거기서는 볼 수 없다.
# - wwwroot는 css/site.css 하나여야 한다(csproj의 CompressionEnabled=false가 지워지면 .gz·.br가 돌아와 공개 경로가 늘어난다).
# - EF 디자인 타임 어셈블리·.pdb(빌드 경로·줄 번호가 실린 디버그 심볼)가 섞이면 안 된다.
RUN test "$(cd /app/wwwroot && find . -type f | sort)" = "./css/site.css" \
    && test -z "$(find /app -name 'Microsoft.EntityFrameworkCore.Design*' -o -name 'Microsoft.CodeAnalysis*' -o -name '*.Development.json' -o -name '*.pdb')"
# 볼륨 마운트 지점. 빈 named volume은 처음 마운트될 때 이 디렉터리의 소유자·권한으로 초기화된다.
RUN mkdir -p /data/attachments /data/dpkeys && chmod 700 /data/attachments /data/dpkeys

# chiseled: 셸·패키지 관리자가 없고 기본 사용자가 비루트(1654)다. extra: ICU·tzdata 포함(한글 정렬·정규화가 개발 환경과 같게 동작한다).
FROM mcr.microsoft.com/dotnet/aspnet:10.0.12-noble-chiseled-extra AS final
WORKDIR /app
COPY --from=build /app ./
COPY --from=build --chown=1654:1654 /data /data
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=8080 \
    Attachments__RootPath=/data/attachments \
    DataProtection__KeysPath=/data/dpkeys
USER 1654
EXPOSE 8080
ENTRYPOINT ["dotnet", "PortfolioBlog.Api.dll"]
