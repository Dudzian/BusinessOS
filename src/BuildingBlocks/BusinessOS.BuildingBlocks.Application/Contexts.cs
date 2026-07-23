using BusinessOS.BuildingBlocks.Domain.Ids;

namespace BusinessOS.BuildingBlocks.Application;

public interface ICurrentCompanyContext
{
    CompanyId? CompanyId { get; }

    CompanyId RequireCompanyId();
}

public interface ICurrentUserContext
{
    UserId UserId { get; }
}
