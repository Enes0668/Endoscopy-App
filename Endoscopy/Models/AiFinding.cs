namespace Endoscopy.Models;

/// <summary>Claude Vision'ın bir capture üzerinde ürettiği tek bir bulgu.</summary>
public class AiFinding
{
    public long Id { get; set; }

    public long CaptureId { get; set; }

    /// <summary>EF Core navigation property — hangi capture'a ait olduğu.</summary>
    public MediaCapture? Capture { get; set; }

    public string FindingType { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public double Confidence { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
