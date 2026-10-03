# Janet.Entra

Reads who a request is from out of an Entra ID sign-in, and fails closed.

```csharp
EntraReading reading = EntraClaims.Read(httpContext.User);
if (reading.User is not { } user)
    return Forbid(reading.Reason);       // "not signed in", "the sign-in has no object ID", ...

bool isOperator = user.HasAppRole("Operator");
bool inClient   = user.IsInGroup(clientGroupId);
```

- **Object ID, tenant, groups and app roles**, under either claim spelling the Microsoft
  stacks use (`oid` or the long objectidentifier URI, `roles` or `ClaimTypes.Role`).
- **Fails closed.** Not signed in, no object ID, or a group overage (a user in more groups
  than the token holds gets a pointer to Microsoft Graph instead of the list) all give no user
  and a reason, never a partial reading an access check could get wrong.
- It decides nothing. Which groups or roles mean what is the app's business; this only says
  what the sign-in states.
