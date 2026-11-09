#!/usr/bin/env pwsh
<#
.SYNOPSIS
Proves a deployed stack works end to end through the HTTPS edge: web client, API, SQL Server, Redis, Kafka, and Worker.

.DESCRIPTION
Creates throwaway accounts and one product, places a COD order through the edge, and waits until the Worker has moved it to
Processing, which needs the outbox publisher, Kafka, and three consumers. Also checks the browser session contract: the refresh
cookie is Secure, HttpOnly, and SameSite=Strict, and a refresh through the edge succeeds. Returns the order ID.
The staff role is granted in SQL Server, because public registration cannot choose a role (D-33).

.EXAMPLE
pwsh deploy/smoke.ps1
#>
param([string]$EnvFile = (Join-Path $PSScriptRoot '.env.prod'))

. (Join-Path $PSScriptRoot 'common.ps1')
$script:EnvFile = $EnvFile
$values = Read-EnvFile $EnvFile
$site = Get-SiteUrl $values
$skipCertificate = $values['SITE_ADDRESS'] -in @($null, '', 'localhost')
$database = $values['SQL_DATABASE'] ?? 'OrderFlow'
$run = Get-Date -Format yyyyMMddHHmmss
$password = "Smoke-$([guid]::NewGuid().ToString('N').Substring(0, 12))!A1"
$failures = 0

function Call([string]$method, [string]$path, $body = $null, [string]$token = $null, [hashtable]$headers = @{}) {
    $arguments = @{ Method = $method; Uri = "$site$path"; SkipHttpErrorCheck = $true; StatusCodeVariable = 'status'; Headers = $headers.Clone(); SkipCertificateCheck = $skipCertificate }
    if ($token) { $arguments.Headers['Authorization'] = "Bearer $token" }
    if ($null -ne $body) { $arguments.Body = $body | ConvertTo-Json -Depth 5; $arguments.ContentType = 'application/json' }
    $response = Invoke-RestMethod @arguments
    return [pscustomobject]@{ Status = [int]$status; Body = $response }
}
function Check([string]$name, [bool]$passed) {
    Write-Host "$(if ($passed) { 'PASS' } else { 'FAIL' })  $name"
    if (-not $passed) { $script:failures++ }
}
function Sign-In([string]$email) { (Call POST '/api/v1/auth/login' @{ email = $email; password = $password }).Body.accessToken }

$page = Invoke-WebRequest "$site/" -SkipCertificateCheck:$skipCertificate
Check 'The web client is served over HTTPS' ($page.StatusCode -eq 200 -and $page.Content -match '<div id="root">')
Check 'The web client has a content security policy' ([string]$page.Headers['Content-Security-Policy'] -match "script-src 'self'")
Check 'HSTS is sent' ([string]$page.Headers['Strict-Transport-Security'] -match 'max-age')
$deepLink = Invoke-WebRequest "$site/orders/does-not-matter" -SkipCertificateCheck:$skipCertificate
Check 'Client routes fall back to index.html' ($deepLink.StatusCode -eq 200 -and $deepLink.Content -match '<div id="root">')
Check 'Readiness is not exposed at the edge' ((Invoke-WebRequest "$site/health/ready" -SkipCertificateCheck:$skipCertificate -SkipHttpErrorCheck).StatusCode -eq 404)

$admin = "smoke-admin-$run@example.invalid"
$customer = "smoke-customer-$run@example.invalid"
foreach ($email in $admin, $customer) {
    $registered = Call POST '/api/v1/auth/register' @{ email = $email; password = $password; firstName = 'Smoke'; lastName = 'Test' }
    if ($registered.Status -ne 201) { throw "Registering $email failed with $($registered.Status)." }
}
Invoke-Sql "INSERT dbo.AspNetUserRoles (UserId, RoleId) SELECT u.Id, r.Id FROM dbo.AspNetUsers u CROSS JOIN dbo.AspNetRoles r WHERE u.Email = N'$admin' AND r.Name IN (N'Admin', N'InventoryManager')" $database | Out-Null
$adminToken = Sign-In $admin

# Browser session: the refresh token travels only as a cookie, and the production cookie must be Secure.
$session = $null
$login = Invoke-WebRequest "$site/api/v1/auth/login" -Method Post -ContentType 'application/json' -Body (@{ email = $customer; password = $password } | ConvertTo-Json) `
    -Headers @{ 'X-Refresh-Token-Transport' = 'cookie' } -SessionVariable session -SkipCertificateCheck:$skipCertificate
$cookie = [string]$login.Headers['Set-Cookie']
Check 'The refresh cookie is Secure, HttpOnly, and SameSite=Strict' ($cookie -match 'orderflow_refresh=' -and $cookie -match '(?i)secure' -and $cookie -match '(?i)httponly' -and $cookie -match '(?i)samesite=strict')
Check 'The login body does not contain the refresh token' (-not ($login.Content | ConvertFrom-Json).PSObject.Properties['refreshToken'])
$refresh = Invoke-WebRequest "$site/api/v1/auth/refresh" -Method Post -ContentType 'application/json' -Body '{}' -Headers @{ 'X-Refresh-Token-Transport' = 'cookie' } `
    -WebSession $session -SkipCertificateCheck:$skipCertificate -SkipHttpErrorCheck
Check 'A refresh through the edge rotates the session' ($refresh.StatusCode -eq 200)
$customerToken = ($refresh.Content | ConvertFrom-Json).accessToken

$category = Call POST '/api/v1/categories' @{ name = "Smoke $run"; slug = "smoke-$run"; description = 'Deployment smoke test'; isActive = $true } $adminToken
$product = Call POST '/api/v1/products' @{ categoryId = $category.Body.id; sku = "SMOKE-$run"; name = "Smoke $run"; description = 'Deployment smoke test'; price = 100000; currencyCode = 'VND'; isActive = $true } $adminToken
Check 'Staff can create a product' ($product.Status -eq 201)
$stock = Call POST "/api/v1/inventory/$($product.Body.id)/adjust" @{ quantityDelta = 5; reasonCode = 'SMOKE'; note = 'Deployment smoke test' } $adminToken
Check 'Staff can add stock' ($stock.Status -eq 200)
$detail = Call GET "/api/v1/products/$($product.Body.id)"
Check 'The product is readable anonymously (Redis-backed read path)' ($detail.Status -eq 200 -and $detail.Body.available -eq 5)

$shippingMethod = (Call GET '/api/v1/shipping-methods').Body[0].id
$cart = Call POST '/api/v1/cart/items' @{ productId = $product.Body.id; quantity = 1 } $customerToken
$address = @{ recipientName = 'Smoke Test'; phone = '0900000000'; line1 = 'Line 1'; ward = 'Ward'; district = 'District'; province = 'Province'; countryCode = 'VN' }
$order = Call POST '/api/v1/orders' @{ cartId = $cart.Body.id; shippingMethodId = $shippingMethod; paymentMethod = 'COD'; couponCode = $null; deliveryAddress = $address } $customerToken @{ 'Idempotency-Key' = "smoke-$run" }
Check 'Checkout is accepted' ($order.Status -eq 202)
$processed = Wait-Until { (Call GET "/api/v1/orders/$($order.Body.id)" -token $customerToken).Body.order.status -eq 'Processing' } 120
Check 'The Worker moves the order to Processing through Kafka' $processed

if ($failures -gt 0) { throw "$failures smoke check(s) failed against $site." }
Write-Host "Smoke test passed against $site"
return $order.Body.id
