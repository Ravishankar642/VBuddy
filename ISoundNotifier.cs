using System.Threading.Tasks;

namespace VBuddy {

    // Optional capability for drivers whose service supports a per-message notification
    // sound (Pushover, Bark). Kept separate from INotifier so existing drivers and call
    // sites are untouched; callers check `notifier as ISoundNotifier`.
    internal interface ISoundNotifier {
        Task SendMessageAsync(string text, string title, int priority, string source, string sound);
    }
}
