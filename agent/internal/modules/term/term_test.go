package term

import (
	"testing"
)

func TestCreateAndReconnect(t *testing.T) {
	m := NewManager()
	s := m.Create()
	if s.ID == "" || s.Token == "" {
		t.Fatal("session must have id and token")
	}
	got, err := m.Reconnect(s.ID, s.Token)
	if err != nil || got.ID != s.ID {
		t.Fatalf("Reconnect: %v %v", got, err)
	}
	if _, err := m.Reconnect(s.ID, "wrong-token"); err == nil {
		t.Fatal("wrong token must fail")
	}
	if _, err := m.Reconnect("ghost", "t"); err == nil {
		t.Fatal("unknown session must fail")
	}
}

func TestCloseBlocksReconnect(t *testing.T) {
	m := NewManager()
	s := m.Create()
	if err := m.Close(s.ID); err != nil {
		t.Fatalf("Close: %v", err)
	}
	if _, err := m.Reconnect(s.ID, s.Token); err == nil {
		t.Fatal("closed session must not reconnect")
	}
	if err := m.Close(s.ID); err == nil {
		t.Fatal("closing missing session should fail")
	}
}

func TestExecRunsCommand(t *testing.T) {
	m := NewManager()
	s := m.Create()
	out, code, err := m.Exec(s.ID, "echo hello")
	if err != nil {
		t.Fatalf("Exec: %v", err)
	}
	if code != 0 || out != "hello\n" {
		t.Fatalf("out=%q code=%d", out, code)
	}
}

func TestExecNonZeroExit(t *testing.T) {
	m := NewManager()
	s := m.Create()
	out, code, err := m.Exec(s.ID, "false")
	if err != nil {
		t.Fatalf("Exec: %v", err)
	}
	if code == 0 {
		t.Fatalf("false should exit non-zero, got %d out=%q", code, out)
	}
}

func TestExecUnknownSession(t *testing.T) {
	m := NewManager()
	if _, _, err := m.Exec("ghost", "true"); err == nil {
		t.Fatal("exec on unknown session should fail")
	}
}

func TestHandlerOps(t *testing.T) {
	m := NewManager()
	h := Handler(m)
	if _, err := h(map[string]any{"op": "bogus"}); err == nil {
		t.Fatal("unknown op should fail")
	}
	res, err := h(map[string]any{"op": "create"})
	if err != nil {
		t.Fatalf("create: %v", err)
	}
	id, _ := res.Data["id"].(string)
	token, _ := res.Data["token"].(string)
	if _, err := h(map[string]any{"op": "reconnect", "id": id, "token": token}); err != nil {
		t.Fatalf("reconnect: %v", err)
	}
	if _, err := h(map[string]any{"op": "exec", "id": id, "command": "echo ok"}); err != nil {
		t.Fatalf("exec: %v", err)
	}
	if _, err := h(map[string]any{"op": "close", "id": id}); err != nil {
		t.Fatalf("close: %v", err)
	}
}

func TestAttachContainer(t *testing.T) {
	m := NewManager()
	h := Handler(m)
	res, err := h(map[string]any{"op": "attach_container", "container": "web"})
	if err != nil {
		t.Fatalf("attach_container: %v", err)
	}
	id, _ := res.Data["id"].(string)
	token, _ := res.Data["token"].(string)
	container, _ := res.Data["container"].(string)
	if id == "" || token == "" || container != "web" {
		t.Fatalf("data = %+v", res.Data)
	}
	// 会话路由不变：reconnect/exec 沿用同一 session_id。
	if _, err := h(map[string]any{"op": "reconnect", "id": id, "token": token}); err != nil {
		t.Fatalf("reconnect: %v", err)
	}
	if _, err := h(map[string]any{"op": "close", "id": id}); err != nil {
		t.Fatalf("close: %v", err)
	}
}

func TestAttachContainerValidation(t *testing.T) {
	m := NewManager()
	h := Handler(m)
	if _, err := h(map[string]any{"op": "attach_container", "container": "-bad/name"}); err == nil {
		t.Fatal("invalid container name should fail")
	}
	if _, err := h(map[string]any{"op": "attach_container"}); err == nil {
		t.Fatal("missing container should fail")
	}
}
