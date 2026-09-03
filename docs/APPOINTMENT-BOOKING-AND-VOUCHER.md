# AI appointment booking and PDF voucher

This vertical slice uses Puter.js only for conversation and tool selection. The approved
`.NET` tool performs the real tenant-scoped SQL Server booking.

## Test conversation

1. `I need an appointment on August 29, 2026.`
2. Select one of the returned times: `Book 14:00.`
3. Provide the requested information: `My name is Anik and my phone is 01XXXXXXXXX.`
4. Confirm when asked: `Yes, confirm the booking.`
5. Use **Download PDF voucher** after the backend returns a booking reference.

The frontend retains the exact `slotId` from the approved availability result between
conversation turns. It never invents or derives a database identifier from the displayed time.

## Security and consistency

- Tenant ID comes from the validated JWT, not from AI arguments or browser input.
- Slots, providers, bookings, and voucher retrieval all use explicit tenant predicates.
- Booking uses a serializable SQL transaction and changes the slot to `Booked` together with
  creation of the booking record.
- The voucher endpoint returns a booking only when it belongs to the authenticated tenant.
- PDF generation uses framework code and introduces no additional NuGet dependency.

## Endpoints

- `GET /api/appointments/availability?date=2026-08-29`
- AI tool: `book_appointment`
- `GET /api/appointments/{bookingId}/voucher`

## Verify in SQL Server

```sql
SELECT TOP 10
    B.BookingReference,
    B.CustomerName,
    B.CustomerContact,
    B.Status AS BookingStatus,
    S.StartsAtUtc,
    S.Status AS SlotStatus,
    B.TenantId
FROM appointment.AppointmentBooking AS B
INNER JOIN appointment.AppointmentAvailabilitySlot AS S
    ON S.Id = B.AvailabilitySlotId
ORDER BY B.ConfirmedAtUtc DESC;
```

`BookingStatus = 0` means confirmed and `SlotStatus = 2` means booked.

## Rebuild

```powershell
docker compose build --no-cache ccaas-api
docker compose up -d --force-recreate ccaas-api

cd frontend
npm run build
npm start
```

No new EF migration is required for this implementation.
