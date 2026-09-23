var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<AppDbContext>();
var app = builder.Build();
app.MapPostEndpoints();
app.MapAuthEndpoints();
app.Run();
public partial class Program { }
