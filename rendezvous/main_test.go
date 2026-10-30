package main

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"
)

var room = strings.Repeat("ab", 32)

func start(t *testing.T, ttl time.Duration) *httptest.Server {
	ts := httptest.NewServer(newServer(ttl).handler())
	t.Cleanup(ts.Close)
	return ts
}

func meet(ts *httptest.Server, op, port string) map[string]any {
	body := `{"v":1,"op":"` + op + `","room":"` + room + `","local":["192.168.1.5:1"]}`
	rq, _ := http.NewRequest("POST", ts.URL+"/v1/meet", strings.NewReader(body))
	rq.Header.Set("X-Real-IP", "203.0.113.7")
	rq.Header.Set("X-Real-Port", port)
	resp, err := http.DefaultClient.Do(rq)
	if err != nil {
		return map[string]any{"error": err.Error()}
	}
	defer resp.Body.Close()
	m := map[string]any{}
	json.NewDecoder(resp.Body).Decode(&m)
	return m
}

func TestMeet(t *testing.T) {
	ts := start(t, time.Minute)
	hc := make(chan map[string]any)
	go func() { hc <- meet(ts, "host", "1111") }()
	time.Sleep(200 * time.Millisecond)
	jm := meet(ts, "join", "2222")
	hm := <-hc
	if hm["you"] != "203.0.113.7:1111" || hm["peer"] != "203.0.113.7:2222" || jm["peer"] != hm["you"] || jm["you"] != hm["peer"] {
		t.Fatalf("mismatch %v %v", hm, jm)
	}
	if hm["peer_local"].([]any)[0] != "192.168.1.5:1" {
		t.Fatal("peer_local")
	}
}

func TestBusy(t *testing.T) {
	ts := start(t, 2*time.Second)
	go meet(ts, "host", "1")
	time.Sleep(200 * time.Millisecond)
	if meet(ts, "host", "2")["error"] != "room_busy" {
		t.Fatal("want room_busy")
	}
}

func TestTTL(t *testing.T) {
	ts := start(t, 200*time.Millisecond)
	if meet(ts, "host", "1")["error"] != "timeout" {
		t.Fatal("want timeout")
	}
}

func TestPing(t *testing.T) {
	ts := start(t, time.Minute)
	rq, _ := http.NewRequest("GET", ts.URL+"/v1/ping", nil)
	rq.Header.Set("X-Real-IP", "203.0.113.7")
	rq.Header.Set("X-Real-Port", "4000")
	resp, err := http.DefaultClient.Do(rq)
	if err != nil {
		t.Fatal(err)
	}
	m := map[string]any{}
	json.NewDecoder(resp.Body).Decode(&m)
	if m["pong"] != true || m["you"] != "203.0.113.7:4000" {
		t.Fatal(m)
	}
}
