namespace AdmissionSystem.Api.Domain;

/// <summary>A student's application for a seat in a class.</summary>
public class Hold
{
    public required string Id { get; set; }
    public required string ClassId { get; set; }
    public required string StudentId { get; set; }
    public HoldStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }

    public SchoolClass? Class { get; set; }
}
