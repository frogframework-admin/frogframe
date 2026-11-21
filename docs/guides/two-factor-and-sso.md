# Two-Factor Authentication and Single Sign-On

FrogFrame supports two sign-in additions on top of username and password:

- **Two-factor authentication (2FA):** a 6-digit code from an authenticator app (Google Authenticator, Microsoft Authenticator, 1Password, Authy and others), with one-time recovery codes. Optional per user; can be required for roles.
- **Single sign-on (SSO):** "Continue with Google" and "Continue with Microsoft" (Entra ID) on the login page. SSO signs in **existing** FrogFrame accounts only.

## Two-factor authentication

### For users

1. Open the profile menu and choose **Two-factor**, or go to `/account/two-factor`.
2. Scan the QR code with the authenticator app, or type the key shown under it.
3. Enter the 6-digit code the app shows. Two-factor authentication turns on, and sessions on other devices are signed out.
4. Save the ten recovery codes that appear. They are shown once. Each code signs you in once if you lose your phone.

After that, signing in asks for the password and then for a code (`/account/login-2fa`). The same box accepts a recovery code. *Don't ask again on this device* skips the code on that browser for 14 days.

On the same page you can generate new recovery codes (the old ones stop working) or turn two-factor authentication off. Both need your current password.

If codes are always refused, the phone's clock is usually wrong: set the time to automatic.

### Lost phone

- With a recovery code: sign in with it, then turn two-factor off and on again to enrol the new phone.
- Without recovery codes: an administrator opens the user in **Users**, and chooses **Reset two-factor**. This switches 2FA off for that account and ends its sessions; the user then signs in with the password and enrols again. Confirm the person's identity before doing this.

### API clients

`POST /api/auth/login` takes the second factor in the same request:

```json
{ "userName": "rina", "password": "…", "twoFactorCode": "123456" }
```

or `"twoFactorRecoveryCode": "XXXXX-XXXXX"`. Without one, an account with 2FA gets `401` with `"requiresTwoFactor": true`. Refresh tokens keep working without a code. API keys (service accounts) are not affected by 2FA.

### Requiring 2FA for roles

```json
"Security": {
  "TwoFactor": {
    "Issuer": "FrogFrame",
    "RequiredForRoles": [ "System Manager", "Accounts Manager" ],
    "TrustSingleSignOn": true,
    "RecoveryCodeCount": 10
  }
}
```

With Docker Compose: `FROGFRAME_2FA_REQUIRED_ROLES=System Manager,Accounts Manager` and `FROGFRAME_2FA_ISSUER=…` in `.env`.

A signed-in user with one of these roles who has not enrolled is sent to `/account/two-factor` for every page, and API calls get `403`. Account pages, static files and health checks stay reachable. Users who signed in with SSO count as satisfying the rule while `TrustSingleSignOn` is `true`, because multi-factor authentication is then enforced by Google or Entra (for example with conditional access). Set it to `false` to require the FrogFrame code as well; note that SSO sign-in does not ask for the FrogFrame code, so such users must then sign in with their password.

### How it is stored

| Item | Storage |
|---|---|
| On/off | `frogframe_users.two_factor_enabled` |
| Authenticator secret | `frogframe_users.authenticator_key`, encrypted with ASP.NET Core Data Protection (purpose `FrogFrame.Identity.AuthenticatorKey.v1`) |
| Recovery codes | `frogframe_users.recovery_codes`, SHA-256 hashes; a code is removed when used |

Migration `042_two_factor.sql` adds the columns. Data Protection keys are already persisted in PostgreSQL. If they are lost, enrolled users cannot use app codes (sign-in still asks for a second factor; recovery codes still work) until an administrator resets their two-factor authentication.

Only the authenticator app counts as a second factor. The default e-mail and SMS code providers of ASP.NET Core Identity are switched off. Wrong codes count towards the same lockout as wrong passwords (5 attempts, 15 minutes).

The QR code is drawn on the server as SVG by `Framework.Web.Identity.QrCode`, so the secret is never sent to an outside QR service.

## Single sign-on

### What it does

1. The user chooses **Continue with Google** or **Continue with Microsoft** on the login page.
2. The provider signs the user in (OAuth 2.0 authorization code with PKCE; FrogFrame then reads the OpenID Connect userinfo endpoint over TLS).
3. FrogFrame looks for an active account whose e-mail address equals the provider's address.
4. If one is found, that account is signed in for the browser session. Roles, tenant and permissions come from the FrogFrame account, as with a password sign-in.

SSO never creates accounts and never changes roles. Add the user in **Users** first, with the same e-mail address they use at Google or Microsoft. The account still has a password; the user can keep using it.

Sign-in is refused when:

| Message code (`/account/login?sso=…`) | Reason |
|---|---|
| `noaccount` | No account has this e-mail address. |
| `unverified` | Google did not confirm the address (`email_verified` is not true). |
| `noemail` | The provider did not share an e-mail address. |
| `domain` | The address is outside `Authentication:AllowedEmailDomains`. |
| `inactive` | The account is inactive or locked. |
| `failed` | The user cancelled, or the provider returned an error. |

Each refusal is written to the log with its reason.

E-mail addresses do not have to be unique between accounts in FrogFrame. For SSO they should be: if two accounts share an address, SSO signs in one of them. Give each person their own address.

### Google

1. In Google Cloud Console, create an **OAuth client ID** of type *Web application*.
2. Authorized redirect URI: `https://<your FrogFrame address>/signin-google`.
3. Set:

```json
"Authentication": {
  "Google": { "ClientId": "…apps.googleusercontent.com", "ClientSecret": "…" },
  "AllowedEmailDomains": [ "acme.co.id" ]
}
```

or `FROGFRAME_SSO_GOOGLE_CLIENT_ID`, `FROGFRAME_SSO_GOOGLE_CLIENT_SECRET` and `FROGFRAME_SSO_ALLOWED_DOMAIN` in `.env`.

Use `AllowedEmailDomains` with Google Workspace so that only your company's addresses can sign in.

### Microsoft Entra ID

1. In the Entra admin center, **App registrations → New registration**, *Accounts in this organizational directory only* (single tenant).
2. Redirect URI (Web): `https://<your FrogFrame address>/signin-microsoft`.
3. **Certificates & secrets → New client secret**.
4. Set:

```json
"Authentication": {
  "Microsoft": { "ClientId": "<application (client) ID>", "ClientSecret": "…", "TenantId": "<directory (tenant) ID>" }
}
```

or `FROGFRAME_SSO_MICROSOFT_CLIENT_ID`, `…_CLIENT_SECRET` and `…_TENANT_ID` in `.env`.

`TenantId` must be your own directory. `common`, `organizations` and `consumers` are refused at startup: with them, any Microsoft account could present an e-mail address that matches a FrogFrame account. The address used is the `email` claim of the user; make sure each user's mail attribute is set in Entra.

### Notes

- Behind a reverse proxy, the redirect URI is built from the forwarded scheme and host, so set `FROGFRAME_TRUSTED_PROXY_CIDR` (`ForwardedHeaders:KnownNetworks`) or the provider will reject the redirect as `http://…`.
- `CallbackPath` can be changed per provider (`Authentication:Google:CallbackPath`), for example when `/signin-google` is already in use.
- Keep client secrets out of `appsettings.json`; use `.env`, Docker secrets or a key vault (see `docs/guides/secrets.md`).
- The mobile ESS app and other API clients keep using `/api/auth/login`; SSO is for the browser.

## Code map

| Part | File |
|---|---|
| Account fields | `src/Framework.Core/Security/Accounts.cs` |
| Identity store (2FA stores) | `src/Framework.Web/Identity/FrogFrameIdentity.cs` |
| Options, role rule middleware, helpers | `src/Framework.Web/Identity/TwoFactor.cs` |
| QR code | `src/Framework.Web/Identity/QrCode.cs` |
| SSO providers and endpoints | `src/Framework.Web/Identity/SingleSignOn.cs` |
| Pages | `Pages/Account/Login.razor`, `LoginTwoFactor.razor`, `AccountTwoFactor.razor` |
| Admin reset | `Pages/UserAdmin.razor` |
| Tests | `tests/Framework.Tests/TwoFactorTests.cs` |
