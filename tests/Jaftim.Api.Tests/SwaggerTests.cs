using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Jaftim.Api.Tests;

/// <summary>The generated document is part of the contract with the frontend team; these guard its key features.</summary>
public sealed class SwaggerTests(ApiSmokeTests.Factory factory) : IClassFixture<ApiSmokeTests.Factory>
{
    [Fact]
    public async Task Document_is_served_with_bearer_scheme_permission_notes_and_problem_responses()
    {
        HttpClient client = factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/swagger/v1/swagger.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = doc.RootElement;

        Assert.True(root.GetProperty("components").GetProperty("securitySchemes").TryGetProperty("Bearer", out JsonElement bearer));
        Assert.Equal("http", bearer.GetProperty("type").GetString());
        Assert.True(root.GetProperty("components").GetProperty("schemas").TryGetProperty("ProblemDetails", out _));

        JsonElement stocks = root.GetProperty("paths").GetProperty("/api/stocks").GetProperty("get");
        Assert.Contains("Requires permission", stocks.GetProperty("description").GetString());
        Assert.Contains("StockListing", stocks.GetProperty("description").GetString());
        Assert.True(stocks.GetProperty("responses").TryGetProperty("401", out _));
        Assert.True(stocks.GetProperty("responses").TryGetProperty("403", out _));
        Assert.Equal("Bearer", stocks.GetProperty("security")[0].EnumerateObject().First().Name);

        JsonElement login = root.GetProperty("paths").GetProperty("/api/auth/login").GetProperty("post");
        Assert.False(login.GetProperty("responses").TryGetProperty("401", out _));   // anonymous: no auth responses
        Assert.False(login.TryGetProperty("security", out JsonElement s) && s.GetArrayLength() > 0);
        Assert.Contains("tenantCode", login.GetProperty("summary").GetString());     // XML summary made it in
    }

    [Fact]
    public async Task Swagger_ui_and_scalar_are_served_in_development()
    {
        HttpClient client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/swagger/index.html")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/scalar")).StatusCode);
    }
}
