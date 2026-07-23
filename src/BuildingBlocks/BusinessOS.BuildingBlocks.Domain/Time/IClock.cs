namespace BusinessOS.BuildingBlocks.Domain.Time;

public interface IClock
{
    DateTimeOffset UtcNow { get; }

    DateOnly Today { get; }
}
