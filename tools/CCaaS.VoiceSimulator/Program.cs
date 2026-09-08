using System.Text;
using System.Text.Json;
using CCaaS.Application.Appointment;
using CCaaS.Application.Appointment.Voice;
using CCaaS.VoiceSimulator;

Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = Encoding.UTF8;
var tenant = Guid.NewGuid(); var session = Guid.NewGuid();
var now = DateTimeOffset.Parse("2026-09-08T03:00:00Z");
var zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Dhaka");
var provider = Guid.NewGuid();
var memory = new MemoryAppointments(tenant, new[] {
    new AvailableAppointmentSlot(Guid.NewGuid(), provider, "Demo", new DateTime(2026,9,9,4,0,0,DateTimeKind.Utc), new DateTime(2026,9,9,4,30,0,DateTimeKind.Utc)),
    new AvailableAppointmentSlot(Guid.NewGuid(), provider, "Demo", new DateTime(2026,9,9,10,30,0,DateTimeKind.Utc), new DateTime(2026,9,9,11,0,0,DateTimeKind.Utc)) });
var engine = new AppointmentConversation(memory);
AppointmentConversationState? state = null;
Console.WriteLine("SIMULATOR ONLY — no SQL, no real booking. Fixed date 2026-09-08, Asia/Dhaka.");
Console.WriteLine("Tomorrow's demo slots: 10:00 AM and 4:30 PM. /race fails next write; /new starts another session; /quit exits.");
for (var turn = 1; ; turn++)
{
    var input = Console.ReadLine(); if (input is null || input == "/quit") break;
    if (input == "/race") { memory.FailNextWrite = true; continue; }
    if (input == "/new") { state = null; session = Guid.NewGuid(); continue; }
    var response = await engine.StepAsync(tenant, session, state, turn.ToString(), input, now, zone);
    state = response.State;
    Console.WriteLine(response.Text);
    Console.WriteLine(JsonSerializer.Serialize(new { response.Action, state.Stage, state.Date, state.Time, memory.Mutations }));
}
