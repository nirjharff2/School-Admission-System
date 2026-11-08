using System.Net;
using static AdmissionSystem.Tests.TestSupport;

namespace AdmissionSystem.Tests;

/// <summary>
/// The required concurrency tests. They run against the real API and a real PostgreSQL 16 (a Testcontainers container
/// started by <see cref="ApiFixture"/>, so Docker must be running). Every request is created first and released at the
/// same instant, so the requests really are in flight together; student ids are fixed, distinct 17-digit numbers, so a
/// run is reproducible and a refusal can only mean "class full", never "already applied".
/// </summary>
[Collection(ApiCollection.Name)]
public class ConcurrencyTests(ApiFixture api)
{
    private readonly HttpClient _client = api.CreateClient();

    private static string StudentId(int index) => (31_000_000_000_000_000L + index).ToString();

    /// <summary>Starts all <paramref name="count"/> requests behind one gate, opens the gate and returns every response.</summary>
    private static async Task<HttpResponseMessage[]> FireTogetherAsync(int count, Func<int, Task<HttpResponseMessage>> send)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inFlight = Enumerable.Range(0, count).Select(i => Task.Run(async () =>
        {
            await gate.Task;
            return await send(i);
        })).ToArray();
        gate.SetResult();
        return await Task.WhenAll(inFlight);
    }

    [Fact]
    public async Task Two_hundred_simultaneous_applications_for_ten_seats_grant_exactly_ten()
    {
        const int seats = 10;
        const int requests = 200;
        var classId = await CreateClassAsync(_client, seats);

        var responses = await FireTogetherAsync(requests, i =>
            SendAsync(_client, HttpMethod.Post, $"/classes/{classId}/holds", StudentId(i)));

        Assert.Equal(seats, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(requests - seats, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(requests, responses.Count(r => r.StatusCode is HttpStatusCode.Created or HttpStatusCode.Conflict)); // no 5xx
        var granted = await Task.WhenAll(responses.Where(r => r.StatusCode == HttpStatusCode.Created).Select(ReadJsonAsync));
        Assert.Equal(seats, granted.Select(g => g.GetProperty("holdId").GetString()).Distinct().Count());
        Assert.Equal(seats, granted.Select(g => g.GetProperty("studentId").GetString()).Distinct().Count());
        Assert.All(granted, g => Assert.Equal("HELD", g.GetProperty("status").GetString()));
        var refused = await Task.WhenAll(responses.Where(r => r.StatusCode == HttpStatusCode.Conflict).Select(ReadJsonAsync));
        Assert.All(refused, b => Assert.Equal("no_seats_available", b.GetProperty("error").GetString()));
        AssertSeatCounts(await GetClassAsync(_client, classId), null, total: seats, held: seats, confirmed: 0, available: 0);
    }

    [Fact]
    public async Task Simultaneous_confirms_of_one_hold_all_return_200_and_confirm_the_seat_once()
    {
        const int confirms = 50;
        var student = StudentId(0);
        var classId = await CreateClassAsync(_client, 10);
        var holdId = await ApplyAsync(_client, classId, student);

        var responses = await FireTogetherAsync(confirms, _ =>
            SendAsync(_client, HttpMethod.Post, $"/holds/{holdId}/confirm", student));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var bodies = await Task.WhenAll(responses.Select(ReadJsonAsync));
        Assert.All(bodies, b => Assert.Equal("CONFIRMED", b.GetProperty("status").GetString()));
        AssertSeatCounts(await GetClassAsync(_client, classId), null, total: 10, held: 0, confirmed: 1, available: 9);
    }

    [Fact]
    public async Task Two_backend_instances_on_one_database_together_grant_exactly_the_seats_of_the_class()
    {
        // A second host is a second set of services (its own gate, pool and sweeper) on the same PostgreSQL database,
        // so the only thing the two share is the database: the capacity guarantee must come from there.
        const int seats = 10;
        const int requests = 200;
        using var second = api.WithWebHostBuilder(_ => { });
        var clients = new[] { _client, second.CreateClient() };
        var classId = await CreateClassAsync(_client, seats);

        var responses = await FireTogetherAsync(requests, i =>
            SendAsync(clients[i % 2], HttpMethod.Post, $"/classes/{classId}/holds", StudentId(i)));

        Assert.Equal(seats, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(requests - seats, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        foreach (var client in clients)
        {
            AssertSeatCounts(await GetClassAsync(client, classId), null, total: seats, held: seats, confirmed: 0, available: 0);
        }
    }
}
