using System.Net.Http;

namespace VBuddy {

    // One HttpClient for the whole plugin — it is thread-safe and meant to be reused,
    // shared by the notifier drivers, the ACARS poller and the update checker.
    internal static class Http {
        public static readonly HttpClient Client = new HttpClient();
    }
}
