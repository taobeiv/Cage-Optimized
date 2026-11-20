package workbench

import (
	"os"
	"runtime"
	"testing"

	"github.com/google/uuid"
	"github.com/sylvinus/agent-vm/internal/guestos"
)

// Opt-in read-only test: the supplied QA guest must have the standard zero-NIC,
// secure Windows configuration. No guest input or lifecycle changes are made.
func TestWindowsLayoutInspectionE2E(t *testing.T) {
	id := os.Getenv("CAGE_TEST_WINDOWS_LAYOUT_VM")
	if runtime.GOOS != "windows" || id == "" {
		t.Skip("requires an explicitly supplied local Windows QA VM")
	}
	vm, err := uuid.Parse(id)
	if err != nil {
		t.Fatal(err)
	}
	s := testServer(t)
	s.windowsGuest = &guestos.WindowsProfile{VMID: vm}
	_, network := s.windowsVMLayout()
	if !network {
		t.Fatal("verified zero-NIC secure VM was rejected by layout inspection")
	}
}
