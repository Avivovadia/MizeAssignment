using SupplierFeed.Api.Data;
using SupplierFeed.Api.Domain;
using SupplierFeed.Api.Services;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Default") ?? "Data Source=supplierfeed.db";
var throttleOptions = builder.Configuration.GetSection(ThrottleOptions.SectionName).Get<ThrottleOptions>() ?? new ThrottleOptions();
var updatedAtOptions = builder.Configuration.GetSection(UpdatedAtOptions.SectionName).Get<UpdatedAtOptions>() ?? new UpdatedAtOptions();

builder.Services.AddSingleton(new SqliteConnectionFactory(connectionString));
builder.Services.AddSingleton(new ThrottleStore(throttleOptions)); // validates the options at startup
builder.Services.AddSingleton<ReservationStore>();
builder.Services.AddSingleton<StatsStore>();
builder.Services.AddSingleton(new UpdatedAtPolicy(updatedAtOptions)); // validates the options at startup
builder.Services.AddSingleton<IngestOrchestrator>();
builder.Services.AddControllers();

var app = builder.Build();

DatabaseInitializer.Initialize(app.Services.GetRequiredService<SqliteConnectionFactory>());

app.MapControllers();

app.Run();

// Exposed so integration tests can host the app with WebApplicationFactory<Program>.
public partial class Program;
