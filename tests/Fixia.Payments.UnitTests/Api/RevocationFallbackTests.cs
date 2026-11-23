using Fixia.Payments.Api.Security;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Fixia.Payments.UnitTests.Api;

/// <summary>
/// Revocación sin consultar a ms-users (GC-547): decide la lista local de revocados, solo si está al día (≤ 5 s); si no,
/// se falla cerrado.
/// </summary>
public class RevocationFallbackTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    private sealed class MovableTime : TimeProvider
    {
        public DateTimeOffset Current { get; set; } = Now;
        public override DateTimeOffset GetUtcNow() => Current;
    }

    private readonly MovableTime _time = new();
    private readonly RevocationList _list;
    private readonly ReplicatedTokenRevocationChecker _checker;

    public RevocationFallbackTests()
    {
        _list = new RevocationList(_time, Options.Create(new SessionRevocationOptions { MaxStalenessSeconds = 5 }));
        _checker = new ReplicatedTokenRevocationChecker(_list);
    }

    [Fact]
    public async Task ConLaListaAlDiaUnTokenNoRevocadoSirve()
    {
        _list.MarkCaughtUp();

        (await _checker.IsRevokedAsync("jti-1")).Should().BeFalse();
    }

    [Fact]
    public async Task UnTokenQueLaListaConoceComoRevocadoSeRechaza()
    {
        _list.MarkCaughtUp();
        _list.Add("jti-1", Now.AddMinutes(10));

        (await _checker.IsRevokedAsync("jti-1")).Should().BeTrue();
    }

    [Fact]
    public async Task UnRevocadoSeRechazaAunqueLaListaEsteAtrasada()
    {
        _list.Add("jti-1", Now.AddMinutes(10));

        (await _checker.IsRevokedAsync("jti-1")).Should().BeTrue();
    }

    [Fact]
    public async Task SinConfirmacionDelConsumidorFallaCerrado()
    {
        var act = () => _checker.IsRevokedAsync("jti-1");

        await act.Should().ThrowAsync<TokenRevocationCheckException>();
    }

    [Fact]
    public async Task SiLaListaSeAtrasaMasDeCincoSegundosFallaCerrado()
    {
        _list.MarkCaughtUp();
        _time.Current = Now.AddSeconds(5);
        (await _checker.IsRevokedAsync("jti-1")).Should().BeFalse();

        _time.Current = Now.AddSeconds(6);

        var act = () => _checker.IsRevokedAsync("jti-1");
        await act.Should().ThrowAsync<TokenRevocationCheckException>();
    }

    [Fact]
    public void LaListaOlvidaLosTokensVencidos()
    {
        _list.Add("jti-1", Now.AddSeconds(10));
        _list.Add("jti-viejo", Now.AddSeconds(-1));
        _list.Contains("jti-viejo").Should().BeFalse();

        _time.Current = Now.AddSeconds(11);

        _list.Contains("jti-1").Should().BeFalse();
    }
}
