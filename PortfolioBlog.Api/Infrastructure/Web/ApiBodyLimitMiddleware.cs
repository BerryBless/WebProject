using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;

namespace PortfolioBlog.Api.Infrastructure.Web;

/// <summary><c>/api</c>의 JSON 본문을 256KB로 제한한다(스펙 3.7). 자체 상한을 선언한 엔드포인트(업로드의 <c>RequestSizeLimit</c>)는 건드리지 않는다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 무상태 클래스. <see cref="InvokeAsync"/>는 요청마다 새로 전달되는 <see cref="HttpContext"/>만 다루므로 여러 요청 스레드에서 동시에 호출돼도 안전하다.</description></item>
/// <item><description><b>Memory Allocation:</b> Content-Length 초과 시 <see cref="ErrorResponses.WriteAsync"/>의 응답 버퍼만 할당한다. 통과 경로는 <see cref="LengthLimitedStream"/> 래퍼 1개를 할당해 <c>Request.Body</c>를 감싼다(내부 버퍼를 새로 잡지 않는다).</description></item>
/// <item><description><b>Blocking:</b> 즉시 반환(Non-blocking). 실제 본문 읽기·바이트 계수는 다음 미들웨어(모델 바인딩)가 <see cref="LengthLimitedStream.ReadAsync(Memory{byte}, CancellationToken)"/>를 호출하는 시점으로 지연된다.</description></item>
/// </list>
/// <see cref="JsonLimitBytes"/>는 직렬화 후 전송 바이트 기준이다. 비 ASCII 문자를 <c>\uXXXX</c>로 이스케이프하는 클라이언트(.NET의 기본 <c>JsonSerializerOptions</c> 인코더)는
/// 같은 논리 내용이라도 본문이 최대 6배까지 부풀 수 있다 — 관리 SPA가 쓰는 브라우저의 <c>JSON.stringify</c>는 비 ASCII를 이스케이프하지 않으므로 운영 트래픽에서는
/// 해당하지 않는다(이번 라운드에서 기존 테스트 2건이 이 인코더 차이로 413이 나는 것을 실측했다).
/// </remarks>
public sealed class ApiBodyLimitMiddleware(RequestDelegate next)
{
    /// <summary>관리 JSON 본문 상한(직렬화 후 바이트).</summary>
    public const long JsonLimitBytes = 262_144;

    private static readonly PathString ApiPrefix = new("/api");

    /// <summary><c>/api</c> 요청 중 매칭되는 엔드포인트가 있고 자체 크기 상한이 없는 것만 골라 <see cref="JsonLimitBytes"/>를 넘는 본문을 읽기 전에(선언 길이) 또는 읽는 도중(미선언 길이) 413으로 끊는다.</summary>
    /// <param name="context">현재 HTTP 요청 컨텍스트.</param>
    /// <returns>통과 시 다음 미들웨어가 완료되면 끝나는 작업, 거부 시 413 응답 쓰기가 완료되면 끝나는 작업.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. <paramref name="context"/>는 요청마다 새로 전달되며 인스턴스 필드는 불변인 <c>next</c>뿐이다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 선언 길이 초과 거부 경로는 <see cref="ErrorResponses.WriteAsync"/>의 버퍼만, 통과 경로는 <see cref="LengthLimitedStream"/> 1개를 할당한다.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환(Non-blocking). <c>next(context)</c>를 그대로 반환한다.</description></item>
    /// </list>
    /// </remarks>
    public Task InvokeAsync(HttpContext context)
    {
        // GetEndpoint()가 null이면(=매칭되는 라우트가 없다) 이 요청은 어차피 404로 끝나고 어떤 핸들러도 본문을 읽지 않는다.
        // 여기서 걸러 두지 않으면 큰 본문을 가진 존재하지 않는 /api 경로가 404가 아니라 413이 된다.
        var endpoint = context.GetEndpoint();
        if (!context.Request.Path.StartsWithSegments(ApiPrefix)
            || endpoint is null
            || endpoint.Metadata.GetMetadata<IRequestSizeLimitMetadata>() is not null)
        {
            return next(context);
        }
        if (context.Request.ContentLength > JsonLimitBytes)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return ErrorResponses.WriteAsync(context, StatusCodes.Status413PayloadTooLarge);
        }
        // Kestrel에서는 서버가 직접 끊는다(TestServer에는 이 기능이 없어 null이다).
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature) feature.MaxRequestBodySize = JsonLimitBytes;
        // 길이를 선언하지 않은(chunked) 본문: 읽는 쪽에서 센다. 최소 API의 JSON 바인딩은 BadHttpRequestException의 상태 코드를 그대로 응답한다(실측 413).
        context.Request.Body = new LengthLimitedStream(context.Request.Body, JsonLimitBytes);
        return next(context);
    }

    // Stream 래퍼: 복사 없이 안쪽 스트림의 읽기를 그대로 넘기며 누적 바이트만 센다(버퍼를 새로 잡지 않는다).
    // 길이를 선언하지 않은(chunked) 요청은 Content-Length 검사를 통과하므로, 읽어 들이는 매 청크마다 누적치를 확인해야 상한을 넘는 순간
    // BadHttpRequestException(413)으로 끊을 수 있다 — Content-Length 검사만으로는 이 경로를 잡지 못한다.
    private sealed class LengthLimitedStream(Stream inner, long limit) : Stream
    {
        private long _total;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Count(await inner.ReadAsync(buffer, cancellationToken));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int Count(int read)
        {
            _total += read;
            return _total > limit
                ? throw new BadHttpRequestException("요청 본문이 상한을 넘었습니다.", StatusCodes.Status413PayloadTooLarge)
                : read;
        }
    }
}
