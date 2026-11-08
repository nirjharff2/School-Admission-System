namespace AdmissionSystem.Api.Domain;

/// <summary>One class-and-shift with a fixed number of seats.</summary>
public class SchoolClass
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public int TotalSeats { get; set; }
    public DateTime CreatedAt { get; set; }

    public List<Hold> Holds { get; set; } = [];
}
