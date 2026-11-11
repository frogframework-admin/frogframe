# Outbound Webhooks & Event Integration

FrogFrame provides a reliable, secure outbound webhook delivery system that dispatches HTTP POST notifications to external services whenever document lifecycle operations or workflow state transitions occur.

*Last updated: November 2026*

---

## 1. Webhook Payload Format

Every webhook payload delivered by FrogFrame is formatted as a standardized JSON object:

```json
{
  "id": "7b049d10-85f2-4919-8664-df81e05d2c4b",
  "event": "document.inserted",
  "occurredAt": "2026-11-11T12:00:00.0000000Z",
  "tenant": "acme",
  "docType": "PurchaseRequest",
  "name": "PR-2026-0001",
  "version": 1,
  "actor": "andi",
  "data": {
    "name": "PR-2026-0001",
    "request_date": "2026-11-11",
    "required_by": "2026-11-15",
    "total_amount": 5400000,
    "vendor": "VND-0001"
  }
}
```

### Top-Level Fields
- `id`: Unique delivery UUID, identical to the `X-FrogFrame-Delivery` header.
- `event`: Event type name from the core catalog (e.g., `document.inserted`, `document.updated`, `document.deleted`, `document.submit`, `workflow.transitioned`).
- `occurredAt`: ISO 8601 UTC timestamp of when the event originally occurred.
- `tenant`: Tenant identifier (`tenant_id`).
- `docType`: Target DocType schema name.
- `name`: Primary key / document number.
- `version`: Document version at the time of the event.
- `actor`: User ID of the user whose action triggered the event.
- `data`: Snapshot of document fields (fields with `PermLevel == 0` by default, or explicitly selected fields).

---

## 2. HTTP Request Headers & Security

Every webhook HTTP POST request dispatched by FrogFrame includes the following standard headers:

| Header | Description |
|---|---|
| `User-Agent` | `FrogFrame-Webhook/1` |
| `Content-Type` | `application/json; charset=utf-8` |
| `X-FrogFrame-Event` | Event type name (e.g. `document.submit`). |
| `X-FrogFrame-Delivery` | Unique delivery UUID used for deduplication on the receiver. |
| `X-FrogFrame-Timestamp` | Unix timestamp in seconds of when the delivery was initiated. |
| `X-FrogFrame-Signature` | HMAC-SHA256 signature in the format `sha256=<hex_digest>`. |

---

## 3. Verifying Webhook Signatures

To ensure that received webhooks originate genuinely from your FrogFrame instance and have not been tampered with or replayed:

1. Extract `X-FrogFrame-Timestamp` and `X-FrogFrame-Signature` from the request headers.
2. **Replay Protection**: Verify that `|current_unix_time - timestamp| <= 300` (5-minute tolerance). Discard older requests to prevent replay attacks.
3. Compute the HMAC-SHA256 hash of the string `"{timestamp}.{raw_request_body}"` using your webhook signing secret.
4. Perform a **constant-time string comparison** against the hex value in `X-FrogFrame-Signature`.

### Verification Examples

#### C# (.NET 8 / 10)
```csharp
using System.Security.Cryptography;
using System.Text;

bool VerifyFrogFrameWebhook(string rawBody, string signatureHeader, string timestampHeader, string secret)
{
    if (!long.TryParse(timestampHeader, out var timestamp)) return false;
    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    if (Math.Abs(now - timestamp) > 300) return false; // Replay window: 5 minutes

    var payload = $"{timestamp}.{rawBody}";
    using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
    var expectedHex = "sha256=" + Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();

    return CryptographicOperations.FixedTimeEquals(
        Encoding.ASCII.GetBytes(expectedHex),
        Encoding.ASCII.GetBytes(signatureHeader)
    );
}
```

#### Node.js (Express / Fastify)
```javascript
const crypto = require('crypto');

function verifyFrogFrameWebhook(rawBody, signatureHeader, timestampHeader, secret) {
    const timestamp = parseInt(timestampHeader, 10);
    const now = Math.floor(Date.now() / 1000);
    if (Math.abs(now - timestamp) > 300) return false; // 5-minute replay window

    const payload = `${timestamp}.${rawBody}`;
    const hmac = crypto.createHmac('sha256', secret);
    const expectedSignature = `sha256=${hmac.update(payload).digest('hex')}`;

    return crypto.timingSafeEqual(
        Buffer.from(expectedSignature),
        Buffer.from(signatureHeader)
    );
}
```

#### Python (FastAPI / Flask)
```python
import hmac
import hashlib
import time

def verify_frogframe_webhook(raw_body: bytes, signature_header: str, timestamp_header: str, secret: str) -> bool:
    try:
        timestamp = int(timestamp_header)
    except ValueError:
        return False

    now = int(time.time())
    if abs(now - timestamp) > 300:
        return False  # 5-minute replay window

    payload = f"{timestamp}.".encode('utf-8') + raw_body
    expected_hex = "sha256=" + hmac.new(secret.encode('utf-8'), payload, hashlib.sha256).hexdigest()
    
    return hmac.compare_digest(expected_hex, signature_header)
```

---

## 4. Idempotency & Deduplication

In distributed networks, transient network timeouts may cause FrogFrame to retry delivery even if your server processed the previous attempt.

To ensure idempotent processing:
- Store the UUID received in `X-FrogFrame-Delivery` (or the top-level `id` in the JSON body) in a cache or database table with a unique constraint.
- If a delivery ID has already been processed, immediately return `HTTP 200 OK` without re-executing side effects.

---

## 5. Retry & Dead-Letter Behavior

- **Success (`2xx`)**: Delivery marked as `Sent`.
- **Transient Failures (`408`, `429`, `5xx`, connection timeout)**: FrogFrame schedules retries with exponential backoff ($2, 4, 8, 16, 32$ seconds). Deliveries that fail after 5 attempts enter the `Dead` state.
- **Client Errors (`4xx`)**: Treated as fatal configuration errors and marked `Dead` immediately without retry.
- **Permanent Removal (`410 Gone`)**: If the receiver returns `HTTP 410`, the delivery is marked `Dead` and the webhook is automatically deactivated to prevent wasted traffic.
- **SSRF Protection**: FrogFrame automatically blocks destinations resolving to internal RFC 1918 private subnets, loopback addresses (`127.0.0.1`), link-local metadata endpoints (`169.254.169.254`), and internal Docker container hostnames.

