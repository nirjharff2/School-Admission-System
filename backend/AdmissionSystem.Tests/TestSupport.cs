using System.Net.Http.Json;
using System.Text.Json;
using AdmissionSystem.Api.Domain;

namespace AdmissionSystem.Tests;

/// <summary>Helpers shared by the API test classes.</summary>
internal static class TestSupport
{
    /// <summary>Sends a request, adding the X-Student-Id header only when <paramref name="studentId"/> is not null.</summary>
    public static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string? studentId)
    {
        var request = new HttpRequestMessage(method, path);
        if (studentId is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Student-Id", studentId);
        }
        return await client.SendAsync(request);
    }

    /// <summary>Applies for a seat and returns the new hold's id; fails the test if the apply is not a 201.</summary>
    public static async Task<string> ApplyAsync(HttpClient client, string classId, string studentId)
    {
        var response = await SendAsync(client, HttpMethod.Post, $"/classes/{classId}/holds", studentId);
        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
        return (await ReadJsonAsync(response)).GetProperty("holdId").GetString()!;
    }

    /// <summary>A fresh valid 17-digit student id, so a test never sees holds created by another test.</summary>
    public static string NewStudentId() => string.Concat(Enumerable.Range(0, 17).Select(_ => Random.Shared.Next(10)));

    public static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    public static void AssertSeatCounts(JsonElement c, string? name, int total, int held, int confirmed, int available)
    {
        Assert.Equal(
            ["availableSeats", "confirmedSeats", "heldSeats", "id", "name", "totalSeats"],
            c.EnumerateObject().Select(p => p.Name).Order().ToArray());
        if (name is not null)
        {
            Assert.Equal(name, c.GetProperty("name").GetString());
        }
        Assert.Equal(total, c.GetProperty("totalSeats").GetInt32());
        Assert.Equal(held, c.GetProperty("heldSeats").GetInt32());
        Assert.Equal(confirmed, c.GetProperty("confirmedSeats").GetInt32());
        Assert.Equal(available, c.GetProperty("availableSeats").GetInt32());
    }

    public static async Task<JsonElement> GetClassAsync(HttpClient client, string classId) =>
        await ReadJsonAsync(await client.GetAsync($"/classes/{classId}"));

    public static void AssertErrorBody(JsonElement body)
    {
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("error").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("message").GetString()));
    }

    public static async Task<string> CreateClassAsync(HttpClient client, int total)
    {
        var response = await client.PostAsJsonAsync("/classes", new { name = $"Test class {Guid.NewGuid():N}", totalSeats = total });
        response.EnsureSuccessStatusCode();
        return (await ReadJsonAsync(response)).GetProperty("id").GetString()!;
    }

    public static Hold NewHold(string classId, string studentId, HoldStatus status, DateTime expiresAt) => new()
    {
        Id = $"h_{Guid.NewGuid():N}"[..18],
        ClassId = classId,
        StudentId = studentId,
        Status = status,
        CreatedAt = expiresAt.AddMinutes(-10),
        ExpiresAt = expiresAt,
    };
}
