namespace BusinessOS.BuildingBlocks.Domain.Ids;

public readonly record struct OrganizationId(Guid Value)
{
    public static OrganizationId New() => new(Guid.NewGuid());
}

public readonly record struct CompanyId(Guid Value)
{
    public static CompanyId New() => new(Guid.NewGuid());
}

public readonly record struct BranchId(Guid Value)
{
    public static BranchId New() => new(Guid.NewGuid());
}

public readonly record struct BusinessProjectId(Guid Value)
{
    public static BusinessProjectId New() => new(Guid.NewGuid());
}

public readonly record struct BudgetId(Guid Value)
{
    public static BudgetId New() => new(Guid.NewGuid());
}

public readonly record struct BudgetVersionId(Guid Value)
{
    public static BudgetVersionId New() => new(Guid.NewGuid());
}

public readonly record struct UserId(Guid Value)
{
    public static UserId New() => new(Guid.NewGuid());
}
