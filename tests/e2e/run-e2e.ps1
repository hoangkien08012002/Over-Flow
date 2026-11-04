#!/usr/bin/env pwsh
<#
.SYNOPSIS
Black-box end-to-end run of OrderFlow: real API and two Worker processes against Compose SQL Server and Kafka.

.DESCRIPTION
Uses an isolated database and topic prefix per run, drives only public HTTP plus operator tools, and injects real
failures: stopping the Kafka container, killing a Worker, and making the Notifications table refuse writes.
Exits non-zero when any scenario fails. Requires Docker, .env, and the EF Core 10 tool.

.EXAMPLE
pwsh tests/e2e/run-e2e.ps1 -EfTool dotnet-ef
#>
param(
    [string]$EfTool = 'dotnet-ef',
    [string]$LogDir = (Join-Path ([IO.Path]::GetTempPath()) "orderflow-e2e-$(Get-Date -Format yyyyMMddHHmmss)"),
    [int]$ApiPort = 5199,
    [switch]$KeepDatabase
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
Set-Location $root
New-Item -ItemType Directory -Force $LogDir | Out-Null

$password = (Get-Content .env | Where-Object { $_ -like 'MSSQL_SA_PASSWORD=*' } | Select-Object -First 1).Split('=', 2)[1]
if (-not $password) { throw 'MSSQL_SA_PASSWORD is missing from .env' }
$runId = Get-Date -Format yyyyMMddHHmmss
$database = "OrderFlow_E2E_$runId"
$topicPrefix = "e2e$runId."
$api = "http://127.0.0.1:$ApiPort"
$results = [System.Collections.Generic.List[object]]::new()
$processes = @{}
$workerCounter = 0

function Compose { docker compose --env-file .env @args }

function Produce([string]$topic, [string]$key, [string]$value) {
    "$key|$value" | docker compose --env-file .env exec -T kafka /opt/kafka/bin/kafka-console-producer.sh --bootstrap-server localhost:9092 --topic "$topicPrefix$topic" --property parse.key=true --property 'key.separator=|' 2>&1 | Out-File -Append (Join-Path $LogDir 'produce.log')
    if ($LASTEXITCODE -ne 0) { throw "Producing to $topic failed; see produce.log" }
}

function Sql([string]$query) {
    # -y 0 keeps long values on one line, and the value is always the last line of output.
    $output = Compose exec -T -e "SQLCMDPASSWORD=$password" sqlserver /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -d $database -y 0 -b -Q "SET NOCOUNT ON; $query" 2>&1
    if ($LASTEXITCODE -ne 0) { throw "SQL failed: $query`n$output" }
    return (@($output) | Where-Object { "$_".Trim() } | Select-Object -Last 1 | ForEach-Object { "$_".Trim() })
}

function Check([string]$scenario, [string]$name, [bool]$passed, [string]$detail = '') {
    $results.Add([pscustomobject]@{ Scenario = $scenario; Check = $name; Passed = $passed; Detail = $detail })
    $mark = if ($passed) { 'PASS' } else { 'FAIL' }
    $suffix = if ($detail) { " ($detail)" } else { '' }
    Write-Host "  [$mark] $name$suffix"
}

function Equal([string]$scenario, [string]$name, $expected, $actual) {
    Check $scenario $name ("$expected" -eq "$actual") "expected $expected, got $actual"
}

function Invoke-Api([string]$method, [string]$path, $body = $null, [string]$token = $null, [hashtable]$headers = @{}) {
    $all = @{} + $headers
    if ($token) { $all['Authorization'] = "Bearer $token" }
    $arguments = @{ Method = $method; Uri = "$api$path"; Headers = $all; SkipHttpErrorCheck = $true; StatusCodeVariable = 'status'; TimeoutSec = 60 }
    if ($null -ne $body) { $arguments['Body'] = ($body | ConvertTo-Json -Depth 8); $arguments['ContentType'] = 'application/json; charset=utf-8' }
    $response = Invoke-RestMethod @arguments
    return [pscustomobject]@{ Status = [int]$status; Body = $response }
}

function Wait-Until([scriptblock]$condition, [int]$timeoutSeconds = 120, [int]$intervalMilliseconds = 500) {
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try { if (& $condition) { return $true } } catch { }
        Start-Sleep -Milliseconds $intervalMilliseconds
    }
    return $false
}

function Start-App([string]$name, [string]$project) {
    $directory = Join-Path $root "src/$project/bin/Debug/net10.0"
    $process = Start-Process dotnet -ArgumentList (Join-Path $directory "$project.dll") -WorkingDirectory $directory -PassThru -NoNewWindow `
        -RedirectStandardOutput (Join-Path $LogDir "$name.log") -RedirectStandardError (Join-Path $LogDir "$name.err.log")
    $processes[$name] = $process
    return $process
}

function Start-Worker {
    $script:workerCounter++
    Start-App "worker-$script:workerCounter" 'OrderFlow.Worker' | Out-Null
    return "worker-$script:workerCounter"
}

function Stop-All { foreach ($process in $processes.Values) { if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue } } }

function Worker-Logs { (Get-ChildItem $LogDir -Filter 'worker-*.log' | Where-Object { $_.Name -notlike '*.err.log' } | ForEach-Object { Get-Content -Raw $_.FullName }) -join "`n" }

function Get-OrderStatus([string]$orderId, [string]$token) { (Invoke-Api GET "/api/v1/orders/$orderId" -token $token).Body.order.status }

function Wait-OrderStatus([string]$orderId, [string]$status, [string]$token, [int]$timeoutSeconds = 120) {
    Wait-Until { (Get-OrderStatus $orderId $token) -eq $status } $timeoutSeconds | Out-Null
    return Get-OrderStatus $orderId $token
}

$address = @{ recipientName = 'E2E Buyer'; phone = '0900000000'; line1 = 'Line 1'; ward = 'Ward'; district = 'District'; province = 'Province'; countryCode = 'VN' }
$shippingMethodId = $null

function Place-Order([string]$token, [string]$productId, [int]$quantity, [string]$paymentMethod, [string]$key = "e2e-$([guid]::NewGuid().ToString('N'))", [string]$couponCode = $null) {
    $cart = Invoke-Api POST '/api/v1/cart/items' @{ productId = $productId; quantity = $quantity } $token
    if ($cart.Status -ne 200) { throw "Adding to cart failed with $($cart.Status)" }
    $response = Invoke-Api POST '/api/v1/orders' @{ cartId = $cart.Body.id; shippingMethodId = $shippingMethodId; paymentMethod = $paymentMethod; couponCode = $(if ($couponCode) { $couponCode } else { $null }); deliveryAddress = $address } $token @{ 'Idempotency-Key' = $key }
    return [pscustomobject]@{ Status = $response.Status; Order = $response.Body; Key = $key; CartId = $cart.Body.id }
}

function Sign-In([string]$email) {
    $response = Invoke-Api POST '/api/v1/auth/login' @{ email = $email; password = 'StrongPassword123!' }
    if ($response.Status -ne 200) { throw "Login failed for $email with $($response.Status)" }
    return $response.Body.accessToken
}

function Register([string]$email) {
    $response = Invoke-Api POST '/api/v1/auth/register' @{ email = $email; password = 'StrongPassword123!'; firstName = 'E2E'; lastName = 'User' }
    if ($response.Status -ne 201) { throw "Registration failed for $email with $($response.Status)" }
}

function New-Product([string]$token, [string]$categoryId, [string]$sku, [decimal]$price, [int]$stock, [string]$inventoryToken) {
    $product = Invoke-Api POST '/api/v1/products' @{ categoryId = $categoryId; sku = "$runId-$sku"; name = "E2E $sku"; description = 'E2E'; price = $price; currencyCode = 'VND'; isActive = $true } $token
    if ($product.Status -ne 201) { throw "Creating product $sku failed with $($product.Status)" }
    $adjust = Invoke-Api POST "/api/v1/inventory/$($product.Body.id)/adjust" @{ quantityDelta = $stock; reasonCode = 'E2E_SEED'; note = 'seed' } $inventoryToken
    if ($adjust.Status -ne 200) { throw "Seeding stock for $sku failed with $($adjust.Status)" }
    return $product.Body.id
}

try {
    Write-Host "Logs: $LogDir"
    Write-Host '== Infrastructure and build =='
    Compose up -d sqlserver redis kafka 2>&1 | Out-Null
    if (-not (Wait-Until { Compose exec -T -e "SQLCMDPASSWORD=$password" sqlserver /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -Q 'SELECT 1' 2>&1 | Out-Null; $LASTEXITCODE -eq 0 } 120 2000)) { throw 'SQL Server did not become ready.' }
    dotnet build OrderFlow.sln --nologo -v quiet 2>&1 | Out-File (Join-Path $LogDir 'build.log')
    if ($LASTEXITCODE -ne 0) { throw 'Build failed; see build.log' }

    $connection = "Server=127.0.0.1,1433;Database=$database;User ID=sa;Password=$password;TrustServerCertificate=True;Encrypt=False"
    $env:ORDERFLOW_SQL_CONNECTION = $connection
    $env:ConnectionStrings__OrderFlow = $connection
    & $EfTool database update --project src/OrderFlow.Infrastructure --startup-project src/OrderFlow.Infrastructure --no-build --connection $connection 2>&1 | Out-File (Join-Path $LogDir 'migrate.log')
    if ($LASTEXITCODE -ne 0) { throw 'Migration failed; see migrate.log' }

    $env:ASPNETCORE_URLS = $api
    $env:ASPNETCORE_ENVIRONMENT = 'Production'
    $env:DOTNET_ENVIRONMENT = 'Production'
    $env:Jwt__SigningKey = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(48))
    $env:Logging__LogLevel__Microsoft_EntityFrameworkCore = 'Warning'
    ${env:Logging__LogLevel__Microsoft.EntityFrameworkCore} = 'Warning'
    $env:Kafka__TopicPrefix = $topicPrefix
    $env:Kafka__ConsumerGroupPrefix = $topicPrefix
    $env:Outbox__PollIntervalMilliseconds = '200'
    $env:Outbox__MaxRetrySeconds = '10'
    $env:Consumers__ReplayPollSeconds = '1'
    $env:Retention__SweepSeconds = '2'

    Start-App 'api' 'OrderFlow.Api' | Out-Null
    if (-not (Wait-Until { (Invoke-Api GET '/api/v1/shipping-methods').Status -eq 200 } 60 1000)) { throw 'API did not start; see api.log' }
    $firstWorker = Start-Worker
    $secondWorker = Start-Worker

    Write-Host '== Setup through the public API =='
    Register "admin-$runId@example.invalid"
    Register "customer-$runId@example.invalid"
    Register "stranger-$runId@example.invalid"
    Sql "INSERT dbo.AspNetUserRoles (UserId, RoleId) SELECT u.Id, r.Id FROM dbo.AspNetUsers u CROSS JOIN dbo.AspNetRoles r WHERE u.Email = 'admin-$runId@example.invalid' AND r.Name IN ('Admin','InventoryManager')" | Out-Null
    $adminToken = Sign-In "admin-$runId@example.invalid"
    $customerToken = Sign-In "customer-$runId@example.invalid"
    $strangerToken = Sign-In "stranger-$runId@example.invalid"
    $shippingMethodId = (Invoke-Api GET '/api/v1/shipping-methods').Body[0].id
    $category = Invoke-Api POST '/api/v1/categories' @{ name = 'E2E'; slug = "e2e-$runId"; description = 'E2E'; isActive = $true } $adminToken
    if ($category.Status -ne 201) { throw "Creating category failed with $($category.Status)" }
    $mainProduct = New-Product $adminToken $category.Body.id 'MAIN' 100000 100 $adminToken
    $declinedProduct = New-Product $adminToken $category.Body.id 'DECLINE' 100000.13 10 $adminToken
    $scarceProduct = New-Product $adminToken $category.Body.id 'SCARCE' 50000 1 $adminToken
    $mainSold = 0

    $scenario = 'Happy path COD'
    Write-Host "== $scenario =="
    $cod = Place-Order $customerToken $mainProduct 2 'COD'
    Equal $scenario 'Checkout returns 202' 202 $cod.Status
    Equal $scenario 'Response is PendingInventory, not a payment result' 'PendingInventory' $cod.Order.status
    Equal $scenario 'Server calculates totals' 230000 $cod.Order.grandTotal
    Equal $scenario 'Order reaches Processing' 'Processing' (Wait-OrderStatus $cod.Order.id 'Processing' $customerToken)
    $mainSold += 2
    Equal $scenario 'History follows the state machine' 'PendingInventory,AwaitingPayment,Paid,Processing' (Sql "SELECT STRING_AGG(ToStatus, ',') WITHIN GROUP (ORDER BY ChangedAtUtc) FROM dbo.OrderStatusHistories WHERE OrderId = '$($cod.Order.id)'")
    $apiHistory = (Invoke-Api GET "/api/v1/orders/$($cod.Order.id)" -token $customerToken).Body.history
    Equal $scenario 'D-25: API history uses readable statuses' 'PendingInventory,AwaitingPayment,Paid,Processing' (($apiHistory | ForEach-Object toStatus) -join ',')
    Equal $scenario 'One correlation ID across history and events' 1(Sql "SELECT COUNT(DISTINCT c) FROM (SELECT CorrelationId c FROM dbo.OrderStatusHistories WHERE OrderId = '$($cod.Order.id)' UNION ALL SELECT CorrelationId FROM dbo.OutboxMessages WHERE AggregateId = '$($cod.Order.id)') x")
    Equal $scenario 'COD stays pending collection' 'Succeeded/PendingCollection' (Sql "SELECT Status + '/' + SettlementStatus FROM dbo.Payments WHERE OrderId = '$($cod.Order.id)'")
    $stock = (Invoke-Api GET "/api/v1/inventory/$mainProduct" -token $adminToken).Body
    Equal $scenario 'Stock committed' "$(100 - $mainSold)/0" "$($stock.onHand)/$($stock.reserved)"
    Equal $scenario 'Customer notified' 1 (Sql "SELECT COUNT(*) FROM dbo.Notifications WHERE OrderId = '$($cod.Order.id)' AND Type = 'PaymentSucceeded'")
    Equal $scenario 'Each consumer recorded PaymentSucceeded once' 3 (Sql "SELECT COUNT(*) FROM dbo.InboxMessages i JOIN dbo.OutboxMessages o ON o.Id = i.EventId WHERE o.AggregateId = '$($cod.Order.id)' AND o.EventType = 'PaymentSucceeded'")

    $scenario = 'AC-02 and AC-09 at the API'
    Write-Host "== $scenario =="
    $replay = Invoke-Api POST '/api/v1/orders' @{ cartId = $cod.CartId; shippingMethodId = $shippingMethodId; paymentMethod = 'COD'; couponCode = $null; deliveryAddress = $address } $customerToken @{ 'Idempotency-Key' = $cod.Key }
    Equal $scenario 'Same key replays the same order' $cod.Order.id $replay.Body.id
    Equal $scenario 'Replay creates no second order' 1 (Sql "SELECT COUNT(*) FROM dbo.Orders WHERE OrderNumber = '$($cod.Order.orderNumber)'")
    $reused = Invoke-Api POST '/api/v1/orders' @{ cartId = $cod.CartId; shippingMethodId = $shippingMethodId; paymentMethod = 'CardSimulation'; couponCode = $null; deliveryAddress = $address } $customerToken @{ 'Idempotency-Key' = $cod.Key }
    Equal $scenario 'Different body with same key conflicts' 'IDEMPOTENCY_KEY_REUSED' $reused.Body.code
    Equal $scenario "Another customer's order is hidden" 404 (Invoke-Api GET "/api/v1/orders/$($cod.Order.id)" -token $strangerToken).Status
    Equal $scenario 'Cancelling after fulfilment began is refused' 409 (Invoke-Api POST "/api/v1/orders/$($cod.Order.id)/cancel" @{ reason = 'Too late' } $customerToken).Status

    $scenario = 'Other payment methods'
    Write-Host "== $scenario =="
    foreach ($method in 'CardSimulation', 'BankTransferSimulation') {
        $order = Place-Order $customerToken $mainProduct 1 $method
        Equal $scenario "$method reaches Processing" 'Processing' (Wait-OrderStatus $order.Order.id 'Processing' $customerToken)
        Equal $scenario "$method is settled" 'Succeeded/Settled' (Sql "SELECT Status + '/' + SettlementStatus FROM dbo.Payments WHERE OrderId = '$($order.Order.id)'")
        $mainSold++
    }

    $scenario = 'AC-08 payment failure'
    Write-Host "== $scenario =="
    # The simulation declines on the grand total's cents, so the quantity must stay 1 to keep the .13 ending.
    $declined = Place-Order $customerToken $declinedProduct 1 'CardSimulation'
    Equal $scenario 'Order ends PaymentFailed' 'PaymentFailed' (Wait-OrderStatus $declined.Order.id 'PaymentFailed' $customerToken)
    Check $scenario 'Reservation released' (Wait-Until { (Sql "SELECT Status FROM dbo.InventoryReservations WHERE OrderId = '$($declined.Order.id)'") -eq 'Released' } 60)
    Equal $scenario 'Payment recorded as declined' 'Failed/CARD_DECLINED' (Sql "SELECT Status + '/' + ResultCode FROM dbo.Payments WHERE OrderId = '$($declined.Order.id)'")
    $stock = (Invoke-Api GET "/api/v1/inventory/$declinedProduct" -token $adminToken).Body
    Equal $scenario 'Stock fully restored' '10/0' "$($stock.onHand)/$($stock.reserved)"
    Check $scenario 'Customer notified' (Wait-Until { (Sql "SELECT COUNT(*) FROM dbo.Notifications WHERE OrderId = '$($declined.Order.id)' AND Type = 'PaymentFailed'") -eq '1' } 60)

    $scenario = 'D-22 coupon return'
    Write-Host "== $scenario =="
    # Coupon administration arrives in Phase 10, so the single-use coupon is seeded directly.
    $couponCode = "E2E$runId"
    Sql "INSERT dbo.Coupons (Id, Code, DiscountType, DiscountValue, MinimumSubtotal, ValidFromUtc, ValidUntilUtc, TotalUsageLimit, PerUserUsageLimit, UsageCount, IsEnabled, CreatedAtUtc, UpdatedAtUtc) VALUES (NEWID(), '$couponCode', 'Fixed', 10000, 0, DATEADD(day, -1, SYSUTCDATETIME()), DATEADD(day, 1, SYSUTCDATETIME()), 1, 1, 0, 1, SYSUTCDATETIME(), SYSUTCDATETIME())" | Out-Null
    # A round discount keeps the .13 ending that the simulation declines.
    $couponDeclined = Place-Order $customerToken $declinedProduct 1 'CardSimulation' -couponCode $couponCode
    Equal $scenario 'Discount is applied' 120000.13 $couponDeclined.Order.grandTotal
    Equal $scenario 'Declined order ends PaymentFailed' 'PaymentFailed' (Wait-OrderStatus $couponDeclined.Order.id 'PaymentFailed' $customerToken)
    Equal $scenario 'Coupon use is returned' 0 (Sql "SELECT UsageCount FROM dbo.Coupons WHERE Code = '$couponCode'")
    $couponSuccess = Place-Order $customerToken $mainProduct 1 'COD' -couponCode $couponCode
    Equal $scenario 'The same single-use coupon works again' 202 $couponSuccess.Status
    Equal $scenario 'Successful order reaches Processing' 'Processing' (Wait-OrderStatus $couponSuccess.Order.id 'Processing' $customerToken)
    $mainSold++
    Equal $scenario 'Successful order keeps its coupon use' 1 (Sql "SELECT UsageCount FROM dbo.Coupons WHERE Code = '$couponCode'")
    Equal $scenario 'Usage history keeps both orders' '1/1' (Sql "SELECT CONCAT(SUM(CASE WHEN ReleasedAtUtc IS NULL THEN 1 ELSE 0 END), '/', SUM(CASE WHEN ReleasedAtUtc IS NULL THEN 0 ELSE 1 END)) FROM dbo.CouponUsages")
    $couponExhausted = Place-Order $customerToken $mainProduct 1 'COD' -couponCode $couponCode
    Equal $scenario 'An exhausted coupon is still refused' 400 $couponExhausted.Status
    foreach ($line in (Invoke-Api GET '/api/v1/cart' -token $customerToken).Body.items) { Invoke-Api DELETE "/api/v1/cart/items/$($line.id)" -token $customerToken | Out-Null }

    $scenario = 'Inventory rejection'
    Write-Host "== $scenario =="
    $rejected = Place-Order $customerToken $scarceProduct 2 'CardSimulation'
    Equal $scenario 'Order ends Rejected' 'Rejected' (Wait-OrderStatus $rejected.Order.id 'Rejected' $customerToken)
    Equal $scenario 'Payment never started' 0 (Sql "SELECT COUNT(*) FROM dbo.Payments WHERE OrderId = '$($rejected.Order.id)'")
    Equal $scenario 'PaymentRequested never emitted' 0 (Sql "SELECT COUNT(*) FROM dbo.OutboxMessages WHERE AggregateId = '$($rejected.Order.id)' AND EventType = 'PaymentRequested'")
    Check $scenario 'Customer notified' (Wait-Until { (Sql "SELECT COUNT(*) FROM dbo.Notifications WHERE OrderId = '$($rejected.Order.id)' AND Type = 'InventoryRejected'") -eq '1' } 60)

    $scenario = 'AC-04 duplicate delivery'
    Write-Host "== $scenario =="
    $eventId = Sql "SELECT CAST(Id AS nvarchar(36)) FROM dbo.OutboxMessages WHERE AggregateId = '$($cod.Order.id)' AND EventType = 'OrderCreated'"
    $rawEvent = Sql "SELECT Payload FROM dbo.OutboxMessages WHERE Id = '$eventId'"
    $reservationsBefore = Sql "SELECT COUNT(*) FROM dbo.InventoryReservations WHERE OrderId = '$($cod.Order.id)'"
    $stockBefore = (Invoke-Api GET "/api/v1/inventory/$mainProduct" -token $adminToken).Body
    Produce 'order.created' $cod.Order.id $rawEvent
    Check $scenario 'Worker logged the redelivery as a duplicate' (Wait-Until { (Worker-Logs) -match "skipped duplicate OrderCreated $eventId" } 60)
    Equal $scenario 'No second reservation' $reservationsBefore (Sql "SELECT COUNT(*) FROM dbo.InventoryReservations WHERE OrderId = '$($cod.Order.id)'")
    $stockAfter = (Invoke-Api GET "/api/v1/inventory/$mainProduct" -token $adminToken).Body
    Equal $scenario 'Stock unchanged' "$($stockBefore.onHand)/$($stockBefore.reserved)" "$($stockAfter.onHand)/$($stockAfter.reserved)"

    $scenario = 'AC-03 Kafka outage'
    Write-Host "== $scenario =="
    Compose stop kafka 2>&1 | Out-Null
    $outage = Place-Order $customerToken $mainProduct 3 'CardSimulation'
    Equal $scenario 'Checkout still commits with Kafka down' 202 $outage.Status
    $cancelledDuringOutage = Place-Order $customerToken $mainProduct 4 'CardSimulation'
    Check $scenario 'Failed publish recorded in the outbox' (Wait-Until { [int](Sql "SELECT COUNT(*) FROM dbo.OutboxMessages WHERE AggregateId = '$($outage.Order.id)' AND ProcessedAtUtc IS NULL AND AttemptCount >= 1 AND LastError IS NOT NULL") -eq 1 } 60)
    Equal $scenario 'Event is not marked published' 'NULL' (Sql "SELECT ISNULL(CONVERT(nvarchar(40), ProcessedAtUtc), 'NULL') FROM dbo.OutboxMessages WHERE AggregateId = '$($outage.Order.id)'")
    Equal $scenario 'Order waits in PendingInventory' 'PendingInventory' (Get-OrderStatus $outage.Order.id $customerToken)
    Equal $scenario 'Customer can cancel while pending' 200 (Invoke-Api POST "/api/v1/orders/$($cancelledDuringOutage.Order.id)/cancel" @{ reason = 'Changed my mind' } $customerToken).Status
    Compose start kafka 2>&1 | Out-Null
    Equal $scenario 'Order completes after Kafka recovers' 'Processing' (Wait-OrderStatus $outage.Order.id 'Processing' $customerToken 240)
    $mainSold += 3
    Check $scenario 'Late OrderCreated does not reserve for the cancelled order' (Wait-Until { [int](Sql "SELECT COUNT(*) FROM dbo.InboxMessages i JOIN dbo.OutboxMessages o ON o.Id = i.EventId WHERE o.AggregateId = '$($cancelledDuringOutage.Order.id)' AND o.EventType = 'OrderCreated' AND i.ConsumerName = 'inventory'") -eq 1 } 120)
    Equal $scenario 'Cancelled order holds no reservation' 0 (Sql "SELECT COUNT(*) FROM dbo.InventoryReservations WHERE OrderId = '$($cancelledDuringOutage.Order.id)'")
    Equal $scenario 'Cancelled order stays Cancelled' 'Cancelled' (Get-OrderStatus $cancelledDuringOutage.Order.id $customerToken)

    $scenario = 'AC-10 poison and retry exhaustion'
    Write-Host "== $scenario =="
    Sql "ALTER TABLE dbo.Notifications WITH NOCHECK ADD CONSTRAINT CK_E2E_Refuse CHECK (1 = 0)" | Out-Null
    $poisoned = Place-Order $customerToken $mainProduct 1 'COD'
    Equal $scenario 'A failing notification does not hold up the order' 'Processing' (Wait-OrderStatus $poisoned.Order.id 'Processing' $customerToken)
    $mainSold++
    $paymentEventId = Sql "SELECT CAST(Id AS nvarchar(36)) FROM dbo.OutboxMessages WHERE AggregateId = '$($poisoned.Order.id)' AND EventType = 'PaymentSucceeded'"
    Check $scenario 'Failure stored once the retry limit is reached' (Wait-Until { (Sql "SELECT COUNT(*) FROM dbo.FailedMessages WHERE EventId = '$paymentEventId' AND ConsumerName = 'notifications' AND DeadLetteredAtUtc IS NOT NULL") -eq '1' } 90)
    Equal $scenario 'Attempt count equals the configured limit' 5 (Sql "SELECT AttemptCount FROM dbo.FailedMessages WHERE EventId = '$paymentEventId'")
    Equal $scenario 'Diagnostics hold a safe summary' 'DbUpdateException: SQL error 547' (Sql "SELECT ErrorSummary FROM dbo.FailedMessages WHERE EventId = '$paymentEventId'")
    $deadLetters = Compose exec -T kafka /opt/kafka/bin/kafka-console-consumer.sh --bootstrap-server localhost:9092 --topic "$($topicPrefix)payment.succeeded.dlq" --from-beginning --timeout-ms 15000 2>$null
    Check $scenario 'DLQ topic holds the original event' (($deadLetters -join "`n") -match $paymentEventId)
    Sql "ALTER TABLE dbo.Notifications DROP CONSTRAINT CK_E2E_Refuse" | Out-Null
    $failedId = Sql "SELECT CAST(Id AS nvarchar(36)) FROM dbo.FailedMessages WHERE EventId = '$paymentEventId'"
    Equal $scenario 'Customers cannot reprocess' 403 (Invoke-Api POST "/api/v1/admin/failed-messages/$failedId/reprocess" -token $customerToken).Status
    $listed = (Invoke-Api GET '/api/v1/admin/failed-messages?status=Failed' -token $adminToken).Body.items | Where-Object id -eq $failedId
    Equal $scenario 'Admin list shows the failure with the original EventId' $paymentEventId $listed.eventId
    Equal $scenario 'Admin reprocess is accepted' 202 (Invoke-Api POST "/api/v1/admin/failed-messages/$failedId/reprocess" -token $adminToken).Status
    Check $scenario 'Replay delivers exactly one notification' (Wait-Until { (Sql "SELECT COUNT(*) FROM dbo.Notifications WHERE SourceEventId = '$paymentEventId'") -eq '1' } 90)
    Check $scenario 'Replay is marked complete' (Wait-Until { (Sql "SELECT Status FROM dbo.FailedMessages WHERE Id = '$failedId'") -eq 'Replayed' } 30)
    Equal $scenario 'Replay did not repeat the charge' 1 (Sql "SELECT COUNT(*) FROM dbo.Payments WHERE OrderId = '$($poisoned.Order.id)'")
    Produce 'order.created' 'poison' '{not json'
    Check $scenario 'Malformed message goes straight to the failure store' (Wait-Until { (Sql "SELECT COUNT(*) FROM dbo.FailedMessages WHERE MessageKey = 'poison' AND ConsumerName = 'inventory' AND AttemptCount = 1") -eq '1' } 60)
    $unsafeId = Sql "SELECT CAST(Id AS nvarchar(36)) FROM dbo.FailedMessages WHERE MessageKey = 'poison'"
    Equal $scenario 'A message without an EventId cannot be replayed' 409 (Invoke-Api POST "/api/v1/admin/failed-messages/$unsafeId/reprocess" -token $adminToken).Status
    $afterPoison = Place-Order $customerToken $mainProduct 1 'COD'
    Equal $scenario 'The partition keeps flowing after poison' 'Processing' (Wait-OrderStatus $afterPoison.Order.id 'Processing' $customerToken)
    $mainSold++

    $scenario = 'Worker crash and multi-worker'
    Write-Host "== $scenario =="
    $burst = @()
    for ($i = 0; $i -lt 8; $i++) {
        $burst += Place-Order $customerToken $mainProduct 1 'CardSimulation'
        if ($i -eq 2) { Stop-Process -Id $processes[$firstWorker].Id -Force }
    }
    $mainSold += 8
    $replacement = Start-Worker
    Check $scenario "Hard-killed worker's work finishes on survivors" (Wait-Until { @($burst | Where-Object { (Get-OrderStatus $_.Order.id $customerToken) -ne 'Processing' }).Count -eq 0 } 240 1000)
    Equal $scenario 'Each burst order was charged once' 8 (Sql "SELECT COUNT(*) FROM dbo.Payments WHERE OrderId IN ('$(($burst | ForEach-Object { $_.Order.id }) -join "','")')")

    $scenario = 'D-24 retention'
    Write-Host "== $scenario =="
    $inboxBefore = [int](Sql "SELECT COUNT(*) FROM dbo.InboxMessages")
    Sql "UPDATE dbo.OutboxMessages SET ProcessedAtUtc = DATEADD(day, -8, SYSUTCDATETIME()) WHERE Id = '$eventId'; UPDATE dbo.InboxMessages SET ProcessedAtUtc = DATEADD(day, -31, SYSUTCDATETIME()) WHERE EventId = '$eventId'" | Out-Null
    Check $scenario 'Aged outbox row is removed' (Wait-Until { (Sql "SELECT COUNT(*) FROM dbo.OutboxMessages WHERE Id = '$eventId'") -eq '0' } 30)
    Check $scenario 'Aged Inbox row is removed' (Wait-Until { (Sql "SELECT COUNT(*) FROM dbo.InboxMessages WHERE EventId = '$eventId'") -eq '0' } 30)
    Check $scenario 'Recent Inbox rows stay' ([int](Sql "SELECT COUNT(*) FROM dbo.InboxMessages") -ge ($inboxBefore - 1))
    Check $scenario 'Worker logged the removal' ((Worker-Logs) -match 'Retention removed')

    $scenario = 'Final invariants'
    Write-Host "== $scenario =="
    Check $scenario 'Outbox drains completely' (Wait-Until { (Sql "SELECT COUNT(*) FROM dbo.OutboxMessages WHERE ProcessedAtUtc IS NULL") -eq '0' } 120)
    Equal $scenario 'Nothing is parked' 0 (Sql "SELECT COUNT(*) FROM dbo.OutboxMessages WHERE ParkedAtUtc IS NOT NULL")
    $stock = (Invoke-Api GET "/api/v1/inventory/$mainProduct" -token $adminToken).Body
    Equal $scenario 'Main product stock is exact with nothing reserved' "$(100 - $mainSold)/0" "$($stock.onHand)/$($stock.reserved)"
    Equal $scenario 'No active reservations remain' 0 (Sql "SELECT COUNT(*) FROM dbo.InventoryReservations WHERE Status = 'Active'")
    Equal $scenario 'No order has more than one payment' 0 (Sql "SELECT COUNT(*) FROM (SELECT OrderId FROM dbo.Payments GROUP BY OrderId HAVING COUNT(*) > 1) x")
    Equal $scenario 'No orders are stuck mid-workflow' 0 (Sql "SELECT COUNT(*) FROM dbo.Orders WHERE Status IN ('PendingInventory','AwaitingPayment','Paid')")
    $combined = (Get-ChildItem $LogDir -Filter '*.log' | ForEach-Object { Get-Content -Raw $_.FullName }) -join "`n"
    Check $scenario 'Logs contain no secrets or addresses' (-not ($combined -match [regex]::Escape($password) -or $combined -match 'Line 1' -or $combined -match 'accessToken'))
    Check $scenario 'Worker log shows outbox failure and recovery' ((Worker-Logs) -match 'failed on attempt' -and (Worker-Logs) -match 'on attempt [2-9]')
}
catch {
    $results.Add([pscustomobject]@{ Scenario = 'Harness'; Check = 'Script ran to completion'; Passed = $false; Detail = "$_" })
    Write-Host "HARNESS FAILURE: $_"
}
finally {
    Stop-All
    Compose start kafka 2>&1 | Out-Null
    if (-not $KeepDatabase) { Compose exec -T -e "SQLCMDPASSWORD=$password" sqlserver /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -Q "ALTER DATABASE [$database] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$database]" 2>&1 | Out-Null }
}

$results | ConvertTo-Json -Depth 3 | Out-File (Join-Path $LogDir 'summary.json')
$failed = @($results | Where-Object { -not $_.Passed })
Write-Host ''
Write-Host '== Summary =='
$results | Group-Object Scenario | ForEach-Object { '{0,-40} {1}/{2}' -f $_.Name, @($_.Group | Where-Object Passed).Count, $_.Count } | Write-Host
Write-Host "Total: $($results.Count - $failed.Count)/$($results.Count) passed. Logs: $LogDir"
if ($failed.Count -gt 0) {
    Write-Host 'Failures:'
    $failed | ForEach-Object { Write-Host " - [$($_.Scenario)] $($_.Check): $($_.Detail)" }
    exit 1
}
