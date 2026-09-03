# SQL-backed Appointment Module

The AI tools now query and update SQL Server through `IAppointmentService`. Puter receives only structured tool results and never database access.

## Database setup on Windows

Run SQL Server first, then apply the new EF Core migration from PowerShell:

```powershell
docker compose up -d sqlserver
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
Get-ChildItem -Path . -Recurse -File | Unblock-File
.\scripts\setup-appointment-database.ps1
```

The script uses `localhost,1433` because it runs on Windows. Containers continue using the Compose hostname `sqlserver`.

After migration, rebuild the API. Development startup seeds one provider and three 30-minute slots per day for the next 14 days. Availability is then read from `appointment.AppointmentAvailabilitySlot`; confirmed bookings are stored in `appointment.AppointmentBooking` and the selected slot becomes `Booked`.

```powershell
docker compose up -d --build ccaas-api
docker compose logs ccaas-api --tail 100
```

## Verification

Use `GET /api/appointments/availability?date=YYYY-MM-DD` and `GET /api/appointments/bookings`, or query the three `appointment` schema tables in SSMS.
