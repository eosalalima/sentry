namespace SentrySMS.Services;

public enum ToastLevel
{
    Info,
    Success,
    Warning,
    Error
}

public record ToastMessage(string Title, string Message, ToastLevel Level);

public class ToastService
{
    public event Action<ToastMessage>? OnToast;

    public void ShowToast(string title, string message, ToastLevel level = ToastLevel.Info)
    {
        OnToast?.Invoke(new ToastMessage(title, message, level));
    }
}
