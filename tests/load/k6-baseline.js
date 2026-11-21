import http from 'k6/http';
import { check, sleep, group } from 'k6';

// ============================================================================
// FrogFrame Baseline Load Testing Script (k6)
// Simulates concurrent user workload: health checks, document reads, and writes.
// Token is acquired in setup() following k6 best practices.
// ============================================================================

export const options = {
  stages: [
    { duration: '15s', target: 20 },  // Ramp-up
    { duration: '30s', target: 50 },  // Steady load
    { duration: '15s', target: 100 }, // Peak stress spike
    { duration: '15s', target: 0 },   // Ramp-down
  ],
  thresholds: {
    // Overall error rate must stay below 2% under peak stress
    http_req_failed: ['rate<0.02'],
    // 95% of all requests must complete within 350ms
    http_req_duration: ['p(95)<350'],
    // Endpoint-specific performance budgets
    'http_req_duration{scenario:health}': ['p(95)<50'],
    'http_req_duration{scenario:read}': ['p(95)<200'],
    'http_req_duration{scenario:write}': ['p(95)<400'],
  },
};

const BASE_URL = __ENV.BASE_URL || 'http://localhost:5080';

export function setup() {
  const loginPayload = JSON.stringify({
    userName: 'admin',
    password: __ENV.DEMO_PASSWORD || 'FrogFrame#2026',
  });

  const res = http.post(`${BASE_URL}/api/auth/login`, loginPayload, {
    headers: { 'Content-Type': 'application/json' },
  });

  if (res.status !== 200) {
    throw new Error(`Setup login failed with status ${res.status}: ${res.body}`);
  }

  const body = JSON.parse(res.body);
  return { token: body.accessToken || body.token || '' };
}

export default function (data) {
  const authHeaders = {
    'Content-Type': 'application/json',
    ...(data.token ? { Authorization: `Bearer ${data.token}` } : {}),
  };

  // 1. Health Checks (Liveness and Readiness probes)
  group('Health Probes', () => {
    const liveRes = http.get(`${BASE_URL}/health/live`, {
      tags: { scenario: 'health' },
    });
    check(liveRes, {
      'liveness probe status 200': (r) => r.status === 200,
    });

    const readyRes = http.get(`${BASE_URL}/health/ready`, {
      tags: { scenario: 'health' },
    });
    check(readyRes, {
      'readiness probe status 200': (r) => r.status === 200,
    });
  });

  sleep(0.5);

  // 2. Document List & Search Queries (Read throughput)
  group('Document Queries', () => {
    const listRes = http.get(
      `${BASE_URL}/api/data/PurchaseRequest?pageSize=10`,
      {
        headers: authHeaders,
        tags: { scenario: 'read' },
      }
    );

    check(listRes, {
      'document query returns status 200': (r) => r.status === 200,
    });
  });

  sleep(0.5);

  // 3. Document Creation (Write throughput)
  group('Document Creation', () => {
    // Matches the current PurchaseRequest metadata (requested_by, company, purpose, items). Needs the demo masters
    // from deploy/sql/seed-phase-1..8.sql (Company ACME, Employee EMP-0001), or set REQUESTER / COMPANY.
    const requiredBy = new Date(Date.now() + 30 * 24 * 3600 * 1000).toISOString().split('T')[0];
    const reqBody = JSON.stringify({
      requested_by: __ENV.REQUESTER || 'EMP-0001',
      company: __ENV.COMPANY || 'ACME',
      required_by: requiredBy,
      purpose: `Automated Load Test Item - VU ${__VU} Iteration ${__ITER}`,
      items: [
        {
          item_name: 'Standard Developer Laptop',
          qty: 1,
          estimated_rate: 15000000,
        },
      ],
    });

    const createRes = http.post(
      `${BASE_URL}/api/data/PurchaseRequest`,
      reqBody,
      {
        headers: authHeaders,
        tags: { scenario: 'write' },
      }
    );

    check(createRes, {
      'document create returns status 201': (r) => r.status === 201,
    });
    if (createRes.status !== 201 && __ITER === 0) {
      console.error(`Create failed (${createRes.status}): ${createRes.body}`);
    }
  });

  sleep(1);
}
