namespace ParkFlow.Modules.Reservation.Application.Reservations;

public sealed record CreateReservationRequest(
    Guid UserId,
    Guid VehicleId,
    Guid ParkingSpotId,
    DateTimeOffset StartTime,
    DateTimeOffset EndTime,
    decimal Price,
    Guid IdempotencyKey);
