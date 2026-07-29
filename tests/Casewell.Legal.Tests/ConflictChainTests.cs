using Cortex.Modules.Legal;
using Cortex.Modules.Legal.Persistence;

namespace Cortex.Modules.Legal.Tests;

public sealed class ConflictChainTests
{
    private static ConflictAttestation Link(
        string snapshot, string? priorHash, DateTimeOffset at, Guid userId) => new()
    {
        TenantId = Guid.NewGuid(),
        MatterId = Guid.NewGuid(),
        AttestedByUserId = userId,
        PerformedAt = at,
        SearchTermsJson = "[]",
        DataSnapshotJson = snapshot,
        PriorAttestationHash = priorHash,
        AttestationHash = ConflictChain.ComputeHash(snapshot, priorHash, at, userId),
    };

    [Fact]
    public void ComputeHash_IsDeterministic_AndSensitiveToEveryInput()
    {
        var at = DateTimeOffset.UtcNow;
        var user = Guid.NewGuid();
        var baseline = ConflictChain.ComputeHash("{}", null, at, user);

        Assert.Equal(baseline, ConflictChain.ComputeHash("{}", null, at, user));
        Assert.NotEqual(baseline, ConflictChain.ComputeHash("{\"x\":1}", null, at, user));
        Assert.NotEqual(baseline, ConflictChain.ComputeHash("{}", "abc", at, user));
        Assert.NotEqual(baseline, ConflictChain.ComputeHash("{}", null, at.AddTicks(1), user));
        Assert.NotEqual(baseline, ConflictChain.ComputeHash("{}", null, at, Guid.NewGuid()));
    }

    [Fact]
    public void FindBrokenLink_IntactChain_ReturnsNull()
    {
        var user = Guid.NewGuid();
        var t0 = DateTimeOffset.UtcNow;
        var first = Link("{\"matches\":[]}", null, t0, user);
        var second = Link("{\"matches\":[\"Initech\"]}", first.AttestationHash, t0.AddHours(1), user);

        Assert.Null(ConflictChain.FindBrokenLink([first, second]));
    }

    [Fact]
    public void FindBrokenLink_TamperedSnapshot_IsDetected_AtThatLink()
    {
        var user = Guid.NewGuid();
        var t0 = DateTimeOffset.UtcNow;
        var first = Link("{\"matches\":[]}", null, t0, user);
        var second = Link("{\"matches\":[\"Initech\"]}", first.AttestationHash, t0.AddHours(1), user);

        first.DataSnapshotJson = "{\"matches\":[\"REDACTED\"]}"; // the cover-up

        Assert.Equal(0, ConflictChain.FindBrokenLink([first, second]));
    }

    [Fact]
    public void FindBrokenLink_RemovedMiddleLink_BreaksTheChain()
    {
        var user = Guid.NewGuid();
        var t0 = DateTimeOffset.UtcNow;
        var first = Link("{\"a\":1}", null, t0, user);
        var second = Link("{\"b\":2}", first.AttestationHash, t0.AddHours(1), user);
        var third = Link("{\"c\":3}", second.AttestationHash, t0.AddHours(2), user);

        // Deleting the middle attestation leaves third pointing at a hash no prior row carries.
        Assert.Equal(1, ConflictChain.FindBrokenLink([first, third]));
    }
}

public sealed class ConflictMatchingTests
{
    [Theory]
    [InlineData("Initech LLC", "initech", true)]
    [InlineData("initech", "Initech LLC", true)]
    [InlineData("Jane Roe", "jane roe", true)]
    [InlineData("Acme Corp", "Initech", false)]
    public void Matches_IsSymmetricCaseInsensitiveContainment(string candidate, string term, bool expected)
    {
        Assert.Equal(expected, ConflictTools.Matches(candidate, term));
    }

    [Fact]
    public void ParseNames_SplitsTrimsAndDeduplicates()
    {
        var names = ConflictTools.ParseNames(" Initech ;\n jane roe; INITECH ;;");
        Assert.Equal(["Initech", "jane roe"], names);
    }
}
