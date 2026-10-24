using SyWater.Devices.Application.Devices;
using SyWater.Devices.Application.Heartbeats;
using SyWater.Devices.Application.Ports.Out;
using SyWater.Devices.Application.UseCases;
using SyWater.Devices.Domain.Devices;

namespace SyWater.Devices.Application.Tests;

public class DeviceUseCasesTests
{
    private readonly FakeDeviceRepository _devices = new();
    private readonly FakePlaceOwnershipChecker _places = new();
    private readonly FakeClock _clock = new(TestDevices.Now);
    private readonly FakeEventPublisher _events = new();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _placeId = Guid.NewGuid();

    public DeviceUseCasesTests() => _places.OwnedPlaces.Add(_placeId);

    private LinkDeviceUseCase Link(DevicePolicy? policy = null) =>
        new(_devices, _places, policy ?? DevicePolicy.Default, _events, _clock);

    private LinkDeviceCommand Command(string serial = TestDevices.Serial, string code = TestDevices.Code) =>
        new(_userId, _placeId, serial, code);

    private Device AddOnlineDevice(string serial = TestDevices.Serial)
    {
        var device = TestDevices.New(serial, lastReportAt: TestDevices.Now.AddSeconds(-20));
        _devices.Devices.Add(device);
        return device;
    }

    // ── HU-012 ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Link_happy_path_links_and_writes_history()
    {
        var device = AddOnlineDevice();

        var view = await Link().ExecuteAsync(Command(serial: " sw-esp32-000001 ", code: "4hzx vcdp"), default);

        Assert.Equal(device.Id, view.Id);
        Assert.Equal(_placeId, view.PlaceId);
        Assert.Equal(DeviceStatus.Connected, view.Status);
        var link = Assert.Single(_devices.Links);
        Assert.Equal(_userId, link.LinkedBy);
        var linked = Assert.IsType<SyWater.Devices.Application.Events.DeviceLinked>(Assert.Single(_events.Published));
        Assert.Equal((device.Id, _placeId, _userId), (linked.DeviceId, linked.PlaceId, linked.UserId));
    }

    [Fact]
    public async Task Link_to_a_place_of_someone_else_is_404_and_does_not_touch_the_device()
    {
        AddOnlineDevice();
        var command = Command() with { PlaceId = Guid.NewGuid() };

        await Assert.ThrowsAsync<PlaceNotFoundException>(() => Link().ExecuteAsync(command, default));
        Assert.Equal(0, _devices.Updates);
    }

    [Fact]
    public async Task Link_when_places_service_is_down_bubbles_up()
    {
        _places.IsDown = true;
        await Assert.ThrowsAsync<ExternalServiceUnavailableException>(() => Link().ExecuteAsync(Command(), default));
    }

    [Fact]
    public async Task Link_to_a_place_that_already_has_another_device_is_rejected()
    {
        _devices.Devices.Add(TestDevices.New("SW-ESP32-000002", TestDevices.Now, placeId: _placeId, linkedBy: _userId));
        AddOnlineDevice();

        await Assert.ThrowsAsync<PlaceAlreadyHasDeviceException>(() => Link().ExecuteAsync(Command(), default));
    }

    [Fact]
    public async Task Link_the_same_device_twice_says_already_linked()
    {
        _devices.Devices.Add(TestDevices.New(lastReportAt: TestDevices.Now, placeId: _placeId, linkedBy: _userId));

        await Assert.ThrowsAsync<DeviceAlreadyLinkedException>(() => Link().ExecuteAsync(Command(), default));
    }

    [Fact]
    public async Task Link_with_an_unknown_serial_gives_the_generic_pairing_error() =>
        await Assert.ThrowsAsync<PairingFailedException>(() => Link().ExecuteAsync(Command(), default));

    [Fact]
    public async Task Link_with_a_wrong_code_saves_the_failed_attempt()
    {
        var device = AddOnlineDevice();

        await Assert.ThrowsAsync<PairingFailedException>(() =>
            Link().ExecuteAsync(Command(code: "AAAA-AAAA"), default));

        Assert.Equal(1, device.PairingFailedAttempts);
        Assert.Equal(1, _devices.Updates);
        Assert.False(device.IsLinked);
    }

    [Fact]
    public async Task Link_after_too_many_wrong_codes_is_locked_even_with_the_right_code()
    {
        AddOnlineDevice();
        var policy = DevicePolicy.Default;
        for (var i = 0; i < policy.Pairing.MaxFailedAttempts; i++)
            await Assert.ThrowsAsync<PairingFailedException>(() =>
                Link(policy).ExecuteAsync(Command(code: "AAAA-AAAA"), default));

        await Assert.ThrowsAsync<PairingLockedException>(() => Link(policy).ExecuteAsync(Command(), default));

        _clock.UtcNow = TestDevices.Now.AddMinutes(16);       // lock expired (the device keeps reporting)
        _devices.Devices[0].RecordHeartbeat(TestDevices.Token, null, _clock.UtcNow);
        var view = await Link(policy).ExecuteAsync(Command(), default);
        Assert.Equal(_placeId, view.PlaceId);
    }

    [Fact]
    public async Task Link_of_an_offline_device_is_rejected_by_default()
    {
        _devices.Devices.Add(TestDevices.New());   // never reported

        await Assert.ThrowsAsync<DeviceOfflineException>(() => Link().ExecuteAsync(Command(), default));
    }

    [Fact]
    public async Task Link_of_an_offline_device_is_allowed_when_the_policy_says_so()
    {
        _devices.Devices.Add(TestDevices.New());
        var policy = DevicePolicy.Default with { RequireOnlineToLink = false };

        var view = await Link(policy).ExecuteAsync(Command(), default);

        Assert.Equal(DeviceStatus.NeverReported, view.Status);
    }

    [Fact]
    public async Task Link_with_a_bad_code_format_fails_before_any_io()
    {
        _places.IsDown = true; // would throw if the use case called ms-places

        await Assert.ThrowsAsync<InvalidDeviceDataException>(() => Link().ExecuteAsync(Command(code: "123"), default));
    }

    // ── HU-013 ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_returns_the_calculated_status()
    {
        _devices.Devices.Add(TestDevices.New(lastReportAt: TestDevices.Now.AddMinutes(-11), placeId: _placeId, linkedBy: _userId));

        var view = await new GetPlaceDeviceUseCase(_devices, new FakePlaceAccessReader(), _clock).ExecuteAsync(_userId, _placeId, default);

        Assert.Equal(DeviceStatus.Disconnected, view.Status); // the column still says CONNECTED
    }

    [Fact]
    public async Task Get_of_another_users_device_looks_like_no_device()
    {
        _devices.Devices.Add(TestDevices.New(lastReportAt: TestDevices.Now, placeId: _placeId, linkedBy: Guid.NewGuid()));

        await Assert.ThrowsAsync<DeviceNotLinkedException>(() =>
            new GetPlaceDeviceUseCase(_devices, new FakePlaceAccessReader(), _clock).ExecuteAsync(_userId, _placeId, default));
    }

    [Fact]
    public async Task Refresh_counts_devices_that_stopped_reporting()
    {
        _devices.Devices.Add(TestDevices.New("SW-ESP32-000001", TestDevices.Now.AddMinutes(-30)));
        _devices.Devices.Add(TestDevices.New("SW-ESP32-000002", TestDevices.Now.AddMinutes(-1)));

        Assert.Equal(1, await new RefreshDeviceStatusUseCase(_devices, _clock).ExecuteAsync(default));
    }

    // ── HU-014 ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Unlink_frees_the_device_and_closes_the_history()
    {
        var device = TestDevices.New(lastReportAt: TestDevices.Now, placeId: _placeId, linkedBy: _userId);
        _devices.Devices.Add(device);

        await new UnlinkDeviceUseCase(_devices, _events, _clock).ExecuteAsync(_userId, _placeId, default);

        Assert.False(device.IsLinked);
        var unlink = Assert.Single(_devices.Unlinks);
        Assert.Equal((device.Id, _userId, TestDevices.Now), unlink);
        var unlinked = Assert.IsType<SyWater.Devices.Application.Events.DeviceUnlinked>(Assert.Single(_events.Published));
        Assert.Equal(_placeId, unlinked.PlaceId);
    }

    [Fact]
    public async Task Unlink_without_device_is_404() =>
        await Assert.ThrowsAsync<DeviceNotLinkedException>(() =>
            new UnlinkDeviceUseCase(_devices, _events, _clock).ExecuteAsync(_userId, _placeId, default));

    [Fact]
    public async Task After_unlinking_the_device_can_be_linked_again()
    {
        var device = TestDevices.New(lastReportAt: TestDevices.Now, placeId: _placeId, linkedBy: _userId);
        _devices.Devices.Add(device);
        await new UnlinkDeviceUseCase(_devices, _events, _clock).ExecuteAsync(_userId, _placeId, default);

        var view = await Link().ExecuteAsync(Command(), default);

        Assert.Equal(_placeId, view.PlaceId);
    }

    // ── Heartbeats ───────────────────────────────────────────────────────

    [Fact]
    public async Task Heartbeat_of_an_unknown_serial_is_ignored() =>
        Assert.Equal(HeartbeatOutcome.UnknownDevice,
            await new RecordHeartbeatUseCase(_devices, _clock)
                .ExecuteAsync(new HeartbeatMessage("SW-ESP32-999999", "x", null), default));

    [Fact]
    public async Task Heartbeat_with_the_right_token_is_saved()
    {
        var device = TestDevices.New();
        _devices.Devices.Add(device);

        var outcome = await new RecordHeartbeatUseCase(_devices, _clock)
            .ExecuteAsync(new HeartbeatMessage(TestDevices.Serial, TestDevices.Token, "1.0.0"), default);

        Assert.Equal(HeartbeatOutcome.Accepted, outcome);
        Assert.Equal(1, _devices.Updates);
        Assert.Equal(TestDevices.Now, device.LastReportAt);
    }

    [Fact]
    public async Task Heartbeat_with_a_wrong_token_is_not_saved()
    {
        _devices.Devices.Add(TestDevices.New());

        var outcome = await new RecordHeartbeatUseCase(_devices, _clock)
            .ExecuteAsync(new HeartbeatMessage(TestDevices.Serial, "stolen?", null), default);

        Assert.Equal(HeartbeatOutcome.InvalidToken, outcome);
        Assert.Equal(0, _devices.Updates);
    }
}
