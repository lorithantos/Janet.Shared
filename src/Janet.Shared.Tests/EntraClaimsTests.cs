using System.Security.Claims;
using Janet.Entra;
using Xunit;

namespace Janet.Shared.Tests;

public class EntraClaimsTests
{
    private static readonly Guid GroupA = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static ClaimsPrincipal SignedIn(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), authenticationType: "test"));

    [Fact]
    public void A_full_sign_in_reads_every_part()
    {
        var reading = EntraClaims.Read(SignedIn(
            ("oid", "user-1"), ("tid", "tenant-1"), ("groups", GroupA.ToString()), ("roles", "Operator")));

        var user = Assert.IsType<EntraUser>(reading.User);
        Assert.Equal("user-1", user.ObjectId);
        Assert.Equal("tenant-1", user.TenantId);
        Assert.True(user.IsInGroup(GroupA));
        Assert.True(user.HasAppRole("Operator"));
    }

    [Fact]
    public void The_long_claim_spellings_are_read_too()
    {
        var reading = EntraClaims.Read(SignedIn(
            ("http://schemas.microsoft.com/identity/claims/objectidentifier", "user-2"),
            ("http://schemas.microsoft.com/identity/claims/tenantid", "tenant-2"),
            (ClaimTypes.Role, "Operator")));

        Assert.Equal("user-2", reading.User!.ObjectId);
        Assert.Equal("tenant-2", reading.User.TenantId);
        Assert.True(reading.User.HasAppRole("Operator"));
    }

    [Fact]
    public void App_roles_compare_exactly()
    {
        var user = EntraClaims.Read(SignedIn(("oid", "u"), ("roles", "operator"))).User!;

        Assert.False(user.HasAppRole("Operator"));
    }

    [Fact]
    public void A_group_claim_that_is_not_an_id_is_ignored()
    {
        var user = EntraClaims.Read(SignedIn(("oid", "u"), ("groups", "not-a-guid"), ("groups", GroupA.ToString()))).User!;

        Assert.Equal([GroupA], user.Groups);
    }

    [Fact]
    public void Not_signed_in_gives_no_user()
    {
        var reading = EntraClaims.Read(new ClaimsPrincipal(new ClaimsIdentity()));

        Assert.False(reading.IsSignedIn);
        Assert.Equal("not signed in", reading.Reason);
    }

    [Fact]
    public void No_object_id_gives_no_user()
    {
        var reading = EntraClaims.Read(SignedIn(("groups", GroupA.ToString()), ("roles", "Operator")));

        Assert.Null(reading.User);
        Assert.Contains("object ID", reading.Reason);
    }

    // Too many groups and Entra sends a pointer instead of the list. Reading what is there would
    // hide a membership, so there is no user.
    [Fact]
    public void A_group_overage_gives_no_user()
    {
        var reading = EntraClaims.Read(SignedIn(("oid", "u"), ("groups", GroupA.ToString()), ("_claim_names", "{\"groups\":\"src1\"}")));

        Assert.Null(reading.User);
        Assert.Contains("too many groups", reading.Reason);
    }
}
