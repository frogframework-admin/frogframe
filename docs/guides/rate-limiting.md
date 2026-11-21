# Rate Limiting

FrogFrame limits how many requests a client can make per minute. Requests fall into tiers, and each tier has its own budget, so heavy use of one kind of request does not block the others.

| Tier | Requests | Counted per | Setting | Default / minute | `.env` |
|---|---|---|---|---|---|
| Sign-in | `POST /api/auth/login`, `/api/auth/refresh`; browser login and 2FA form posts (`/account/login`, `/account/login-2fa`); SSO start (`/account/external/…`) | client IP | `Api:AuthRateLimitPerMinute` | 1200 | `FROGFRAME_AUTH_RATE_LIMIT` |
| Password reset | `/api/auth/forgot-password`, `/api/auth/reset-password`; the forgot/reset password form posts | client IP | `Api:PasswordResetRateLimitPerMinute` | 20 | `FROGFRAME_PASSWORD_RESET_RATE_LIMIT` |
| Heavy | `/api/reports/…`, `/api/report-templates/…`, `/api/query/…` (saved queries), `/api/print/…` (print and PDF) | user or API key | `Api:HeavyRateLimitPerMinute` | 60 | `FROGFRAME_API_HEAVY_RATE_LIMIT` |
| Write | other `POST`, `PUT`, `PATCH`, `DELETE` under `/api` | user or API key | `Api:WriteRateLimitPerMinute` | 120 | `FROGFRAME_API_WRITE_RATE_LIMIT` |
| Read | other `GET`, `HEAD` under `/api` | user or API key | `Api:RateLimitPerMinute` | 300 | `FROGFRAME_API_RATE_LIMIT` |

Pages, the Blazor connection, static files and health checks are not limited. The ESS mobile API (`/api/ess/v1/…`) uses the same tiers.

A request that comes in with an API key is counted against that key, even when a user is also signed in. Otherwise it is counted against the signed-in user. Anonymous API calls are counted against the client IP.

## Why sign-in is per IP and high

Sign-in requests are anonymous, so the only key is the client IP. At shift start, many employees behind one office router or a mobile carrier's NAT sign in from the same address, so the default is high (1200). Guessing passwords on one account is stopped by the account lockout instead: 5 failed attempts (passwords or 2FA codes) lock the account for 15 minutes.

Password reset sends e-mail, so it has a strict limit of its own (20 per IP per minute). This keeps the mail server from being used to flood inboxes.

## When the limit is reached

- **API:** `429 Too Many Requests` with a `Retry-After` header (seconds until the window resets) and a JSON body naming the tier, for example `{"title":"Too many requests","tier":"write",…}`.
- **Browser forms:** `429` with a short plain-text message.
- Every rejection is logged as a warning, with the tier, path and client IP (`Framework.Web.RateLimiting`).

Each window is one minute and fixed. Requests are not queued.

## Behind a reverse proxy

The client IP is taken from `X-Forwarded-For` only when the proxy is trusted (`FROGFRAME_TRUSTED_PROXY_CIDR`). Without it, every request seems to come from the proxy, and all users share one sign-in budget.

## Several nodes

Counters are kept in each node's memory. With N nodes behind a load balancer that spreads requests evenly, set `Cluster:Nodes` to N. Every limit is then divided by N, so the total stays close to the configured value. See `multi-node.md`.

## Load tests

`docker-compose.demo.yml` raises the read, write and heavy limits to 10,000 so k6 can run from one machine. Do not use those values in production.

## Code

- `src/Framework.Web/RateLimitTiers.cs`: how a request is classified, and the limiters.
- `src/Framework.Web/Hosting.cs`: `AddRateLimiter`. The global limiter handles sign-in and password reset; the `frogframe-api` policy handles read, write and heavy.
- `tests/Framework.Tests/RateLimitTests.cs`.
