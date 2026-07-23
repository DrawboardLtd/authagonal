---
layout: default
title: Self-Service SSO
---

# Self-Service SSO Onboarding

Once you've [federated a connection](oidc-federation) to a customer's IdP, the next
question is: **what happens when someone who has never logged in before shows up?** Authagonal gives you
three postures for that unknown user, from strictest to most open, plus the controls to keep an *external*
IdP from becoming a foot-gun. This guide is about picking and wiring the posture you want.

All of it is per-connection config (`OidcProviderConfig` / the `OidcProviders` seed, or the equivalent SAML
fields). The relevant knobs:

| Knob | Effect |
|---|---|
| `JitProvisioningEnabled` | May an unknown user be created at all? |
| `ProvisioningAttributeParams` | Require *invite context* on the request before creating one. |
| `AllowUninvitedJit` | Allow self-service creation with **no** invite (tagged with the connection). |
| `IsExternalConnection` | Mark a third-party IdP so the first-party-only flags can't apply. |
| `InteractionPath` | Show a login-app page (name/terms) *before* federating. |

## Posture 1 — Invite-only (reject the uninvited)

The default. With `JitProvisioningEnabled: false`, an unknown SSO user is rejected outright
(`access_denied`, "contact your administrator") — good when every user must be pre-created by an admin or
SCIM.

If you want JIT but *only* when an invite is present, enable JIT **and** declare
`ProvisioningAttributeParams`. Those name the whitelisted `/authorize` query params that carry the invite
context (e.g. `acceptKind`, `acceptToken`). An unknown user is provisioned only when that context actually
arrived on the request; a bare SSO login with no invite is rejected, so a stray login can't silently
self-provision a new account/org.

```json
{
  "OidcProviders": [
    {
      "ConnectionId": "acme-entra",
      "ConnectionName": "Acme (Entra)",
      "MetadataLocation": "https://login.microsoftonline.com/<tenant>/v2.0/.well-known/openid-configuration",
      "ClientId": "…", "ClientSecret": "…",
      "RedirectUrl": "https://auth.example.com/oidc/callback",
      "AllowedDomains": ["acme.com"],
      "JitProvisioningEnabled": true,
      "ProvisioningAttributeParams": ["acceptKind", "acceptToken"]
    }
  ]
}
```

The captured params land on the JIT user's `CustomAttributes` and reach your
[provisioning `Try` handler](provisioning), which is the real gate on the *values* (e.g.
"does this invite token match this email?"). Authagonal captures whitelisted keys; your provisioner decides
if they're valid.

## Posture 2 — Self-service (auto-provision an allowed-domain user)

For "any employee of a customer can just log in and get an account," set `AllowUninvitedJit: true`. Now an
unknown user from an **allowed domain** is provisioned even without invite context, and Authagonal tags them
with the connection they came through so your provisioner can place them in the right tenant instead of
spinning up a new one:

```json
{
  "ConnectionId": "acme-entra",
  "AllowedDomains": ["acme.com"],
  "JitProvisioningEnabled": true,
  "ProvisioningAttributeParams": ["acceptKind", "acceptToken"],
  "AllowUninvitedJit": true
}
```

The tag arrives as a `federated_connection` custom attribute (its value is the connection name). Your `Try`
handler branches on it:

```javascript
app.post('/provisioning/try', async (req, res) => {
  const { userId, email, customAttributes } = req.body;

  if (customAttributes?.acceptToken) {
    // Invited: validate the invite and add them to that org.
    const org = await validateInvite(customAttributes.acceptToken, email);
    if (!org) return res.json({ approved: false, reason: 'Invalid invite' });
    stage(userId, { orgId: org.id, role: customAttributes.acceptKind ?? 'member' });
    return res.json({ approved: true, organizationId: org.id });
  }

  if (customAttributes?.federated_connection) {
    // Self-service: no invite, but they came through a known enterprise connection.
    const org = await orgForConnection(customAttributes.federated_connection);
    stage(userId, { orgId: org.id, role: 'member' });
    return res.json({ approved: true, organizationId: org.id });
  }

  return res.json({ approved: false, reason: 'No invite and no known connection' });
});
```

`AllowUninvitedJit` is opt-in per connection: a connection that declares `ProvisioningAttributeParams` but
does **not** set it stays invite-only.

## Keep external IdPs from becoming foot-guns

A few connection flags are safe on a connection **you** control but dangerous on an arbitrary third-party
IdP:

- **`UseUpstreamSubjectAsUserId`** — the upstream chooses the local user id. On your own share-link
  provider that keeps ids aligned; on a customer's IdP it lets *them* pick your user ids.
- **`AutoLinkExistingByEmail`** — attach a federated login to a pre-existing local account by email,
  skipping the domain-ownership check. Inbox-verified and first-party, fine; on an external IdP it's an
  account-takeover lever.

Mark third-party connections **external** and those flags are neutralised even if set:

```json
{
  "ConnectionId": "acme-entra",
  "IsExternalConnection": true,
  "UseUpstreamSubjectAsUserId": false,
  "AutoLinkExistingByEmail": false
}
```

`IsExternalConnection` defaults to `false` (first-party) so existing connections are unaffected. Set it on
every connection that points at someone else's IdP; a later misconfiguration then can't hand that IdP
control over local identities. (Attaching a federated identity to a pre-existing account still additionally
requires the connection's `AllowedDomains` to vouch for the email's domain — see
[OIDC Federation → Security](oidc-federation).)

## Collect something before federating

Sometimes you need to show the user a page **before** bouncing them to the IdP — a guest's display name, a
terms checkbox, a plan picker. `InteractionPath` names a login-app route to render first:

```json
{ "ConnectionId": "guest-link", "InteractionPath": "/guest" }
```

When an unauthenticated `idp_hint={ConnectionId}` request hits `/connect/authorize`, Authagonal redirects to
`{LoginAppUrl}{InteractionPath}?returnUrl=<authorize url>&connection={id}` instead of straight to the IdP.
Your page collects what it needs, appends the values to the `returnUrl`'s query (where `PassthroughParams` /
`ProvisioningAttributeParams` read them from), and continues to `/oidc/{id}/login` itself. A page that
decides no interaction is needed can continue immediately.

## Related

- [OIDC Federation](oidc-federation) — setting up the connection and the security model.
- [TCC Provisioning](provisioning) — the `Try` handler these flows call.
- [Keeping federated sessions in sync](federated-sessions) — revoking local sessions when the upstream does.
