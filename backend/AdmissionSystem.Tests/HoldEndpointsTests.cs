using System.Net;
using System.Text.RegularExpressions;
using AdmissionSystem.Api.Domain;
using Microsoft.EntityFrameworkCore;
using static AdmissionSystem.Tests.TestSupport;

namespace AdmissionSystem.Tests;

[Collection(ApiCollection.Name)]
public partial class HoldEndpointsTests(ApiFixture api)
{
    private const string Alice = "20150000000000001";
    private const string Bob = "20150000000000002";
    private const string Carol = "20150000000000003";

    private readonly HttpClient _client = api.CreateClient();

    // ---- X-Student-Id handling -------------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("2015000000000001")]       // 16 digits
    [InlineData("201500000000000012")]     // 18 digits
    [InlineData("2015000000000000a")]      // letter
    [InlineData("2015 000000000000")]      // inner space
    [InlineData("-2015000000000000")]      // sign
    [InlineData("２０１５００００００００００００１")] // 17 full-width digits: digits, but not 0-9
    public async Task Missing_or_malformed_student_id_is_rejected_with_400_and_a_readable_message(string? studentId)
    {
        var classId = await CreateClassAsync(_client, 5);

        var apply = await Send(HttpMethod.Post, $"/classes/{classId}/holds", studentId);
        var mine = await Send(HttpMethod.Get, "/me/holds", studentId);

        foreach (var response in new[] { apply, mine })
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var body = await ReadJsonAsync(response);
            AssertErrorBody(body);
            Assert.Contains("17", body.GetProperty("message").GetString());
        }
        AssertSeatCounts(await GetClassAsync(_client, classId), null, total: 5, held: 0, confirmed: 0, available: 5);
    }

    [Fact]
    public async Task Student_id_is_validated_before_the_class_is_looked_up()
    {
        var response = await Send(HttpMethod.Post, "/classes/c_doesnotexist/holds", "123");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- POST /classes/{classId}/holds ----------------------------------------------------------------------

    [Fact]
    public async Task Applying_creates_a_held_hold_and_decrements_available_seats()
    {
        var classId = await CreateClassAsync(_client, 5);
        var before = DateTime.UtcNow;

        var response = await Send(HttpMethod.Post, $"/classes/{classId}/holds", Alice);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var hold = await ReadJsonAsync(response);
        Assert.Equal(
            ["classId", "expiresAt", "holdId", "status", "studentId"],
            hold.EnumerateObject().Select(p => p.Name).Order().ToArray());
        var holdId = hold.GetProperty("holdId").GetString()!;
        Assert.StartsWith("h_", holdId);
        Assert.Equal(classId, hold.GetProperty("classId").GetString());
        Assert.Equal(Alice, hold.GetProperty("studentId").GetString());
        Assert.Equal("HELD", hold.GetProperty("status").GetString());
        Assert.Equal($"/holds/{holdId}", response.Headers.Location?.ToString());

        AssertSeatCounts(await GetClassAsync(_client, classId), null, total: 5, held: 1, confirmed: 0, available: 4);

        // expiresAt: ISO-8601 UTC, whole seconds, trailing Z, exactly createdAt + HOLD_DURATION_SECONDS.
        var expiresAtText = hold.GetProperty("expiresAt").GetString()!;
        Assert.Matches(Iso8601UtcSeconds(), expiresAtText);
        var expiresAt = DateTime.Parse(expiresAtText, null, System.Globalization.DateTimeStyles.AdjustToUniversal);
        Assert.InRange(expiresAt, before.AddSeconds(ApiFixture.HoldDurationSeconds - 1), DateTime.UtcNow.AddSeconds(ApiFixture.HoldDurationSeconds));
        await api.WithDbAsync(async db =>
        {
            var stored = await db.Holds.AsNoTracking().SingleAsync(h => h.Id == holdId);
            Assert.Equal(stored.CreatedAt.AddSeconds(ApiFixture.HoldDurationSeconds), stored.ExpiresAt);
            Assert.Equal(expiresAt, stored.ExpiresAt);
        });
    }

    [Fact]
    public async Task Applying_twice_to_the_same_class_returns_409_and_takes_no_second_seat()
    {
        var classId = await CreateClassAsync(_client, 5);
        Assert.Equal(HttpStatusCode.Created, (await Send(HttpMethod.Post, $"/classes/{classId}/holds", Alice)).StatusCode);

        var duplicate = await Send(HttpMethod.Post, $"/classes/{classId}/holds", Alice);

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        AssertErrorBody(await ReadJsonAsync(duplicate));
        AssertSeatCounts(await GetClassAsync(_client, classId), null, total: 5, held: 1, confirmed: 0, available: 4);
    }

    [Fact]
    public async Task One_student_may_hold_different_classes_and_different_students_may_hold_one_class()
    {
        var first = await CreateClassAsync(_client, 5);
        var second = await CreateClassAsync(_client, 5);

        Assert.Equal(HttpStatusCode.Created, (await Send(HttpMethod.Post, $"/classes/{first}/holds", Alice)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Send(HttpMethod.Post, $"/classes/{second}/holds", Alice)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Send(HttpMethod.Post, $"/classes/{first}/holds", Bob)).StatusCode);

        AssertSeatCounts(await GetClassAsync(_client, first), null, total: 5, held: 2, confirmed: 0, available: 3);
        AssertSeatCounts(await GetClassAsync(_client, second), null, total: 5, held: 1, confirmed: 0, available: 4);
    }

    [Fact]
    public async Task Applying_to_a_full_class_returns_409_and_keeps_counts_unchanged()
    {
        var classId = await CreateClassAsync(_client, 2);
        Assert.Equal(HttpStatusCode.Created, (await Send(HttpMethod.Post, $"/classes/{classId}/holds", Alice)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Send(HttpMethod.Post, $"/classes/{classId}/holds", Bob)).StatusCode);

        var full = await Send(HttpMethod.Post, $"/classes/{classId}/holds", Carol);

        Assert.Equal(HttpStatusCode.Conflict, full.StatusCode);
        AssertErrorBody(await ReadJsonAsync(full));
        AssertSeatCounts(await GetClassAsync(_client, classId), null, total: 2, held: 2, confirmed: 0, available: 0);
    }

    [Fact]
    public async Task Applying_to_an_unknown_class_returns_404()
    {
        var response = await Send(HttpMethod.Post, "/classes/c_doesnotexist/holds", Alice);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        AssertErrorBody(await ReadJsonAsync(response));
    }

    [Fact]
    public async Task Lapsed_and_released_holds_free_their_seat_and_do_not_block_a_new_application()
    {
        var classId = await CreateClassAsync(_client, 1);
        var now = DateTime.UtcNow;
        await api.WithDbAsync(async db =>
        {
            db.Holds.AddRange(
                NewHold(classId, Alice, HoldStatus.HELD, now.AddSeconds(-1)),     // lapsed, not yet swept
                NewHold(classId, Bob, HoldStatus.CANCELLED, now.AddMinutes(5)),
                NewHold(classId, Carol, HoldStatus.EXPIRED, now.AddMinutes(-5)));
            await db.SaveChangesAsync();
        });

        // The only seat is free again, and Alice's own lapsed hold does not count as "already applied".
        var response = await Send(HttpMethod.Post, $"/classes/{classId}/holds", Alice);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        AssertSeatCounts(await GetClassAsync(_client, classId), null, total: 1, held: 1, confirmed: 0, available: 0);
    }

    [Fact]
    public async Task A_confirmed_hold_blocks_a_second_application_by_the_same_student()
    {
        var classId = await CreateClassAsync(_client, 5);
        await api.WithDbAsync(async db =>
        {
            db.Holds.Add(NewHold(classId, Alice, HoldStatus.CONFIRMED, DateTime.UtcNow.AddMinutes(-30)));
            await db.SaveChangesAsync();
        });

        var response = await Send(HttpMethod.Post, $"/classes/{classId}/holds", Alice);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // ---- GET /holds/{holdId} --------------------------------------------------------------------------------

    [Fact]
    public async Task Reading_a_hold_returns_its_current_status_with_exactly_the_contract_fields()
    {
        var classId = await CreateClassAsync(_client, 5);
        var created = await ReadJsonAsync(await Send(HttpMethod.Post, $"/classes/{classId}/holds", Alice));
        var holdId = created.GetProperty("holdId").GetString()!;

        var response = await _client.GetAsync($"/holds/{holdId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var hold = await ReadJsonAsync(response);
        Assert.Equal(["classId", "expiresAt", "holdId", "status"], hold.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal(holdId, hold.GetProperty("holdId").GetString());
        Assert.Equal(classId, hold.GetProperty("classId").GetString());
        Assert.Equal("HELD", hold.GetProperty("status").GetString());
        Assert.Equal(created.GetProperty("expiresAt").GetString(), hold.GetProperty("expiresAt").GetString());
    }

    [Fact]
    public async Task A_stored_held_hold_past_its_expiry_is_reported_as_expired()
    {
        var classId = await CreateClassAsync(_client, 5);
        var lapsed = NewHold(classId, Alice, HoldStatus.HELD, DateTime.UtcNow.AddSeconds(-1));
        var live = NewHold(classId, Bob, HoldStatus.HELD, DateTime.UtcNow.AddMinutes(5));
        var cancelled = NewHold(classId, Carol, HoldStatus.CANCELLED, DateTime.UtcNow.AddMinutes(5));
        await api.WithDbAsync(async db =>
        {
            db.Holds.AddRange(lapsed, live, cancelled);
            await db.SaveChangesAsync();
        });

        Assert.Equal("EXPIRED", await StatusOf(lapsed.Id));
        Assert.Equal("HELD", await StatusOf(live.Id));
        Assert.Equal("CANCELLED", await StatusOf(cancelled.Id));
    }

    [Fact]
    public async Task Reading_an_unknown_hold_returns_404()
    {
        var response = await _client.GetAsync("/holds/h_doesnotexist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        AssertErrorBody(await ReadJsonAsync(response));
    }

    // ---- GET /me/holds --------------------------------------------------------------------------------------

    [Fact]
    public async Task My_holds_lists_only_the_header_students_applications_with_class_name_and_computed_status()
    {
        var first = await CreateClassAsync(_client, 5);
        var second = await CreateClassAsync(_client, 5);
        var firstName = (await GetClassAsync(_client, first)).GetProperty("name").GetString();
        var secondName = (await GetClassAsync(_client, second)).GetProperty("name").GetString();
        var student = NewStudentId();
        var other = NewStudentId();
        var lapsed = NewHold(second, student, HoldStatus.HELD, DateTime.UtcNow.AddSeconds(-1));
        lapsed.CreatedAt = DateTime.UtcNow.AddMinutes(-10);
        await api.WithDbAsync(async db =>
        {
            db.Holds.Add(lapsed);
            await db.SaveChangesAsync();
        });
        var live = await ReadJsonAsync(await Send(HttpMethod.Post, $"/classes/{first}/holds", student));
        await Send(HttpMethod.Post, $"/classes/{first}/holds", other);

        var response = await Send(HttpMethod.Get, "/me/holds", student);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var mine = (await ReadJsonAsync(response)).EnumerateArray().ToList();
        Assert.Equal(2, mine.Count);
        Assert.All(mine, h => Assert.Equal(
            ["classId", "className", "expiresAt", "holdId", "status"],
            h.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray()));

        var liveRow = Assert.Single(mine, h => h.GetProperty("holdId").GetString() == live.GetProperty("holdId").GetString());
        Assert.Equal(first, liveRow.GetProperty("classId").GetString());
        Assert.Equal(firstName, liveRow.GetProperty("className").GetString());
        Assert.Equal("HELD", liveRow.GetProperty("status").GetString());

        var lapsedRow = Assert.Single(mine, h => h.GetProperty("holdId").GetString() == lapsed.Id);
        Assert.Equal(secondName, lapsedRow.GetProperty("className").GetString());
        Assert.Equal("EXPIRED", lapsedRow.GetProperty("status").GetString());
    }

    [Fact]
    public async Task My_holds_is_an_empty_list_for_a_student_without_applications()
    {
        var response = await Send(HttpMethod.Get, "/me/holds", "20159999999999999");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, (await ReadJsonAsync(response)).GetArrayLength());
    }

    // ---- helpers --------------------------------------------------------------------------------------------

    private Task<HttpResponseMessage> Send(HttpMethod method, string path, string? studentId) =>
        SendAsync(_client, method, path, studentId);

    private async Task<string?> StatusOf(string holdId) =>
        (await ReadJsonAsync(await _client.GetAsync($"/holds/{holdId}"))).GetProperty("status").GetString();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$")]
    private static partial Regex Iso8601UtcSeconds();
}
