var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

var app = builder.Build();

app.MapControllers();

app.Run();

// Exposed so integration tests can host the app with WebApplicationFactory<Program>.
public partial class Program;
