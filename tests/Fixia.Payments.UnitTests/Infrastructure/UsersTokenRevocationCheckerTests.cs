using System.Net;
using System.Text;
using Fixia.Payments.Api.Security;
using FluentAssertions;

namespace Fixia.Payments.UnitTests.Infrastructure;

/// <summary>El cliente HTTP de la revocación (GC-502) frente a las respuestas posibles de ms-users.</summary>
public class UsersTokenRevocationCheckerTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            _respond = respond;
        }

        public HttpRequestMessage? Last { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Last = request;
            return Task.FromResult(_respond(request));
        }
    }

    private static (UsersTokenRevocationChecker Checker, StubHandler Handler) With(HttpStatusCode status, string body)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://ms-users:8080") };
        return (new UsersTokenRevocationChecker(http), handler);
    }

    [Theory]
    [InlineData("{\"revoked\":true}", true)]
    [InlineData("{\"revoked\":false}", false)]
    public async Task ShouldReadTheStatusFromUsers(string body, bool expected)
    {
        var (checker, handler) = With(HttpStatusCode.OK, body);

        (await checker.IsRevokedAsync("abc-123")).Should().Be(expected);
        handler.Last!.RequestUri!.AbsolutePath.Should().Be("/internal/tokens/abc-123/status");
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, "{}")]
    [InlineData(HttpStatusCode.OK, "no es json")]
    [InlineData(HttpStatusCode.Unauthorized, "{\"revoked\":false}")]
    [InlineData(HttpStatusCode.InternalServerError, "")]
    public async Task AnyOtherAnswer_ShouldBeUnverifiable(HttpStatusCode status, string body)
    {
        var (checker, _) = With(status, body);

        var check = () => checker.IsRevokedAsync("abc-123");

        await check.Should().ThrowAsync<TokenRevocationCheckException>();
    }

    [Fact]
    public async Task NetworkFailure_ShouldBeUnverifiable()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("sin conexión"));
        var checker = new UsersTokenRevocationChecker(new HttpClient(handler) { BaseAddress = new Uri("http://x") });

        var check = () => checker.IsRevokedAsync("abc");

        await check.Should().ThrowAsync<TokenRevocationCheckException>();
    }
}
