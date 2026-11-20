package workbench

import (
	"encoding/json"
	"net/http"
	"testing"

	"github.com/sylvinus/agent-vm/internal/guestos"
)

func TestWindowsSystemAttentionReachesWorkbenchStatus(t *testing.T) {
	for _, reason := range []string{"requires_system_attention", "windows_security_prompt", "windows_application_prompt", "requires_gui_login"} {
		t.Run(reason, func(t *testing.T) {
			s := testServer(t)
			s.windowsGuest = &guestos.WindowsProfile{}
			s.windowsState.Reason = reason
			w := windowsBrowserRequestForTest(s, http.MethodGet, "/api/status", nil)
			var status map[string]any
			if json.Unmarshal(w.Body.Bytes(), &status) != nil || status["guest_attention"] != reason {
				t.Fatalf("system attention was hidden: %d %s", w.Code, w.Body.String())
			}
		})
	}
}

func TestWindowsConsoleRequiresTrustedLocalDesktop(t *testing.T) {
	s := testServer(t)
	s.windowsGuest = &guestos.WindowsProfile{}
	w := request(s, http.MethodPost, "/api/environment/windows-console", s.origin, `{}`, true)
	if w.Code != http.StatusForbidden {
		t.Fatalf("untrusted console request status=%d", w.Code)
	}
	s.windowsGuest.Mode = "remote"
	w = windowsBrowserRequestForTest(s, http.MethodPost, "/api/environment/windows-console", []byte(`{}`))
	if w.Code != http.StatusConflict {
		t.Fatalf("remote guest opened a host console: status=%d", w.Code)
	}
}
