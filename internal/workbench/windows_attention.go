package workbench

import (
	"net/http"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
)

func windowsSystemAttention(reason string) bool {
	return reason == "requires_system_attention" || reason == "windows_security_prompt" || reason == "windows_application_prompt" || reason == "requires_gui_login"
}

func (s *Server) openWindowsConsole(w http.ResponseWriter, r *http.Request) {
	if !s.hasTrustedView(r) {
		http.Error(w, "请从本机 Cage 窗口打开虚拟机", http.StatusForbidden)
		return
	}
	if s.windowsGuest == nil || s.windowsGuest.Remote() || runtime.GOOS != "windows" {
		http.Error(w, "此环境没有本机 Hyper-V 控制台", http.StatusConflict)
		return
	}
	var input struct{}
	if !readJSON(w, r, &input) {
		return
	}
	// The request cannot select another VM or supply an executable/arguments.
	// Windows displays its own permission checks and the guest's real dialog.
	console := filepath.Join(os.Getenv("SystemRoot"), "System32", "vmconnect.exe")
	cmd := exec.Command(console, "localhost", "-G", s.windowsGuest.VMID.String())
	if err := cmd.Start(); err != nil {
		http.Error(w, "无法打开虚拟机连接，请从 Hyper-V 管理器连接当前环境", http.StatusBadGateway)
		return
	}
	go func() { _ = cmd.Wait() }()
	jsonReply(w, map[string]bool{"opened": true})
}
