using BusinessOS.BuildingBlocks.Domain.Ids;
using BusinessOS.BuildingBlocks.Domain.Primitives;
using BusinessOS.Modules.BusinessProjects.Domain;
using FluentAssertions;
using Xunit;
namespace BusinessOS.UnitTests;

public sealed class BusinessProjectDomainTests
{
    private static readonly UserId User = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));
    private static BusinessProject Create() => BusinessProject.Create(new(Guid.NewGuid()), " Gym ", " Gym 24/7 ", " Leczyca ", " Description ", new(2026, 1, 1), new(2026, 2, 1), new("PLN"), User, DateTimeOffset.Parse("2026-01-01T12:00:00+02:00"));
    [Fact] public void Create_normalizes_and_starts_in_draft() { var p = Create(); p.Name.Should().Be("Gym"); p.BusinessType.Should().Be("Gym 24/7"); p.Location.Should().Be("Leczyca"); p.Description.Should().Be("Description"); p.Status.Should().Be(BusinessProjectStatus.Draft); p.CreatedAt.Offset.Should().Be(TimeSpan.Zero); p.Version.Value.Should().Be(1); }
    [Theory]
    [InlineData("", "Type", "Place")]
    [InlineData("Name", "", "Place")]
    [InlineData("Name", "Type", "")]
    public void Required_values_are_rejected(string name, string type, string location) { Action action = () => BusinessProject.Create(new(Guid.NewGuid()), name, type, location, "", new(2026, 1, 1), new(2026, 2, 1), new("PLN"), User, DateTimeOffset.UtcNow); action.Should().Throw<ArgumentException>(); }
    [Fact] public void Opening_before_start_is_rejected() { Action action = () => BusinessProject.Create(new(Guid.NewGuid()), "N", "T", "L", "", new(2026, 2, 1), new(2026, 1, 1), new("PLN"), User, DateTimeOffset.UtcNow); action.Should().Throw<ArgumentException>(); }
    [Fact] public void Update_advances_version_once_and_audit() { var p = Create(); var created = p.CreatedAt; p.Update("New", "Retail", "Lodz", "Changed", new(2025, 1, 1), new(2025, 1, 2), new("EUR"), new(Guid.NewGuid()), DateTimeOffset.Parse("2026-03-01T00:00:00Z")); p.Version.Value.Should().Be(2); p.Name.Should().Be("New"); p.BaseCurrency.Value.Should().Be("EUR"); p.CreatedAt.Should().Be(created); }
    [Fact] public void Every_allowed_transition_is_accepted() { foreach (var target in Create().AllowedTransitions) { var p = Create(); p.ChangeStatus(target, User, DateTimeOffset.UtcNow); p.Status.Should().Be(target); p.Version.Value.Should().Be(2); } }
    [Fact] public void Invalid_transition_does_not_mutate() { var p = Create(); Action action = () => p.ChangeStatus(BusinessProjectStatus.Operating, User, DateTimeOffset.UtcNow); action.Should().Throw<InvalidOperationException>(); p.Status.Should().Be(BusinessProjectStatus.Draft); p.Version.Value.Should().Be(1); }
    [Fact] public void Soft_delete_preserves_status_and_blocks_changes() { var p = Create(); p.ChangeStatus(BusinessProjectStatus.Analysis, User, DateTimeOffset.UtcNow); p.SoftDelete(User, DateTimeOffset.UtcNow); p.Status.Should().Be(BusinessProjectStatus.Analysis); p.IsDeleted.Should().BeTrue(); p.Version.Value.Should().Be(3); Action delete = () => p.SoftDelete(User, DateTimeOffset.UtcNow); delete.Should().Throw<InvalidOperationException>(); Action update = () => p.Update("X", "T", "L", "", new(2026, 1, 1), new(2026, 1, 2), new("PLN"), User, DateTimeOffset.UtcNow); update.Should().Throw<InvalidOperationException>(); }

    public static IEnumerable<object[]> AllowedTransitions()
    {
        yield return [Array.Empty<BusinessProjectStatus>(), BusinessProjectStatus.Analysis];
        yield return [Array.Empty<BusinessProjectStatus>(), BusinessProjectStatus.Cancelled];
        yield return [new[] { BusinessProjectStatus.Analysis }, BusinessProjectStatus.Draft];
        yield return [new[] { BusinessProjectStatus.Analysis }, BusinessProjectStatus.Approved];
        yield return [new[] { BusinessProjectStatus.Analysis }, BusinessProjectStatus.Cancelled];
        yield return [new[] { BusinessProjectStatus.Analysis, BusinessProjectStatus.Approved }, BusinessProjectStatus.InPreparation];
        yield return [new[] { BusinessProjectStatus.Analysis, BusinessProjectStatus.Approved }, BusinessProjectStatus.Cancelled];
        yield return [new[] { BusinessProjectStatus.Analysis, BusinessProjectStatus.Approved, BusinessProjectStatus.InPreparation }, BusinessProjectStatus.InProgress];
        yield return [new[] { BusinessProjectStatus.Analysis, BusinessProjectStatus.Approved, BusinessProjectStatus.InPreparation }, BusinessProjectStatus.Paused];
        yield return [new[] { BusinessProjectStatus.Analysis, BusinessProjectStatus.Approved, BusinessProjectStatus.InPreparation }, BusinessProjectStatus.Cancelled];
        yield return [new[] { BusinessProjectStatus.Analysis, BusinessProjectStatus.Approved, BusinessProjectStatus.InPreparation, BusinessProjectStatus.InProgress }, BusinessProjectStatus.ReadyToOpen];
        yield return [new[] { BusinessProjectStatus.Analysis, BusinessProjectStatus.Approved, BusinessProjectStatus.InPreparation, BusinessProjectStatus.InProgress }, BusinessProjectStatus.Paused];
        yield return [new[] { BusinessProjectStatus.Analysis, BusinessProjectStatus.Approved, BusinessProjectStatus.InPreparation, BusinessProjectStatus.InProgress }, BusinessProjectStatus.Cancelled];
        yield return [new[] { BusinessProjectStatus.Analysis, BusinessProjectStatus.Approved, BusinessProjectStatus.InPreparation, BusinessProjectStatus.InProgress, BusinessProjectStatus.ReadyToOpen }, BusinessProjectStatus.Operating];
        yield return [new[] { BusinessProjectStatus.Analysis, BusinessProjectStatus.Approved, BusinessProjectStatus.InPreparation, BusinessProjectStatus.InProgress, BusinessProjectStatus.ReadyToOpen }, BusinessProjectStatus.Paused];
        yield return [new[] { BusinessProjectStatus.Analysis, BusinessProjectStatus.Approved, BusinessProjectStatus.InPreparation, BusinessProjectStatus.InProgress, BusinessProjectStatus.ReadyToOpen }, BusinessProjectStatus.Cancelled];
        yield return [new[] { BusinessProjectStatus.Analysis, BusinessProjectStatus.Approved, BusinessProjectStatus.InPreparation, BusinessProjectStatus.InProgress, BusinessProjectStatus.ReadyToOpen, BusinessProjectStatus.Operating }, BusinessProjectStatus.Paused];
        yield return [new[] { BusinessProjectStatus.Analysis, BusinessProjectStatus.Approved, BusinessProjectStatus.InPreparation, BusinessProjectStatus.InProgress, BusinessProjectStatus.ReadyToOpen, BusinessProjectStatus.Operating }, BusinessProjectStatus.Closed];
        var paused = new[] { BusinessProjectStatus.Analysis, BusinessProjectStatus.Approved, BusinessProjectStatus.InPreparation, BusinessProjectStatus.Paused };
        foreach (var target in new[] { BusinessProjectStatus.InPreparation, BusinessProjectStatus.InProgress, BusinessProjectStatus.ReadyToOpen, BusinessProjectStatus.Operating, BusinessProjectStatus.Cancelled }) yield return [paused, target];
    }

    [Theory, MemberData(nameof(AllowedTransitions))]
    public void Complete_transition_matrix_is_accepted(BusinessProjectStatus[] path, BusinessProjectStatus target)
    {
        var project = Create();
        foreach (var step in path) project.ChangeStatus(step, User, DateTimeOffset.UtcNow);
        var before = project.Version.Value;
        project.ChangeStatus(target, User, DateTimeOffset.UtcNow);
        project.Status.Should().Be(target);
        project.Version.Value.Should().Be(before + 1);
    }
}
