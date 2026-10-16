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
        [DllImport("__Internal")] static extern string RW_GetDeviceInfo();
        [DllImport("__Internal")] static extern string RW_GetDefaultIonToken();
        public static string DeviceInfo() => RW_GetDeviceInfo();
        public static string BaseUrl() => RW_GetBaseUrl();
        public static string Query(string name) => RW_GetQueryParam(name);
        public static string Prompt(string message, string def) => RW_Prompt(message, def);
        public static void HideLoading() => RW_HideLoading();
        /// <summary>Token baked into index.html by CI from the ION_TOKEN secret (empty if not set).</summary>
        public static string DefaultIonToken() => RW_GetDefaultIonToken() ?? "";
#else
        public static string Query(string name) => System.Environment.GetEnvironmentVariable("RW_" + name.ToUpperInvariant()) ?? "";
        public static string Prompt(string message, string def) => def;   // Editor/desktop: use the RW_KEY env var
        public static string BaseUrl() => "http://localhost/";
        public static string DeviceInfo() => "8;8;0;1;1";
        public static void HideLoading() { }
        /// <summary>Editor/desktop: ION_TOKEN or RW_ION env var.</summary>
        public static string DefaultIonToken() =>
            System.Environment.GetEnvironmentVariable("ION_TOKEN")
            ?? System.Environment.GetEnvironmentVariable("RW_ION")
            ?? "";
#endif
    }
}
