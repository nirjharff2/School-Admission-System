using System.Net;
using AdmissionSystem.Api.Domain;
using static AdmissionSystem.Tests.TestSupport;

namespace AdmissionSystem.Tests;

/// <summary>Confirm (pay the fee) and cancel. Each test creates its own class and students.</summary>
[Collection(ApiCollection.Name)]
public class HoldLifecycleTests(ApiFixture api)
{
    private readonly HttpClient _client = api.CreateClient();

    // ---- confirm --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Confirming_returns_200_and_turns_a_held_seat_into_a_confirmed_one()
    {
        var student = NewStudentId();
        var classId = await CreateClassAsync(_client, 5);
        var holdId = await ApplyAsync(_client, classId, student);

        var response = await SendAsync(_client, HttpMethod.Post, $"/holds/{holdId}/confirm", student);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal(["holdId", "status"], body.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal(holdId, body.GetProperty("holdId").GetString());
        Assert.Equal("CONFIRMED", body.GetProperty("status").GetString());
        Assert.Equal("CONFIRMED", await StatusOf(holdId));
        AssertSeatCounts(await GetClassAsync(_client, classId), null, total: 5, held: 0, confirmed: 1, available: 4);
    }

    [Fact]
    public async Task Confirming_twice_returns_200_both_times_and_counts_the_seat_once()
    {
        var student = NewStudentId();
        var classId = await CreateClassAsync(_client, 5);
        var holdId = await ApplyAsync(_client, classId, student);

        var first = await SendAsync(_client, HttpMethod.Post, $"/holds/{holdId}/confirm", student);
        var second = await SendAsync(_client, HttpMethod.Post, $"/holds/{holdId}/confirm", student);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal("CONFIRMED", (await ReadJsonAsync(second)).GetProperty("status").GetString());
        AssertSeatCounts(await GetClassAsync(_client, classId), null, total: 5, held: 0, confirmed: 1, available: 4);
    }

    [Fact]
    public async Task Many_simultaneous_confirms_of_one_hold_all_succeed_and_count_the_seat_once()
    {
        var student = NewStudentId();
        var classId = await CreateClassAsync(_client, 5);
        var holdId = await ApplyAsync(_client, classId, student);

        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            Task.Run(() => SendAsync(_client, HttpMethod.Post, $"/holds/{holdId}/confirm", student))));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        AssertSeatCounts(await GetClassAsync(_client, classId), null, total: 5, held: 0, confirmed: 1, available: 4);
    }

    [Fact]
    public async Task Confirming_after_expiry_returns_409_and_the_seat_stays_available()
    {
        var student = NewStudentId();
        var classId = await CreateClassAsync(_client, 3);
        var lapsed = NewHold(classId, student, HoldStatus.HELD, DateTime.UtcNow.AddSeconds(-1)); // still stored as HELD
        await SeedAsync(lapsed);

        var response = await SendAsync(_client, HttpMethod.Post, $"/holds/{lapsed.Id}/confirm", student);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await ReadJsonAsync(response);
        AssertErrorBody(body);
        Assert.Equal("hold_expired", body.GetProperty("error").GetString());
        Assert.Equal("EXPIRED", await StatusOf(lapsed.Id));
        AssertSeatCounts(await GetClassAsync(_client, classId), null, total: 3, held: 0, confirmed: 0, available: 3);
    }

    [Fact]
    public async Task Confirming_a_cancelled_or_already_expired_hold_returns_409_and_changes_nothing()
    {
        var classId = await CreateClassAsync(_client, 3);
        var cancelled = NewHold(classId, NewStudentId(), HoldStatus.CANCELLED, DateTime.UtcNow.AddMinutes(5));
        var expired = NewHold(classId, NewStudentId(), HoldStatus.EXPIRED, DateTime.UtcNow.AddMinutes(-5));
        await SeedAsync(cancelled, expired);

        var onCancelled = await SendAsync(_client, HttpMethod.Post, $"/holds/{cancelled.Id}/confirm", cancelled.StudentId);
        var onExpired = await SendAsync(_client, HttpMethod.Post, $"/holds/{expired.Id}/confirm", expired.StudentId);

        Assert.Equal(HttpStatusCode.Conflict, onCancelled.StatusCode);
        Assert.Equal("hold_cancelled", (await ReadJsonAsync(onCancelled)).GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.Conflict, onExpired.StatusCode);
        Assert.Equal("hold_expired", (await ReadJsonAsync(onExpired)).GetProperty("error").GetString());
        Assert.Equal("CANCELLED", await StatusOf(cancelled.Id));
        Assert.Equal("EXPIRED", await StatusOf(expired.Id));
        AssertSeatCounts(await GetClassAsync(_client, classId), null, total: 3, held: 0, confirmed: 0, available: 3);
    }

    [Fact]
    public async Task Confirming_another_students_hold_returns_403_and_leaves_it_held()
    {
        var owner = NewStudentId();
        var classId = await CreateClassAsync(_client, 5);
        var holdId = await ApplyAsync(_client, classId, owner);

        var response = await SendAsync(_client, HttpMethod.Post, $"/holds/{holdId}/confirm", NewStudentId());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        AssertErrorBody(await ReadJsonAsync(response));
        Assert.Equal("HELD", await StatusOf(holdId));
        AssertSeatCounts(await GetClassAsync(_client, classId), null, total: 5, held: 1, confirmed: 0, available: 4);
    }

    [Fact]
    public async Task Confirming_an_unknown_hold_returns_404()
    {
        var response = await SendAsync(_client, HttpMethod.Post, "/holds/h_doesnotexist/confirm", NewStudentId());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        AssertErrorBody(await ReadJsonAsync(response));
    }

    // ---- cancel ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Cancelling_returns_204_frees_the_seat_and_lets_the_student_apply_again()
    {
        var student = NewStudentId();
        var classId = await CreateClassAsync(_client, 1);
        var holdId = await ApplyAsync(_client, classId, student);
        AssertSeatCounts(await GetClassAsync(_client, classId), null, total: 1, held: 1, confirmed: 0, available: 0);

        var response = await SendAsync(_client, HttpMethod.Delete, $"/holds/{holdId}", student);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync());
        Assert.Equal("CANCELLED", await StatusOf(holdId));
        AssertSeatCounts(await GetClassAsync(_client, classId), null, total: 1, held: 0, confirmed: 0, available: 1);
        await ApplyAsync(_client, classId, student);
    }

    [Fact]
    public async Task Cancelling_twice_is_idempotent_and_returns_204_both_times()
    {
        var student = NewStudentId();
        var classId = await CreateClassAsync(_client, 2);
        var holdId = await ApplyAsync(_client, classId, student);

        var first = await SendAsync(_client, HttpMethod.Delete, $"/holds/{holdId}", student);
        var second = await SendAsync(_client, HttpMethod.Delete, $"/holds/{holdId}", student);

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
        AssertSeatCounts(await GetClassAsync(_client, classId), null, total: 2, held: 0, confirmed: 0, available: 2);
    }

    [Fact]
    public async Task Cancelling_a_confirmed_hold_is_refused_with_409_and_the_seat_stays_confirmed()
    {
        var student = NewStudentId();
        var classId = await CreateClassAsync(_client, 2);
        var holdId = await ApplyAsync(_client, classId, student);
        await SendAsync(_client, HttpMethod.Post, $"/holds/{holdId}/confirm", student);

        var response = await SendAsync(_client, HttpMethod.Delete, $"/holds/{holdId}", student);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await ReadJsonAsync(response);
        AssertErrorBody(body);
        Assert.Equal("hold_confirmed", body.GetProperty("error").GetString());
        Assert.Equal("CONFIRMED", await StatusOf(holdId));
        AssertSeatCounts(await GetClassAsync(_client, classId), null, total: 2, held: 0, confirmed: 1, available: 1);
    }

    [Fact]
    public async Task Cancelling_an_expired_hold_returns_409()
    {
        var student = NewStudentId();
        var classId = await CreateClassAsync(_client, 2);
        var lapsed = NewHold(classId, student, HoldStatus.HELD, DateTime.UtcNow.AddSeconds(-1));
        await SeedAsync(lapsed);

        var response = await SendAsync(_client, HttpMethod.Delete, $"/holds/{lapsed.Id}", student);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("hold_expired", (await ReadJsonAsync(response)).GetProperty("error").GetString());
        Assert.Equal("EXPIRED", await StatusOf(lapsed.Id));
    }

    [Fact]
    public async Task Cancelling_another_students_hold_returns_403_and_leaves_it_held()
    {
        var owner = NewStudentId();
        var classId = await CreateClassAsync(_client, 5);
        var holdId = await ApplyAsync(_client, classId, owner);

        var response = await SendAsync(_client, HttpMethod.Delete, $"/holds/{holdId}", NewStudentId());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        AssertErrorBody(await ReadJsonAsync(response));
        Assert.Equal("HELD", await StatusOf(holdId));
        AssertSeatCounts(await GetClassAsync(_client, classId), null, total: 5, held: 1, confirmed: 0, available: 4);
    }

    [Fact]
    public async Task Cancelling_an_unknown_hold_returns_404()
    {
        var response = await SendAsync(_client, HttpMethod.Delete, "/holds/h_doesnotexist", NewStudentId());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        AssertErrorBody(await ReadJsonAsync(response));
    }

    // ---- both -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("POST", "/confirm")]
    [InlineData("DELETE", "")]
    public async Task Confirm_and_cancel_require_a_valid_student_id(string method, string suffix)
    {
        var student = NewStudentId();
        var classId = await CreateClassAsync(_client, 2);
        var holdId = await ApplyAsync(_client, classId, student);

        foreach (var header in new string?[] { null, "123", "2015000000000000x" })
        {
            var response = await SendAsync(_client, new HttpMethod(method), $"/holds/{holdId}{suffix}", header);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            AssertErrorBody(await ReadJsonAsync(response));
        }
        Assert.Equal("HELD", await StatusOf(holdId));
    }

    // ---- helpers --------------------------------------------------------------------------------------------

    private async Task SeedAsync(params Hold[] holds) =>
        await api.WithDbAsync(async db =>
        {
            db.Holds.AddRange(holds);
            await db.SaveChangesAsync();
        });

    private async Task<string?> StatusOf(string holdId) =>
        (await ReadJsonAsync(await _client.GetAsync($"/holds/{holdId}"))).GetProperty("status").GetString();
}
