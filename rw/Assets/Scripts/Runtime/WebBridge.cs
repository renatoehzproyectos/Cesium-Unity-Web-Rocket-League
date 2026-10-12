using System.Runtime.InteropServices;

namespace RocketWorld
{
    /// <summary>Tiny browser interop (query string, prompt dialog). Editor/desktop fall back to harmless defaults.</summary>
    public static class WebBridge
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] static extern string RW_GetQueryParam(string name);
        [DllImport("__Internal")] static extern string RW_Prompt(string message, string def);
        [DllImport("__Internal")] static extern void RW_HideLoading();
        [DllImport("__Internal")] static extern string RW_GetBaseUrl();
        public static string BaseUrl() => RW_GetBaseUrl();
        public static string Query(string name) => RW_GetQueryParam(name);
        public static string Prompt(string message, string def) => RW_Prompt(message, def);
        public static void HideLoading() => RW_HideLoading();
#else
        public static string Query(string name) => System.Environment.GetEnvironmentVariable("RW_" + name.ToUpperInvariant()) ?? "";
        public static string Prompt(string message, string def) => def;   // Editor/desktop: use the RW_KEY env var
        public static string BaseUrl() => "http://localhost/";
        public static void HideLoading() { }
#endif
    }
}
