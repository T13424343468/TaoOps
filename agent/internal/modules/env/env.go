// Package env implements FR-A7 (one-click environment deployment):
// Nginx/Apache/MySQL/PHP/Node/Java/Docker installed from offline
// packages with no internet access. Package import validates a local
// tarball; install runs the packaged installer through a Runner.
package env

import (
	"bytes"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"strings"

	"taoops/agent/internal/modules/registry"
)

// Runner executes external commands; tests inject fakes.
type Runner func(bin string, args []string, stdin string) (stdout, stderr string, err error)

// KnownEnvironments lists the PRD-supported environments.
var KnownEnvironments = []string{"nginx", "apache", "mysql", "php", "node", "java", "docker"}

// Manager installs environments from offline packages.
type Manager struct {
	Run        Runner
	PackageDir string
	InstallDir string
}

// NewManager wires a Manager with the real runner.
func NewManager(packageDir, installDir string) *Manager {
	return &Manager{Run: runExec, PackageDir: packageDir, InstallDir: installDir}
}

func runExec(bin string, args []string, stdin string) (string, string, error) {
	cmd := exec.Command(bin, args...)
	var out, errBuf bytes.Buffer
	cmd.Stdout = &out
	cmd.Stderr = &errBuf
	err := cmd.Run()
	return out.String(), errBuf.String(), err
}

// ImportPackage validates an offline package tarball for an environment
// and stages it under PackageDir. The package must exist and match the
// environment name.
func (m *Manager) ImportPackage(envName, srcPath string) (string, error) {
	if !isKnown(envName) {
		return "", fmt.Errorf("env: unknown environment %q", envName)
	}
	if _, err := os.Stat(srcPath); err != nil {
		return "", fmt.Errorf("env: package not found: %w", err)
	}
	if !strings.HasSuffix(srcPath, ".tar.gz") && !strings.HasSuffix(srcPath, ".tgz") {
		return "", fmt.Errorf("env: package must be a .tar.gz archive")
	}
	if err := os.MkdirAll(m.PackageDir, 0o755); err != nil {
		return "", err
	}
	dst := filepath.Join(m.PackageDir, envName+".tar.gz")
	data, err := os.ReadFile(srcPath)
	if err != nil {
		return "", err
	}
	if err := os.WriteFile(dst, data, 0o644); err != nil {
		return "", err
	}
	return dst, nil
}

// Install extracts and runs the offline installer for an environment.
// It fails if no package has been imported.
func (m *Manager) Install(envName string) (string, error) {
	if !isKnown(envName) {
		return "", fmt.Errorf("env: unknown environment %q", envName)
	}
	pkg := filepath.Join(m.PackageDir, envName+".tar.gz")
	if _, err := os.Stat(pkg); err != nil {
		return "", fmt.Errorf("env: no offline package imported for %q", envName)
	}
	if err := os.MkdirAll(m.InstallDir, 0o755); err != nil {
		return "", err
	}
	_, stderr, err := m.Run("tar", []string{"-xzf", pkg, "-C", m.InstallDir}, "")
	if err != nil {
		return "", fmt.Errorf("env: extract failed: %s", stderr)
	}
	installer := filepath.Join(m.InstallDir, envName, "install.sh")
	if _, err := os.Stat(installer); err != nil {
		return "", fmt.Errorf("env: package missing install.sh for %q", envName)
	}
	_, stderr, err = m.Run("/bin/sh", []string{installer}, "")
	if err != nil {
		return "", fmt.Errorf("env: install failed: %s", stderr)
	}
	return installer, nil
}

// Detect reports whether an environment binary is already available.
func (m *Manager) Detect(envName string) (bool, error) {
	if !isKnown(envName) {
		return false, fmt.Errorf("env: unknown environment %q", envName)
	}
	stdout, _, err := m.Run("/bin/sh", []string{"-c", "command -v " + envName + " || true"}, "")
	if err != nil {
		return false, err
	}
	return strings.TrimSpace(stdout) != "", nil
}

func isKnown(name string) bool {
	for _, k := range KnownEnvironments {
		if k == name {
			return true
		}
	}
	return false
}

// Handler returns the registry handler "env.manage".
func Handler(m *Manager) registry.Handler {
	return func(args map[string]any) (registry.Result, error) {
		op, _ := args["op"].(string)
		envName, _ := args["env"].(string)
		switch op {
		case "import":
			src, _ := args["src"].(string)
			dst, err := m.ImportPackage(envName, src)
			if err != nil {
				return registry.Result{ExitCode: 1}, err
			}
			return registry.Result{ExitCode: 0, Output: dst, Data: map[string]any{"staged": dst}}, nil
		case "install":
			installer, err := m.Install(envName)
			if err != nil {
				return registry.Result{ExitCode: 1}, err
			}
			return registry.Result{ExitCode: 0, Output: "installed via " + installer}, nil
		case "detect":
			found, err := m.Detect(envName)
			if err != nil {
				return registry.Result{ExitCode: 1}, err
			}
			return registry.Result{ExitCode: 0, Output: fmt.Sprintf("%v", found), Data: map[string]any{"installed": found}}, nil
		}
		return registry.Result{ExitCode: 2}, fmt.Errorf("env: unknown op %q", op)
	}
}

// Register installs the env handlers into r.
func Register(r *registry.Registry, m *Manager) error {
	return r.Register("env.manage", Handler(m))
}
