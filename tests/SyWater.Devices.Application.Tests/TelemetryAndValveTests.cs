using SyWater.Devices.Application.Events;
using SyWater.Devices.Application.Ports.Out;
using SyWater.Devices.Application.Telemetry;
using SyWater.Devices.Application.UseCases;
using SyWater.Devices.Application.Valve;
using SyWater.Devices.Domain.Devices;

namespace SyWater.Devices.Application.Tests;

/// <summary>Readings (HU-012/015), valve state (HU-019) and the valve bridge (HU-020/021).</summary>
public class TelemetryAndValveTests
{
    private readonly FakeDeviceRepository _devices = new();
    private readonly FakeEventPublisher _events = new();
    private readonly FakeValveCommandSender _sender = new();
    private readonly FakeClock _clock = new(TestDevices.Now);
    private readonly Guid _placeId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();

    private Device AddLinked(DateTime? lastReportAt = null)
    {
        var device = TestDevices.New(lastReportAt: lastReportAt ?? TestDevices.Now.AddSeconds(-20), placeId: _placeId, linkedBy: _userId);
        _devices.Devices.Add(device);
        return device;
    }

    private static TelemetryMessage Reading(string token = TestDevices.Token, decimal lpm = 2m, decimal vol = 0.333m,
        DateTime? at = null) =>
        new(TestDevices.Serial, token, at ?? TestDevices.Now.AddSeconds(-5), lpm, vol, 12.5m);

    private RecordTelemetryUseCase Telemetry() => new(_devices, _events, _clock);

    // ── Readings ─────────────────────────────────────────────────────────

    [Fact]
    public async Task A_valid_reading_of_a_linked_device_becomes_an_event_for_its_place()
    {
        var device = AddLinked();

        var outcome = await Telemetry().ExecuteAsync(Reading(), default);

        Assert.True(outcome.IsAccepted);
        var reading = Assert.IsType<ReadingReceived>(Assert.Single(_events.Published));
        Assert.Equal((device.Id, _placeId, 2m, 0.333m), (reading.DeviceId, reading.PlaceId, reading.FlowLpm, reading.VolumeLiters));
        Assert.Equal(TestDevices.Now, device.LastReportAt);   // a reading also proves the device is online
    }

    [Fact]
    public async Task A_reading_with_a_wrong_token_is_rejected_and_publishes_nothing()
    {
        AddLinked();
        Assert.Equal(MessageOutcome.InvalidToken, await Telemetry().ExecuteAsync(Reading(token: "fake"), default));
        Assert.Empty(_events.Published);
    }

    [Fact]
    public async Task An_unlinked_device_stops_sending_data_to_the_place_immediately()
    {
        _devices.Devices.Add(TestDevices.New(lastReportAt: TestDevices.Now));   // not linked
        Assert.Equal(MessageOutcome.NotLinked, await Telemetry().ExecuteAsync(Reading(), default));
        Assert.Empty(_events.Published);
    }

    [Theory]
    [InlineData(-1, 0.1)]   // negative flow
    [InlineData(2, -0.1)]   // negative volume
    [InlineData(61, 1)]     // impossible flow for the sensor
    [InlineData(2, 101)]    // impossible volume for 10 s
    public async Task Impossible_values_are_rejected(decimal lpm, decimal vol)
    {
        AddLinked();
        var outcome = await Telemetry().ExecuteAsync(Reading(lpm: lpm, vol: vol), default);
        Assert.Equal("Rejected", outcome.Code);
        Assert.Empty(_events.Published);
    }

    [Fact]
    public async Task Readings_from_the_future_or_older_than_a_day_are_rejected()
    {
        AddLinked();
        Assert.Equal("Rejected", (await Telemetry().ExecuteAsync(Reading(at: TestDevices.Now.AddMinutes(10)), default)).Code);
        Assert.Equal("Rejected", (await Telemetry().ExecuteAsync(Reading(at: TestDevices.Now.AddHours(-25)), default)).Code);
        Assert.True((await Telemetry().ExecuteAsync(Reading(at: TestDevices.Now.AddMinutes(-50)), default)).IsAccepted); // buffered while offline
    }

    [Fact]
    public void Telemetry_payload_is_parsed()
    {
        Assert.True(TelemetryMessage.TryParse(TestDevices.Serial,
            "{\"token\": \"t\", \"ts\": 1790607600, \"lpm\": 2.0, \"vol\": 0.333, \"total\": 12.5}", out var m));
        Assert.Equal(new DateTime(2026, 9, 28, 15, 0, 0, DateTimeKind.Utc), m!.RecordedAt);
        Assert.Equal(0.333m, m.VolumeLiters);
        Assert.False(TelemetryMessage.TryParse(TestDevices.Serial, "{\"token\":\"t\",\"ts\":\"x\"}", out _));
        Assert.False(TelemetryMessage.TryParse(TestDevices.Serial, "not json", out _));
    }

    [Theory]
    [InlineData("sywater/devices/sw-1/telemetry", true, "SW-1", "telemetry")]
    [InlineData("sywater/devices/SW-1/valve/state", true, "SW-1", "valve/state")]
    [InlineData("sywater/devices/SW-1/heartbeat", true, "SW-1", "heartbeat")]
    [InlineData("sywater/devices/SW-1/valve/set", false, "", "")]   // our own orders are not inputs
    [InlineData("other/SW-1/telemetry", false, "", "")]
    public void Topics_are_routed_by_kind(string topic, bool ok, string serial, string kind)
    {
        Assert.Equal(ok, DeviceTopics.TryParse(topic, out var s, out var k));
        if (ok) Assert.Equal((serial, kind), (s, k));
    }

    // ── Valve state ──────────────────────────────────────────────────────

    [Fact]
    public async Task The_valve_state_of_a_linked_device_is_published_with_the_command_it_obeyed()
    {
        var device = AddLinked();
        var commandId = Guid.NewGuid();
        Assert.True(ValveStateMessage.TryParse(TestDevices.Serial,
            $"{{\"token\":\"{TestDevices.Token}\",\"state\":\"closed\",\"commandId\":\"{commandId}\",\"ts\":1790607600}}", out var msg));

        var outcome = await new RecordValveStateUseCase(_devices, _events, _clock).ExecuteAsync(msg!, default);

        Assert.True(outcome.IsAccepted);
        var reported = Assert.IsType<ValveReported>(Assert.Single(_events.Published));
        Assert.Equal((device.Id, _placeId, "CLOSED", (Guid?)commandId), (reported.DeviceId, reported.PlaceId, reported.State, reported.CommandId));
    }

    [Fact]
    public void Valve_state_must_be_OPEN_or_CLOSED() =>
        Assert.False(ValveStateMessage.TryParse(TestDevices.Serial, "{\"token\":\"t\",\"state\":\"HALF\"}", out _));

    // ── Valve orders (bridge for valve-service) ──────────────────────────

    private readonly FakePlaceAccessReader _access = new();
    private SendValveCommandUseCase Send() => new(_devices, _sender, _access, _clock);

    [Fact]
    public async Task An_order_goes_to_the_device_of_the_place()
    {
        AddLinked();
        var commandId = Guid.NewGuid();

        var sent = await Send().ExecuteAsync(_userId, _placeId, commandId, ValveAction.Close, default);

        Assert.Equal((TestDevices.Serial, commandId, ValveAction.Close), Assert.Single(_sender.Sent));
        Assert.Equal(TestDevices.Serial, sent.SerialNumber);
    }

    [Fact]
    public async Task Nobody_can_order_the_valve_of_someone_elses_device() =>
        await Assert.ThrowsAsync<DeviceNotLinkedException>(() =>
            AddLinkedThen(() => Send().ExecuteAsync(Guid.NewGuid(), _placeId, Guid.NewGuid(), ValveAction.Close, default)));

    [Fact]
    public async Task An_offline_device_cannot_receive_orders()
    {
        AddLinked(lastReportAt: TestDevices.Now.AddMinutes(-30));
        await Assert.ThrowsAsync<DeviceOfflineException>(() =>
            Send().ExecuteAsync(_userId, _placeId, Guid.NewGuid(), ValveAction.Open, default));
        Assert.Empty(_sender.Sent);
    }

    [Fact]
    public async Task Broker_down_is_reported_as_unavailable()
    {
        AddLinked();
        _sender.BrokerDown = true;
        await Assert.ThrowsAsync<MessagingUnavailableException>(() =>
            Send().ExecuteAsync(_userId, _placeId, Guid.NewGuid(), ValveAction.Open, default));
    }

    [Fact]
    public void Order_payload_matches_the_firmware()
    {
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");
        Assert.Equal("{\"commandId\":\"11111111-2222-3333-4444-555555555555\",\"action\":\"CLOSE\"}",
            ValveCommandPayload.Build(id, ValveAction.Close));
        Assert.Equal("sywater/devices/SW-1/valve/set", DeviceTopics.ValveSetFor("SW-1"));
    }

    private Task AddLinkedThen(Func<Task> action)
    {
        AddLinked();
        return action();
    }
}
