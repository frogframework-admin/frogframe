# Welcome to FrogFramework

<p align="center">
  <img src="docs/images/logo.png" alt="FrogFramework Logo" width="140" />
</p>

**FrogFramework** is a metadata-driven .NET Business Framework inspired by modern extensible enterprise architectures and designed around **The "Two Worlds" Architecture** (strict workload segregation between World 1 OLTP operational workflows and World 2 OLAP analytical reporting). Built with **.NET 10**, **ASP.NET Core**, **Blazor Server**, and **Blazor Blueprint UI** (shadcn/ui styling for Blazor).

---

## Key Pillars

1. **The "Two Worlds" Architecture**: Clear separation of operational concerns (**World 1 OLTP**) for transactional agility and concurrency from analytical workloads (**World 2 OLAP**) for high-throughput reporting, projections, and read-replica offloading.
2. **DocType & Metadata Engine**: Dynamic schemas, document lifecycle, validations, and versioned publishing.
3. **Document Service**: Unified pipeline handling validation, workflow transitions, outbox events, and audit logging.
4. **Role-Based Security & Permissions**: Granular permissions per DocType and tenant-isolated operations.
5. **Dual Persistence Support**: Seamlessly switch between **In-Memory** (rapid dev/tests) and **PostgreSQL** (production).
6. **Modern Blazor UI**: Responsive web portal with interactive widgets, generic views, and dynamic document forms.
7. **Extensibility & Integrations**: Hooks, form actions, scheduled jobs, Excel/CSV import/export, and auto-generated query APIs.

---

## Quick Navigation

- [Getting Started & Installation](docs/index.md)
- [Project Structure & Architecture](docs/PROJECT_STRUCTURE.md)
- [Analytics Engine (World 2 OLAP)](docs/guides/analytics-engine.md)
- [Security Baseline](docs/security-baseline.md)
- [Observability](docs/observability.md)
- [Backup & Disaster Recovery](docs/backup-disaster-recovery.md)
- [Hooks, Form Actions & Scheduler](docs/hooks-formactions-scheduler.md)
- [.NET API Reference](api/index.md)

