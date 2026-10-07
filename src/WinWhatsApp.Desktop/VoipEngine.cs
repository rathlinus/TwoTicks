namespace WinWhatsApp.App.Calls;

internal sealed partial class VoipEngine
{
    public partial bool IsRunning => false;

    private partial Task OpenPageAsync() => throw new NotSupportedException();

    private partial void PostToPage(string json)
    {
    }

    private partial void ClosePage()
    {
    }
}
