using System.Net;
using System.Net.Http.Json;
using Jaftim.Api.Middleware;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Jaftim.Api.Tests;

/// <summary>Boots the real host with dummy config (no DB needed for these) and checks the pipeline wiring.</summary>
public sealed class ApiSmokeTests : IClassFixture<ApiSmokeTests.Factory>
{
    private readonly HttpClient _client;

    public ApiSmokeTests(Factory factory) => _client = factory.CreateClient();

    [Fact]
    public async Task Protected_endpoint_without_token_is_401()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/stocks");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_with_invalid_body_is_400_problem_details()
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/auth/login", new { email = "not-an-email", password = "" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("10.0.0.5", "10.0.0.0/24", true)]
    [InlineData("10.0.1.5", "10.0.0.0/24", false)]
    [InlineData("125.209.124.210", "125.209.124.208/28", true)]
    [InlineData("125.209.124.224", "125.209.124.208/28", false)]
    [InlineData("110.93.230.26", "110.93.230.26/32", true)]
    public void Cidr_matching(string ip, string cidr, bool expected) =>
        Assert.Equal(expected, IpAllowlistMiddleware.IsInCidr(IPAddress.Parse(ip), cidr));

    public sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            // UseSetting flows into the host configuration before Program.cs reads it (ConfigureAppConfiguration is too late
            // for values Program.cs reads at startup under minimal hosting).
            builder.UseSetting("ConnectionStrings:Catalog", "Server=(local);Database=none;Integrated Security=true;TrustServerCertificate=true");
            builder.UseSetting("ConnectionStrings:Hangfire", "Server=(local);Database=none;Integrated Security=true;TrustServerCertificate=true");
            builder.UseSetting("Auth:SigningKey", "integration-test-signing-key-32-bytes-min!!");
        }
    }
}
