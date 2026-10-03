using System.Security.Claims;

namespace Janet.Entra;

/// <summary>
/// Who a request is from, as an Entra ID sign-in states it.
/// </summary>
/// <param name="ObjectId">The user's object ID: stable, unique within the tenant, never reassigned.</param>
/// <param name="TenantId">The tenant that issued the token, when the token says.</param>
/// <param name="Groups">The object IDs of the security groups the user is in.</param>
/// <param name="AppRoles">The app roles assigned to the user on this app's registration.</param>
public sealed record EntraUser(string ObjectId, string? TenantId, IReadOnlySet<Guid> Groups, IReadOnlySet<string> AppRoles)
{
    /// <summary>Whether the user is in the security group with this object ID.</summary>
    /// <param name="groupId">The group's object ID.</param>
    public bool IsInGroup(Guid groupId) => Groups.Contains(groupId);

    /// <summary>Whether the user holds this app role. Role values are compared exactly.</summary>
    /// <param name="role">The role's value, as defined on the app registration.</param>
    public bool HasAppRole(string role) => AppRoles.Contains(role);
}

/// <summary>
/// The result of reading a principal: a user, or the reason there is none.
/// </summary>
/// <param name="User">The user, or null when the principal does not say enough.</param>
/// <param name="Reason">Why there is no user, or how the user was read.</param>
public sealed record EntraReading(EntraUser? User, string Reason)
{
    /// <summary>Gets whether the principal identified a user.</summary>
    public bool IsSignedIn => User is not null;
}

/// <summary>
/// Reads an <see cref="EntraUser"/> out of a <see cref="ClaimsPrincipal"/>.
/// </summary>
/// <remarks>
/// Microsoft.Identity.Web, App Service authentication and raw JWT handling name the same claims
/// differently (<c>oid</c> or the long objectidentifier URI, <c>roles</c> or
/// <see cref="ClaimTypes.Role"/>), so both spellings are read. Everything here fails closed: an
/// access decision made from a partial reading is a decision nobody can trust, so a principal that
/// cannot be read completely gives no user at all.
/// </remarks>
public static class EntraClaims
{
    /// <summary>The claim types that carry the object ID.</summary>
    public static readonly IReadOnlyList<string> ObjectIdTypes =
        ["oid", "http://schemas.microsoft.com/identity/claims/objectidentifier"];

    /// <summary>The claim types that carry the tenant ID.</summary>
    public static readonly IReadOnlyList<string> TenantIdTypes =
        ["tid", "http://schemas.microsoft.com/identity/claims/tenantid"];

    /// <summary>The claim types that carry app roles.</summary>
    public static readonly IReadOnlyList<string> RoleTypes = ["roles", ClaimTypes.Role];

    /// <summary>The claim type that carries security group IDs.</summary>
    public const string GroupsType = "groups";

    /// <summary>Reads the user a principal identifies, or says why it identifies none.</summary>
    /// <param name="principal">The request's principal.</param>
    public static EntraReading Read(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        if (principal.Identity?.IsAuthenticated != true)
            return new(null, "not signed in");

        var objectId = First(principal, ObjectIdTypes);
        if (string.IsNullOrWhiteSpace(objectId))
            return new(null, "the sign-in has no object ID");

        // A user in more groups than a token can hold (200 in a JWT) gets a pointer to Microsoft
        // Graph in _claim_names instead of the list. Reading what is present would hide a
        // membership, so that is refused rather than worked round.
        if (principal.HasClaim(c => c.Type == "_claim_names" && c.Value.Contains(GroupsType, StringComparison.Ordinal)))
            return new(null, "the sign-in lists too many groups to read membership from");

        var groups = principal.FindAll(GroupsType)
            .Select(c => Guid.TryParse(c.Value, out var id) ? id : (Guid?)null)
            .OfType<Guid>()
            .ToHashSet();
        var roles = principal.Claims
            .Where(c => RoleTypes.Contains(c.Type))
            .Select(c => c.Value)
            .ToHashSet(StringComparer.Ordinal);

        return new(new EntraUser(objectId, First(principal, TenantIdTypes), groups, roles), "from the Entra sign-in");
    }

    private static string? First(ClaimsPrincipal principal, IReadOnlyList<string> types) =>
        types.Select(principal.FindFirst).FirstOrDefault(c => c is not null)?.Value;
}
