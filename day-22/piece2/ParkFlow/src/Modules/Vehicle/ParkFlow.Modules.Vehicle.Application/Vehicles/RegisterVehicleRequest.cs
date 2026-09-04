namespace ParkFlow.Modules.Vehicle.Application.Vehicles;

using VehicleType = ParkFlow.Modules.Vehicle.Domain.VehicleType;

public sealed record RegisterVehicleRequest(Guid OwnerUserId, string LicensePlate, VehicleType VehicleType);
