namespace AudioWorkflow;

public enum AudioEditRegionKind
{
    Deleted,
    AuditionRepair,
    AuditionPending,
}

/// <summary>An edit annotation expressed on the immutable original-audio axis.</summary>
public sealed record AudioEditRegion(
    string Id,
    AudioEditRegionKind Kind,
    double StartSeconds,
    double EndSeconds)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || !double.IsFinite(StartSeconds) ||
            !double.IsFinite(EndSeconds) || StartSeconds < 0 || EndSeconds <= StartSeconds)
            throw new InvalidDataException("Invalid audio edit region.");
    }
}
