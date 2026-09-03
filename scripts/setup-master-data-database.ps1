param(
    [string]$MigrationName = "AddMasterDataAndDynamicRouting",
    [string]$ConnectionString = "Server=localhost,1433;Database=CCaaS;User Id=sa;Password=Your_password123!;TrustServerCertificate=True;"
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location $projectRoot
try {
    dotnet tool restore
    dotnet restore CCaaS.sln
    $migrations = dotnet ef migrations list --project src/CCaaS.Infrastructure --startup-project src/CCaaS.Api --context CcaasDbContext --connection $ConnectionString
    if ($migrations -notmatch [regex]::Escape($MigrationName)) {
        dotnet ef migrations add $MigrationName --project src/CCaaS.Infrastructure --startup-project src/CCaaS.Api --context CcaasDbContext
    }
    dotnet ef database update --project src/CCaaS.Infrastructure --startup-project src/CCaaS.Api --context CcaasDbContext --connection $ConnectionString
    Write-Host "Master Data and Dynamic Routing database update completed." -ForegroundColor Green
}
finally { Pop-Location }
