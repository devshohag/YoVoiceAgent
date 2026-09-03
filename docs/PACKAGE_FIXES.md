# Package restore fix

Corrected the package versions reported by `dotnet restore`:

- `System.IdentityModel.Tokens.Jwt`: `9.0.10` -> `8.22.0`
- `AspNetCore.HealthChecks.Redis`: `9.0.10` -> `9.0.0`
- `OpenTelemetry.Exporter.OpenTelemetryProtocol`: `1.9.0` -> `1.18.0`

The first two failures were caused by package versions that do not exist on NuGet. The
OpenTelemetry update addresses the `NU1902` warning raised for version `1.9.0`.

After replacing the previous project folder, run:

```powershell
dotnet nuget locals all --clear
dotnet restore CCaaS.sln --force
dotnet build CCaaS.sln
dotnet test CCaaS.sln
```
