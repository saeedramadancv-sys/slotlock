# SlotLock

[![CI](https://github.com/saeedramadancv-sys/slotlock/actions/workflows/ci.yml/badge.svg)](https://github.com/saeedramadancv-sys/slotlock/actions/workflows/ci.yml)

A booking API built around one question: **what happens when two people want the same seat at the same moment?**

Booking systems are easy to write and hard to get right. The naive version — count the bookings, and insert one more if there is room — passes every test you write by hand and oversells the moment two requests arrive together. SlotLock is the version that holds up: seats are never sold twice, retried requests never book twice, and confirmation emails are never sent for bookings that rolled back.

```
ASP.NET Core 9  ·  EF Core 9  ·  SQL Server  ·  xUnit  ·  Serilog  ·  Prometheus
```

**85 tests** — 60 unit, 25 integration against a real SQL Server instance.

---

## The problem

Two customers press **Book** on the last seat at the same instant.

```
Request A                          Request B
---------                          ---------
read slot: 0 of 1 taken
                                   read slot: 0 of 1 taken
"there is room"
                                   "there is room"
write booking                      write booking
```

Neither request did anything wrong. The slot is oversold by the *interleaving*, and no line of code is at fault — which is why it survives code review and shows up in production.

## The defence

Three layers, each one catching what the layer above it missed.

### 1. The decision and the write are the same operation

The seat count lives on the slot row as `ReservedCount`, not in a `COUNT(*)` over bookings. There is no window between checking and acting, because there is no separate check.

### 2. A concurrency token on that row

`Slots.RowVersion` is a SQL Server `rowversion`. EF Core maps it as a concurrency token, so the update is emitted as:

```sql
UPDATE Slots SET ReservedCount = 1
WHERE Id = @id AND RowVersion = @versionThatWasRead
```

A write built on state somebody else has already changed matches **zero rows**. EF raises `DbUpdateConcurrencyException`, the loser reloads and tries again — and usually wins on the retry, because the seat was genuinely free.

### 3. A CHECK constraint in the schema

```sql
CONSTRAINT CK_Slots_ReservedWithinCapacity
    CHECK (ReservedCount >= 0 AND ReservedCount <= Capacity)
```

Layers 1 and 2 should make this unreachable. It is there because *should* is not a guarantee: if the application logic is ever wrong, the transaction fails instead of the slot overselling.

**No pessimistic locking.** Holding a row lock across a request serialises every booking on a resource and turns a popular slot into a queue. The optimistic path lets uncontended bookings run in parallel and pays a retry only when there is an actual race.

### The proof

`tests/SlotLock.IntegrationTests/OverbookingTests.cs` — 50 simultaneous HTTP requests, one seat:

```
created    == 1
conflicted == 49
Slots.ReservedCount == 1
```

Asserted twice: what the API told each caller, **and** what the database holds afterwards. A service can answer `201` to one caller and still have written two rows.

---

## What else is in here

| Concern | How it is handled |
|---|---|
| **Holds** | A booking starts `Held` with a deadline, so a seat can be taken off sale during checkout without being lost if the customer walks away. |
| **Idempotency** | `Idempotency-Key` on `POST /bookings`. The key is claimed by an INSERT under a unique index *before* the work runs, so two simultaneous retries cannot both book. |
| **Transactional outbox** | The booking and its notification are written by the same `SaveChanges`. A separate dispatcher delivers with exponential backoff and dead-letters what will never succeed. |
| **Hold sweeper** | Reclaims lapsed holds on a timer — and is deliberately *not* part of the correctness argument. |
| **Time zones** | Opening hours are local wall-clock times. Slots that fall in a daylight-saving gap are skipped; repeated hours resolve to exactly one slot. |
| **Observability** | Serilog structured logs, `/health/live` and `/health/ready`, Prometheus metrics at `/metrics`. |
| **Errors** | RFC 9457 problem responses with a stable machine-readable `code`. |

### Two design decisions worth stating

**Holds count against capacity.** A seat being paid for is not available. Treating it as available is exactly how a system sells the same seat twice during checkout.

**A lapsed hold can never be confirmed, regardless of whether the sweeper has run.** `Booking.Confirm` checks the deadline itself. If confirmation depended on a background job having fired, the outcome would change with how loaded the server was — and correctness that degrades under load is not correctness.

---

## Three bugs worth keeping

Each is documented in the code where it happened, because the fix is less interesting than the reason.

**JSON enums were ordinals.** `"days":["Sunday"]` — the exact call in this README — was
rejected as unconvertible, and a booking's status came back as `0`. Every test passed: the
unit tests never touched a serialiser, and the integration tests happened to use only
endpoints carrying no enum. It surfaced the first time the API was driven by hand, which is
the argument for doing that at least once. A `JsonStringEnumConverter` fixes it, and
`JsonContractTests` now pins the wire format.

**The retry budget was too small.** 60 callers, 12 seats — and only **11 seats sold**. Nothing was oversold, so the safety property held; but one caller was refused a seat that existed, because it burned five attempts in about five milliseconds while the winners were still committing. A retry budget has to outlast the queue ahead of it, not merely exist. Worse, `MaxConcurrencyAttempts` was bound from configuration and then ignored — the settings file described behaviour that was not happening.

**`"24:00:00"` is twenty-four days, not twenty-four hours.** `TimeSpan` reads a leading number above 23 as a day count, so a one-line appsettings entry set idempotency retention to 24 days. `ValidateDataAnnotations().ValidateOnStart()` caught it at boot — but only because a bound was declared. Both the trap and the bound are pinned by tests now.

---

## Running it

**Needs:** .NET 9 SDK, and SQL Server (Express, LocalDB, or a container).

```bash
dotnet tool install --global dotnet-ef
dotnet ef database update --project src/SlotLock.Infrastructure --startup-project src/SlotLock.Api
dotnet run --project src/SlotLock.Api
```

`dotnet run` opens Swagger at **https://localhost:7210/swagger** (or
`http://localhost:5101/swagger`). The database is created by the `dotnet ef database update`
above; the API does not migrate on startup, because a service that rewrites the schema as it
boots will eventually do so on a replica you did not mean to migrate.

### Tests

```bash
dotnet test
```

The integration suite needs a reachable SQL Server; it creates and migrates `SlotLock_Tests` itself. Point it elsewhere with:

```bash
SLOTLOCK_TEST_SQL="Server=localhost;User Id=sa;Password=...;TrustServerCertificate=True"
```

There is no in-memory provider here on purpose. Everything the suite exists to prove — the concurrency token, the CHECK constraint, the unique index — is a database feature the in-memory provider does not implement. A suite built on it would pass while the deployed service overbooked.

---

## Deploying it

`infra/` holds a Bicep template and a script that provisions the whole thing and pushes a
build to it:

```powershell
az login
./infra/deploy.ps1 -ResourceGroup slotlock-rg -Location westeurope
```

It creates an App Service on the free tier and an Azure SQL database on the free serverless
offer. Both sleep when idle, so the first request after a quiet spell pays for the wake-up.

**There is no administrator password anywhere in it.** The SQL server is created with
Entra-only authentication, the web app connects as its own managed identity, and the
connection string in the site's configuration therefore holds nothing worth stealing. The
application is granted `db_datareader` and `db_datawriter` and nothing else: it does not own
the schema, so migrations are applied by a person, and an application that was somehow
compromised still could not drop the CHECK constraint protecting it from overselling.

---

## API

```http
POST   /api/v1/resources                      create a bookable resource
POST   /api/v1/resources/{id}/slots           generate slots from local opening hours
GET    /api/v1/resources/{id}/availability     what is free between two instants

POST   /api/v1/bookings                       hold a seat   [Idempotency-Key]
POST   /api/v1/bookings/{id}/confirm          make it final
DELETE /api/v1/bookings/{id}                  release it
GET    /api/v1/bookings/{id}
```

### A booking, end to end

```bash
# 1. A resource, in its own time zone
curl -X POST localhost:5000/api/v1/resources \
  -H 'Content-Type: application/json' \
  -d '{"name":"Dr. Haddad","timeZoneId":"Asia/Amman","defaultCapacity":1}'

# 2. Slots for a working week - Sunday to Thursday, 09:00-17:00 local, 30 minutes each
curl -X POST localhost:5000/api/v1/resources/{id}/slots \
  -H 'Content-Type: application/json' \
  -d '{"fromDate":"2026-10-04","toDate":"2026-10-08",
       "dailyStart":"09:00","dailyEnd":"17:00","slotMinutes":30,
       "days":["Sunday","Monday","Tuesday","Wednesday","Thursday"]}'

# 3. Hold a seat - the key makes the request safe to retry
curl -X POST localhost:5000/api/v1/bookings \
  -H 'Content-Type: application/json' \
  -H 'Idempotency-Key: 6f1c2b9a-...' \
  -d '{"slotId":"{slotId}","customerReference":"saeed@example.com"}'

# 4. Confirm before the hold expires
curl -X POST localhost:5000/api/v1/bookings/{bookingId}/confirm
```

---

## Layout

```
src/
  SlotLock.Domain           entities and rules - no packages, no database, no clock
  SlotLock.Application      use cases, abstractions, the retry policy
  SlotLock.Infrastructure   EF Core, SQL Server, outbox dispatcher, background workers
  SlotLock.Api              endpoints, idempotency filter, problem details
tests/
  SlotLock.UnitTests        rules and arithmetic, in memory
  SlotLock.IntegrationTests real HTTP, real SQL Server, real concurrency
infra/
  main.bicep                App Service, Azure SQL, managed identity
  deploy.ps1                provision, migrate, publish
```

Dependencies point inward. The domain has no package references at all, which is what lets the booking rules be tested without a database, a clock, or a DI container — and what makes it obvious when a rule has leaked into a controller.
