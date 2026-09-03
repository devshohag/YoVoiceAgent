FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY .config/ .config/
COPY CCaaS.sln .
COPY src/CCaaS.Api/CCaaS.Api.csproj src/CCaaS.Api/
COPY src/CCaaS.Infrastructure/CCaaS.Infrastructure.csproj src/CCaaS.Infrastructure/
COPY src/CCaaS.Application/CCaaS.Application.csproj src/CCaaS.Application/
COPY src/CCaaS.Domain/CCaaS.Domain.csproj src/CCaaS.Domain/
COPY src/CCaaS.Shared/CCaaS.Shared.csproj src/CCaaS.Shared/
RUN dotnet restore src/CCaaS.Api/CCaaS.Api.csproj && dotnet tool restore
COPY src/ src/
RUN dotnet ef migrations bundle \
    --project src/CCaaS.Infrastructure \
    --startup-project src/CCaaS.Api \
    --context CcaasDbContext \
    --configuration Release \
    --self-contained \
    --target-runtime linux-x64 \
    --output /out/ccaas-migrate

FROM mcr.microsoft.com/dotnet/runtime-deps:9.0
WORKDIR /app
COPY --from=build /out/ccaas-migrate /app/ccaas-migrate
USER 65532:65532
ENTRYPOINT ["/app/ccaas-migrate"]
