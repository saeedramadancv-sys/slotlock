# SlotLock

[![CI](https://github.com/saeedramadancv-sys/slotlock/actions/workflows/ci.yml/badge.svg)](https://github.com/saeedramadancv-sys/slotlock/actions/workflows/ci.yml)

A booking API I built around one question: what happens when two people want the same seat at the same moment?

The simple version of a booking system counts the bookings and inserts one more if there's room. It passes every test you write by hand, and it oversells the first time two requests arrive together. SlotLock is my attempt at a version that doesn't. A seat can't be sold twice, a retried request can't book twice, and a booking that rolled back never sends a notification.

```
ASP.NET Core 9  ·  EF Core 9  ·  SQL Server  ·  xUnit  ·  Serilog  ·  Prometheus
```

85 tests: 60 unit tests and 25 integration tests against a real SQL Server.

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

Neither request did anything wrong on its own. The oversell comes from the timing, so no single line of code looks guilty. That's why this kind of bug gets through code review.

## How SlotLock prevents it

There are three layers, and each one catches what the one before it might miss.

### 1. Checking and writing happen in one step

The seat count is stored on the slot row as `ReservedCount` instead of being worked out with a `COUNT(*)` over bookings. Since there's no separate "check" query, there's no gap between checking and writing.

### 2. A concurrency token on the slot row

`Slots.RowVersion` is a SQL Server `rowversion`. EF Core treats it as a concurrency token, so the update it sends looks like this:

```sql
UPDATE Slots SET ReservedCount = 1
WHERE Id = @id AND RowVersion = @versionThatWasRead
```

If someone else changed the row after it was read, this matches zero rows. EF throws `DbUpdateConcurrencyException`, and the request reloads the slot and tries again. Usually it succeeds on the retry, because the seat really was free.

### 3. A CHECK constraint in the database

```sql
CONSTRAINT CK_Slots_ReservedWithinCapacity
    CHECK (ReservedCount >= 0 AND ReservedCount <= Capacity)
```

Layers 1 and 2 should already make an oversell impossible. This is here in case my application code is ever wrong: the transaction fails instead of the slot being oversold.

I didn't use pessimistic locking. Holding a row lock for the length of a request would line up every booking for a resource one behind the other. With the optimistic approach, bookings that don't clash run in parallel, and only real races pay for a retry.

### Testing it

`tests/SlotLock.IntegrationTests/OverbookingTests.cs` sends 50 HTTP requests for one seat at the same time:

```
created    == 1
conflicted == 49
Slots.ReservedCount == 1
```

The test checks two things: what the API told each caller, and what's actually in the database afterwards. An API can return `201` to one caller and still have written two rows, so checking only the responses isn't enough.

---

## What else is in here

| Feature | How it works |
|---|---|
| Holds | A booking starts as `Held` with a deadline. The seat is off sale during checkout, but it comes back if the customer walks away. |
| Idempotency | `POST /bookings` accepts an `Idempotency-Key`. The key is inserted under a unique index *before* any work runs, so two retries arriving together can't both book. |
| Transactional outbox | The booking and its notification message are saved in the same `SaveChanges`. A separate dispatcher delivers the messages with exponential backoff and dead-letters the ones that keep failing. (For now the delivery handler only logs the message; no email is sent.) |
| Hold sweeper | A timer clears expired holds. Correctness doesn't depend on it running on time (see below). |
| Time zones | Opening hours are in local time. Slots that fall in a daylight-saving gap are skipped, and a repeated hour produces exactly one slot. |
| Observability | Serilog structured logs, `/health/live` and `/health/ready`, and Prometheus metrics at `/metrics`. |
| Errors | RFC 9457 problem responses with a stable `code` field that clients can rely on. |

### Two decisions I made on purpose

Holds count against capacity. A seat someone is paying for isn't available, and treating it as available is exactly how a system sells the same seat twice during checkout.

An expired hold can never be confirmed, whether or not the sweeper has run. `Booking.Confirm` checks the deadline itself. If confirming depended on a background job running on time, the result would change with how busy the server was.

---

## Three bugs the tests (and I) caught

Each one is also explained in a comment next to the fix, because why it happened is more useful than the fix itself.

**Enums were sent as numbers.** `"days":["Sunday"]`, the exact request in this README, was rejected, and a booking's status came back as `0`. Every test passed anyway: the unit tests never touched JSON, and the integration tests happened to only call endpoints without enums. I found it the first time I called the API by hand. A `JsonStringEnumConverter` fixed it, and `JsonContractTests` now locks the format in.

**The retry limit was too low.** 60 callers, 12 seats, and only 11 seats were sold. Nothing was oversold, so the important rule held, but one caller was turned away from a seat that existed. It used up five attempts in about five milliseconds while the winning requests were still committing. On top of that, `MaxConcurrencyAttempts` was read from configuration and then never used, so the settings file described behaviour that wasn't happening.

**`"24:00:00"` means 24 days, not 24 hours.** `TimeSpan` reads a first number above 23 as days, so one line in appsettings set idempotency retention to 24 days. `ValidateDataAnnotations().ValidateOnStart()` caught it at startup, but only because I had declared a range for that setting. Both the trap and the range are covered by tests now.

---

## Running it

You need the .NET 9 SDK and a SQL Server (Express, LocalDB or a container).

```bash
dotnet tool install --global dotnet-ef
dotnet ef database update --project src/SlotLock.Infrastructure --startup-project src/SlotLock.Api
dotnet run --project src/SlotLock.Api
```

`dotnet run` serves Swagger at **http://localhost:5101/swagger** (or https://localhost:7210/swagger with the `https` profile). The `dotnet ef database update` step above creates the database. The API doesn't run migrations on startup, because a service that changes the schema whenever it boots will eventually do it on a server where you didn't want it to.

### Tests

```bash
dotnet test
```

The integration tests need a SQL Server they can reach. They create and migrate a `SlotLock_Tests` database themselves. To point them at a different server:

```bash
export SLOTLOCK_TEST_SQL="Server=localhost;User Id=sa;Password=...;TrustServerCertificate=True"
```

I don't use EF Core's in-memory provider here. The things these tests exist to check (the concurrency token, the CHECK constraint, the unique index) are database features the in-memory provider doesn't have, so tests built on it would pass while the real service oversold.

---

## Deploying it

`infra/` has a Bicep template and a script that create everything on Azure and push a build to it:

```powershell
az login
./infra/deploy.ps1 -ResourceGroup slotlock-rg -Location westeurope
```

It creates an App Service on the free tier and an Azure SQL database on the free serverless offer. Both go to sleep when idle, so the first request after a quiet period is slow.

There's no admin password anywhere in it. The SQL server uses Entra-only authentication, the web app connects with its own managed identity, and so the connection string in the app's settings has nothing worth stealing. The app only gets `db_datareader` and `db_datawriter`. It doesn't own the schema, so a person applies migrations, and even a compromised app couldn't drop the CHECK constraint that stops overselling.

---

## API

```http
POST   /api/v1/resources                      create a bookable resource
GET    /api/v1/resources                      list resources
POST   /api/v1/resources/{id}/slots           generate slots from local opening hours
GET    /api/v1/resources/{id}/availability     what is free between two instants

POST   /api/v1/bookings                       hold a seat   [Idempotency-Key]
POST   /api/v1/bookings/{id}/confirm          make it final
DELETE /api/v1/bookings/{id}                  release it
GET    /api/v1/bookings/{id}
```

### A booking, start to finish

Use dates in the future: the API won't let you hold a slot that has already started.

```bash
# 1. A resource, in its own time zone
curl -X POST localhost:5101/api/v1/resources \
  -H 'Content-Type: application/json' \
  -d '{"name":"Dr. Haddad","timeZoneId":"Asia/Amman","defaultCapacity":1}'

# 2. Slots for a working week: Sunday to Thursday, 09:00-17:00 local, 30 minutes each
curl -X POST localhost:5101/api/v1/resources/{id}/slots \
  -H 'Content-Type: application/json' \
  -d '{"fromDate":"2027-01-03","toDate":"2027-01-07",
       "dailyStart":"09:00","dailyEnd":"17:00","slotMinutes":30,
       "days":["Sunday","Monday","Tuesday","Wednesday","Thursday"]}'

# 3. Hold a seat (the key makes the request safe to retry)
curl -X POST localhost:5101/api/v1/bookings \
  -H 'Content-Type: application/json' \
  -H 'Idempotency-Key: 6f1c2b9a-...' \
  -d '{"slotId":"{slotId}","customerReference":"saeed@example.com"}'

# 4. Confirm before the hold expires
curl -X POST localhost:5101/api/v1/bookings/{bookingId}/confirm
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

Dependencies point inward. The domain project has no package references at all, so the booking rules can be tested without a database, a clock or a DI container, and it's easy to spot when a rule has crept into a controller.
