#!/usr/bin/env pwsh
<#
.SYNOPSIS
Repeatable benchmark of product detail, product search, and checkout submission, followed by query statistics and plans.

.DESCRIPTION
Builds Release, seeds an isolated database with -Products products directly through SQL, starts the API and one Worker, and runs k6 from its Docker image.
Writes k6-summary.json, query-stats.txt, query-plans.txt, and machine.txt to -LogDir. Exits non-zero when a threshold from the SRS is missed.
Rate limits are lifted for this run, because all the load comes from one address.

.EXAMPLE
pwsh tests/perf/run-benchmark.ps1 -LogDir artifacts/perf
#>
param(
    [string]$EfTool = 'dotnet-ef',
    [string]$LogDir = (Join-Path ([IO.Path]::GetTempPath()) "orderflow-perf-$(Get-Date -Format yyyyMMddHHmmss)"),
    [int]$ApiPort = 5197,
    [int]$Products = 5000,
    [int]$Customers = 10,
    [string]$Duration = '60s',
    [string]$WarmUp = '15s',
    [switch]$KeepDatabase
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
Set-Location $root
New-Item -ItemType Directory -Force $LogDir | Out-Null
$LogDir = (Resolve-Path $LogDir).Path

$password = (Get-Content .env | Where-Object { $_ -like 'MSSQL_SA_PASSWORD=*' } | Select-Object -First 1).Split('=', 2)[1]
if (-not $password) { throw 'MSSQL_SA_PASSWORD is missing from .env' }
$runId = Get-Date -Format yyyyMMddHHmmss
$database = "OrderFlow_Perf_$runId"
$processes = @()
$exitCode = 1

function Compose { docker compose --env-file .env @args }
function Wait-Until([scriptblock]$condition, [int]$timeoutSeconds) {
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    while ((Get-Date) -lt $deadline) { try { if (& $condition) { return $true } } catch { }; Start-Sleep -Seconds 1 }
    return $false
}
function Sql([string]$query) {
    $output = Compose exec -T -e "SQLCMDPASSWORD=$password" sqlserver /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -d $database -b -y 0 -Q "$query" 2>&1
    if ($LASTEXITCODE -ne 0) { throw "SQL failed: $output" }
    return $output
}

$seed = @"
SET NOCOUNT ON;
DECLARE @now datetimeoffset = SYSUTCDATETIME();
INSERT dbo.Categories (Id, Name, Slug, Description, IsActive, CreatedAtUtc, UpdatedAtUtc)
SELECT NEWID(), CONCAT('Category ', n), CONCAT('bench-category-', n), 'Benchmark', 1, @now, @now FROM (SELECT TOP 20 ROW_NUMBER() OVER (ORDER BY (SELECT 1)) n FROM sys.objects) x;
;WITH numbers AS (SELECT TOP ($Products) ROW_NUMBER() OVER (ORDER BY (SELECT 1)) n FROM sys.all_objects a CROSS JOIN sys.all_objects b)
INSERT dbo.Products (Id, CategoryId, Sku, Name, Description, Price, CurrencyCode, IsActive, CreatedAtUtc, UpdatedAtUtc)
SELECT NEWID(), (SELECT Id FROM (SELECT Id, ROW_NUMBER() OVER (ORDER BY Id) - 1 r FROM dbo.Categories) c WHERE c.r = n % 20), CONCAT('BENCH-', n), CONCAT('Bench Product ', n), 'A representative product description for the benchmark.',
    1000 + (n % 500) * 100, 'VND', 1, DATEADD(minute, -n, @now), @now FROM numbers;
INSERT dbo.Inventories (Id, ProductId, OnHand, Reserved, LowStockThreshold, UpdatedAtUtc) SELECT NEWID(), Id, 1000000, 0, 0, @now FROM dbo.Products;
"@

try {
    Write-Host "Logs: $LogDir"
    Write-Host '== Infrastructure and Release build =='
    Compose up -d sqlserver redis kafka 2>&1 | Out-Null
    if (-not (Wait-Until { Compose exec -T -e "SQLCMDPASSWORD=$password" sqlserver /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -Q 'SELECT 1' 2>&1 | Out-Null; $LASTEXITCODE -eq 0 } 120)) { throw 'SQL Server did not become ready.' }
    dotnet build OrderFlow.sln --configuration Release --nologo -v quiet 2>&1 | Out-File (Join-Path $LogDir 'build.log')
    if ($LASTEXITCODE -ne 0) { throw 'Build failed; see build.log' }

    $connection = "Server=127.0.0.1,1433;Database=$database;User ID=sa;Password=$password;TrustServerCertificate=True;Encrypt=False"
    $env:ORDERFLOW_SQL_CONNECTION = $connection
    $env:ConnectionStrings__OrderFlow = $connection
    & $EfTool database update --project src/OrderFlow.Infrastructure --startup-project src/OrderFlow.Infrastructure --configuration Release --no-build --connection $connection 2>&1 | Out-File (Join-Path $LogDir 'migrate.log')
    if ($LASTEXITCODE -ne 0) { throw 'Migration failed; see migrate.log' }
    Write-Host "== Seeding $Products products =="
    Sql $seed | Out-Null

    $env:ASPNETCORE_ENVIRONMENT = 'Production'
    $env:DOTNET_ENVIRONMENT = 'Production'
    $env:ASPNETCORE_URLS = "http://0.0.0.0:$ApiPort"
    $env:Jwt__SigningKey = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(48))
    $env:Kafka__TopicPrefix = "perf$runId."
    $env:Kafka__ConsumerGroupPrefix = "perf$runId."
    # Logging every request would measure the console, so only warnings are written.
    $env:Logging__LogLevel__Default = 'Warning'
    foreach ($limit in 'Register', 'Login', 'Refresh', 'Logout', 'Search', 'Checkout') { Set-Item "Env:RateLimits__$limit" '10000000' }

    foreach ($project in 'OrderFlow.Api', 'OrderFlow.Worker') {
        $directory = Join-Path $root "src/$project/bin/Release/net10.0"
        $processes += Start-Process dotnet -ArgumentList (Join-Path $directory "$project.dll") -WorkingDirectory $directory -PassThru -NoNewWindow -RedirectStandardOutput (Join-Path $LogDir "$project.log") -RedirectStandardError (Join-Path $LogDir "$project.err.log")
    }
    if (-not (Wait-Until { (Invoke-RestMethod "http://127.0.0.1:$ApiPort/api/v1/shipping-methods").Count -ge 0 } 60)) { throw 'API did not start; see OrderFlow.Api.log' }

    $machine = @(
        "Date (UTC): $((Get-Date).ToUniversalTime().ToString('u'))"
        "CPU: $((Get-CimInstance Win32_Processor).Name), $([Environment]::ProcessorCount) logical processors"
        "RAM: $([math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB, 1)) GiB"
        "OS: $([Environment]::OSVersion.VersionString)"
        "Docker: $(docker version --format '{{.Server.Version}}'), $(docker info --format '{{.NCPU}} CPUs, {{.MemTotal}} bytes')"
        ".NET: $(dotnet --version), Release build, Production environment"
        "Data: $Products products in 20 categories, each with inventory"
        "Load: 20 VUs on reads, $Customers VUs on checkout, $WarmUp warm-up then $Duration measured per scenario, no think time"
    )
    $machine | Out-File (Join-Path $LogDir 'machine.txt')
    $machine | Write-Host

    Write-Host '== k6 =='
    docker run --rm -i -v "${root}/tests/perf:/scripts:ro" -v "${LogDir}:/out" -e "API_URL=http://host.docker.internal:$ApiPort" -e "CUSTOMERS=$Customers" -e "DURATION=$Duration" -e "WARMUP=$WarmUp" `
        grafana/k6:1.3.0 run --quiet --summary-export /out/k6-summary.json /scripts/orderflow.js 2>&1 | Tee-Object (Join-Path $LogDir 'k6.log')
    $exitCode = $LASTEXITCODE

    Write-Host '== Query statistics and plans =='
    # Submission only commits the order. This records how long one Worker then takes to finish everything the load submitted.
    $drainStarted = Get-Date
    $drained = Wait-Until { "$(Sql "SET NOCOUNT ON; SELECT CONCAT('left=', COUNT(*)) FROM dbo.Orders WHERE Status IN ('PendingInventory','AwaitingPayment','Paid')")" -match 'left=0(?!\d)' } 900
    "Worker drain after the load: drained=$drained in $([int]((Get-Date) - $drainStarted).TotalSeconds) s" | Tee-Object (Join-Path $LogDir 'drain.txt')
    "Orders by status:`n$(Sql 'SET NOCOUNT ON; SELECT Status, COUNT(*) FROM dbo.Orders GROUP BY Status' | Out-String)`nDeadlock victims in the API log: $(@(Select-String -Path (Join-Path $LogDir 'OrderFlow.Api.log') -Pattern 'deadlocked on lock resources').Count)" | Out-File (Join-Path $LogDir 'workload.txt')
    $queries = Get-Content -Raw tests/perf/query-plans.sql
    function Run-Script([string]$text, [string]$file) {
        # Called directly, because the Compose function does not forward pipeline input.
        "$text`nGO`nEXIT`n" | docker compose --env-file .env exec -T -e "SQLCMDPASSWORD=$password" sqlserver /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -d $database -y 0 2>&1 | Out-File (Join-Path $LogDir $file)
    }
    Run-Script $queries 'query-stats.txt'
    # SHOWPLAN must be alone in its batch. It compiles the queries without running them.
    Run-Script "SET SHOWPLAN_TEXT ON;`nGO`n$($queries -replace 'SET STATISTICS IO (ON|OFF);', '')" 'query-plans.txt'
}
catch {
    Write-Host "HARNESS FAILURE: $_"
    $exitCode = 1
}
finally {
    foreach ($process in $processes) { if ($process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue } }
    if (-not $KeepDatabase) { Compose exec -T -e "SQLCMDPASSWORD=$password" sqlserver /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -Q "IF DB_ID('$database') IS NOT NULL BEGIN ALTER DATABASE [$database] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$database] END" 2>&1 | Out-Null }
}

Write-Host "Benchmark exit code: $exitCode. Results: $LogDir"
exit $exitCode
