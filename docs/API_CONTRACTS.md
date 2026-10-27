# OrderFlow API Contracts

## Conventions

- Base path: `/api/v1`.
- JSON uses camel case.
- Protected endpoints use Bearer JWT.
- `X-Correlation-Id` is accepted or generated and returned.
- `POST /orders` requires `Idempotency-Key`.
- Pagination response contains `items`, `page`, `pageSize`, `totalCount`, and `totalPages`.
- Maximum default `pageSize` is 100.
- Validation failures use HTTP 400; authentication 401; forbidden policy 403; hidden ownership resources 404; concurrency/idempotency conflict 409; rate limit 429.

## Error format

```json
{
  "type": "https://orderflow/errors/validation",
  "title": "Request validation failed",
  "status": 400,
  "code": "VALIDATION_FAILED",
  "correlationId": "01J...",
  "errors": {
    "email": ["Email is invalid."]
  }
}
```

Stable codes include `VALIDATION_FAILED`, `AUTH_INVALID_CREDENTIALS`, `AUTHENTICATION_REQUIRED`, `ACCOUNT_LOCKED`, `EMAIL_ALREADY_EXISTS`, `REFRESH_TOKEN_REVOKED`, `RESOURCE_NOT_FOUND`, `ORDER_NOT_FOUND`, `FORBIDDEN`, `RATE_LIMITED`, `INSUFFICIENT_STOCK`, `INVALID_STATE_TRANSITION`, `CONCURRENCY_CONFLICT`, `IDEMPOTENCY_KEY_REQUIRED`, `IDEMPOTENCY_KEY_REUSED`, and `REQUEST_IN_PROGRESS`.

## Authentication

### POST `/auth/register`

```json
{
  "email": "customer@example.com",
  "password": "StrongPassword123!",
  "firstName": "Kien",
  "lastName": "Nguyen"
}
```

Returns `201 Created` with user ID and email. Duplicate email returns `409 EMAIL_ALREADY_EXISTS`.

### POST `/auth/login`

```json
{ "email": "customer@example.com", "password": "StrongPassword123!" }
```

Returns `accessToken`, `accessTokenExpiresAtUtc`, and the raw `refreshToken`. Only its SHA-256 hash is stored. Unknown email and incorrect password share the same `401 AUTH_INVALID_CREDENTIALS` response. A locked account returns `423 ACCOUNT_LOCKED`.

### POST `/auth/refresh`

Accepts the current refresh token, rotates its token family, and returns a new access/refresh pair. Reuse returns `401 REFRESH_TOKEN_REVOKED` and revokes all active tokens in that family. Concurrent use allows at most one rotation.

### POST `/auth/logout`

Revokes the supplied refresh token and returns `204 No Content`. Logout is idempotent for an unknown or already revoked token.

Authentication requests are rate limited by client IP. Local defaults allow 20 registrations, 20 logins, and 20 refresh/logout requests per minute. A limited request returns `429 RATE_LIMITED` in the standard ProblemDetails format.

## Catalog

### GET `/categories`

Returns active categories with `id`, `name`, `slug`, `description`, `isActive`, and Base64 `version`. Admins may pass `includeInactive=true` to include inactive categories.

### POST `/categories`

Admin only. Accepts `name` (1–150 characters), unique `slug` (1–180 characters), optional `description` (up to 1000 characters), and `isActive`. Returns 201 with the category. Duplicate slug returns `409 SLUG_ALREADY_EXISTS`.

### PUT `/categories/{id}` and PUT `/categories/{id}/activation`

Admin only. The update accepts the same fields as create and an optional Base64 `version`. Activation accepts `isActive` and optional `version`. A stale version returns `409 CONCURRENCY_CONFLICT`.

### GET `/products`

Query parameters: `search`, `categoryId`, `minPrice`, `maxPrice`, `isAvailable`, `sort`, `page`, and `pageSize`. Public endpoint. Invalid sort or range returns 400.

Sort values are `name` (default), `name_desc`, `price`, `price_desc`, and `newest`. Default page size is 20; maximum is 100. `isAvailable=true` filters to positive SQL inventory availability. Each item includes `available` and Base64 `version`. Public reads include only active products in active categories; Admin may pass `includeInactive=true`.

### GET `/products/{id}`

Returns active product details and availability summary. Inactive products are visible only to authorized staff.

Details include category name, SQL inventory `available` quantity, and Base64 `version`. Admins and Support staff may inspect inactive products and products in inactive categories.

### POST `/products`

Admin only.

```json
{
  "categoryId": "guid",
  "sku": "OF-001",
  "name": "Example product",
  "description": "Description",
  "price": 150000.00,
  "currencyCode": "VND",
  "isActive": true
}
```

Returns `201 Created`. Duplicate SKU returns `409 SKU_ALREADY_EXISTS`.

The referenced category must exist and be active, including when creating an inactive product. Otherwise the API returns `400 VALIDATION_FAILED`.

### PUT `/products/{id}`

Admin only. Updates mutable catalog fields and accepts an optional `version` containing the Base64-encoded SQL Server rowversion returned by a prior read. It commits SQL first, then invalidates relevant Redis keys. Returns 409 for an optimistic concurrency conflict when the supplied version is stale.

The request has the same mutable fields as create and may set `isActive`. The referenced category must be active. Redis invalidation applies once Phase 7 introduces catalog caching.

### PUT `/products/{id}/activation`

Admin only. Accepts `isActive` and optional Base64 `version`. Activating a product requires an active category. A stale version returns `409 CONCURRENCY_CONFLICT`.

## Shipping methods

### GET `/shipping-methods`

Returns active shipping methods available for checkout, including `id`, `code`, `name`, `fee`, and `currencyCode`. Checkout reloads the selected method from SQL Server and snapshots its server-owned fee; it does not trust a client-provided fee.

## Inventory

### POST `/inventory/{productId}/adjust`

Inventory Manager policy.

```json
{
  "quantityDelta": 20,
  "reasonCode": "PURCHASE_RECEIPT",
  "note": "Initial warehouse receipt"
}
```

Returns the new `onHand`, `reserved`, `available`, and version. A negative result returns `409 INVENTORY_BELOW_ZERO`.

## Cart

### GET `/cart`

Returns current items, current prices, availability, and recalculated display totals.

### POST `/cart/items`

```json
{ "productId": "guid", "quantity": 2 }
```

Creates or increases a cart line. Quantity must be positive and within configured maximum.

### PUT `/cart/items/{id}`

```json
{ "quantity": 3 }
```

### DELETE `/cart/items/{id}`

Returns `204 No Content`.

## Orders

### POST `/orders`

Customer only. Header `Idempotency-Key` is required.

```json
{
  "cartId": "guid",
  "shippingMethodId": "guid",
  "paymentMethod": "CardSimulation",
  "couponCode": null,
  "deliveryAddress": {
    "recipientName": "Nguyen Hoang Kien",
    "phone": "0900000000",
    "line1": "Example address",
    "ward": "Example ward",
    "district": "Example district",
    "province": "Thanh Hoa",
    "countryCode": "VN"
  }
}
```

Returns `202 Accepted` after order, idempotency record, and outbox message commit:

```json
{
  "id": "guid",
  "orderNumber": "OF-20261001-000001",
  "status": "PendingInventory",
  "subtotal": 300000.00,
  "discountTotal": 0.00,
  "shippingFee": 30000.00,
  "grandTotal": 330000.00,
  "currencyCode": "VND",
  "statusUrl": "/api/v1/orders/guid"
}
```

### GET `/orders`

Customer gets only their orders. Supports status and date filters plus pagination.

### GET `/orders/{id}`

Customer ownership is applied in the database query. Staff access requires policy authorization. Missing or unauthorized customer resources return `404 ORDER_NOT_FOUND`.

### POST `/orders/{id}/cancel`

```json
{ "reason": "Ordered by mistake" }
```

Returns the updated order or `409 INVALID_STATE_TRANSITION`.

## Health

- `/health/live`: process liveness only.
- `/health/ready`: SQL Server readiness; Redis and Kafka status are reported without making non-critical cache health the sole readiness gate.
