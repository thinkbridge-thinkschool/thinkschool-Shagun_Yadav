using Microsoft.EntityFrameworkCore;
using ParkFlow.Modules.Notification.Application;
using ParkFlow.Modules.Notification.Infrastructure;
using ParkFlow.Modules.Parking.Application;
using ParkFlow.Modules.Parking.Infrastructure;
using ParkFlow.Modules.Payment.Application;
using ParkFlow.Modules.Payment.Infrastructure;
using ParkFlow.Modules.Reservation.Application;
using ParkFlow.Modules.Reservation.Infrastructure;
using ParkFlow.Modules.Vehicle.Application;
using ParkFlow.Modules.Vehicle.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

// Each module wires its own Application use cases and Infrastructure (persistence, outbox,
// caching, senders) independently — the composition root only assembles them, it never contains
// business logic itself. Day 22 uses EF Core's InMemory provider so the scaffold runs without a
// real SQL Server instance; swapping to a real provider is a one-line change per module here.
builder.Services
    .AddParkingApplication()
    .AddParkingInfrastructure(db => db.UseInMemoryDatabase("ParkFlow.Parking"))
    .AddReservationApplication()
    .AddReservationInfrastructure(db => db.UseInMemoryDatabase("ParkFlow.Reservation"))
    .AddVehicleApplication()
    .AddVehicleInfrastructure(db => db.UseInMemoryDatabase("ParkFlow.Vehicle"))
    .AddPaymentApplication()
    .AddPaymentInfrastructure(db => db.UseInMemoryDatabase("ParkFlow.Payment"))
    .AddNotificationApplication()
    .AddNotificationInfrastructure(db => db.UseInMemoryDatabase("ParkFlow.Notification"));

var app = builder.Build();

app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.Run();

public partial class Program;
