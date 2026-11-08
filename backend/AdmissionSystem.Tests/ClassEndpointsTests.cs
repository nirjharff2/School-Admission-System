using static AdmissionSystem.Tests.TestSupport;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AdmissionSystem.Api.Domain;

namespace AdmissionSystem.Tests;

[Collection(ApiCollection.Name)]
public class ClassEndpointsTests(ApiFixture api)
{
    private readonly HttpClient _client = api.CreateClient();

    [Fact]
    public async Task Created_class_is_returned_by_create_list_and_detail_with_zero_counts()
    {
        var name = $"Class 6 - Morning Shift {Guid.NewGuid():N}";

        var create = await _client.PostAsJsonAsync("/classes", new { name, totalSeats = 120 });

        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await ReadJsonAsync(create);
        Assert.Equal(["id", "name", "totalSeats"], created.EnumerateObject().Select(p => p.Name).Order().ToArray());
        var id = created.GetProperty("id").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(id));
        Assert.Equal(name, created.GetProperty("name").GetString());
        Assert.Equal(120, created.GetProperty("totalSeats").GetInt32());

        var list = await ReadJsonAsync(await _client.GetAsync("/classes"));
        var listed = Assert.Single(list.EnumerateArray(), c => c.GetProperty("id").GetString() == id);
        AssertSeatCounts(listed, name, total: 120, held: 0, confirmed: 0, available: 120);

        var detail = await _client.GetAsync($"/classes/{id}");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        AssertSeatCounts(await ReadJsonAsync(detail), name, total: 120, held: 0, confirmed: 0, available: 120);
    }

    [Theory]
    [InlineData("""{"name":"","totalSeats":10}""")]
    [InlineData("""{"name":"   ","totalSeats":10}""")]
    [InlineData("""{"name":"a\u0000b","totalSeats":10}""")]
    [InlineData("""{"name":"Class\n1","totalSeats":10}""")]
    [InlineData("""{"totalSeats":10}""")]
    [InlineData("""{"name":null,"totalSeats":10}""")]
    [InlineData("""{"name":"Class 1","totalSeats":0}""")]
    [InlineData("""{"name":"Class 1","totalSeats":-5}""")]
    [InlineData("""{"name":"Class 1"}""")]
    [InlineData("""{"name":"Class 1","totalSeats":null}""")]
    [InlineData("""{"name":"Class 1","totalSeats":"many"}""")]
    [InlineData("""{"name":"Class 1","totalSeats":2.5}""")]
    [InlineData("""{"name":"Class 1","totalSeats":99999999999}""")]
    [InlineData("{not json")]
    [InlineData("null")]
    [InlineData("")]
    public async Task Invalid_create_payload_is_rejected_with_400_and_uniform_error_body(string body)
    {
        var before = await CountClassesAsync();

        var response = await _client.PostAsync("/classes", new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertErrorBody(await ReadJsonAsync(response));
        Assert.Equal(before, await CountClassesAsync());
    }

    [Fact]
    public async Task Class_name_longer_than_the_limit_is_rejected_with_400()
    {
        var response = await _client.PostAsJsonAsync("/classes", new { name = new string('x', 201), totalSeats = 5 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertErrorBody(await ReadJsonAsync(response));
    }

    [Fact]
    public async Task Unknown_class_id_returns_404_with_uniform_error_body()
    {
        var response = await _client.GetAsync("/classes/c_doesnotexist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        AssertErrorBody(await ReadJsonAsync(response));
    }

    [Fact]
    public async Task Seat_counts_include_only_unexpired_held_holds_and_confirmed_holds()
    {
        var classId = await CreateClassAsync(_client, total: 10);
        var now = DateTime.UtcNow;
        await api.WithDbAsync(async db =>
        {
            db.Holds.AddRange(
                NewHold(classId, "10000000000000001", HoldStatus.HELD, now.AddMinutes(5)),
                NewHold(classId, "10000000000000002", HoldStatus.HELD, now.AddMinutes(5)),
                NewHold(classId, "10000000000000003", HoldStatus.HELD, now.AddMinutes(-1)), // lapsed, not yet swept
                NewHold(classId, "10000000000000004", HoldStatus.CONFIRMED, now.AddMinutes(-30)),
                NewHold(classId, "10000000000000005", HoldStatus.EXPIRED, now.AddMinutes(-30)),
                NewHold(classId, "10000000000000006", HoldStatus.CANCELLED, now.AddMinutes(5)));
            await db.SaveChangesAsync();
        });

        var detail = await ReadJsonAsync(await _client.GetAsync($"/classes/{classId}"));
        AssertSeatCounts(detail, null, total: 10, held: 2, confirmed: 1, available: 7);

        var list = await ReadJsonAsync(await _client.GetAsync("/classes"));
        var listed = Assert.Single(list.EnumerateArray(), c => c.GetProperty("id").GetString() == classId);
        AssertSeatCounts(listed, null, total: 10, held: 2, confirmed: 1, available: 7);
    }

    [Fact]
    public async Task Cross_origin_preflight_with_student_header_is_allowed()
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "/classes");
        request.Headers.Add("Origin", "http://localhost:3000");
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "content-type,x-student-id");

        var response = await _client.SendAsync(request);

        Assert.True(response.IsSuccessStatusCode);
        Assert.Contains("*", response.Headers.GetValues("Access-Control-Allow-Origin"));
        Assert.Contains("x-student-id", string.Join(",", response.Headers.GetValues("Access-Control-Allow-Headers")),
            StringComparison.OrdinalIgnoreCase);
    }

    private async Task<int> CountClassesAsync() =>
        (await ReadJsonAsync(await _client.GetAsync("/classes"))).GetArrayLength();
}
