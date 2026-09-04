using Microsoft.AspNetCore.Mvc;
using ParkFlow.Modules.Vehicle.Application.Vehicles;

namespace ParkFlow.Api.Controllers;

[ApiController]
[Route("api/vehicles")]
public sealed class VehiclesController(VehicleApplicationService vehicles) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Register(RegisterVehicleRequest request, CancellationToken cancellationToken)
    {
        var result = await vehicles.RegisterAsync(request, cancellationToken);
        return result.IsSuccess
            ? CreatedAtAction(nameof(Register), new { id = result.Value }, new { vehicleId = result.Value })
            : BadRequest(new { error = result.Error });
    }
}
