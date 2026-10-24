using SyWater.Devices.Application.Ports.In;
using SyWater.Devices.Application.Ports.Out;
using SyWater.Devices.Application.Valve;
using SyWater.Devices.Domain.Devices;

namespace SyWater.Devices.Application.UseCases;

/// <summary>
/// The user who linked the device may order its valve; so may a collaborator with CONTROL_VALVE (HU-044). valve-service
/// already checked that permission before calling, and it is checked again here with the user's own token because
/// this is the service that touches the hardware.
/// </summary>
public sealed class SendValveCommandUseCase(
    IDeviceRepository devices, IValveCommandSender sender, IPlaceAccessReader access, TimeProvider clock)
    : ISendValveCommandUseCase
{
    public async Task<ValveCommandSent> ExecuteAsync(
        Guid userId, Guid placeId, Guid commandId, ValveAction action, CancellationToken ct)
    {
        var device = await devices.GetByPlaceAsync(placeId, ct);
        if (device is null) throw new DeviceNotLinkedException(placeId);

        if (!device.IsLinkedBy(userId))
        {
            var granted = await access.GetRequesterAccessAsync(placeId, ct);
            if (granted is null || !granted.Has(PlaceAccess.ControlValve))
                throw new DeviceNotLinkedException(placeId);
        }

        var now = clock.GetUtcNow().UtcDateTime;
        if (device.StatusAt(now) != DeviceStatus.Connected)
            throw new DeviceOfflineException(device.SerialNumber);

        await sender.SendAsync(device.SerialNumber, commandId, action, ct);
        return new ValveCommandSent(commandId, device.SerialNumber, now);
    }
}
