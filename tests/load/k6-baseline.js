import http from 'k6/http';
import { check, sleep, group } from 'k6';

// ============================================================================
// DocFrame Baseline Load Testing Script (k6)
// Simulates concurrent user workload: health checks, authentication,
// metadata-driven document reads, and transactional writes.
// ============================================================================

export const options = {
  stages: [
    { duration: '15s', target: 20 },  // Ramp-up
    { duration: '30s', target: 50 },  // Steady load
    { duration: '15s', target: 100 }, // Peak stress spike
    { duration: '15s', target: 0 },   // Ramp-down
  ],
  thresholds: {
    // Overall error rate must stay below 1%
    http_req_failed: ['rate<0.01'],
    // 95% of all requests must complete within 350ms
    http_req_duration: ['p(95)<350'],
    // Endpoint-specific performance budgets
    'http_req_duration{scenario:health}': ['p(95)<50'],
    'http_req_duration{scenario:read}': ['p(95)<200'],
    'http_req_duration{scenario:write}': ['p(95)<400'],
  },
};

const BASE_URL = __ENV.BASE_URL || 'http://localhost:5080';

export default function () {
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

  // 2. Authentication & Token Acquisition
  let authToken = '';
  group('Authentication', () => {
    const loginPayload = JSON.stringify({
      userName: 'admin',
      password: 'password123',
    });

    const loginRes = http.post(`${BASE_URL}/api/auth/login`, loginPayload, {
      headers: { 'Content-Type': 'application/json' },
      tags: { scenario: 'auth' },
    });

    // In ASP.NET Core Bearer Auth, token is written in JSON response or cookies
    if (loginRes.status === 200) {
      try {
        const body = JSON.parse(loginRes.body);
        authToken = body.accessToken || body.token || '';
      } catch (e) {
        // Fallback or cookie session
      }
    }
  });

  // Headers for authenticated requests
  const authHeaders = {
    'Content-Type': 'application/json',
    ...(authToken ? { Authorization: `Bearer ${authToken}` } : {}),
  };

  // 3. Document List & Search Queries (Read throughput)
  group('Document Queries', () => {
    const listRes = http.get(
      `${BASE_URL}/api/data/PurchaseRequest?pageSize=10`,
      {
        headers: authHeaders,
        tags: { scenario: 'read' },
      }
    );

    check(listRes, {
      'document query returns valid response': (r) => r.status === 200 || r.status === 401,
    });
  });

  sleep(0.5);

  // 4. Document Creation (Write throughput)
  group('Document Creation', () => {
    const requestDate = new Date().toISOString().split('T')[0];
    const reqBody = JSON.stringify({
      department: 'HR',
      required_date: requestDate,
      reason: `Automated Load Test Item - VU ${__VU} Iteration ${__ITER}`,
      items: [
        {
          item_code: 'LAPTOP-01',
          item_name: 'Standard Developer Laptop',
          qty: 1,
          estimated_cost: 15000000,
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
      'document create returns 201 or 401/403': (r) =>
        r.status === 201 || r.status === 401 || r.status === 403,
    });
  });

  sleep(1);
}

