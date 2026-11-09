using System.Net;
using AdmissionSystem.Api.Domain;
using AdmissionSystem.Api.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using static AdmissionSystem.Tests.TestSupport;

namespace AdmissionSystem.Tests;

/// <summary>
/// The sweeper runs for real here: the API is configured with HOLD_DURATION_SECONDS=1 and a 1-second sweep interval.
/// Stored status is always read straight from the database, because API reads compute EXPIRED on their own and would
/// hide whether the sweeper did anything.
/// </summary>
[Collection(SweeperCollection.Name)]
public class ExpirySweeperTests(SweeperFixture api)
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly HttpClient _client = api.CreateClient();

    [Fact]
    public async Task A_hold_that_runs_out_is_stored_as_expired_without_any_api_call_and_its_seat_is_back()
    {
        var classId = await CreateClassAsync(_client, 2);
        var holdId = await ApplyAsync(_client, classId, NewStudentId()); // the one and only API call about this hold

        await WaitUntilAsync(async () => await StoredStatus(holdId) == HoldStatus.EXPIRED);

        AssertSeatCounts(await GetClassAsync(_client, classId), null, total: 2, held: 0, confirmed: 0, available: 2);
    }

    [Fact]
    public async Task The_sweeper_expires_only_lapsed_held_holds()
    {
        var classId = await CreateClassAsync(_client, 10);
        var now = DateTime.UtcNow;
        var lapsed = NewHold(classId, NewStudentId(), HoldStatus.HELD, now.AddSeconds(-1));
        var live = NewHold(classId, NewStudentId(), HoldStatus.HELD, now.AddMinutes(10));
        var confirmed = NewHold(classId, NewStudentId(), HoldStatus.CONFIRMED, now.AddSeconds(-1)); // paid; expiry no longer applies
        var cancelled = NewHold(classId, NewStudentId(), HoldStatus.CANCELLED, now.AddSeconds(-1));
        await api.WithDbAsync(async db =>
        {
            db.Holds.AddRange(lapsed, live, confirmed, cancelled);
            await db.SaveChangesAsync();
        });

        await WaitUntilAsync(async () => await StoredStatus(lapsed.Id) == HoldStatus.EXPIRED);
        await Task.Delay(TimeSpan.FromSeconds(2.5)); // at least two more sweeps; a wrong predicate would have hit the others by now

        Assert.Equal(HoldStatus.HELD, await StoredStatus(live.Id));
        Assert.Equal(HoldStatus.CONFIRMED, await StoredStatus(confirmed.Id));
        Assert.Equal(HoldStatus.CANCELLED, await StoredStatus(cancelled.Id));
        AssertSeatCounts(await GetClassAsync(_client, classId), null, total: 10, held: 1, confirmed: 1, available: 8);
    }

    [Fact]
    public async Task Two_backend_instances_sweeping_the_same_database_expire_every_lapsed_hold_exactly_once()
    {
        var classId = await CreateClassAsync(_client, 100);
        var lapsed = Enumerable.Range(0, 60)
            .Select(_ => NewHold(classId, NewStudentId(), HoldStatus.HELD, DateTime.UtcNow.AddSeconds(-1)))
            .ToList();
        await api.WithDbAsync(async db =>
        {
            db.Holds.AddRange(lapsed);
            await db.SaveChangesAsync();
        });

        using var secondInstance = api.WithWebHostBuilder(_ => { });
        _ = secondInstance.Server; // starts the second host, and with it a second sweeper on the same database

        var ids = lapsed.Select(h => h.Id).ToList();
        await WaitUntilAsync(async () => await StoredStatuses(ids) is [HoldStatus.EXPIRED]);
        AssertSeatCounts(await GetClassAsync(_client, classId), null, total: 100, held: 0, confirmed: 0, available: 100);
    }

    [Fact]
    public async Task Applications_and_confirms_spread_over_two_instances_while_holds_keep_expiring_never_overbook_or_fail()
    {
        // Two processes' worth of everything: each instance has its own sweeper (1 s), its own gate and its own pool, and
        // the 1-second holds keep lapsing under the traffic, so seats are released and re-granted all the time.
        const int seats = 10;
        const int maxConfirms = 3;
        using var secondInstance = api.WithWebHostBuilder(_ => { });
        var clients = new[] { _client, secondInstance.CreateClient() };
        var classId = await CreateClassAsync(_client, seats);

        var statuses = new List<HttpStatusCode>();
        var observed = new List<(int Held, int Confirmed)>();
        var granted = 0;
        var confirmed = 0;
        var stop = DateTime.UtcNow.AddSeconds(7);
        for (var round = 0; DateTime.UtcNow < stop; round++)
        {
            var applications = await Task.WhenAll(Enumerable.Range(0, 40).Select(i => Task.Run(async () =>
            {
                var student = NewStudentId();
                var response = await SendAsync(clients[i % 2], HttpMethod.Post, $"/classes/{classId}/holds", student);
                var holdId = response.StatusCode == HttpStatusCode.Created
                    ? (await ReadJsonAsync(response)).GetProperty("holdId").GetString()
                    : null;
                return (response.StatusCode, holdId, student);
            })));
            statuses.AddRange(applications.Select(a => a.StatusCode));
            granted += applications.Count(a => a.StatusCode == HttpStatusCode.Created);

            // pay for one granted hold, alternating the instance from round to round, a few times in total
            var payable = applications.FirstOrDefault(a => a.holdId is not null);
            if (payable.holdId is not null && confirmed < maxConfirms && round % 2 == 0)
            {
                var pay = await SendAsync(clients[(round + 1) % 2], HttpMethod.Post, $"/holds/{payable.holdId}/confirm", payable.student);
                statuses.Add(pay.StatusCode);
                if (pay.StatusCode == HttpStatusCode.OK) confirmed++;
            }

            foreach (var client in clients)
            {
                var counts = await GetClassAsync(client, classId);
                observed.Add((counts.GetProperty("heldSeats").GetInt32(), counts.GetProperty("confirmedSeats").GetInt32()));
            }
        }

        Assert.DoesNotContain(statuses, s => (int)s >= 500);
        Assert.All(observed, o => Assert.True(o.Held + o.Confirmed <= seats, $"held {o.Held} + confirmed {o.Confirmed} > {seats}"));
        Assert.True(granted > seats, $"only {granted} applications were granted: expired holds did not free seats"); // seats were re-granted

        await WaitUntilAsync(async () => (await GetClassAsync(clients[0], classId)).GetProperty("heldSeats").GetInt32() == 0);
        foreach (var client in clients)
        {
            AssertSeatCounts(await GetClassAsync(client, classId), null, total: seats, held: 0, confirmed: confirmed, available: seats - confirmed);
        }
    }

    // ---- settings -------------------------------------------------------------------------------------------

    [Fact]
    public void Sweep_interval_defaults_to_at_most_five_seconds_and_is_configurable()
    {
        Assert.InRange(SweeperSettings.From(Config()).Interval, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromSeconds(7), SweeperSettings.From(Config((SweeperSettings.ConfigKey, "7"))).Interval);
    }

    [Fact]
    public void Hold_duration_defaults_to_600_seconds_and_is_configurable()
    {
        Assert.Equal(TimeSpan.FromSeconds(600), HoldSettings.From(Config()).Duration);
        Assert.Equal(TimeSpan.FromSeconds(1), HoldSettings.From(Config((HoldSettings.ConfigKey, "1"))).Duration);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("1.5")]
    [InlineData("10s")]
    public void Invalid_durations_and_intervals_are_rejected_instead_of_silently_defaulted(string value)
    {
        Assert.Throws<InvalidOperationException>(() => SweeperSettings.From(Config((SweeperSettings.ConfigKey, value))));
        Assert.Throws<InvalidOperationException>(() => HoldSettings.From(Config((HoldSettings.ConfigKey, value))));
    }

    // ---- helpers --------------------------------------------------------------------------------------------

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    private async Task<HoldStatus> StoredStatus(string holdId)
    {
        HoldStatus status = default;
        await api.WithDbAsync(async db =>
            status = await db.Holds.AsNoTracking().Where(h => h.Id == holdId).Select(h => h.Status).SingleAsync());
        return status;
    }

    /// <summary>The distinct stored statuses of the given holds, so "all expired" is <c>[EXPIRED]</c>.</summary>
    private async Task<HoldStatus[]> StoredStatuses(List<string> holdIds)
    {
        HoldStatus[] statuses = [];
        await api.WithDbAsync(async db =>
            statuses = await db.Holds.AsNoTracking().Where(h => holdIds.Contains(h.Id)).Select(h => h.Status).Distinct().ToArrayAsync());
        return statuses;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Condition not met within {Patience.TotalSeconds:0}s");
            await Task.Delay(200);
        }
    }
}
