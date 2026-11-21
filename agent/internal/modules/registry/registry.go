// Package registry defines the unified module handler contract for the
// TaoOps agent: every base-layer module exposes a Handler with the same
// signature and registers itself into a Registry for command dispatch.
package registry

import (
	"fmt"
	"sync"
)

// Result is the unified return value of every module handler.
// ExitCode follows shell semantics: 0 means success.
type Result struct {
	ExitCode int               `json:"exit_code"`
	Output   string            `json:"output"`
	Data     map[string]any    `json:"data,omitempty"`
}

// Handler is the unified signature every module operation implements.
type Handler func(args map[string]any) (Result, error)

// Registry stores named module operations for command dispatch.
type Registry struct {
	mu       sync.RWMutex
	handlers map[string]Handler
}

// New returns an empty Registry.
func New() *Registry {
	return &Registry{handlers: make(map[string]Handler)}
}

// Register adds or replaces a named handler. Name must be non-empty and
// handler non-nil; a violation returns an error instead of panicking.
func (r *Registry) Register(name string, h Handler) error {
	if name == "" {
		return fmt.Errorf("registry: handler name must not be empty")
	}
	if h == nil {
		return fmt.Errorf("registry: handler %q must not be nil", name)
	}
	r.mu.Lock()
	defer r.mu.Unlock()
	r.handlers[name] = h
	return nil
}

// Get returns the handler registered under name.
func (r *Registry) Get(name string) (Handler, bool) {
	r.mu.RLock()
	defer r.mu.RUnlock()
	h, ok := r.handlers[name]
	return h, ok
}

// Names lists all registered handler names in sorted order.
func (r *Registry) Names() []string {
	r.mu.RLock()
	defer r.mu.RUnlock()
	names := make([]string, 0, len(r.handlers))
	for n := range r.handlers {
		names = append(names, n)
	}
	for i := 0; i < len(names); i++ {
		for j := i + 1; j < len(names); j++ {
			if names[j] < names[i] {
				names[i], names[j] = names[j], names[i]
			}
		}
	}
	return names
}
