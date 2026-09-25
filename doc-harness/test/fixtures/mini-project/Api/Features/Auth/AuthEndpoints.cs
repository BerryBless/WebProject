namespace Mini.Features.Auth;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        // TODO: rate limit
        app.MapPost("/api/auth/login", (LoginRequest req) => Results.Ok());
    }
}
public record LoginRequest(string Password);
