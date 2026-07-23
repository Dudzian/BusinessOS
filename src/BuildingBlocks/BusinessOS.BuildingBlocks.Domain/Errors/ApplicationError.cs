namespace BusinessOS.BuildingBlocks.Domain.Errors;

public sealed record ApplicationError(
    string Code,
    string UserMessage,
    string TechnicalMessage);

public sealed class BusinessRuleException : Exception
{
    public BusinessRuleException(
        string code,
        string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}
