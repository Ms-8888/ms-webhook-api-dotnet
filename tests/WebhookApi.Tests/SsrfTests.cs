using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using WebhookApi.Services;

namespace WebhookApi.Tests;

public class SsrfTests : IClassFixture<TestFactory>
{
    private readonly TestFactory _f;
    public SsrfTests(TestFactory f) => _f = f;

    [Theory]
    [InlineData("http://localhost:8080/hook")]
    [InlineData("http://LOCALHOST/hook")]
    [InlineData("http://foo.localhost/hook")]
    [InlineData("http://127.0.0.1/hook")]
    [InlineData("http://127.1.2.3/hook")]
    [InlineData("http://[::1]/hook")]
    [InlineData("http://10.0.0.5/hook")]
    [InlineData("http://172.16.0.1/hook")]
    [InlineData("http://172.31.255.255/hook")]
    [InlineData("http://192.168.1.1/hook")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("http://0.0.0.0/hook")]
    [InlineData("http://[::ffff:127.0.0.1]/hook")]
    [InlineData("http://[fd00::1]/hook")]
    [InlineData("http://[fe80::1]/hook")]
    [InlineData("http://2130706433/hook")]
    [InlineData("ftp://example.com/hook")]
    public async Task Unsafe_urls_are_rejected_with_422(string url)
    {
        var (_, key) = await _f.CreateTenantAsync();
        var response = await _f.ClientFor(key).PostAsJsonAsync("/endpoints", new { url });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Hostname_that_resolves_to_a_private_address_is_rejected()
    {
        _f.Dns.Records["internal.example.test"] = new[] { IPAddress.Parse("10.1.2.3") };
        var (_, key) = await _f.CreateTenantAsync();

        var response = await _f.ClientFor(key).PostAsJsonAsync("/endpoints", new { url = "https://internal.example.test/hook" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("private", body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Hostname_with_one_public_and_one_private_record_is_rejected()
    {
        _f.Dns.Records["mixed.example.test"] = new[] { IPAddress.Parse("93.184.216.34"), IPAddress.Parse("192.168.0.9") };
        var (_, key) = await _f.CreateTenantAsync();

        var response = await _f.ClientFor(key).PostAsJsonAsync("/endpoints", new { url = "https://mixed.example.test/hook" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Public_https_url_is_accepted()
    {
        var (_, key) = await _f.CreateTenantAsync();
        var response = await _f.ClientFor(key).PostAsJsonAsync("/endpoints", new { url = "https://hooks.example.com/in" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Theory]
    [InlineData("8.8.8.8", false)]
    [InlineData("93.184.216.34", false)]
    [InlineData("172.15.0.1", false)]
    [InlineData("172.32.0.1", false)]
    [InlineData("2606:4700:4700::1111", false)]
    [InlineData("10.255.255.255", true)]
    [InlineData("100.64.0.1", true)]
    [InlineData("::ffff:10.0.0.1", true)]
    [InlineData("::", true)]
    public void IsForbidden_classifies_addresses(string address, bool expected)
    {
        Assert.Equal(expected, SsrfGuard.IsForbidden(IPAddress.Parse(address)));
    }

    [Fact]
    public async Task SafeConnect_refuses_loopback_at_connect_time()
    {
        // Simulates a hostname that resolves to loopback after the endpoint was registered.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        

        var ex = await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await SafeConnect.ConnectAsync("localhost", port, CancellationToken.None));
        Assert.Contains("Blocked", ex.Message);
    }
}
