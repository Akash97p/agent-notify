namespace AgentNotify.Core.Router.Translation;

/// <summary>Thrown when translation fails.</summary>
public sealed class TranslationException : Exception
{
    public string Code { get; }

    public TranslationException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public TranslationException(string code, string message, Exception inner)
        : base(message, inner)
    {
        Code = code;
    }
}
