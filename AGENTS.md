# AGENTS.md — SwiftRide

SwiftRide is a **Software Architecture course project**: a ride-hailing backend built as a small
microservices system in .NET. Requirements come from the assignment brief (`docs/requirements.md`).
Optimize for **clarity, correct service boundaries, testable business rules and controlled complexity**.
A working basic flow is worth more than an incomplete, highly complex distributed system.

## 1. Language and communication

- Code, identifiers, code comments, commit messages: **English**.
- Replies to the user and documents under `docs/`: **Vietnamese**, unless asked otherwise.
- Never claim a build or test passed unless you actually ran it. Report what was and was not run.

## 2. Priority of instructions

1. The user's request in the current task.
2. This `AGENTS.md`.
3. `docs/requirements.md` and other docs under `docs/`.
4. Existing code and tests (the source of truth for _current behavior_).

If docs and code disagree: state the mismatch, keep working behavior, and propose the smallest safe change.
Do not silently replace the architecture with a different one.

## 3. Hard rules (MUST / NEVER)

- MUST keep **at most 3 services**: Trip, Matching & Pricing, Payment. NEVER add a 4th service
  (Identity, Notification, Outbox worker…). Background work runs as `BackgroundService` inside the owning service.
  The API Gateway is an edge component, not a business service.
- MUST give each service its **own database/schema**. NEVER query another service's database or join across databases.
- MUST route all client traffic through the **YARP API Gateway**. Clients NEVER call services directly.
- MUST keep business rules out of controllers and the Gateway.
- MUST make payment operations and message consumers **idempotent**.
- MUST put a **timeout** on every network call. Retry only transient failures, with exponential backoff + jitter and a hard limit.
- NEVER store card numbers/CVV; use a (mock) payment token. NEVER log tokens, card data or unnecessary personal data.
- NEVER hard-code or commit secrets; use environment variables or user-secrets.
- NEVER expose persistence entities as API or event contracts.
- NEVER add Kafka, Kubernetes, Redis, event sourcing, CQRS or other heavy infrastructure unless the task asks for it.

## 4. Scope: MVP first, extensions later

**MVP (do first):** 3 services + Gateway + 3 databases in `docker-compose.yml`; synchronous REST calls;
trip state machine; pricing strategies; payment with idempotency and ledger; unit tests for domain rules.

**Extensions (only after the MVP works):** RabbitMQ/MassTransit events, DLQ, Transactional Outbox,
Saga/compensation, OpenTelemetry.

**MVP acceptance scenario:** `docker compose up` → rider calls `POST /trips` through the Gateway →
the trip passes quote, driver acceptance, pickup, dropoff and reaches `Paid`, with one ledger entry set and no double charge on retry.

Development order: contracts → Trip lifecycle → Matching & Pricing → Payment → sync end-to-end flow →
Gateway → RabbitMQ events → idempotency/failure handling → Outbox/Saga → observability → tests/docs hardening.
In the MVP, Trip calls Payment through an abstraction (`IPaymentClient`) so it can later be swapped for events without touching domain code.

## 5. Services

### 5.1 Trip Service — SQL Server

Owns the trip lifecycle (State pattern), stores the agreed fare, publishes domain/integration events.
Transitions are validated **in the Domain layer**.

States: `Requested`, `Quoted`, `DriverOffered`, `DriverAccepted`, `EnRoute`, `PickedUp`, `DroppedOff`,
`PaymentPending`, `Paid`, `Cancelled`, `Failed`.
Terminal states: **`Paid`, `Cancelled`, `Failed`** only. `PaymentPending` is NOT terminal.

| From             | Trigger                                   | To               | Notes                                      |
| ---------------- | ----------------------------------------- | ---------------- | ------------------------------------------ |
| `Requested`      | Matching returns quote + candidate driver | `Quoted`         | No driver available → `Failed`             |
| `Quoted`         | Rider confirms price                      | `DriverOffered`  | Fare is locked from here on                |
| `Quoted`         | Rider rejects price                       | `Cancelled`      |                                            |
| `DriverOffered`  | Driver accepts                            | `DriverAccepted` |                                            |
| `DriverOffered`  | Driver rejects, rejections ≤ k            | `DriverOffered`  | Re-match another driver, increment counter |
| `DriverOffered`  | Driver rejects, rejections > k            | `Failed`         | k is configurable (default 3)              |
| `DriverAccepted` | Driver starts heading to rider            | `EnRoute`        |                                            |
| `EnRoute`        | Driver picks up rider                     | `PickedUp`       |                                            |
| `PickedUp`       | Driver drops off rider                    | `DroppedOff`     | Emit `RideCompleted`                       |
| `DroppedOff`     | Payment requested                         | `PaymentPending` |                                            |
| `PaymentPending` | `PaymentSettled`                          | `Paid`           |                                            |
| `PaymentPending` | Transient failure                         | `PaymentPending` | Bounded retry, same idempotency key        |

Rules:

- Cancellation is allowed only before `DriverAccepted` unless the task says otherwise.
- If payment keeps failing after the retry limit, the trip stays `PaymentPending`. NEVER mark it `Paid` falsely or charge twice.
- Any transition not in the table throws a domain exception. Every row needs a unit test, and so does at least one invalid transition per state.
- The Trip stores `QuoteId`, `FareAmount` and `Currency` as a snapshot of what the rider agreed to.

### 5.2 Matching & Pricing Service — MongoDB

Owns driver locations (GeoJSON + `2dsphere` index), nearest-driver search, and quotes.
It needs a way to receive data: expose `PUT /drivers/{id}/location` and provide **seed data** for demos and tests.

Pricing formula (all values configurable, none hard-coded in controllers):

```text
price = ((costPerKm * distance + costPerMinute * estimatedTime) * surge + toll)
        * promotionFactor * (1 + tax)
```

- Use `decimal`. Currency is VND (whole units). Round once, at the end; keep the rounding policy in configuration.
- Pricing input is a service-owned `PricingContext`/`QuoteRequest` (distance, time, surge, toll, promo, tax).
  NEVER depend on Trip Service's `Trip` entity.
- Strategy pattern: a base strategy computes `costPerKm * d + costPerMinute * t`; surge, toll, promotion and tax are
  composable steps/decorators with a documented order. New modes are added without editing existing ones.
- Invariants: `price >= 0`, `surge >= 1`, `0 < promotionFactor <= 1`, `tax >= 0`. Cover them with tests.

### 5.3 Payment Service — PostgreSQL

Owns payments and the **append-only, double-entry ledger** (each settled payment writes balanced debit/credit
entries in one local transaction). Handles refunds and compensation.

- Charge the **fare sent by Trip Service**. NEVER recompute the price.
- Idempotency key is deterministic, e.g. `trip-{tripId}-payment`, with a **unique index** in PostgreSQL.
  Same key + same payload returns the original result; same key + different payload returns a conflict.
- Payment methods are created via a Factory (Card/Wallet) behind `IPaymentGateway`; the course project uses a mock gateway and tokens.

### 5.4 Routes: public vs internal

Public routes (through the Gateway, JWT required):

| Route                                                                                         | Caller |
| --------------------------------------------------------------------------------------------- | ------ |
| `POST /trips`, `GET /trips/{id}`, `POST /trips/{id}/confirm-quote`, `POST /trips/{id}/cancel` | Rider  |
| `POST /trips/{id}/driver-accept`, `/driver-reject`, `/en-route`, `/pickup`, `/dropoff`        | Driver |
| `PUT /drivers/{id}/location`                                                                  | Driver |

Internal routes (NOT exposed by the Gateway): `POST /match`, `POST /quote` (called by Trip Service),
`POST /payments`, `POST /payments/{id}/refund` (called by Trip Service or via events).

## 6. Communication and reliability

- Synchronous (REST by default; gRPC only if the task asks): when an immediate answer is needed (Trip → Matching).
- Asynchronous (RabbitMQ via MassTransit): business events such as `TripAccepted`, `RideCompleted`,
  `PaymentSettled`, `PaymentFailed`. Pin the MassTransit version and check its license terms before major upgrades.
- Shared integration-event contracts live in `src/SwiftRide.Contracts/`: plain records only, versioned
  (e.g. `V1` suffix/namespace), no business logic, no domain or persistence types.
- Payment flow (choreography, no central orchestrator): Trip emits `RideCompleted` → Payment charges with the idempotency key →
  Payment emits `PaymentSettled` or `PaymentFailed` → Trip updates state. Refund is the compensation if a charged trip is invalidated.
- HTTP resilience: use timeouts + bounded retry for transient errors only (e.g. `Microsoft.Extensions.Http.Resilience`).
- Messaging: bounded retry with exponential backoff + jitter; messages that keep failing go to the dead-letter/error queue. NEVER drop them silently.
- Consumers assume **at-least-once** delivery and must tolerate duplicates.
- Transactional Outbox: write the state change and its outgoing event in the same local transaction; a background publisher sends it.
  Outbox does not replace Saga, and Saga does not replace local transactions or idempotency.
- Consistency: strong inside a service, eventual between services.

## 7. Architecture and layering

Preserve the existing layout if one exists. Otherwise use:

```text
SwiftRide/
├── src/
│   ├── ApiGateway/                 # YARP, JWT, rate limiting
│   ├── SwiftRide.Contracts/        # integration events only
│   ├── TripService/        {Domain, Application, Infrastructure, Api}
│   ├── MatchingService/    {Domain, Application, Infrastructure, Api}
│   └── PaymentService/     {Domain, Application, Infrastructure, Api}
├── tests/                          # one test project per service
├── docs/                           # requirements.md and design notes
├── docker-compose.yml
├── SwiftRide.sln
└── AGENTS.md
```

- **Domain**: entities, value objects, state rules, domain events. No dependency on DB, HTTP, RabbitMQ or frameworks.
- **Application**: use cases, orchestration, DTO mapping, interfaces. Depends on Domain.
- **Infrastructure**: EF Core, MongoDB, repositories, MassTransit, payment adapters, migrations. Implements interfaces from Domain/Application.
- **Api**: thin controllers/Minimal APIs, request/response contracts, auth configuration.
- NEVER create a generic `Common` project containing business logic.

SOLID, applied concretely (keep it visible in the code):

- **SRP**: pricing, persistence, transport and messaging live in separate classes (`FareCalculator` ≠ `TripRepository`).
- **OCP**: new pricing modes are new `IPricingStrategy` implementations; existing ones are not edited.
- **LSP**: every strategy honors the contract (`price >= 0`); enforce it with tests, not only runtime checks.
- **ISP**: small interfaces (`ITripReader`, `ITripWriter`), not one fat service interface.
- **DIP**: Application/Domain depend on abstractions (`ITripWriter`, `IEventBus`, `IPaymentGateway`, `IPaymentClient`);
  Infrastructure implements them via ASP.NET Core DI.

Design patterns (use only where they solve a real problem, never just to show off): State (trip lifecycle),
Strategy (pricing), Repository (persistence), Factory (payment methods), Pub/Sub (events), API Gateway, Saga, Outbox.

## 8. Security

- Gateway validates JWT Bearer tokens (OAuth 2.0 / OIDC concepts), applies rate limiting and routing.
- Services still authorize resources: a rider sees only their own trips; only the assigned driver may move a trip forward.
- Roles: `rider`, `driver`. No Identity Service is created. In **Development only**, tokens come from a dev-token endpoint
  (or a script) using a signing key from configuration/environment.

## 9. Observability (extension)

Use OpenTelemetry. Every HTTP request and message carries `CorrelationId`/trace context. Use structured logging.
Useful metrics: successful trip rate, matching duration, API latency, payment failure rate, queue depth.

## 10. Tech stack

.NET 10 (LTS) · ASP.NET Core · EF Core 10 (SQL Server, PostgreSQL) · MongoDB.Driver · YARP · RabbitMQ + MassTransit ·
Docker / docker-compose · xUnit + Moq · OpenTelemetry. If the repo already uses a nearby supported .NET version, do not upgrade it unprompted.
Using one DB technology with isolated schemas/databases per service is acceptable for this project.

## 11. Testing

Use xUnit + Moq. Test business behavior, not implementation details. Priorities:
valid/invalid trip transitions (see the table), driver-rejection limit k, pricing strategies and invariants,
payment idempotency (same key twice → one charge, balanced ledger), duplicate event handling,
retryable-failure behavior, and Saga compensation where implemented.
Add infrastructure/integration tests only when they give clear value.

## 12. Commands

Run the smallest relevant check first, and only commands that exist in the repo:

```bash
dotnet build SwiftRide.sln
dotnet test SwiftRide.sln
docker compose config
docker compose up --build
```

## 13. Workflow for every non-trivial task

1. **Inspect** relevant code, tests, config and docs before editing.
2. **Scope**: affected service and layers, contracts, DB impact, events, tests needed.
3. **Plan** briefly for medium/large changes; prefer incremental edits and avoid unrelated refactors.
4. **Implement** following existing conventions.
5. **Validate** with the commands above.
6. **Report**: what changed, why, files touched, tests run, open issues.

A change is done only when it respects service ownership, keeps rules out of controllers, handles important failure modes,
keeps side effects idempotent, builds, passes relevant tests, and updates docs when the architecture changes.

## 14. Skills (optional)

Do not generate skills up front. Add one under `.agents/skills/<name>/SKILL.md` only after the code exists and a
workflow keeps repeating (likely candidates: `trip-domain`, `payment-reliability`, `messaging-outbox-saga`, `testing-dotnet`).
Each skill needs YAML frontmatter (`name`, `description`) and must not duplicate this file.
