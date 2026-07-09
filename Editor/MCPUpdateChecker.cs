using System;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Update checking is disabled for this distribution. The method is kept as a
    /// no-op so existing callers keep compiling; it performs no network requests
    /// and always reports "no update available".
    /// </summary>
    public static class MCPUpdateChecker
    {
        // Kept only so the callback has a version string to echo back.
        // Reconcile with package.json when a canonical version is chosen.
        private const string CurrentVersion = "0.0.3";

        /// <summary>
        /// No-op. Immediately reports no update. Callback receives (hasUpdate: false, CurrentVersion).
        /// </summary>
        public static void CheckForUpdates(Action<bool, string> callback)
        {
            callback?.Invoke(false, CurrentVersion);
        }
    }
}
