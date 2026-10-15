namespace YakUI.App.Services;

/// <summary>Подтверждения перед необратимыми действиями (FR-85).</summary>
public interface IDialogService
{
    Task<bool> ConfirmAsync(string title, string message, string confirmText);
}

/// <summary>Для тестов: заранее заданный ответ, запоминает вопросы.</summary>
public sealed class FixedDialogService(bool answer) : IDialogService
{
    public List<string> Asked { get; } = [];

    public Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        Asked.Add(message);
        return Task.FromResult(answer);
    }
}
