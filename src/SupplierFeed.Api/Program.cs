using SupplierFeed.Api.Data;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Default") ?? "Data Source=supplierfeed.db";
builder.Services.AddSingleton(new SqliteConnectionFactory(connectionString));
builder.Services.AddControllers();

var app = builder.Build();

DatabaseInitializer.Initialize(app.Services.GetRequiredService<SqliteConnectionFactory>());

app.MapControllers();

app.Run();

// Exposed so integration tests can host the app with WebApplicationFactory<Program>.
public partial class Program;
