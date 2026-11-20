package workbench

import (
	"context"
	"encoding/base64"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"testing"
	"time"

	"github.com/sylvinus/agent-vm/internal/guestos"
)

func TestWindowsGuestProjectReconnectE2E(t *testing.T) {
	profile := os.Getenv("CAGE_TEST_WINDOWS_PROJECT_RECOVERY_PROFILE")
	if runtime.GOOS != "windows" || profile == "" {
		t.Skip("requires an explicitly paired Windows profile for a temporary project recovery test")
	}
	binding, err := guestos.LoadWindowsProfile(profile)
	if err != nil || binding == nil {
		t.Fatal("paired profile unavailable", err)
	}
	s := &Server{profile: profile, windowsGuest: binding}
	s.lifetimeContext()
	defer s.lifeCancel()
	defer s.closeProjectShares()
	project := t.TempDir()
	probe := ".cage-mount-probe-recovery"
	if err = os.WriteFile(filepath.Join(project, probe), []byte("recovery-test"), 0600); err != nil {
		t.Fatal(err)
	}
	if err = s.ensureWindowsProjectShare(project); err != nil {
		t.Fatal(err)
	}
	first := *s.projectShares[project].windowsProject
	for attempt := 0; attempt < 3; attempt++ {
		before := *s.projectShares[project].windowsProject
		ctx, cancel := context.WithTimeout(s.lifetimeContext(), 5*time.Second)
		_, err = s.windowsProjectRequest(ctx, "unmount", before, "")
		cancel()
		if err != nil {
			t.Fatal("could not disconnect the test mount", err)
		}
		if err = s.ensureWindowsProjectShare(project); err != nil {
			t.Fatal("real guest mount did not recover", err)
		}
		after := *s.projectShares[project].windowsProject
		if after.ID != first.ID || after.Path != first.Path || after.Grant == before.Grant {
			t.Fatal("recovery lost the original drive or reused the revoked grant")
		}
		ctx, cancel = context.WithTimeout(s.lifetimeContext(), 5*time.Second)
		ok := s.windowsProjectProof(ctx, s.projectShares[project], probe, "recovery-test")
		cancel()
		if !ok {
			t.Fatal("recovered guest mount could not read the host file")
		}
		t.Logf("recovered the same guest drive on attempt %d", attempt+1)
	}
}

// This opt-in test uses only a deliberately supplied paired QA profile. It
// creates its own host project and leaves the VM, accounts and clipboard intact.
func TestWindowsGuestProjectAndResourceE2E(t *testing.T) {
	profile := os.Getenv("CAGE_TEST_WINDOWS_WORKBENCH_PROFILE")
	if profile == "" {
		t.Skip("set CAGE_TEST_WINDOWS_WORKBENCH_PROFILE to an isolated paired QA profile")
	}
	if runtime.GOOS != "windows" {
		t.Skip("requires native Windows Hyper-V host")
	}
	binding, err := guestos.LoadWindowsProfile(profile)
	if err != nil || binding == nil {
		t.Fatalf("paired QA profile unavailable: %v", err)
	}
	s := &Server{profile: profile, configPath: filepath.Join(profile, "env.json"), windowsGuest: binding, ready: true}
	s.lifetimeContext()
	defer s.lifeCancel()
	defer s.closeProjectShares()
	project := t.TempDir()
	outside := t.TempDir()
	if err = os.WriteFile(filepath.Join(project, "hello.txt"), []byte("first live value"), 0600); err != nil {
		t.Fatal(err)
	}
	if err = os.Mkdir(filepath.Join(project, ".git"), 0700); err != nil {
		t.Fatal(err)
	}
	if err = os.WriteFile(filepath.Join(project, ".git", "config"), []byte("protected"), 0600); err != nil {
		t.Fatal(err)
	}
	if err = os.WriteFile(filepath.Join(outside, "private.txt"), []byte("outside selected project"), 0600); err != nil {
		t.Fatal(err)
	}
	ctx, cancel := context.WithTimeout(s.lifetimeContext(), 60*time.Second)
	defer cancel()
	if _, err = s.windowsStatus(ctx); err != nil {
		t.Fatal(err)
	}
	owners, err := controllerVMOwners(profile)
	if err != nil || len(owners) != 1 || owners[0] != "windows/"+binding.VMID.String()+"/Running" {
		t.Fatalf("real desktop controller missed the bound Hyper-V workload: %v %v", owners, err)
	}
	if err = s.ensureProjectShare(project); err != nil {
		t.Fatal(err)
	}
	mapped, err := s.windowsProjectForHost(project)
	if err != nil {
		t.Fatal(err)
	}
	command, err := guestos.WindowsRelayCommand(ctx, profile, "terminal")
	if err != nil {
		t.Fatal(err)
	}
	terminal, err := startWindowsGuestTerminalAt(command, 100, 30, mapped)
	if err != nil {
		t.Fatal(err)
	}
	defer terminal.Close()
	verify := func(script string) {
		t.Helper()
		marker := "CAGE-REAL-" + randomToken()
		encoded := base64.StdEncoding.EncodeToString([]byte(marker))
		input := `& { $ErrorActionPreference='Stop'; ` + script + `; Write-Output ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('` + encoded + `'))) }` + "\r"
		if _, err := io.WriteString(terminal, input); err != nil {
			t.Fatal(err)
		}
		finished := make(chan error, 1)
		go func() {
			var output strings.Builder
			buffer := make([]byte, 8192)
			for output.Len() < 1<<20 {
				n, err := terminal.Read(buffer)
				if n > 0 {
					output.Write(buffer[:n])
					if strings.Contains(output.String(), marker) {
						finished <- nil
						return
					}
				}
				if err != nil {
					finished <- err
					return
				}
			}
			finished <- fmt.Errorf("terminal output limit")
		}()
		select {
		case err := <-finished:
			if err != nil {
				t.Fatal(err)
			}
		case <-ctx.Done():
			terminal.Close()
			t.Fatal("real guest terminal probe timed out")
		}
	}
	verify(`if([IO.File]::ReadAllText('hello.txt') -ne 'first live value'){throw 'initial shared read failed'}`)
	if err = os.WriteFile(filepath.Join(project, "hello.txt"), []byte("host changed value"), 0600); err != nil {
		t.Fatal(err)
	}
	unicode := base64.StdEncoding.EncodeToString([]byte("原子保存🙂"))
	verify(`if([IO.File]::ReadAllText('hello.txt') -ne 'host changed value'){throw 'host live change failed'}; [IO.File]::WriteAllText('atomic.tmp',[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('` + unicode + `')),[Text.UTF8Encoding]::new($false)); Move-Item -Force atomic.tmp saved.txt; $denied=$false;try{[IO.File]::WriteAllText('.GiT/config','altered')}catch{$denied=$true};if(!$denied){throw 'protected metadata write accepted'}`)
	if data, err := os.ReadFile(filepath.Join(project, "saved.txt")); err != nil || string(data) != "原子保存🙂" {
		t.Fatalf("real atomic save failed: %q %v", data, err)
	}
	if data, err := os.ReadFile(filepath.Join(project, ".git", "config")); err != nil || string(data) != "protected" {
		t.Fatal("real guest changed protected metadata")
	}
	terminal.Close()
	_ = terminal.Wait()
	s.closeProjectShares()
	command, err = guestos.WindowsRelayCommand(ctx, profile, "terminal")
	if err != nil {
		t.Fatal(err)
	}
	if rejected, err := startWindowsGuestTerminalAt(command, 100, 30, mapped); err == nil {
		rejected.Close()
		t.Fatal("revoked real project grant still starts a terminal")
	}
	state, err := guestos.WindowsVMResources(ctx, profile, 0, 0)
	if err != nil {
		t.Fatal(err)
	}
	sample, err := s.windowsResourceSample(ctx, state)
	if err != nil {
		t.Fatal(err)
	}
	t.Logf("real Windows: live shared file, atomic save, .git protection, revoked grant; %d vCPUs and guest memory sample present=%v", state.CPUs, sample["guest"] != nil)
}
