param(
    [string]$MigrationName = "AddAiVoiceModule"
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot

Push-Location $projectRoot
try {
    dotnet tool restore
    dotnet restore CCaaS.sln
    dotnet ef migrations add $MigrationName `
        --project src/CCaaS.Infrastructure `
        --startup-project src/CCaaS.Api `
        --context CcaasDbContext
    dotnet ef database update `
        --project src/CCaaS.Infrastructure `
        --startup-project src/CCaaS.Api `
        --context CcaasDbContext
}
finally {
    Pop-Location
}
