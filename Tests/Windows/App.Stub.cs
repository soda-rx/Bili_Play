namespace BiliPlay;

// The Windows interop tests link the service sources directly and never compile the WinUI
// application, so expose the same visual-check switch that ProfileManager reads at startup.
internal static class App
{
    public static bool IsVisualCheck => false;
    public static string? VisualCheckDirectory => null;
}
