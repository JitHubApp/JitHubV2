using JitHub.Security;
using JitHub.Web.Services;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class OAuthHandoffStoreTests
{
    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public async Task RedeemRequiresMatchingStateAndVerifierAndIsSingleUse()
    {
        var time = new TestTimeProvider(DateTimeOffset.Parse("2026-08-07T12:00:00Z"));
        var store = new OAuthHandoffStore(time);
        string state = OAuthHandoffProtocol.CreateState(development: true, out string verifier);
        string handoff = await store.CreateAsync("secret-token", state);

        Assert.Equal("secret-token", await store.RedeemAsync(handoff, state, verifier));
        Assert.Null(await store.RedeemAsync(handoff, state, verifier));
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public async Task InvalidVerifierConsumesHandoff()
    {
        var store = new OAuthHandoffStore(TimeProvider.System);
        string state = OAuthHandoffProtocol.CreateState(development: false, out string verifier);
        string handoff = await store.CreateAsync("secret-token", state);

        Assert.Null(await store.RedeemAsync(handoff, state, verifier + "attacker"));
        Assert.Null(await store.RedeemAsync(handoff, state, verifier));
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public async Task ExpiredHandoffCannotBeRedeemed()
    {
        var time = new TestTimeProvider(DateTimeOffset.Parse("2026-08-07T12:00:00Z"));
        var store = new OAuthHandoffStore(time);
        string state = OAuthHandoffProtocol.CreateState(development: true, out string verifier);
        string handoff = await store.CreateAsync("secret-token", state);
        time.Advance(TimeSpan.FromMinutes(3));

        Assert.Null(await store.RedeemAsync(handoff, state, verifier));
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public async Task SharedBackendSupportsCrossInstanceAtomicRedemption()
    {
        var time = new TestTimeProvider(DateTimeOffset.Parse("2026-08-07T12:00:00Z"));
        var backend = new InMemoryOAuthHandoffBackend(time);
        var creator = new OAuthHandoffStore(time, backend);
        var redeemer = new OAuthHandoffStore(time, backend);
        string state = OAuthHandoffProtocol.CreateState(development: false, out string verifier);

        string handoff = await creator.CreateAsync("secret-token", state);

        Assert.Equal("secret-token", await redeemer.RedeemAsync(handoff, state, verifier));
        Assert.Null(await creator.RedeemAsync(handoff, state, verifier));
    }

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now += duration;
    }
}
