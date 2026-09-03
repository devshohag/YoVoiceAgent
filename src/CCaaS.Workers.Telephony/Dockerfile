# Build context is the repo root - see docker-compose.yml.
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY CCaaS.sln .
COPY src/CCaaS.Workers.Telephony/CCaaS.Workers.Telephony.csproj src/CCaaS.Workers.Telephony/
COPY src/CCaaS.Domain/CCaaS.Domain.csproj src/CCaaS.Domain/
COPY src/CCaaS.Application/CCaaS.Application.csproj src/CCaaS.Application/
COPY src/CCaaS.Infrastructure/CCaaS.Infrastructure.csproj src/CCaaS.Infrastructure/
COPY src/CCaaS.Shared/CCaaS.Shared.csproj src/CCaaS.Shared/
COPY src/CCaaS.Api/CCaaS.Api.csproj src/CCaaS.Api/
COPY src/CCaaS.Workers.Channel/CCaaS.Workers.Channel.csproj src/CCaaS.Workers.Channel/
COPY tests/CCaaS.Tests.Unit/CCaaS.Tests.Unit.csproj tests/CCaaS.Tests.Unit/
RUN dotnet restore src/CCaaS.Workers.Telephony/CCaaS.Workers.Telephony.csproj

COPY src/ src/
RUN dotnet publish src/CCaaS.Workers.Telephony/CCaaS.Workers.Telephony.csproj -c Release -o /app --no-restore

# Plain dotnet/runtime:9.0 is NOT enough here: CCaaS.Infrastructure (referenced by this worker)
# pulls in Hangfire.AspNetCore, Serilog.AspNetCore, OpenTelemetry.Instrumentation.AspNetCore and
# AspNetCore.HealthChecks.* packages, which add an implicit FrameworkReference to
# Microsoft.AspNetCore.App - even though this project itself uses the plain Worker SDK. Running
# the published app against dotnet/runtime:9.0 (Microsoft.NETCore.App only) fails at startup with
# "You must install or update .NET... Framework: 'Microsoft.AspNetCore.App' ... No frameworks
# were found." dotnet/aspnet:9.0 includes both shared frameworks.
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "CCaaS.Workers.Telephony.dll"]
