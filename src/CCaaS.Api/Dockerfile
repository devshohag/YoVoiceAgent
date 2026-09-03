# Build context is the repo root (see docker-compose.yml `build.context: .`) because this
# project references sibling projects (Domain/Application/Infrastructure/Shared) that live
# outside src/CCaaS.Api/.
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY CCaaS.sln .
COPY src/CCaaS.Api/CCaaS.Api.csproj src/CCaaS.Api/
COPY src/CCaaS.Domain/CCaaS.Domain.csproj src/CCaaS.Domain/
COPY src/CCaaS.Application/CCaaS.Application.csproj src/CCaaS.Application/
COPY src/CCaaS.Infrastructure/CCaaS.Infrastructure.csproj src/CCaaS.Infrastructure/
COPY src/CCaaS.Shared/CCaaS.Shared.csproj src/CCaaS.Shared/
COPY src/CCaaS.Workers.Telephony/CCaaS.Workers.Telephony.csproj src/CCaaS.Workers.Telephony/
COPY src/CCaaS.Workers.Channel/CCaaS.Workers.Channel.csproj src/CCaaS.Workers.Channel/
COPY tests/CCaaS.Tests.Unit/CCaaS.Tests.Unit.csproj tests/CCaaS.Tests.Unit/
RUN dotnet restore src/CCaaS.Api/CCaaS.Api.csproj

COPY src/ src/
RUN dotnet publish src/CCaaS.Api/CCaaS.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "CCaaS.Api.dll"]
