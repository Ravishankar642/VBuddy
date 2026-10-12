using System;
using System.Threading.Tasks;

using VBuddy.Atc;

namespace VBuddy {

    // ATC Ahead glue. Kept in its own file so Main.cs only needs a handful of one-line hooks.
    // Every entry point is wrapped in try/catch: this feature must never break the
    // existing relays (private/radio/SELCAL/Hoppie/disconnect).
    public partial class Main {

        private AtcAheadSettings _atcSettings;
        private AtcAheadService _atcAhead;
        private string _pluginDir;

        private void InitAtcAhead() {
            if (_atcSettings == null || !_atcSettings.Enabled) return;
            try {
                _atcAhead = new AtcAheadService(_atcSettings, _pluginDir, SendDebug, SendAtcAheadAlertAsync);
                SendDebug("[ATC Ahead] Enabled");
            } catch (Exception ex) {
                _atcAhead = null;
                SendDebug($"[ATC Ahead] Failed to start, feature disabled: {ex.Message}");
            }
        }

        private Task SendAtcAheadAlertAsync(string title, string body, int priority, string sound) {
            const string source = "ATC Ahead alert (AtcAhead)";
            var soundNotifier = _notifier as ISoundNotifier;
            if (!string.IsNullOrWhiteSpace(sound) && soundNotifier != null) {
                return soundNotifier.SendMessageAsync(body, title, priority, source, sound);
            }
            return _notifier.SendMessageAsync(body, title, priority, source);
        }

        private void AtcAheadOnConnected(string callsign) {
            if (_atcAhead == null) return;
            try {
                // The VATSIM data feed (polled inside AtcAheadService) is the sole source of
                // the flight plan; it's public data and needs no vPilot SDK event, so this
                // works the same regardless of the installed vPilot/SDK version.
                _atcAhead.Start(callsign);
            } catch (Exception ex) {
                SendDebug($"[ATC Ahead] Start error: {ex.Message}");
            }
        }

        private void AtcAheadOnDisconnected() {
            try { _atcAhead?.Stop(); } catch (Exception ex) { SendDebug($"[ATC Ahead] Stop error: {ex.Message}"); }
        }
    }
}
