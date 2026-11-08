#!/usr/bin/env pwsh
<#
.SYNOPSIS
Fills a local development database with representative demo data through the public API.

.DESCRIPTION
Creates one account per role, categories, products with stock, a coupon, and a shipping method. Safe to run again: anything that already exists is kept.
Staff roles are granted directly in SQL Server, because public registration cannot choose a role. Local development only: the API must be running.
The demo password is meant for this script's local accounts and must never be used anywhere else.

.EXAMPLE
pwsh scripts/seed-dev.ps1 -ApiUrl http://127.0.0.1:53375 -Database OrderFlow
#>
param(
    [string]$ApiUrl = 'http://127.0.0.1:53375',
    [string]$Database = 'OrderFlow',
    [string]$DemoPassword = 'OrderFlow-Demo-2026!'
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-Location (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if ($ApiUrl -notmatch '^https?://(localhost|127\.0\.0\.1)(:|/|$)') { throw 'This script only seeds a local API.' }
$sqlPassword = (Get-Content .env | Where-Object { $_ -like 'MSSQL_SA_PASSWORD=*' } | Select-Object -First 1).Split('=', 2)[1]

function Call([string]$method, [string]$path, $body = $null, [string]$token = $null) {
    $arguments = @{ Method = $method; Uri = "$ApiUrl/api/v1$path"; SkipHttpErrorCheck = $true; StatusCodeVariable = 'status'; Headers = @{} }
    if ($token) { $arguments.Headers['Authorization'] = "Bearer $token" }
    if ($null -ne $body) { $arguments['Body'] = ($body | ConvertTo-Json -Depth 5); $arguments['ContentType'] = 'application/json; charset=utf-8' }
    $response = Invoke-RestMethod @arguments
    return [pscustomobject]@{ Status = [int]$status; Body = $response }
}

function Ensure-Account([string]$email, [string]$firstName, [string[]]$roles) {
    $created = Call POST '/auth/register' @{ email = $email; password = $DemoPassword; firstName = $firstName; lastName = 'Demo' }
    if ($created.Status -notin 201, 409) { throw "Registering $email failed with $($created.Status)." }
    if ($roles) {
        $names = ($roles | ForEach-Object { "'$_'" }) -join ','
        $grant = "INSERT dbo.AspNetUserRoles (UserId, RoleId) SELECT u.Id, r.Id FROM dbo.AspNetUsers u CROSS JOIN dbo.AspNetRoles r WHERE u.Email = '$email' AND r.Name IN ($names) AND NOT EXISTS (SELECT 1 FROM dbo.AspNetUserRoles x WHERE x.UserId = u.Id AND x.RoleId = r.Id)"
        docker compose --env-file .env exec -T -e "SQLCMDPASSWORD=$sqlPassword" sqlserver /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -d $Database -b -Q $grant 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Granting roles to $email failed. Is -Database right?" }
    }
}

function Sign-In([string]$email) {
    $login = Call POST '/auth/login' @{ email = $email; password = $DemoPassword }
    if ($login.Status -ne 200) { throw "Signing in as $email failed with $($login.Status). If it already existed with another password, pass -DemoPassword." }
    return $login.Body.accessToken
}

$accounts = @(
    @{ Email = 'admin@orderflow.example'; Name = 'Admin'; Roles = @('Admin') }
    @{ Email = 'inventory@orderflow.example'; Name = 'Inventory'; Roles = @('InventoryManager') }
    @{ Email = 'support@orderflow.example'; Name = 'Support'; Roles = @('Support') }
    @{ Email = 'customer@orderflow.example'; Name = 'Customer'; Roles = @() }
)
foreach ($account in $accounts) { Ensure-Account $account.Email $account.Name $account.Roles }
$admin = Sign-In 'admin@orderflow.example'
$inventory = Sign-In 'inventory@orderflow.example'

$catalog = [ordered]@{
    'Lighting' = @(@('Desk lamp', 450000), @('Floor lamp', 1250000), @('LED strip 5 m', 320000), @('Reading light', 280000), @('Night light', 150000), @('Pendant lamp', 890000))
    'Furniture' = @(@('Oak bookshelf', 3200000), @('Standing desk', 6800000), @('Office chair', 2900000), @('Side table', 750000), @('Shoe rack', 520000), @('Wardrobe', 7400000))
    'Kitchen' = @(@('Chef knife', 690000), @('Cast iron pan', 980000), @('Rice cooker', 1450000), @('Glass storage set', 410000), @('Kettle', 620000), @('Cutting board', 240000))
    'Stationery' = @(@('Notebook A5', 65000), @('Fountain pen', 380000), @('Desk organizer', 210000), @('Sticky notes', 35000), @('Planner 2027', 185000), @('Marker set', 120000))
    'Outdoor' = @(@('Camping tent', 2450000), @('Hiking backpack', 1680000), @('Water bottle', 260000), @('Headlamp', 340000), @('Folding chair', 560000), @('Picnic blanket', 390000))
}

$existingCategories = (Call GET '/categories').Body
$sequence = 0
foreach ($categoryName in $catalog.Keys) {
    $slug = $categoryName.ToLowerInvariant()
    $category = $existingCategories | Where-Object slug -eq $slug
    if (-not $category) {
        $category = (Call POST '/categories' @{ name = $categoryName; slug = $slug; description = "$categoryName for the demo store."; isActive = $true } $admin).Body
    }
    foreach ($item in $catalog[$categoryName]) {
        $sequence++
        $created = Call POST '/products' @{ categoryId = $category.id; sku = "DEMO-$('{0:d3}' -f $sequence)"; name = $item[0]; description = "Demo product: $($item[0])."; price = $item[1]; currencyCode = 'VND'; isActive = $true } $admin
        if ($created.Status -eq 201) {
            # A few products start low or empty, so the availability filter and the low-stock path have something to show.
            $stock = if ($sequence % 10 -eq 0) { 0 } elseif ($sequence % 7 -eq 0) { 3 } else { 25 + ($sequence * 3) }
            if ($stock -gt 0) { Call POST "/inventory/$($created.Body.id)/adjust" @{ quantityDelta = $stock; reasonCode = 'DEMO_SEED'; note = 'Demo data' } $inventory | Out-Null }
            Call PUT "/inventory/$($created.Body.id)/threshold" @{ lowStockThreshold = 5 } $inventory | Out-Null
        }
        elseif ($created.Status -ne 409) { throw "Creating $($item[0]) failed with $($created.Status)." }
    }
}

$now = (Get-Date).ToUniversalTime()
$coupon = Call POST '/admin/coupons' @{ code = 'WELCOME10'; discountType = 'Percentage'; discountValue = 10; minimumSubtotal = 100000; validFromUtc = $now.AddDays(-1).ToString('o'); validUntilUtc = $now.AddYears(1).ToString('o'); perUserUsageLimit = 1 } $admin
if ($coupon.Status -notin 201, 409) { throw "Creating the coupon failed with $($coupon.Status)." }
$express = Call POST '/admin/shipping-methods' @{ code = 'EXPRESS'; name = 'Express delivery'; fee = 60000; currencyCode = 'VND' } $admin
if ($express.Status -notin 201, 409) { throw "Creating the shipping method failed with $($express.Status)." }

Write-Host "Seeded $sequence products in $($catalog.Count) categories, coupon WELCOME10, and these accounts with the demo password:"
$accounts | ForEach-Object { Write-Host "  $($_.Email) ($(if ($_.Roles) { $_.Roles -join ', ' } else { 'Customer' }))" }
