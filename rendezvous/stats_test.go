package main

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"
)

func post(ts *httptest.Server, path, body string) map[string]any {
	resp, err := http.Post(ts.URL+path, "application/json", strings.NewReader(body))
	if err != nil {
		return map[string]any{"error": err.Error()}
	}
	defer resp.Body.Close()
	m := map[string]any{}
	json.NewDecoder(resp.Body).Decode(&m)
	return m
}

func pair(ts *httptest.Server) {
	hc := make(chan map[string]any)
	go func() { hc <- meet(ts, "host", "1111") }()
	time.Sleep(200 * time.Millisecond)
	meet(ts, "join", "2222")
	<-hc
}

func TestResultAndStats(t *testing.T) {
	s := newServer(time.Minute)
	ts := httptest.NewServer(s.handler())
	defer ts.Close()
	body := func(p string) string { return `{"v":1,"room":"` + room + `","path":"` + p + `"}` }
	if m := post(ts, "/v1/result", body("lan")); m["counted"] != false {
		t.Fatal("unmatched room counted")
	}
	pair(ts)
	if m := post(ts, "/v1/result", body("internet")); m["counted"] != true {
		t.Fatal("not counted")
	}
	if m := post(ts, "/v1/result", body("none")); m["counted"] != false {
		t.Fatal("duplicate counted")
	}
	if m := post(ts, "/v1/result", `{"v":1,"room":"x","path":"lan"}`); m["error"] == nil {
		t.Fatal("bad room accepted")
	}
	c := s.snapshot().Total
	if c.Meets != 1 || c.DirectOK != 1 || c.OKInternet != 1 || c.OKLan != 0 || c.DirectFail != 0 {
		t.Fatalf("%+v", c)
	}
	if d := s.snapshot().Days[today()]; d != c {
		t.Fatalf("day %+v", d)
	}
	s.mu.Lock()
	s.matches[room] = &matched{at: time.Now().Add(-time.Hour)}
	s.mu.Unlock()
	if m := post(ts, "/v1/result", body("none")); m["counted"] != false {
		t.Fatal("expired counted")
	}
}

func TestLanAndFail(t *testing.T) {
	s := newServer(time.Minute)
	ts := httptest.NewServer(s.handler())
	defer ts.Close()
	pair(ts)
	post(ts, "/v1/result", `{"v":1,"room":"`+room+`","path":"lan"}`)
	if c := s.snapshot().Total; c.OKLan != 1 || c.DirectOK != 1 {
		t.Fatalf("%+v", c)
	}
	s.mu.Lock()
	s.matches[room] = &matched{at: time.Now()}
	s.mu.Unlock()
	post(ts, "/v1/result", `{"v":1,"room":"`+room+`","path":"none"}`)
	if c := s.snapshot().Total; c.DirectFail != 1 || c.DirectOK != 1 {
		t.Fatalf("%+v", c)
	}
}

func TestStatsAuth(t *testing.T) {
	s := newServer(time.Minute)
	ts := httptest.NewServer(s.handler())
	defer ts.Close()
	get := func(auth string) int {
		rq, _ := http.NewRequest("GET", ts.URL+"/v1/stats", nil)
		if auth != "" {
			rq.Header.Set("Authorization", auth)
		}
		r, _ := http.DefaultClient.Do(rq)
		r.Body.Close()
		return r.StatusCode
	}
	if get("Bearer k") != 404 {
		t.Fatal("unset key must 404")
	}
	t.Setenv("STATS_KEY", "k")
	if get("") != 401 || get("Bearer no") != 401 || get("Bearer k") != 200 {
		t.Fatal("auth")
	}
}

func TestPersist(t *testing.T) {
	p := t.TempDir() + "/stats.json"
	s := newServer(time.Minute)
	s.statsPath = p
	s.bump(func(c *counts) { c.Meets += 3 })
	s.flush()
	s2 := newServer(time.Minute)
	s2.statsPath = p
	s2.loadStats()
	if s2.snapshot().Total.Meets != 3 || s2.snapshot().Days[today()].Meets != 3 {
		t.Fatal("not restored")
	}
}
