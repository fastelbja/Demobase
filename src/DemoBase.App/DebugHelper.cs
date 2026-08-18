namespace DemoBase.App;

/// <summary>
/// Expose IsDebugMode et AppVersion pour les bindings XAML.
/// </summary>
public static class DebugHelper
{
    public static bool IsDebugMode
    {
        get
        {
#if DEBUG
            return true;
#else
            return false;
#endif
        }
    }

    public static string AppVersion =>
        System.Reflection.Assembly.GetExecutingAssembly()
            .GetName().Version is { } v
            ? $"v{v.Major}.{v.Minor}"
            : "v0.1";
}
