using System.Threading.Tasks;

namespace VBuddy {

    internal interface INotifier {
        void Initialize(NotifierConfig config);
        Task SendMessageAsync(string message, string title = "", int priority = 0, string source = "notification");
        bool HasValidConfig();
    }
}
