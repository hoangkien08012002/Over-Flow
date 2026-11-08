-- The important read and claim queries, shaped like the SQL that EF Core and the Worker send.
-- run-benchmark.ps1 runs this after the load, so the tables hold benchmark data. Read the logical reads and each query's main operators.
SET NOCOUNT ON;
DECLARE @user uniqueidentifier = (SELECT TOP 1 UserId FROM dbo.Orders GROUP BY UserId ORDER BY COUNT(*) DESC);
DECLARE @product uniqueidentifier = (SELECT TOP 1 ProductId FROM dbo.InventoryReservations ORDER BY ExpiresAtUtc DESC);
DECLARE @now datetimeoffset = SYSUTCDATETIME();
SET STATISTICS IO ON;

PRINT '== Q1 catalog search, newest first (GET /products) ==';
SELECT p.Id, p.Name, p.Price, c.Name, i.OnHand - i.Reserved FROM dbo.Products p JOIN dbo.Categories c ON c.Id = p.CategoryId LEFT JOIN dbo.Inventories i ON i.ProductId = p.Id
WHERE p.IsActive = 1 AND c.IsActive = 1 AND (p.Name LIKE '%Product 42%' OR p.Sku LIKE '%Product 42%') AND i.OnHand > i.Reserved ORDER BY p.CreatedAtUtc DESC, p.Id OFFSET 0 ROWS FETCH NEXT 20 ROWS ONLY;

PRINT '== Q2 catalog browse by category and price (GET /products) ==';
SELECT p.Id, p.Name, p.Price FROM dbo.Products p JOIN dbo.Categories c ON c.Id = p.CategoryId
WHERE p.IsActive = 1 AND c.IsActive = 1 AND p.CategoryId = (SELECT TOP 1 Id FROM dbo.Categories) AND p.Price BETWEEN 1000 AND 50000 ORDER BY p.Price, p.Id OFFSET 0 ROWS FETCH NEXT 20 ROWS ONLY;

PRINT '== Q3 customer order history (GET /orders) ==';
SELECT TOP 20 * FROM dbo.Orders WHERE UserId = @user ORDER BY CreatedAtUtc DESC;

PRINT '== Q4 staff order queue by status (GET /admin/orders) ==';
SELECT TOP 20 * FROM dbo.Orders WHERE Status = 'Processing' ORDER BY CreatedAtUtc DESC;

PRINT '== Q5 outbox claim (Worker) ==';
SELECT TOP 50 Id FROM dbo.OutboxMessages WITH (UPDLOCK, READPAST) WHERE ProcessedAtUtc IS NULL AND ParkedAtUtc IS NULL AND (NextAttemptAtUtc IS NULL OR NextAttemptAtUtc <= @now) ORDER BY OccurredAtUtc;

PRINT '== Q6 reservation expiry sweep (Worker) ==';
SELECT TOP 100 Id FROM dbo.InventoryReservations WHERE Status = 'Active' AND ExpiresAtUtc <= @now;

PRINT '== Q7 reservations of one product (GET /inventory/{id}/reservations) ==';
SELECT TOP 100 * FROM dbo.InventoryReservations WHERE ProductId = @product ORDER BY CreatedAtUtc DESC;

PRINT '== Q8 idempotency lookup (POST /orders) ==';
SELECT * FROM dbo.IdempotencyRecords WHERE UserId = @user AND Endpoint = 'POST /api/v1/orders' AND [Key] = 'absent-key';

SET STATISTICS IO OFF;
