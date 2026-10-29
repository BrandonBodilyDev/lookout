using System.Text.Json;
using Lookout.Api;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
builder.Services.AddSingleton(new ScenarioStore(ScenarioStore.Resolve(builder.Configuration["Lookout:ScenarioDir"])));

var app = builder.Build();

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

app.MapGet("/api/scenarios", (ScenarioStore store) =>
    store.List().Select(s => new { s.Id, s.Name, s.Description, s.Start, s.DurationMin }));

app.MapGet("/api/scenarios/{id}/timeline", (string id, ScenarioStore store) =>
    store.Run(id) is { } run ? Results.Ok(TimelineDto.From(run)) : Results.NotFound());

app.Run();
