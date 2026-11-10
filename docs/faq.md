# Frequently Asked Questions (FAQ)

Everything you need to know about **FrogFramework (FrogFrame)** — philosophy, architecture, design decisions, and future roadmap.

---

### 1. What is FrogFramework?

**FrogFramework (FrogFrame)** is a modular, metadata-driven business application and ERP framework built natively on **.NET 10** and **Blazor**.

The core architectural concept is inspired by the metadata-driven philosophy of enterprise frameworks. However, FrogFramework is built completely from the ground up natively on top of the modern .NET ecosystem, with a relentless focus on:
- **High throughput & low latency** using .NET 10 and pure ADO.NET.
- **Enterprise-grade security**, strict type-safety, and database-level multi-tenant isolation.
- **Visual low-code extensibility**, enabling rapid schema generation and workflow modeling directly from the browser.

> **Primary Goal**: FrogFramework was originally conceived for personal and client project delivery — enabling a single developer or small engineering team to deliver bespoke, robust enterprise business applications to clients in record time without starting from scratch on every engagement.

---

### 2. What problem does it solve and what is it used for?

When building typical enterprise line-of-business software, engineering teams spend 70–80% of their time writing repetitive boilerplate: database migrations, domain entities, repositories, service layers, DTO mappings, CRUD API controllers, validation rules, and frontend forms.

FrogFramework eliminates this boilerplate:

- **Metadata-Driven DocTypes**: Define tables, fields, validations, and dynamic UI forms either visually via the web interface or programmatically through C# definitions. Schemas adapt immediately without restarting the application.
- **Multi-Stage Workflows & Approvals**: Built-in state machine engine supporting tiered approval chains (`Draft` &rarr; `Submitted` &rarr; `Approved` &rarr; `Rejected`) governed by role-based transition rules.
- **Granular Layered Security**: Enforces field-level access control (`permLevel`), tenant isolation, anti-tamper forms, and encrypted API key authorization.
- **Reporting & Visual Dashboards**: Interactive SQL widget builder, 18+ analytical chart types powered by Apache ECharts, and zero-dependency Word/Excel/HTML print and template engines.

---

### 3. Why choose Blazor?

Building enterprise business software requires rich interactive forms, complex data grids, and fast internal dashboards. Blazor offers compelling advantages:

1. **Unified Full-Stack C# Language**: Write C# everywhere — from backend services and database access down to web components, client validations, and UI events. This eliminates context-switching and eliminates the maintenance overhead of synchronizing separate frontend frameworks (React, Vue, or Angular).
2. **Rock-Solid Type Safety & Tooling**: Take full advantage of the Roslyn compiler, Visual Studio, JetBrains Rider, and VS Code. Refactoring, compile-time contract checking, debugging, and autocomplete work seamlessly across the entire stack.
3. **Enterprise Performance**: Blazor runs on top of the lightning-fast .NET 10 runtime with low memory allocation, rapid server-side rendering, and modern UI styling through [Blazor Blueprint UI](https://blazorblueprintui.com) and Lucide Icons.
4. **Addressing a Market Gap**: The enterprise ERP and low-code framework space is overwhelmingly dominated by Python (e.g., Frappe, Odoo), PHP, or JavaScript frameworks. There is a noticeable void of open, metadata-driven ERP frameworks running on Blazor and .NET. While it may serve a specialized niche today, there is a dedicated and passionate community of .NET engineers who want modern ERP capabilities without leaving the .NET ecosystem.

---

### 4. Why not use Entity Framework Core (EF Core)?

While EF Core is the standard ORM in the .NET ecosystem, FrogFramework purposefully chose pure **Npgsql (ADO.NET)** for three technical reasons:

- **Runtime Dynamic Schemas vs. Compile-Time Models**: EF Core relies on static, compile-time classes registered in a `DbContext` (`DbSet<T>`). In FrogFramework, business entities (DocTypes), custom fields, dynamic relationships, and child tables can be created, altered, and activated at runtime without recompiling code or restarting the host process.
- **Direct, Transparent Query Control**: FrogFramework uses direct ADO.NET execution. This completely eliminates EF Core's change-tracker memory footprint, avoids unexpected N+1 query traps, and provides total control over raw SQL execution, PostgreSQL `JSONB` path operations, expression indexes, and advisory locks.
- **Native SQL Injection Prevention**: All queries utilize native PostgreSQL parameterized commands (`NpgsqlParameter`), ensuring rock-solid security without dynamic string concatenation.

---

### 5. Do I need RabbitMQ, Kafka, or Redis?

**By default, NO.** FrogFramework adheres strictly to a **zero external infrastructure bloat** philosophy.

You do not need to operate or pay for standalone message brokers or distributed cache clusters for standard deployments:

- **Asynchronous Events via Transactional Outbox**: Asynchronous events and background jobs are queued within PostgreSQL using the **Transactional Outbox pattern**. Because events are written within the same database transaction as business mutations, the notorious *dual-write problem* is eliminated.
- **Distributed Concurrency via PostgreSQL Advisory Locks**: Cross-instance synchronization, scheduler leaders, and document concurrency locks utilize native PostgreSQL advisory locks (`pg_advisory_xact_lock`).
- **Operational Simplicity**: Fewer moving parts mean drastically lower hosting costs, simplified operations, and single-point atomic database backups that capture the complete state of the entire system.

> **Note**: External message brokers (RabbitMQ/Kafka) or distributed caching (Redis) are only necessary if your system scales to tens of thousands of distributed transactions per second across clustered microservices.

---

### 6. Is FrogFramework Production-Ready?

**Honest Answer**: The framework is currently in its active, early development phase and continues to evolve rapidly.

However, the **foundational architecture and core subsystems have been engineered to production-grade standards from day one**:

- **Hardened Security Baseline**: Evaluated against OWASP Top 10 standards with automated tests, security headers (CSP, HSTS, X-Frame-Options), IDOR mitigation, encrypted API keys, and audit logs.
- **Strict Multi-Tenancy**: Tenant data isolation is enforced directly at the query execution boundary.
- **Financial Audit Trail**: General ledger transactions and accounting records feature tamper-evident cryptographic hash chaining.
- **Turnkey Container Deployment**: Dockerized container setup, health check endpoints, and automated redeployment scripts (`./redeploy.ps1`).

---

### 7. Is FrogFramework free and open-source?

**Yes!** FrogFramework is open and free to self-host. You have complete control and ownership over the source code and your business data.

#### Support the Project & Author
If you find FrogFramework valuable and want to help accelerate its development, you are welcome to sponsor development tokens or buy a cup of coffee. There is still a large roadmap ahead!

Beyond being a full-stack .NET engineer, the creator has **over 15 years of domain expertise in Human Resources (HR) and Finance & Accounting**. The long-term vision is to build comprehensive, production-ready DocType modules covering:
- **Human Resource Management (HRMS)**: Payroll, employee lifecycle, attendance, performance.
- **Finance & Accounting**: Chart of accounts, multi-currency general ledger, accounts payable/receivable, financial statements.
- **Customer Relationship Management (CRM) & Operations**: Lead pipelines, quotations, invoicing, inventory.
- **Native AI Integration**: AI-assisted document workflows, automated entry classification, and natural language analytics.

#### Get in Touch
Whether you are interested in commercial collaboration, enterprise consulting, feature sponsorships, or simply saying hello:

- **Email**: [bpristiwanto@gmail.com](mailto:bpristiwanto@gmail.com)
- **Repository**: [FrogFramework on GitHub](https://github.com/frogframework-admin/frogframe)

