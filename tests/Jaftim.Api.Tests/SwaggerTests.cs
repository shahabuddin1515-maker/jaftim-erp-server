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

    /// <summary>
    /// Every public property of PagedRequest becomes a query parameter in the document, so a computed helper there
    /// (the old Skip) shows up as a field clients can fill in but the server ignores.
    /// </summary>
    [Theory]
    [InlineData("/api/users", "UserTypeId")]
    [InlineData("/api/roles", null)]
    [InlineData("/api/tenants", null)]
    [InlineData("/api/tagging/available-customers", null)]
    [InlineData("/api/stocks", null)]
    public async Task Paged_lists_expose_exactly_the_paging_contract(string path, string? extra)
    {
        HttpClient client = factory.CreateClient();
        using JsonDocument doc = JsonDocument.Parse(await client.GetStringAsync("/swagger/v1/swagger.json"));

        HashSet<string> names = doc.RootElement.GetProperty("paths").GetProperty(path).GetProperty("get")
            .GetProperty("parameters").EnumerateArray().Select(p => p.GetProperty("name").GetString()!).ToHashSet();

        Assert.Superset(new HashSet<string> { "Page", "PageSize", "Search", "SortBy", "SortDescending" }, names);
        Assert.DoesNotContain("Skip", names);
        if (extra is not null) Assert.Contains(extra, names);
    }

    [Fact]
    public async Task Office_locations_lookup_is_routed_permission_gated_and_takes_no_paging()
    {
        HttpClient client = factory.CreateClient();
        using JsonDocument doc = JsonDocument.Parse(await client.GetStringAsync("/swagger/v1/swagger.json"));

        JsonElement get = doc.RootElement.GetProperty("paths").GetProperty("/api/lookups/office-locations").GetProperty("get");
        Assert.Contains("AuthTable", get.GetProperty("description").GetString());
        Assert.True(get.GetProperty("responses").TryGetProperty("403", out _));
        Assert.False(get.TryGetProperty("parameters", out JsonElement p) && p.GetArrayLength() > 0);   // fixed list, not paged

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/lookups/office-locations")).StatusCode);
    }

    [Fact]
    public async Task Scoped_lookup_takes_only_the_table_so_the_scope_cannot_come_from_the_client()
    {
        HttpClient client = factory.CreateClient();
        using JsonDocument doc = JsonDocument.Parse(await client.GetStringAsync("/swagger/v1/swagger.json"));

        JsonElement get = doc.RootElement.GetProperty("paths").GetProperty("/api/lookups/scoped/{table}").GetProperty("get");
        Assert.Contains("AuthTable", get.GetProperty("description").GetString());
        Assert.True(get.GetProperty("responses").TryGetProperty("403", out _));
        JsonElement parameter = Assert.Single(get.GetProperty("parameters").EnumerateArray());
        Assert.Equal("table", parameter.GetProperty("name").GetString());
        Assert.Equal("path", parameter.GetProperty("in").GetString());

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/lookups/scoped/SalesPerson_View")).StatusCode);
    }

    [Fact]
    public async Task Swagger_ui_and_scalar_are_served_in_development()
    {
        HttpClient client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/swagger/index.html")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/scalar")).StatusCode);
    }
}
