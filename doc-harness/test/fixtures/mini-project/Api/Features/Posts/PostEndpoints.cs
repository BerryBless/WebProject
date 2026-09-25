namespace Mini.Features.Posts;

public static class PostEndpoints
{
    public static void MapPostEndpoints(this WebApplication app)
    {
        app.MapGet("/api/posts", (AppDbContext db) => db.Posts.ToList());
        app.MapPost("/api/posts", (AppDbContext db, Post post) => { db.Posts.Add(post); db.SaveChanges(); return Results.Created(); });
    }
}
