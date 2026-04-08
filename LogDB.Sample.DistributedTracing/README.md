# LogDB Distributed Tracing Sample

Demonstrates multi-service distributed tracing with LogDB and OpenTelemetry.

## What this sample does

Simulates a realistic e-commerce checkout spanning **5 services**:

```
API Gateway (Server)
  |
  +-- Order Service (Server)
        |
        +-- Inventory Service (Server)
        |     +-- PostgreSQL query (Client)
        |
        +-- Payment Service (Server)
        |     +-- Stripe API call (Client)
        |
        +-- [async] Notification Service (Consumer)
              +-- SendGrid API call (Client)
```

### Three scenarios

1. **Successful order** - Full trace tree, all spans OK
2. **Payment failure** - Error propagates from Stripe -> Payment -> Order -> Gateway
3. **Slow inventory** - Inventory DB query takes 3 seconds, visible as a wide bar in the waterfall

## Running

```bash
# Set your LogDB API key
export LOGDB_API_KEY=your-api-key-here

# Run the sample
dotnet run
```

The sample prints trace IDs to the console. Copy any trace ID and paste it into the LogDB Trace Explorer.

## What to look for in LogDB

1. **Trace Explorer** - Paste a trace ID to see the span tree with parent-child indentation
2. **Waterfall view** - Bars show actual span durations (not log timestamps)
3. **Service swimlanes** - Each of the 5 services appears as a separate lane
4. **Error spans** - Scenario 2 shows red error spans propagating through the tree
5. **Slow spans** - Scenario 3 shows the inventory query as a wide bar (3 seconds)
6. **Span detail panel** - Click any span to see HTTP, RPC, DB, and messaging attributes
7. **Linked logs** - Each span has correlated log entries visible in the detail panel

## Span attributes used

| Service | Key attributes |
|---------|---------------|
| API Gateway | `http.method`, `http.url`, `http.route`, `user.email` |
| Order Service | `order.id`, `order.cart_id`, `rpc.system`, `rpc.method` |
| Inventory Service | `db.system`, `db.name`, `db.statement` |
| Payment Service | `payment.amount`, `payment.currency`, `payment.transaction_id` |
| Notification Service | `messaging.system`, `email.to`, `email.template` |
