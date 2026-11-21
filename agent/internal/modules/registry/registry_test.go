package registry

import (
	"errors"
	"sort"
	"testing"
)

func TestRegisterAndGet(t *testing.T) {
	r := New()
	ok := func(args map[string]any) (Result, error) { return Result{ExitCode: 0}, nil }
	if err := r.Register("sysinfo.get", ok); err != nil {
		t.Fatalf("Register: %v", err)
	}
	got, found := r.Get("sysinfo.get")
	if !found || got == nil {
		t.Fatalf("Get(sysinfo.get) not found")
	}
	if _, found := r.Get("nope"); found {
		t.Fatalf("Get(nope) should not be found")
	}
}

func TestRegisterValidation(t *testing.T) {
	r := New()
	if err := r.Register("", func(map[string]any) (Result, error) { return Result{}, nil }); err == nil {
		t.Fatal("empty name should be rejected")
	}
	if err := r.Register("x", nil); err == nil {
		t.Fatal("nil handler should be rejected")
	}
}

func TestNamesSorted(t *testing.T) {
	r := New()
	for _, n := range []string{"b", "a", "c"} {
		if err := r.Register(n, func(map[string]any) (Result, error) { return Result{}, nil }); err != nil {
			t.Fatalf("Register(%s): %v", n, err)
		}
	}
	got := r.Names()
	want := []string{"a", "b", "c"}
	if !equalStrings(got, want) {
		t.Fatalf("Names() = %v, want %v", got, want)
	}
}

func TestHandlerErrorPropagates(t *testing.T) {
	r := New()
	wantErr := errors.New("boom")
	if err := r.Register("f", func(map[string]any) (Result, error) { return Result{}, wantErr }); err != nil {
		t.Fatalf("Register: %v", err)
	}
	h, _ := r.Get("f")
	if _, err := h(nil); !errors.Is(err, wantErr) {
		t.Fatalf("handler error = %v, want %v", err, wantErr)
	}
}

func equalStrings(a, b []string) bool {
	if len(a) != len(b) {
		return false
	}
	sort.Strings(a)
	sort.Strings(b)
	for i := range a {
		if a[i] != b[i] {
			return false
		}
	}
	return true
}
