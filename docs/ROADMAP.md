# OrderFlow Delivery Roadmap

`MASTER_PLAN.md` is the detailed execution plan. This roadmap is its concise phase index: phase numbers, names, scope, and gates must stay aligned. Complete phases in order; split each phase into small tasks tied to requirement IDs and acceptance criteria. Current repository status: Phase 1–4 implementations are present. Phase 4 catalog acceptance tests and the representative local query measurement in `CATALOG_PERFORMANCE.md` pass. Later phase gates are not implied complete by scaffold code.

## Phase 0 — Design baseline

- Review requirements, business rules, and state machines; classify core and extended scope.
- Maintain SDS, architecture diagrams, ADRs, and an explicit list of unresolved implementation decisions.
- Resolve contract/scope conflicts before any dependent implementation.

Exit: checkout event order and core/extended boundary are clear; no open decision blocks bootstrap.

## Phase 1 — Repository and solution bootstrap

- Create solution/projects, dependency direction, architecture tests, build settings, README, CI, Docker Compose, and secret-free configuration templates.

Exit: clean clone restores/builds/tests and local dependency configuration validates.

## Phase 2 — Domain foundation and persistence

- Establish Identity-aware EF Core context strategy and migration ownership.
- Implement Category, Product, Inventory, and InventoryAdjustment mappings, constraints, concurrency, seed data, and SQL integration coverage.

Exit: empty SQL Server can be migrated; database constraints protect catalog and inventory invariants.

## Phase 3 — Identity and access

- Implement Identity, registration/login, JWT, hashed refresh-token rotation/reuse detection, lockout, policies, ownership, ProblemDetails, correlation/exception middleware, and auth rate limits.

Exit: auth flows are reproducible and protected by integration tests; secrets and raw refresh tokens are not stored or logged.

## Phase 4 — Catalog

- Implement category/product administration and public search/detail APIs, validation, paging/filtering/sorting, availability projection, and optimistic concurrency.

Exit: documented catalog contracts and catalog acceptance tests pass.

## Phase 5 — Inventory and concurrency

- Implement audited stock adjustment, atomic reservation, commit/release/expiry transitions, low-stock event, inspection, and reservation-expiry job interface.

Exit: `AC-01` passes repeatedly; correctness does not depend on Redis or Kafka.

## Phase 6 — Cart and core ordering

- Implement persistent cart and recalculated display; order snapshots/totals/transitions; SQL-backed idempotency and replay; atomic Order + history + outbox checkout.
- Include core checkout dependencies required by the contract: validated shipping-method/fee reference data and optional coupon validation/usage in the checkout transaction.
- Include customer early-cancellation behavior and idempotent release for eligible pre-fulfillment states. Defer staff post-payment compensation/refunds to Phase 10.

Exit: `AC-02`, `AC-07`, and `AC-09` pass; one stable `PendingInventory` response and its outbox record commit atomically.

## Phase 7 — Redis caching

- Cache product detail/category reads with invalidation, TTL jitter, safe fallback, metrics, and measured stampede mitigation.

Exit: `AC-05` and `AC-06` pass; Redis remains non-authoritative.

## Phase 8 — Kafka and transactional outbox publisher

- Implement versioned event envelope/serialization, safe batched multi-worker outbox publishing, retry metadata, correlation/causation propagation, and backlog metrics.

Exit: `AC-03` passes; committed events recover after Kafka outage and publisher crash windows are understood.

## Phase 9 — Kafka consumers and asynchronous checkout

- Implement per-consumer Inbox deduplication, Inventory/Ordering/Payment/Notification consumers, payment and idempotent refund simulations, retry and DLQ/failure storage, safe replay, and reservation expiry worker.

Exit: `AC-04`, `AC-08`, and `AC-10` pass; full checkout is demonstrated end to end.

## Phase 10 — Extended backend features

- Implement extended promotion administration/rules beyond core checkout, shipping/shipment tracking, staff cancellation compensation using the Phase 9 refund capability, returns, wishlist, and verified reviews/moderation as separate vertical slices.

Exit: each slice has its API, persistence, authorization, events where needed, and tests.

## Phase 11 — Cross-cutting operational behavior

- Finalize logging, correlation, health/readiness, rate limits, security headers/CORS, audit views, metrics, startup configuration validation, and safe production errors.

Exit: one order is traceable across HTTP and events; operational behavior is documented without sensitive data leakage.

## Phase 12 — Frontend foundation

- Create React/TypeScript/Vite client, API/auth plumbing, agreed secure refresh-token flow, route protection, and common page states.

Exit: login/refresh, protected routing, correlation-aware errors, and production build work.

## Phase 13 — Frontend features

- Build customer core journey, then staff tools; add extended feature screens after their backend slices are stable.

Exit: core customer and staff journeys are demonstrable without Swagger.

## Phase 14 — Quality, security, and performance

- Automate `AC-01`–`AC-10`, inject dependency failures, benchmark documented workloads, review query plans and security risks, and scan dependencies/secrets.

Exit: clean-clone tests pass and performance claims have repeatable evidence.

## Phase 15 — Packaging and deployment

- Add production images, graceful shutdown, managed migration strategy, deployment configuration, persistent storage/backups as appropriate, and deployment/rollback rehearsal.

Exit: a documented pipeline or command deploys the system without source-controlled secrets.

## Phase 16 — Portfolio and interview release

- Prepare reviewer-facing README, diagrams, demo setup/script, trade-offs, sample requests, and stable release/demo branch.

Exit: a fresh clone can repeat the demo and the engineering decisions are explainable with evidence.

## First task when starting from an empty repository

Read `AGENTS.md`, `docs/SRS.md`, `docs/ARCHITECTURE.md`, `docs/ROADMAP.md`, and `docs/MASTER_PLAN.md`. Implement Phase 1 only: solution/projects, dependency graph, repository configuration, local dependency skeleton, architecture test, CI, and README. Do not implement business entities, persistence, authentication, Redis behavior, or Kafka handlers in this phase.
